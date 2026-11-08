using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using LitJson;
using UnityEngine;

namespace OstranautsMP
{
	// Space on the client mirrors the host's.
	//
	// The client runs no ship AI: AIShipManager.Update (piloting, spawning traffic, region cleanup), AIShipManager.FFWD
	// and StarSystem.CleanupStaleShips are off there. Instead the host sends where ships are: the ship we're on 5 times
	// a second, other free-flying ships within NearAU once a second, the rest every 10 s. Between updates the client's
	// own physics carries them on with the host's accelerations, so they move smoothly.
	// A ship the client doesn't have (the host spawned it) is asked for and sent whole, like a ship being docked to;
	// a ship that disappears on the host is destroyed on the client.
	//
	// Ships in a docking group are left alone (the group moves them; DockSync handles docking), as are bodies, stations,
	// waypoints and projectiles.
	//
	// The ship we're on is also sent as soon as its thrust or turning changes (at most every 50 ms), so a manoeuvre
	// reaches the client after one ping, not up to 200 ms later. On the client its position isn't snapped to the host's
	// but pulled there over a quarter of a second.
	//
	// Collisions are the host's: ProcessCollision (ship-ship, ship-body) and projectile hits don't run on the client.
	// Proximity and the closest-ATC lookup in CheckCollisions still do. Damage reaches the client through item/cond sync.
	//
	// Piloting by the client: the nav station and docking screen call Ship.Maneuver(x, y, rotation, ...) every frame
	// with the current input. On the client, for the ship we're on, that call still runs (so the ship reacts at once)
	// and its input is sent to the host when it changes (PILOT). The host keeps applying it every frame until it's zero
	// or nothing came for 1.5 s; the host's own idle (zero) calls are skipped meanwhile, and if the host steers too, the
	// host's input wins. While the client steers, host updates don't overwrite the client's own thrust and turning.
	//
	// Protocol:
	//   SHIPS <host epoch> <entry>^<entry>...    the client carries each ship on from the host's epoch to its own
	//                               (ShipSitu.TimeAdvance), so clock offset and lag don't shift ships against the
	//                               bodies and stations, which the client places by its own epoch
	//                               entry: reg~x~y~vx~vy~rot~w~a~in.x~in.y~rcs.x~rcs.y~ex.x~ex.y~lift.x~lift.y~drag.x~drag.y~flags~bo~offx~offy
	//                               flags: 1 = locked to a body, 2 = locked in orbit; bo = body name
	//   SHIPGONE <reg>^<reg>...
	//   NEEDSHIP <reg>              client -> host
	//   PILOT <reg>|<x>|<y>|<r>|<noiseOnly>|<engineMode>   client -> host; x = y = r = 0 stops
	//   BIN SHIPNEW <gzip JSON DockSync.DockPayload>
	//   NAVCLEAR <P|T>|<reg>|<id>^<id>...  both ways: the nav station's Prox / Track "Clear" button was pressed; the
	//                               receiver ignores those ships too, and its own current ones
	public static class ShipSync
	{
		private const double NearAU = 0.1;

		private const float OwnInterval = 0.2f;

		private const float NearInterval = 1f;

		private const float FarInterval = 10f;

		private const float GoneInterval = 2f;

		private const float NeedRetry = 10f;

		private const float OwnMinInterval = 0.05f;

		private const float SmoothSeconds = 0.25f;

		// Farther off than this (about 15 000 km) the client's ship jumps straight to the host's position.
		private const double SnapAU = 1e-7;

		private const float PilotTimeout = 1.5f;

		private const float PilotResend = 0.5f;

		private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

		// Host.
		private static bool _clientReady;

		private static float _nextOwn;

		private static float _nextNear;

		private static float _nextFar;

		private static float _nextGone;

		private static HashSet<string> _known;

		private static float _lastOwnSent;

		private static readonly float[] LastOwnAcc = new float[5];

		// Host: the client's helm input.
		private static string _pilotReg;

		private static float _pX;

		private static float _pY;

		private static float _pR;

		private static int _pNoise;

		private static int _pMode;

		private static bool _pilotActive;

		private static float _pilotLast;

		private static int _hostThrustFrame = -10;

		private static bool _relayingPilot;

		// Client: our helm input as last sent, and until when we count as steering.
		private static string _lastPilotKey;

		private static float _lastPilotSent;

		private static float _steeringUntil;

		// Client: what's left of the position (and, while steering, velocity) correction for the ship we're on.
		private static double _corrX;

		private static double _corrY;

		private static double _corrVX;

		private static double _corrVY;

		// Packets older than this (game seconds) aren't carried forward: something's off, take them as they are.
		private const double MaxCatchUp = 120.0;

		// Client: when we last asked for a missing ship.
		private static readonly Dictionary<string, float> Asked = new Dictionary<string, float>();

		public static void Patch(Harmony harmony)
		{
			HarmonyMethod off = new HarmonyMethod(typeof(ShipSync), nameof(OffOnClient));
			harmony.Patch(AccessTools.Method(typeof(AIShipManager), "Update"), prefix: off);
			harmony.Patch(AccessTools.Method(typeof(AIShipManager), "FFWD"), prefix: off);
			harmony.Patch(AccessTools.Method(typeof(StarSystem), "CleanupStaleShips"), prefix: off);
			harmony.Patch(AccessTools.Method(typeof(CollisionManager), "CheckProjectileCollisions"), prefix: off);
			foreach (System.Reflection.MethodInfo m in AccessTools.GetDeclaredMethods(typeof(CollisionManager)))
			{
				if (m.Name == "ProcessCollision")
				{
					harmony.Patch(m, prefix: off);
				}
			}
			harmony.Patch(AccessTools.Method(typeof(Ship), "Maneuver"), prefix: new HarmonyMethod(typeof(ShipSync), nameof(ManeuverPrefix)));
			harmony.Patch(AccessTools.Method(typeof(Ostranauts.ShipGUIs.NavStation.NavModWarnings), "ProxClear"), postfix: new HarmonyMethod(typeof(ShipSync), nameof(ProxClearPostfix)));
			harmony.Patch(AccessTools.Method(typeof(Ostranauts.ShipGUIs.NavStation.NavModWarnings), "TrackClear"), postfix: new HarmonyMethod(typeof(ShipSync), nameof(TrackClearPostfix)));
		}

		private static bool Mirroring => WorldSync.ClientFollowsHost && WorldSync.InGame;

		private static bool OffOnClient()
		{
			return !Mirroring;
		}

		// Host, on the client's READY: it has the world as the save had it.
		internal static void OnClientReady()
		{
			_clientReady = true;
			_known = CurrentShips();
			_nextOwn = _nextNear = _nextFar = _nextGone = 0f;
		}

		private static bool Mirrored(Ship s)
		{
			if (s == null || s.bDestroyed || s.objSS == null || s.strRegID == null)
			{
				return false;
			}
			if (s.Classification == Ship.TypeClassification.Waypoint || s.Classification == Ship.TypeClassification.Projectile)
			{
				return false;
			}
			if (s.objSS.bIsBO || s.IsStation())
			{
				return false;
			}
			return s.GetAllDockedShips().Count == 0;
		}

		private static HashSet<string> CurrentShips()
		{
			HashSet<string> set = new HashSet<string>();
			foreach (Ship s in CrewSim.system.dictShips.Values)
			{
				if (s != null && !s.bDestroyed && s.Classification != Ship.TypeClassification.Projectile && s.Classification != Ship.TypeClassification.Waypoint)
				{
					set.Add(s.strRegID);
				}
			}
			return set;
		}

		// ---- Host ----------------------------------------------------------------------------------------------

		// Called from Plugin.Update.
		public static void Update(float now)
		{
			NetSession net = Plugin.Net;
			if (Mirroring)
			{
				ClientUpdate(now);
				return;
			}
			if (net.CurrentMode != NetSession.Mode.Host || !net.Connected)
			{
				_clientReady = false;
				_known = null;
				_pilotActive = false;
				return;
			}
			if (!_clientReady || !WorldSync.InGame || CrewSim.system == null || SaveSync.Progress != null)
			{
				return;
			}
			HostApplyPilot(now);
			Ship own = CrewSim.shipCurrentLoaded;
			bool ownDue = now >= _nextOwn || (now - _lastOwnSent >= OwnMinInterval && Mirrored(own) && OwnThrustChanged(own.objSS));
			if (ownDue)
			{
				_nextOwn = now + OwnInterval;
				if (Mirrored(own))
				{
					StringBuilder one = new StringBuilder();
					Append(one, own);
					net.Send("SHIPS " + StarSystem.fEpoch.ToString("R", Inv) + " " + one);
					_lastOwnSent = now;
					RememberThrust(own.objSS);
				}
			}
			bool near = now >= _nextNear;
			bool far = now >= _nextFar;
			if (near || far)
			{
				if (near)
				{
					_nextNear = now + NearInterval;
				}
				if (far)
				{
					_nextFar = now + FarInterval;
				}
				StringBuilder sb = new StringBuilder();
				foreach (Ship s in CrewSim.system.dictShips.Values)
				{
					if (s == own || !Mirrored(s))
					{
						continue;
					}
					bool isNear = own != null && own.objSS != null && Distance(own.objSS, s.objSS) < NearAU;
					if ((isNear && near) || (!isNear && far))
					{
						Append(sb, s);
					}
				}
				if (sb.Length > 0)
				{
					net.Send("SHIPS " + StarSystem.fEpoch.ToString("R", Inv) + " " + sb);
				}
			}
			if (now >= _nextGone)
			{
				_nextGone = now + GoneInterval;
				HashSet<string> current = CurrentShips();
				if (_known != null)
				{
					StringBuilder gone = new StringBuilder();
					foreach (string reg in _known)
					{
						if (!current.Contains(reg))
						{
							if (gone.Length > 0)
							{
								gone.Append('^');
							}
							gone.Append(reg);
						}
					}
					if (gone.Length > 0)
					{
						net.Send("SHIPGONE " + gone);
						Plugin.Log.LogInfo("Ships gone: " + gone);
					}
				}
				_known = current;
			}
		}

		private static void ThrustValues(ShipSitu o, float[] v)
		{
			v[0] = o.vAccIn.x;
			v[1] = o.vAccIn.y;
			v[2] = o.vAccRCS.x;
			v[3] = o.vAccRCS.y;
			v[4] = o.fA;
		}

		private static readonly float[] NowAcc = new float[5];

		// Started, stopped, or changed by more than 5% (thrust varies a little every frame as fuel is used).
		private static bool OwnThrustChanged(ShipSitu o)
		{
			ThrustValues(o, NowAcc);
			for (int i = 0; i < 5; i++)
			{
				float a = LastOwnAcc[i];
				float b = NowAcc[i];
				if ((a == 0f) != (b == 0f) || Math.Abs(a - b) > 0.05f * Math.Max(Math.Abs(a), Math.Abs(b)))
				{
					return true;
				}
			}
			return false;
		}

		private static void RememberThrust(ShipSitu o)
		{
			ThrustValues(o, LastOwnAcc);
		}

		// Host: is this the ship we're on, or docked to it?
		private static bool IsOurShip(Ship s)
		{
			Ship cur = CrewSim.shipCurrentLoaded;
			return s != null && cur != null && (s == cur || System.Linq.Enumerable.Contains(cur.GetAllDockedShips(), s));
		}

		private static bool IsZero(float x, float y, float r)
		{
			return Math.Abs(x) <= 1E-05f && Math.Abs(y) <= 1E-05f && Math.Abs(r) <= 1E-05f;
		}

		private static bool ManeuverPrefix(Ship __instance, float fX, float fY, float fR, int nNoiseOnly, Ship.EngineMode engineMode)
		{
			if (_relayingPilot)
			{
				return true;
			}
			if (Mirroring)
			{
				if (IsOurShip(__instance))
				{
					ClientSendPilot(__instance, fX, fY, fR, nNoiseOnly, engineMode);
				}
				return true;
			}
			if (_pilotActive && __instance.strRegID == _pilotReg && Plugin.Net.CurrentMode == NetSession.Mode.Host)
			{
				if (IsZero(fX, fY, fR))
				{
					// The host's helm is idle; don't let it cancel the client's thrust.
					return false;
				}
				_hostThrustFrame = Time.frameCount;
			}
			return true;
		}

		private static void HostApplyPilot(float now)
		{
			if (!_pilotActive)
			{
				return;
			}
			Ship s = CrewSim.system.GetShipByRegID(_pilotReg);
			if (s == null || s.bDestroyed || !IsOurShip(s) || now - _pilotLast > PilotTimeout)
			{
				HostStopPilot();
				return;
			}
			float dt = CrewSim.TimeElapsedScaled();
			// The host steering this frame or the last wins; paused, there's nothing to apply.
			if (_hostThrustFrame >= Time.frameCount - 1 || CrewSim.Paused || dt <= 0f)
			{
				return;
			}
			_relayingPilot = true;
			try
			{
				s.Maneuver(_pX, _pY, _pR, _pNoise, dt, (Ship.EngineMode)_pMode);
			}
			finally
			{
				_relayingPilot = false;
			}
		}

		private static void HostStopPilot()
		{
			_pilotActive = false;
			Ship s = (_pilotReg != null && CrewSim.system != null) ? CrewSim.system.GetShipByRegID(_pilotReg) : null;
			if (s != null && !s.bDestroyed)
			{
				_relayingPilot = true;
				try
				{
					s.Maneuver(0f, 0f, 0f, 0, 1E-10f);
				}
				finally
				{
					_relayingPilot = false;
				}
			}
		}

		private static void HostOnPilot(string arg, float now)
		{
			string[] f = arg.Split('|');
			if (f.Length != 6)
			{
				return;
			}
			try
			{
				string reg = f[0];
				float x = Flt(f[1]);
				float y = Flt(f[2]);
				float r = Flt(f[3]);
				int noise = int.Parse(f[4], Inv);
				int mode = int.Parse(f[5], Inv);
				Ship s = CrewSim.system.GetShipByRegID(reg);
				if (!IsOurShip(s))
				{
					return;
				}
				if (IsZero(x, y, r))
				{
					if (_pilotActive && reg == _pilotReg)
					{
						HostStopPilot();
					}
					return;
				}
				if (!_pilotActive)
				{
					Plugin.Log.LogInfo("Client is steering " + reg);
				}
				_pilotReg = reg;
				_pX = x;
				_pY = y;
				_pR = r;
				_pNoise = noise;
				_pMode = mode;
				_pilotActive = true;
				_pilotLast = now;
			}
			catch (FormatException)
			{
			}
		}

		// ---- Client: helm and smoothing -------------------------------------------------------------------------

		private static void ClientSendPilot(Ship ship, float x, float y, float r, int noise, Ship.EngineMode mode)
		{
			float now = Time.unscaledTime;
			bool zero = IsZero(x, y, r);
			string key = zero ? "0" : (x.ToString("R", Inv) + "|" + y.ToString("R", Inv) + "|" + r.ToString("R", Inv) + "|" + noise.ToString(Inv) + "|" + ((int)mode).ToString(Inv));
			if (!zero)
			{
				_steeringUntil = now + PilotResend + 0.3f;
			}
			if (key == _lastPilotKey && (zero || now - _lastPilotSent < PilotResend))
			{
				return;
			}
			if (zero && _lastPilotKey == null)
			{
				// Never steered: nothing to stop.
				return;
			}
			_lastPilotKey = key;
			_lastPilotSent = now;
			Plugin.Net.Send("PILOT " + ship.strRegID + "|" + (zero ? "0|0|0|0|1" : key));
		}

		private static void ClientUpdate(float now)
		{
			if (_corrX == 0.0 && _corrY == 0.0 && _corrVX == 0.0 && _corrVY == 0.0)
			{
				return;
			}
			Ship own = CrewSim.shipCurrentLoaded;
			if (own == null || own.objSS == null)
			{
				_corrX = _corrY = _corrVX = _corrVY = 0.0;
				return;
			}
			double k = Math.Min(1.0, Time.unscaledDeltaTime / SmoothSeconds);
			double dx = _corrX * k;
			double dy = _corrY * k;
			own.objSS.vPosx += dx;
			own.objSS.vPosy += dy;
			_corrX -= dx;
			_corrY -= dy;
			double dvx = _corrVX * k;
			double dvy = _corrVY * k;
			own.objSS.vVelX += dvx;
			own.objSS.vVelY += dvy;
			_corrVX -= dvx;
			_corrVY -= dvy;
			if (Math.Abs(_corrX) < 1E-15 && Math.Abs(_corrY) < 1E-15)
			{
				_corrX = _corrY = 0.0;
			}
			if (Math.Abs(_corrVX) < 1E-18 && Math.Abs(_corrVY) < 1E-18)
			{
				_corrVX = _corrVY = 0.0;
			}
		}

		private static double Distance(ShipSitu a, ShipSitu b)
		{
			double dx = a.vPosx - b.vPosx;
			double dy = a.vPosy - b.vPosy;
			return Math.Sqrt(dx * dx + dy * dy);
		}

		private static void Append(StringBuilder sb, Ship s)
		{
			ShipSitu o = s.objSS;
			if (sb.Length > 0)
			{
				sb.Append('^');
			}
			sb.Append(s.strRegID);
			D(sb, o.vPosx);
			D(sb, o.vPosy);
			D(sb, o.vVelX);
			D(sb, o.vVelY);
			F(sb, o.fRot);
			F(sb, o.fW);
			F(sb, o.fA);
			F(sb, o.vAccIn.x);
			F(sb, o.vAccIn.y);
			F(sb, o.vAccRCS.x);
			F(sb, o.vAccRCS.y);
			F(sb, o.vAccEx.x);
			F(sb, o.vAccEx.y);
			F(sb, o.vAccLift.x);
			F(sb, o.vAccLift.y);
			F(sb, o.vAccDrag.x);
			F(sb, o.vAccDrag.y);
			sb.Append('~').Append((o.bBOLocked ? 1 : 0) | (o.bOrbitLocked ? 2 : 0));
			sb.Append('~').Append(o.strBOPORShip ?? "");
			D(sb, o.vBOOffsetx);
			D(sb, o.vBOOffsety);
		}

		private static void D(StringBuilder sb, double v)
		{
			sb.Append('~').Append(v.ToString("R", Inv));
		}

		private static void F(StringBuilder sb, float v)
		{
			sb.Append('~').Append(v.ToString("R", Inv));
		}

		private static void HostSendShip(string reg)
		{
			Ship s = CrewSim.system.GetShipByRegID(reg);
			if (s == null || s.bDestroyed)
			{
				net().Send("SHIPGONE " + reg);
				return;
			}
			try
			{
				DockSync.DockPayload p = new DockSync.DockPayload();
				p.Newcomer = reg;
				DockSync.Snapshot(s, p);
				byte[] data = DockSync.Gzip(Encoding.UTF8.GetBytes(JsonMapper.ToJson(p)));
				net().SendBinary("SHIPNEW", data, 0, data.Length);
				Plugin.Log.LogInfo("Sent ship " + reg + ": " + p.COs.Count + " items, " + data.Length + " bytes");
			}
			catch (Exception e)
			{
				Plugin.Log.LogError("Could not send ship " + reg + ": " + e);
			}
		}

		private static NetSession net()
		{
			return Plugin.Net;
		}

		// ---- Messages ------------------------------------------------------------------------------------------

		// Main thread. Returns false if the message isn't ours.
		public static bool OnMessage(string cmd, string arg, float now)
		{
			switch (cmd)
			{
				case "SHIPS":
					if (Mirroring)
					{
						ApplyShips(arg, now);
					}
					return true;
				case "SHIPGONE":
					if (Mirroring)
					{
						ApplyGone(arg);
					}
					return true;
				case "PILOT":
					if (Plugin.Net.CurrentMode == NetSession.Mode.Host && WorldSync.InGame && CrewSim.system != null)
					{
						HostOnPilot(arg, now);
					}
					return true;
				case "NAVCLEAR":
					if (WorldSync.InGame && CrewSim.system != null)
					{
						ApplyNavClear(arg);
					}
					return true;
				case "NEEDSHIP":
					if (Plugin.Net.CurrentMode == NetSession.Mode.Host && WorldSync.InGame)
					{
						HostSendShip(arg);
					}
					return true;
				default:
					return false;
			}
		}

		// ---- Nav station "Clear" (both sides) -------------------------------------------------------------------

		private static readonly System.Reflection.FieldInfo NavCOSelf = AccessTools.Field(typeof(Ostranauts.ShipGUIs.NavStation.NavModBase), "COSelf");

		private static void ProxClearPostfix(object __instance)
		{
			SendNavClear("P", NavShip(__instance));
		}

		private static void TrackClearPostfix(object __instance)
		{
			SendNavClear("T", NavShip(__instance));
		}

		private static Ship NavShip(object nav)
		{
			CondOwner co = (NavCOSelf != null) ? NavCOSelf.GetValue(nav) as CondOwner : null;
			return (co != null) ? co.ship : null;
		}

		private static void SendNavClear(string kind, Ship ship)
		{
			if (ship == null || ship.strRegID == null || !Plugin.Net.Connected || !WorldSync.InGame)
			{
				return;
			}
			List<string> ids = (kind == "P") ? ship.aProxIgnores : ship.aTrackIgnores;
			Plugin.Net.Send("NAVCLEAR " + kind + "|" + ship.strRegID + "|" + string.Join("^", ids.ToArray()));
		}

		// The same as the button (NavModWarnings.ProxClear / TrackClear), with the sender's ships added: each side
		// counts its own current ones (the track warning is only worked out on the host).
		private static void ApplyNavClear(string arg)
		{
			string[] f = arg.Split('|');
			if (f.Length != 3)
			{
				return;
			}
			Ship ship = CrewSim.system.GetShipByRegID(f[1]);
			if (ship == null || ship.bDestroyed || ship.aProxIgnores == null)
			{
				return;
			}
			bool prox = f[0] == "P";
			List<string> ignores = prox ? ship.aProxIgnores : ship.aTrackIgnores;
			List<string> current = prox ? ship.aProxCurrent : ship.aTrackCurrent;
			ignores.Clear();
			ignores.AddRange(current);
			foreach (string id in f[2].Split('^'))
			{
				if (id.Length > 0 && !ignores.Contains(id))
				{
					ignores.Add(id);
				}
			}
			if (prox)
			{
				ship.proximityWarning = false;
			}
			else
			{
				ship.trackWarning = false;
			}
			Plugin.Log.LogInfo("Nav " + (prox ? "proximity" : "tracking") + " warning cleared by the other player on " + f[1]);
		}

		// Main thread; the SHIPNEW binary frame.
		public static void OnBinary(byte[] data)
		{
			if (!Mirroring)
			{
				return;
			}
			try
			{
				DockSync.DockPayload p = JsonMapper.ToObject<DockSync.DockPayload>(Encoding.UTF8.GetString(DockSync.Gunzip(data)));
				if (p.Newcomer != null && p.Ship != null)
				{
					DockSync.ReplaceShip(p);
				}
			}
			catch (Exception e)
			{
				Plugin.Log.LogError("Could not add ship from host: " + e);
			}
		}

		// ---- Client --------------------------------------------------------------------------------------------

		private static void ApplyShips(string arg, float now)
		{
			// "<host epoch> <entries>": how far to carry the host's numbers on to reach our epoch.
			double dt = 0.0;
			int sp = arg.IndexOf(' ');
			double epoch;
			if (sp > 0 && double.TryParse(arg.Substring(0, sp), NumberStyles.Float, Inv, out epoch))
			{
				arg = arg.Substring(sp + 1);
				dt = StarSystem.fEpoch - epoch;
				if (Math.Abs(dt) > MaxCatchUp || double.IsNaN(dt))
				{
					dt = 0.0;
				}
			}
			foreach (string entry in arg.Split('^'))
			{
				string[] f = entry.Split('~');
				if (f.Length != 22)
				{
					continue;
				}
				Ship s = CrewSim.system.GetShipByRegID(f[0]);
				if (s == null || s.bDestroyed)
				{
					float last;
					if (!Asked.TryGetValue(f[0], out last) || now - last > NeedRetry)
					{
						Asked[f[0]] = now;
						Plugin.Net.Send("NEEDSHIP " + f[0]);
						Plugin.Log.LogInfo("Asking host for ship " + f[0]);
					}
					continue;
				}
				if (s.objSS == null || s.GetAllDockedShips().Count > 0)
				{
					continue;
				}
				try
				{
					ShipSitu o = s.objSS;
					bool own = s == CrewSim.shipCurrentLoaded;
					bool steering = own && now < _steeringUntil;
					// Ours, before the host's numbers go in.
					double lx = o.vPosx;
					double ly = o.vPosy;
					double lvx = o.vVelX;
					double lvy = o.vVelY;
					float lRot = o.fRot;
					float lW = o.fW;
					float lA = o.fA;
					// The host's state at the host's epoch.
					o.vPosx = Dbl(f[1]);
					o.vPosy = Dbl(f[2]);
					o.vVelX = Dbl(f[3]);
					o.vVelY = Dbl(f[4]);
					if (!steering)
					{
						// While we steer, our own thrust and turning stand; the host's still lag one ping behind.
						o.fRot = Flt(f[5]);
						o.fW = Flt(f[6]);
						o.fA = Flt(f[7]);
						o.vAccRCS.Set(Flt(f[10]), Flt(f[11]));
					}
					o.vAccIn.Set(Flt(f[8]), Flt(f[9]));
					o.vAccEx.Set(Flt(f[12]), Flt(f[13]));
					o.vAccLift.Set(Flt(f[14]), Flt(f[15]));
					o.vAccDrag.Set(Flt(f[16]), Flt(f[17]));
					int flags = int.Parse(f[18], Inv);
					o.bBOLocked = (flags & 1) != 0;
					o.bOrbitLocked = (flags & 2) != 0;
					o.strBOPORShip = (f[19].Length > 0) ? f[19] : null;
					o.vBOOffsetx = Dbl(f[20]);
					o.vBOOffsety = Dbl(f[21]);
					// A course plotted here would move the ship along its own path instead of the host's numbers.
					o.NavData = null;
					// ...carried on to our epoch, the way the game moves ships every frame.
					if (dt != 0.0)
					{
						o.TimeAdvance(dt);
					}
					if (steering)
					{
						// Our turning is already at our epoch; don't advance it a second time.
						o.fRot = lRot;
						o.fW = lW;
						o.fA = lA;
					}
					if (!own)
					{
						continue;
					}
					// The ship we're on: pulled towards the host's state over SmoothSeconds instead of jumping there.
					double ex = o.vPosx - lx;
					double ey = o.vPosy - ly;
					if (Math.Sqrt(ex * ex + ey * ey) > SnapAU)
					{
						_corrX = _corrY = _corrVX = _corrVY = 0.0;
						continue;
					}
					o.vPosx = lx;
					o.vPosy = ly;
					_corrX = ex;
					_corrY = ey;
					if (steering)
					{
						_corrVX = o.vVelX - lvx;
						_corrVY = o.vVelY - lvy;
						o.vVelX = lvx;
						o.vVelY = lvy;
					}
					else
					{
						_corrVX = _corrVY = 0.0;
					}
				}
				catch (FormatException)
				{
				}
			}
		}

		private static double Dbl(string s)
		{
			return double.Parse(s, NumberStyles.Float, Inv);
		}

		private static float Flt(string s)
		{
			return float.Parse(s, NumberStyles.Float, Inv);
		}

		private static void ApplyGone(string arg)
		{
			Ship cur = CrewSim.shipCurrentLoaded;
			foreach (string reg in arg.Split('^'))
			{
				Ship s = CrewSim.system.GetShipByRegID(reg);
				if (s == null || s.bDestroyed || s == cur || (cur != null && System.Linq.Enumerable.Contains(cur.GetAllDockedShips(), s)))
				{
					continue;
				}
				Plugin.Log.LogInfo("Ship " + reg + " is gone on the host, removing");
				s.Destroy(isDespawning: false);
			}
		}
	}
}
