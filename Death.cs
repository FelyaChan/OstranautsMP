using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using LitJson;
using UnityEngine;

namespace OstranautsMP
{
	// The client's character dies.
	//
	// The host decides it (the game's CondOwner.Kill there). The body stays where it fell with its name, things and
	// history; the profile's mark (Profile.Gpm) on it becomes MPDead: the profile ID, the time of death and the
	// character's training (StatTraining*). The client is left watching (free camera, no orders) with a "your character
	// died" window: a name for the next body (the old one with II, III...) and the New body button, which works while
	// the host's ship is in port (docked with a station). The new body is made as the first one (CrewClaim.HostNewBody)
	// and gets 80% of the training of the profile's last body; skills learned in the game are lost until trained again,
	// the profile's own skills come back with the profile.
	//
	// The record is on the body because the body may stay on a station the host has left: then it's found in that
	// ship's data (json.aCrew), like CrewClaim.OwnedAway does.
	//
	// Not handled: the host's own death (the game's game over) and a death while apart (only logged).
	//
	// Protocol: host -> client DIED <body ID>                 the client's character died (also the answer to DEADQ)
	//                          PORT 1|0                        the host's ship is in port or not
	//                          NAMETAKEN <free name>           with CLAIM_DENIED: the name asked for is taken
	//           client -> host DEADQ <profile ID>              in the game without a character: did it die?
	public static class Death
	{
		internal const string Gpm = "MPDead";

		internal const string TrainingPrefix = "StatTraining";

		internal const double TrainingKept = 0.8;

		private const int WindowId = 0x0B4F7;

		private const float PortInterval = 2f;

		private const float PortRepeat = 10f;

		// Host.
		private static bool _sentPort;

		private static bool _lastPort;

		private static float _nextPort;

		private static float _lastPortSent = -100f;

		// Client.
		internal static bool ClientDead;

		private static string _deadName;

		private static string _newName = "";

		private static string _askedName;

		private static string _status;

		private static bool _inPort;

		private static bool _askedDead;

		private static bool _allowKill;

		private static Rect _rect = new Rect(0f, 0f, 420f, 10f);

		public static void Patch(Harmony harmony)
		{
			harmony.Patch(AccessTools.PropertySetter(typeof(CondOwner), "Kill"), prefix: new HarmonyMethod(typeof(Death), nameof(KillPrefix)), postfix: new HarmonyMethod(typeof(Death), nameof(KillPostfix)));
			harmony.Patch(AccessTools.Method(typeof(CrewSim), "ScheduleCODestruction"), prefix: new HarmonyMethod(typeof(Death), nameof(DestroyBodyPrefix)));
		}

		// Host: death records of bodies the game destroyed (jettisoned), by body name. This session only.
		private static readonly Dictionary<string, Dictionary<string, string>> Destroyed = new Dictionary<string, Dictionary<string, string>>();

		// Host: a body destroyed outright (Jettison takes the unconscious too, without killing them). A profile's living
		// character dies first, the usual way; the death record is kept here, as the body that held it is gone; and
		// the client drops the body too (people's removal isn't sent otherwise).
		private static void DestroyBodyPrefix(CondOwner coToDestroy)
		{
			CondOwner co = coToDestroy;
			if (co == null || co.bDestroyed || Plugin.Net.CurrentMode == NetSession.Mode.Client)
			{
				return;
			}
			if (co.bAlive && Profile.OwnerOf(co) != null)
			{
				Plugin.Log.LogInfo("Death: " + co.strName + " is being destroyed (jettisoned?) alive");
				try
				{
					co.Kill = true;
				}
				catch (Exception e)
				{
					Plugin.Log.LogError("Death: killing " + co.strName + " before destruction: " + e);
				}
			}
			Dictionary<string, string> rec;
			if (co.mapGUIPropMaps == null || !co.mapGUIPropMaps.TryGetValue(Gpm, out rec) || rec == null)
			{
				return;
			}
			Destroyed[co.strID] = rec;
			if (Plugin.Net.CurrentMode == NetSession.Mode.Host && Plugin.Net.Connected)
			{
				Plugin.Net.Send("GONE " + co.strID);
			}
		}

		// ---- Death ------------------------------------------------------------------------------------------------

		// Client: our character dies only when the host says so (DIED), except while apart, where our place runs here.
		private static bool KillPrefix(CondOwner __instance, bool value)
		{
			if (!value || _allowKill || Plugin.Net.CurrentMode != NetSession.Mode.Client || !Plugin.Net.Connected)
			{
				return true;
			}
			string mine = CrewClaim.MineId;
			if (mine == null || __instance == null || __instance.strID != mine || !__instance.bAlive)
			{
				return true;
			}
			if (Apart.ClientActive)
			{
				Plugin.Log.LogInfo("Death: " + __instance.strName + " died here while apart from the host");
				return true;
			}
			return false;
		}

		// Host: a profile's character died.
		private static void KillPostfix(CondOwner __instance, bool value)
		{
			if (!value || __instance == null || __instance.bAlive || !__instance.HasCond("IsDead") || Plugin.Net.CurrentMode == NetSession.Mode.Client)
			{
				return;
			}
			string profileId = Profile.OwnerOf(__instance);
			if (profileId == null)
			{
				return;
			}
			if (Apart.HostActive)
			{
				Plugin.Log.LogInfo("Death: " + __instance.strName + " died while apart; not handled");
				return;
			}
			Dictionary<string, string> rec = new Dictionary<string, string>();
			rec["id"] = profileId;
			rec["t"] = StarSystem.fEpoch.ToString("R", CultureInfo.InvariantCulture);
			int n = 0;
			if (__instance.mapConds != null)
			{
				foreach (string cond in new List<string>(__instance.mapConds.Keys))
				{
					if (cond != null && cond.StartsWith(TrainingPrefix, StringComparison.Ordinal))
					{
						rec[cond] = __instance.GetCondAmount(cond).ToString("R", CultureInfo.InvariantCulture);
						n++;
					}
				}
			}
			if (__instance.mapGUIPropMaps != null)
			{
				__instance.mapGUIPropMaps.Remove(Profile.Gpm);
				__instance.mapGUIPropMaps[Gpm] = rec;
			}
			CrewClaim.HostCharacterDied(__instance.strID);
			Plugin.Log.LogInfo("Death: " + __instance.strName + " died (profile " + profileId + ", " + n + " training values kept)");
			if (Plugin.Net.CurrentMode == NetSession.Mode.Host && Plugin.Net.Connected)
			{
				Plugin.Net.Send("DIED " + __instance.strID);
			}
		}

		// ---- Host -------------------------------------------------------------------------------------------------

		// The host is on a faction's working station or their ship is docked with one (OKLG...); an abandoned one
		// (derelict or nobody's) doesn't count.
		internal static bool InPort()
		{
			CondOwner player = CrewSim.coPlayer;
			Ship ship = (player != null) ? player.ship : null;
			if (ship == null || ship.bDestroyed)
			{
				return false;
			}
			if (IsPort(ship))
			{
				return true;
			}
			IReadOnlyList<Ship> docked = ship.GetAllDockedShips();
			if (docked != null)
			{
				foreach (Ship s in docked)
				{
					if (IsPort(s))
					{
						return true;
					}
				}
			}
			return false;
		}

		private static bool IsPort(Ship s)
		{
			if (s == null || s.bDestroyed || (!s.IsStation() && !s.IsSubStation()) || s.IsDerelict())
			{
				return false;
			}
			List<JsonFaction> factions = s.GetShipFactions();
			return factions != null && factions.Count > 0;
		}

		// The profile's most recent dead body, here or on a ship that isn't loaded: its record (null if none) and name.
		internal static Dictionary<string, string> LastDeath(string profileId, out string bodyName)
		{
			bodyName = null;
			Dictionary<string, string> best = null;
			double bestT = double.MinValue;
			foreach (CondOwner co in DataHandler.mapCOs.Values)
			{
				Dictionary<string, string> rec;
				if (co == null || co.bDestroyed || co.mapGUIPropMaps == null || !co.mapGUIPropMaps.TryGetValue(Gpm, out rec) || rec == null)
				{
					continue;
				}
				Consider(rec, co.strID, profileId, ref best, ref bestT, ref bodyName);
			}
			foreach (KeyValuePair<string, Dictionary<string, string>> kv in Destroyed)
			{
				Consider(kv.Value, kv.Key, profileId, ref best, ref bestT, ref bodyName);
			}
			if (CrewSim.system != null && CrewSim.system.dictShips != null)
			{
				foreach (Ship ship in CrewSim.system.dictShips.Values)
				{
					if (ship == null || ship.bDestroyed || ship.LoadState >= Ship.Loaded.Full || ship.json == null || ship.json.aCrew == null)
					{
						continue;
					}
					// The body is kept with the crew; with the items too, in case the game files it there.
					List<JsonItem> saved = new List<JsonItem>(ship.json.aCrew);
					if (ship.json.aItems != null)
					{
						saved.AddRange(ship.json.aItems);
					}
					foreach (JsonItem it in saved)
					{
						if (it == null || it.aGPMSettings == null)
						{
							continue;
						}
						foreach (JsonGUIPropMap g in it.aGPMSettings)
						{
							if (g == null || g.strName != Gpm || g.dictGUIPropMap == null)
							{
								continue;
							}
							Dictionary<string, string> rec = new Dictionary<string, string>();
							for (int i = 0; i + 1 < g.dictGUIPropMap.Length; i += 2)
							{
								rec[g.dictGUIPropMap[i]] = g.dictGUIPropMap[i + 1];
							}
							Consider(rec, it.strID, profileId, ref best, ref bestT, ref bodyName);
						}
					}
				}
			}
			return best;
		}

		private static void Consider(Dictionary<string, string> rec, string name, string profileId, ref Dictionary<string, string> best, ref double bestT, ref string bodyName)
		{
			string id;
			string ts;
			double t;
			if (!rec.TryGetValue("id", out id) || id != profileId)
			{
				return;
			}
			if (!rec.TryGetValue("t", out ts) || !double.TryParse(ts, NumberStyles.Float, CultureInfo.InvariantCulture, out t))
			{
				t = double.MinValue + 1.0;
			}
			if (best == null || t > bestT)
			{
				best = rec;
				bestT = t;
				bodyName = name;
			}
		}

		// No training at all (a new body comes with the game's random training).
		internal static void ResetTraining(CondOwner co)
		{
			if (co == null || co.mapConds == null)
			{
				return;
			}
			foreach (string cond in new List<string>(co.mapConds.Keys))
			{
				if (cond != null && cond.StartsWith(TrainingPrefix, StringComparison.Ordinal))
				{
					co.ZeroCondAmount(cond);
				}
			}
		}

		// The training of the last body, at TrainingKept.
		internal static void ApplyTraining(CondOwner co, Dictionary<string, string> rec)
		{
			foreach (KeyValuePair<string, string> kv in rec)
			{
				double v;
				if (kv.Key.StartsWith(TrainingPrefix, StringComparison.Ordinal) && double.TryParse(kv.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v != 0.0)
				{
					co.SetCondAmount(kv.Key, v * TrainingKept);
				}
			}
		}

		// Nobody, here or on a ship that isn't loaded, has this name (a character's ID is their name).
		internal static bool NameFree(string name)
		{
			if (Mirror.NameProblem(name, null) != null)
			{
				return false;
			}
			if (CrewSim.system == null || CrewSim.system.dictShips == null)
			{
				return true;
			}
			foreach (Ship ship in CrewSim.system.dictShips.Values)
			{
				if (ship == null || ship.json == null)
				{
					continue;
				}
				if (ship.json.aCrew != null)
				{
					foreach (JsonItem it in ship.json.aCrew)
					{
						if (it != null && it.strID == name)
						{
							return false;
						}
					}
				}
				if (ship.json.aCOs != null)
				{
					foreach (JsonCondOwnerSave s in ship.json.aCOs)
					{
						if (s != null && s.strID == name)
						{
							return false;
						}
					}
				}
			}
			return true;
		}

		// The first free name of name, name II, name III...
		internal static string FreeName(string name)
		{
			string n = name;
			for (int i = 0; i < 50 && !NameFree(n); i++)
			{
				n = NextName(n);
			}
			return n;
		}

		private static readonly string[] Romans = { "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X", "XI", "XII", "XIII", "XIV", "XV", "XVI", "XVII", "XVIII", "XIX", "XX" };

		// "Spica Virginis" -> "Spica Virginis II" -> "Spica Virginis III"...
		internal static string NextName(string name)
		{
			name = (name ?? "").Trim();
			int space = name.LastIndexOf(' ');
			string last = (space >= 0) ? name.Substring(space + 1) : null;
			int at = (last != null) ? Array.IndexOf(Romans, last) : -1;
			if (at >= 0)
			{
				string stem = name.Substring(0, space);
				return (at + 1 < Romans.Length) ? (stem + " " + Romans[at + 1]) : (stem + " " + (at + 3));
			}
			int num;
			if (last != null && int.TryParse(last, out num) && num >= 2)
			{
				return name.Substring(0, space) + " " + (num + 1);
			}
			return name + " II";
		}

		private static void HostUpdate(float now)
		{
			if (now < _nextPort)
			{
				return;
			}
			_nextPort = now + PortInterval;
			bool port = WorldSync.InGame && InPort();
			if (!_sentPort || port != _lastPort || now - _lastPortSent >= PortRepeat)
			{
				Plugin.Net.Send("PORT " + (port ? "1" : "0"));
				_sentPort = true;
				_lastPort = port;
				_lastPortSent = now;
			}
		}

		private static void HostDeadQuestion(string profileId)
		{
			if (string.IsNullOrEmpty(profileId) || !WorldSync.InGame)
			{
				return;
			}
			if (CrewClaim.HasLivingCharacter(profileId))
			{
				return;
			}
			string body;
			if (LastDeath(profileId, out body) != null && body != null)
			{
				Plugin.Net.Send("DIED " + body);
			}
		}

		// ---- Client -----------------------------------------------------------------------------------------------

		private static void ClientDied(string id)
		{
			// Told again about the same body (the host repeats DIED when it makes the new body): already handled; going
			// on would drop "Waiting for host..." and let New body be pressed again.
			if (ClientDead && id != null && id == _deadName)
			{
				Plugin.Log.LogInfo("Death: " + id + " died (already known)");
				return;
			}
			CondOwner co;
			if (id != null && DataHandler.mapCOs.TryGetValue(id, out co) && co != null && !co.bDestroyed && co.bAlive)
			{
				_allowKill = true;
				try
				{
					co.Kill = true;
				}
				finally
				{
					_allowKill = false;
				}
			}
			CrewClaim.ClientLost();
			_newName = NextName(id);
			ClientDead = true;
			_deadName = id;
			_askedName = null;
			_status = null;
			Plugin.Log.LogInfo("Death: " + id + " died");
		}

		internal static void OnGUI()
		{
			if (_rect.x == 0f)
			{
				_rect.x = (Screen.width - _rect.width) / 2f;
				_rect.y = 60f;
			}
			_rect = GUILayout.Window(WindowId, _rect, DrawWindow, "CLONE LINK LOST", MenuLook.Window);
			Chat.AddBlocking(_rect);
		}

		private static void DrawWindow(int id)
		{
			GUISkin prev = MenuLook.Use();
			try
			{
				GUILayout.Label("Link to clone " + _deadName + " lost. Report to a station for a new neural code.");
				GUILayout.Label("WARNING: Traumatic experience detected. Cognitive and skill neurodegeneration: 20%.");
				GUILayout.Label("Designation of the new clone:");
				_newName = GUILayout.TextField(_newName ?? "", 50);
				GUI.enabled = _inPort && _askedName == null && Profile.Current != null && Profile.HasLooks && _newName.Trim().Length > 0;
				if (GUILayout.Button("New body"))
				{
					Profile.Data data = JsonMapper.ToObject<Profile.Data>(JsonMapper.ToJson(Profile.Current));
					data.Name = _newName.Trim();
					_askedName = data.Name;
					_status = "Waiting for host...";
					Plugin.Net.Send("NEWBODY " + Profile.Current.Id + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonMapper.ToJson(data))));
				}
				GUI.enabled = true;
				if (!_inPort)
				{
					GUILayout.Label("No station uplink. A new code is issued in port only.");
				}
				if (_status != null)
				{
					GUILayout.Label(_status);
				}
				GUI.DragWindow();
			}
			finally
			{
				MenuLook.Restore(prev);
			}
		}

		// CrewClaim: the host confirmed the new body.
		internal static void ClientNewBody()
		{
			if (!ClientDead)
			{
				return;
			}
			if (_askedName != null)
			{
				Profile.SetName(_askedName);
			}
			ClientDead = false;
			_deadName = null;
			_askedName = null;
			_status = null;
		}

		// CrewClaim: the host said no.
		internal static void ClientDenied(string reason)
		{
			if (!ClientDead)
			{
				return;
			}
			_askedName = null;
			_status = "Denied: " + reason;
		}

		// ---- Both -------------------------------------------------------------------------------------------------

		internal static void Update(float now)
		{
			if (!Plugin.Net.Connected)
			{
				_sentPort = false;
				ClientDead = false;
				_askedDead = false;
				_askedName = null;
				_inPort = false;
				return;
			}
			if (Plugin.Net.CurrentMode == NetSession.Mode.Host)
			{
				HostUpdate(now);
				return;
			}
			if (!WorldSync.InGame)
			{
				_askedDead = false;
				return;
			}
			// Back in the game without a character (a reconnect): ask once whether ours died.
			if (!_askedDead && !ClientDead && !CrewClaim.Confirmed && WorldSync.ClientFollowsHost && Profile.Current != null && Profile.Current.Id != null)
			{
				_askedDead = true;
				Plugin.Net.Send("DEADQ " + Profile.Current.Id);
			}
		}

		internal static bool OnMessage(string cmd, string arg)
		{
			switch (cmd)
			{
				case "DIED":
					if (Plugin.Net.CurrentMode == NetSession.Mode.Client && (arg == CrewClaim.MineId || !CrewClaim.Confirmed))
					{
						ClientDied(arg);
					}
					return true;
				case "PORT":
					if (Plugin.Net.CurrentMode == NetSession.Mode.Client)
					{
						_inPort = arg == "1";
					}
					return true;
				case "NAMETAKEN":
					if (Plugin.Net.CurrentMode == NetSession.Mode.Client && ClientDead && !string.IsNullOrEmpty(arg))
					{
						_newName = arg;
					}
					return true;
				case "DEADQ":
					if (Plugin.Net.CurrentMode == NetSession.Mode.Host)
					{
						HostDeadQuestion(arg);
					}
					return true;
				default:
					return false;
			}
		}
	}
}
