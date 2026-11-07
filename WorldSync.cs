using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace OstranautsMP
{
	// Host-authoritative pause, time scale, game time and character positions.
	//
	// Protocol (host -> client):
	//   TIME <paused 0|1> <timeScale> <epoch>          on change and every second
	//   POS <id>,<x>,<y>,<rotZ>,<anim>;...              10 times per second
	//   CONDS ...                                       see CondSync
	// Protocol (client -> host):
	//   READY                                           client finished loading into the game
	//   PAUSE <1|0>                                     the client pressed the pause key
	//   LOADP ...                                       see ClientLoad
	//
	// The client keeps running its own simulation; positions and animation state are overridden in LateUpdate,
	// after the game's Pathfinder has moved everyone for the frame.
	public static class WorldSync
	{
		private const float PosInterval = 0.1f;

		private const float TimeInterval = 1f;

		// Further than this the client snaps instead of smoothing.
		private const float SnapDistance = 3f;

		// Smoothing rate toward the latest host position, per second.
		private const float Smoothing = 15f;

		// Drop a target that the host has stopped sending (left the loaded ships, despawned).
		private const float TargetTimeout = 2f;

		// Game-time difference (seconds) the client tolerates before jumping to the host's time, plus a share of the time scale.
		private const double EpochToleranceBase = 1.0;

		private const double EpochTolerancePerScale = 0.25;

		private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

		// Destination footprints; a child of the character, so moving the character would drag them along.
		private static readonly System.Reflection.FieldInfo PathMarker = AccessTools.Field(typeof(Pathfinder), "goPath");

		private struct Target
		{
			public Vector2 Pos;

			public float Rot;

			// Animator "AnimState" value on the host; -1 if the host has no animator for this character.
			public int Anim;

			// The ship they're on at the host (null from an older host).
			public string Ship;

			public float Time;
		}

		private static readonly Dictionary<string, Target> Targets = new Dictionary<string, Target>();

		private static readonly List<string> Stale = new List<string>();

		private static readonly HashSet<string> SeenIds = new HashSet<string>();

		private static readonly StringBuilder Sb = new StringBuilder();

		private static float _nextPos;

		private static float _nextTime;

		private static bool _sentPaused;

		private static float _sentScale = -1f;

		private static bool _hasHostTime;

		private static bool _hostPaused;

		private static float _hostScale = 1f;

		// Client: whether we were in the game last frame, to send READY once per load.
		private static bool _wasInGame;

		// True while we apply host state, so our own prefixes let the call through.
		private static bool _applying;

		public static void Patch(Harmony harmony)
		{
			HarmonyMethod block = new HarmonyMethod(typeof(WorldSync), nameof(BlockOnClientPrefix));
			harmony.Patch(AccessTools.PropertySetter(typeof(CrewSim), "Paused"), prefix: new HarmonyMethod(typeof(WorldSync), nameof(PausedPrefix)));
			harmony.Patch(AccessTools.Method(typeof(GUIData), "Init"), prefix: new HarmonyMethod(typeof(WorldSync), nameof(GuiInitPrefix)), finalizer: new HarmonyMethod(typeof(WorldSync), nameof(GuiInitFinalizer)));
			harmony.Patch(AccessTools.Method(typeof(GUIData), "SaveAndClose"), prefix: new HarmonyMethod(typeof(WorldSync), nameof(GuiInitPrefix)), finalizer: new HarmonyMethod(typeof(WorldSync), nameof(GuiInitFinalizer)));
			harmony.Patch(AccessTools.Method(typeof(CrewSim), "TimeScaleMult"), prefix: block);
			harmony.Patch(AccessTools.Method(typeof(CrewSim), "ResetTimeScale"), prefix: block);
			harmony.Patch(AccessTools.Method(typeof(CondOwner), "SetAnimState"), prefix: new HarmonyMethod(typeof(WorldSync), nameof(SetAnimStatePrefix)));
			harmony.Patch(AccessTools.Method(typeof(Ostranauts.InputControl.CommandPause), "Execute"), prefix: new HarmonyMethod(typeof(WorldSync), nameof(PauseKeyPrefix)));
		}

		// Client: whatever the local simulation wants to play (its Pathfinder sets Walk every frame while it thinks the
		// character walks), a host-synced person plays the host's animation. Done here rather than in LateUpdate,
		// because by LateUpdate the animator has already evaluated the frame.
		private static void SetAnimStatePrefix(CondOwner __instance, ref int nState)
		{
			Target t;
			if (Plugin.Net.CurrentMode == NetSession.Mode.Client && __instance.strID != null && Targets.TryGetValue(__instance.strID, out t) && t.Anim >= 0)
			{
				nState = t.Anim;
			}
		}

		// Client: the pause key (Space) asks the host to pause or unpause for both. Only the key: windows that pause the
		// game on their own (GUIData.bPausesGame) still don't reach the host.
		private static bool PauseKeyPrefix()
		{
			if (!ClientFollowsHost || !InGame)
			{
				return true;
			}
			if (CanvasManager.instance != null && CanvasManager.instance.State != CanvasManager.GUIState.GAMEOVER && !CrewSim.Typing)
			{
				Plugin.Net.Send("PAUSE " + (_hostPaused ? "0" : "1"));
				AudioManager.am.PlayAudioEmitter("UIPauseBtnOut", bLoop: false);
			}
			return false;
		}

		// A game window (shop, jobs, finance...) opening or closing: some pause the game while open and set back on
		// closing the pause they found on opening (GUIData.bPausesGame).
		private static bool _guiOpening;

		private static void GuiInitPrefix()
		{
			_guiOpening = true;
		}

		// A finalizer: runs even when Init throws, so the flag never sticks.
		private static Exception GuiInitFinalizer(Exception __exception)
		{
			_guiOpening = false;
			return __exception;
		}

		// Pause: the client's rule below, the host can't unpause while the client loads (ClientLoad), and in
		// multiplayer a game window doesn't pause the game for everyone (closing it then sets the pause it found).
		private static bool PausedPrefix(bool value)
		{
			// Neither: closing would undo a pause the other player set meanwhile.
			if (_guiOpening && Plugin.Net.Connected)
			{
				return false;
			}
			return BlockOnClientPrefix() && !ClientLoad.BlocksUnpause(value);
		}

		// The client can't change pause or time scale; only the host's state is applied.
		private static bool BlockOnClientPrefix()
		{
			return _applying || !ClientFollowsHost;
		}

		internal static bool ClientFollowsHost => Plugin.Net.CurrentMode == NetSession.Mode.Client && Plugin.Net.Connected && _hasHostTime;

		internal static bool InGame => CrewSim.objInstance != null && CrewSim.objInstance.FinishedLoading && !CrewSim.bShipEdit;

		public static void Update(float now)
		{
			NetSession net = Plugin.Net;
			if (!net.Connected)
			{
				_hasHostTime = false;
				_wasInGame = false;
				ClientLoad.End();
				Targets.Clear();
				_sentScale = -1f;
				CondSync.ResetSnapshot();
				return;
			}
			bool inGame = InGame;
			if (net.CurrentMode == NetSession.Mode.Client)
			{
				if (inGame && !_wasInGame)
				{
					net.Send("READY");
					ItemSync.ResetKnown();
					Apart.OnClientLoaded();
					Plugin.Log.LogInfo("In game, sent READY");
				}
				_wasInGame = inGame;
			}
			if (!inGame)
			{
				return;
			}
			if (net.CurrentMode == NetSession.Mode.Host)
			{
				ClientLoad.HostUpdate();
				HostSendTime(now);
				if (now >= _nextPos)
				{
					_nextPos = now + PosInterval;
					HostSendPositions();
				}
				CondSync.HostUpdate(now);
			}
			else if (net.CurrentMode == NetSession.Mode.Client && _hasHostTime)
			{
				ClientApplyTime();
			}
		}

		public static void LateUpdate(float now, float dt)
		{
			if (Plugin.Net.CurrentMode != NetSession.Mode.Client || !Plugin.Net.Connected || !InGame)
			{
				return;
			}
			float k = 1f - Mathf.Exp((0f - Smoothing) * dt);
			foreach (KeyValuePair<string, Target> item in Targets)
			{
				if (now - item.Value.Time > TargetTimeout)
				{
					Stale.Add(item.Key);
					continue;
				}
				CondOwner co;
				if (!DataHandler.mapCOs.TryGetValue(item.Key, out co) || co == null || co.bDestroyed || co.tf == null)
				{
					continue;
				}
				FollowShip(co, item.Value);
				// Keep the footprints where the Pathfinder put them this frame.
				GameObject marker = (co.Pathfinder != null) ? (PathMarker.GetValue(co.Pathfinder) as GameObject) : null;
				bool hasMarker = marker != null && marker.activeSelf;
				Vector3 markerPos = hasMarker ? marker.transform.position : Vector3.zero;
				Quaternion markerRot = hasMarker ? marker.transform.rotation : Quaternion.identity;
				Vector3 p = co.tf.position;
				Vector2 cur = new Vector2(p.x, p.y);
				Vector2 next = (Vector2.Distance(cur, item.Value.Pos) > SnapDistance) ? item.Value.Pos : Vector2.Lerp(cur, item.Value.Pos, k);
				co.tf.position = new Vector3(next.x, next.y, p.z);
				float rot = Mathf.LerpAngle(co.tf.rotation.eulerAngles.z, item.Value.Rot, k);
				co.tf.rotation = Quaternion.AngleAxis(rot, Vector3.forward);
				if (hasMarker)
				{
					marker.transform.position = markerPos;
					marker.transform.rotation = markerRot;
				}
				// Only on change, so the animator isn't restarted every frame.
				if (item.Value.Anim >= 0 && co.GetAnimState() != item.Value.Anim)
				{
					co.SetAnimState(item.Value.Anim);
				}
			}
			foreach (string id in Stale)
			{
				Targets.Remove(id);
			}
			Stale.Clear();
		}

		// Main thread. Returns false if the message isn't ours.
		public static bool OnMessage(string cmd, string arg, float now)
		{
			NetSession.Mode mode = Plugin.Net.CurrentMode;
			switch (cmd)
			{
				case "TIME":
					if (mode == NetSession.Mode.Client)
					{
						ParseTime(arg);
					}
					return true;
				case "POS":
					if (mode == NetSession.Mode.Client)
					{
						ParsePositions(arg, now);
					}
					return true;
				case "CONDS":
					if (mode == NetSession.Mode.Client && InGame)
					{
						CondSync.Apply(arg);
					}
					return true;
				case "PAUSE":
					if (mode == NetSession.Mode.Host && InGame)
					{
						bool pause = arg == "1";
						if (CrewSim.Paused != pause)
						{
							CrewSim.Paused = pause;
							Chat.AddSystem((Chat.PeerName ?? "The other player") + (pause ? " paused the game" : " unpaused the game"));
						}
					}
					return true;
				case "READY":
					if (mode == NetSession.Mode.Host)
					{
						// The client has just loaded a save; send it everyone's conditions again.
						ClientLoad.End();
						CondSync.ResetSnapshot();
						SaveSync.ResetLocation();
						Apart.OnClientReady();
						ShipSync.OnClientReady();
						PlayerInteractions.ResetReplies();
						Plugin.Log.LogInfo("Client is in game");
					}
					return true;
				default:
					return false;
			}
		}

		private static void HostSendTime(float now)
		{
			bool paused = CrewSim.Paused;
			float scale = Time.timeScale;
			if (paused != _sentPaused || scale != _sentScale || now >= _nextTime)
			{
				_nextTime = now + TimeInterval;
				_sentPaused = paused;
				_sentScale = scale;
				Plugin.Net.Send("TIME " + (paused ? "1" : "0") + " " + scale.ToString("R", Inv) + " " + StarSystem.fEpoch.ToString("R", Inv));
			}
		}

		private static void HostSendPositions()
		{
			List<Ship> ships = CrewSim.GetAllLoadedShips();
			if (ships == null)
			{
				return;
			}
			Sb.Length = 0;
			SeenIds.Clear();
			foreach (Ship ship in ships)
			{
				if (ship == null || ship.bDestroyed || !Apart.HostSendsShip(ship.strRegID))
				{
					continue;
				}
				foreach (CondOwner co in ship.GetPeople(bAllowDocked: false))
				{
					if (co == null || co.bDestroyed || co.tf == null || co.strID == null || !SeenIds.Add(co.strID))
					{
						continue;
					}
					Vector3 p = co.tf.position;
					if (Sb.Length > 0)
					{
						Sb.Append(';');
					}
					Sb.Append(co.strID).Append(',');
					Sb.Append(p.x.ToString("0.###", Inv)).Append(',');
					Sb.Append(p.y.ToString("0.###", Inv)).Append(',');
					Sb.Append(co.tf.rotation.eulerAngles.z.ToString("0.#", Inv)).Append(',');
					Sb.Append(co.GetAnimState().ToString(Inv)).Append(',');
					Sb.Append(ship.strRegID);
				}
			}
			if (Sb.Length > 0)
			{
				Plugin.Net.Send("POS " + Sb);
			}
		}

		private static void ParseTime(string arg)
		{
			string[] parts = arg.Split(' ');
			float scale;
			double epoch;
			if (parts.Length != 3 || !float.TryParse(parts[1], NumberStyles.Float, Inv, out scale) || !double.TryParse(parts[2], NumberStyles.Float, Inv, out epoch))
			{
				return;
			}
			_hostPaused = parts[0] == "1";
			_hostScale = scale;
			_hasHostTime = true;
			if (InGame)
			{
				CorrectEpoch(epoch);
			}
		}

		// Jump to the host's game time when the client has drifted too far. The host's value is about half a ping old.
		private static void CorrectEpoch(double hostEpoch)
		{
			if (!_hostPaused && Plugin.Net.LastRttMs > 0)
			{
				hostEpoch += Plugin.Net.LastRttMs / 2000.0 * _hostScale;
			}
			double offset = hostEpoch - StarSystem.fEpoch;
			if (Math.Abs(offset) > EpochToleranceBase + EpochTolerancePerScale * _hostScale)
			{
				StarSystem.fEpoch = hostEpoch;
				Plugin.Log.LogInfo("Game time corrected by " + offset.ToString("0.00", Inv) + " s");
			}
		}

		// Client: a person who walked onto another (docked) ship at the host is moved onto it here too. Positions alone
		// don't do that: they'd stay a person of the old ship, which this client then doesn't show.
		private const float MineShipDelay = 3f;

		private static float _mineShipSince = -1f;

		private static string _mineShipWanted;

		private static void FollowShip(CondOwner co, Target t)
		{
			if (t.Ship == null || co.ship == null || co.ship.strRegID == t.Ship || CrewSim.system == null)
			{
				if (co == CrewClaim.ClientMine)
				{
					_mineShipSince = -1f;
				}
				return;
			}
			// The client's own character crosses ships by itself while walking (prediction); only a mismatch that
			// lasts is corrected, so a step ahead of the host isn't pulled back.
			if (co == CrewClaim.ClientMine)
			{
				float now = Time.unscaledTime;
				if (_mineShipSince < 0f || _mineShipWanted != t.Ship)
				{
					_mineShipSince = now;
					_mineShipWanted = t.Ship;
					return;
				}
				if (now - _mineShipSince < MineShipDelay)
				{
					return;
				}
				_mineShipSince = -1f;
			}
			Ship to = CrewSim.system.GetShipByRegID(t.Ship);
			if (to == null || to.bDestroyed || to.LoadState < Ship.Loaded.Full)
			{
				return;
			}
			try
			{
				Vector3 p = co.tf.position;
				co.RemoveFromCurrentHome(bForce: true);
				co.tf.position = new Vector3(t.Pos.x, t.Pos.y, p.z);
				to.AddCO(co, bTiles: true);
				Tile tile = to.GetTileAtWorldCoords1(t.Pos.x, t.Pos.y, bAllowDocked: false);
				co.currentRoom = (tile != null) ? tile.room : null;
				Plugin.Log.LogInfo("Moved " + co.strName + " onto " + t.Ship + " (as on the host)");
			}
			catch (Exception e)
			{
				Plugin.Log.LogWarning("Moving " + co.strName + " onto " + t.Ship + " failed: " + e.Message);
			}
		}

		private static void ParsePositions(string arg, float now)
		{
			foreach (string entry in arg.Split(';'))
			{
				string[] f = entry.Split(',');
				float x;
				float y;
				float r;
				int a;
				if ((f.Length == 5 || f.Length == 6) && float.TryParse(f[1], NumberStyles.Float, Inv, out x) && float.TryParse(f[2], NumberStyles.Float, Inv, out y) && float.TryParse(f[3], NumberStyles.Float, Inv, out r) && int.TryParse(f[4], NumberStyles.Integer, Inv, out a))
				{
					Target t;
					t.Pos = new Vector2(x, y);
					t.Rot = r;
					t.Anim = a;
					t.Ship = (f.Length == 6) ? f[5] : null;
					t.Time = now;
					Targets[f[0]] = t;
				}
			}
		}

		private static void ClientApplyTime()
		{
			_applying = true;
			try
			{
				if (CrewSim.Paused != _hostPaused)
				{
					CrewSim.Paused = _hostPaused;
				}
				if (Time.timeScale != _hostScale)
				{
					Time.timeScale = _hostScale;
					CrewSim.OnTimeScaleUpdated.Invoke();
				}
			}
			catch (Exception e)
			{
				Plugin.Log.LogError("Applying host time failed: " + e.Message);
			}
			finally
			{
				_applying = false;
			}
		}
	}
}
