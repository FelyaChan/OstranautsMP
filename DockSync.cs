using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using HarmonyLib;
using LitJson;
using UnityEngine;

namespace OstranautsMP
{
	// Docking and undocking without reloading the save.
	//
	// On the host, docking (CrewSim.DockShip) builds the other ship from data already in memory and moves it to the
	// airlock; undocking (CrewSim.UndockShip) just unlinks the two. The client does the same calls when the host says
	// so. The client's copy of the ship being docked to may differ (contents generated on first visit, AI ships that
	// moved or don't exist there), so the host sends that one ship as it is now: its JsonShip plus every item's
	// JsonCondOwnerSave, the way CrewSim.SaveToShallow stores a ship it unloads. The client swaps its copy for that and
	// docks. If anything fails, the host falls back to resending the whole save (SaveSync.HostLateJoin).
	//
	// Only dock/undock calls involving the ship we're on or something docked to it are relayed. Other ways the docked
	// set can change (AI ships docking through Ship.Dock, mooring) are still caught by SaveSync.HostWatchLocation.
	//
	// Protocol:
	//   BIN DOCK <gzip JSON DockPayload>                        host -> client
	//   UNDOCK <us>|<them>|<pushback 0/1>|<keepClearance 0/1>|<base64 JSON ShipSitu of us after undocking>|<host epoch>
	//   DOCK_OK / DOCK_FAIL <reason>                            client -> host, after either
	public static class DockSync
	{
		public class DockPayload
		{
			public string Us;

			public string Them;

			public string PortUs;

			public string PortThem;

			// The ship that wasn't in our docked set (its reg ID and state), or null if both were.
			public string Newcomer;

			public JsonShip Ship;

			public List<JsonCondOwnerSave> COs;

			public JsonAIShipSave AI;

			// The host's own ship, when the client doesn't have it loaded (coming back after being apart).
			public DockPayload UsCopy;
		}

		// Client: inside a relayed call; the game's own dock/undock calls are blocked otherwise.
		private static bool _applying;

		public static void Patch(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(CrewSim), "DockShip"), prefix: new HarmonyMethod(typeof(DockSync), nameof(DockShipPrefix)), postfix: new HarmonyMethod(typeof(DockSync), nameof(DockShipPostfix)));
			harmony.Patch(AccessTools.Method(typeof(CrewSim), "UndockShip"), prefix: new HarmonyMethod(typeof(DockSync), nameof(UndockShipPrefix)), postfix: new HarmonyMethod(typeof(DockSync), nameof(UndockShipPostfix)));
		}

		private static bool HostTracking => Plugin.Net.CurrentMode == NetSession.Mode.Host && Plugin.Net.Connected && WorldSync.InGame;

		private static bool ClientBlocking => !_applying && WorldSync.ClientFollowsHost && WorldSync.InGame;

		// The ship we're on and everything docked to it.
		private static HashSet<string> LocationSet()
		{
			HashSet<string> set = new HashSet<string>();
			Ship cur = CrewSim.shipCurrentLoaded;
			if (cur == null)
			{
				return set;
			}
			set.Add(cur.strRegID);
			foreach (Ship s in cur.GetAllDockedShips())
			{
				if (s != null)
				{
					set.Add(s.strRegID);
				}
			}
			return set;
		}

		// ---- Host ----------------------------------------------------------------------------------------------

		private static bool DockShipPrefix(Ship shipUs, string strRegID, ref Ship __result, out HashSet<string> __state)
		{
			__state = null;
			if (HostTracking)
			{
				__state = LocationSet();
				return true;
			}
			if (ClientBlocking && shipUs != null)
			{
				HashSet<string> set = LocationSet();
				if (set.Contains(shipUs.strRegID) || (strRegID != null && set.Contains(strRegID)))
				{
					Plugin.Log.LogInfo("Dock " + shipUs.strRegID + " -> " + strRegID + " blocked: the host docks");
					__result = null;
					return false;
				}
			}
			return true;
		}

		private static void DockShipPostfix(Ship shipUs, Ship __result, HashSet<string> __state)
		{
			if (__state == null || __result == null || shipUs == null || !HostTracking)
			{
				return;
			}
			bool usIn = __state.Contains(shipUs.strRegID);
			bool themIn = __state.Contains(__result.strRegID);
			if (!usIn && !themIn)
			{
				return;
			}
			// Apart: docking somewhere else stays here; docking to the client's place brings the host back.
			if (!Apart.RelayDock(__result.strRegID))
			{
				Plugin.Log.LogInfo("Docked " + shipUs.strRegID + " to " + __result.strRegID + ", not relayed (apart)");
				return;
			}
			try
			{
				DockPayload p = new DockPayload();
				p.Us = shipUs.strRegID;
				p.Them = __result.strRegID;
				p.PortUs = shipUs.GetPortIdForDockedShip(__result.strRegID);
				p.PortThem = __result.GetPortIdForDockedShip(shipUs.strRegID);
				Ship newcomer = (!usIn) ? shipUs : ((!themIn) ? __result : null);
				// The client runs that one itself (we're back at its place): it keeps its own copy.
				if (newcomer != null && Apart.ClientHasLoaded(newcomer.strRegID) && newcomer != shipUs)
				{
					newcomer = null;
				}
				if (newcomer != null)
				{
					p.Newcomer = newcomer.strRegID;
					Snapshot(newcomer, p);
				}
				if (newcomer != shipUs && !Apart.ClientHasLoaded(shipUs.strRegID) && Apart.HostActive)
				{
					p.UsCopy = new DockPayload();
					p.UsCopy.Newcomer = shipUs.strRegID;
					Snapshot(shipUs, p.UsCopy);
				}
				byte[] data = Gzip(Encoding.UTF8.GetBytes(JsonMapper.ToJson(p)));
				// Sent here on the main thread, so it reaches the client before any later item change on that ship.
				Plugin.Net.SendBinary("DOCK", data, 0, data.Length);
				SaveSync.SuspendLocation();
				Plugin.Log.LogInfo("Docked " + p.Us + " (" + p.PortUs + ") to " + p.Them + " (" + p.PortThem + "), sent " + ((newcomer != null) ? (newcomer.strRegID + ", " + p.COs.Count + " items, ") : "") + ((p.UsCopy != null) ? ("our ship, " + p.UsCopy.COs.Count + " items, ") : "") + data.Length + " bytes");
			}
			catch (Exception e)
			{
				Plugin.Log.LogError("Dock relay failed, resending the save: " + e);
				SaveSync.HostLateJoin();
			}
		}

		// The ship as a save stores it (LoadManager.SaveGameData): its JsonShip, every item it has loaded, and for a
		// ship that isn't fully loaded the item saves kept for it in DataHandler.dictCOSaves.
		internal static void Snapshot(Ship ship, DockPayload p)
		{
			string reg = ship.strRegID;
			List<CondOwner> cos = ship.GetCOs(null, bSubObjects: true, bAllowDocked: false, bAllowLocked: true);
			p.Ship = ship.GetJSON(reg, bSaveGame: true, cos);
			p.COs = new List<JsonCondOwnerSave>();
			HashSet<string> added = new HashSet<string>();
			List<CondOwner> lots = new List<CondOwner>();
			foreach (CondOwner co in cos)
			{
				if (co == null)
				{
					continue;
				}
				JsonCondOwnerSave s = (ship.LoadState < Ship.Loaded.Edit && co.jCOS != null) ? co.jCOS : co.GetJSONSave();
				if (s != null && added.Add(s.strID))
				{
					p.COs.Add(s);
				}
				lots.AddRange(co.GetLotCOs(bSubItems: true));
			}
			foreach (CondOwner co in lots)
			{
				JsonCondOwnerSave s = co.GetJSONSave();
				if (s != null && added.Add(s.strID))
				{
					p.COs.Add(s);
				}
			}
			List<JsonCondOwnerSave> stored;
			if (SavesByShip().TryGetValue(reg, out stored))
			{
				foreach (JsonCondOwnerSave s in stored)
				{
					if (added.Add(s.strID))
					{
						p.COs.Add(s);
					}
				}
			}
			p.AI = AIShipManager.GetJSONAIShipSave(reg);
		}

		private static Dictionary<string, List<JsonCondOwnerSave>> _savesByShip;

		private static int _savesByShipFrame = -1;

		// DataHandler.dictCOSaves grouped by the ship each save was last on; several ships can be asked for at once.
		private static Dictionary<string, List<JsonCondOwnerSave>> SavesByShip()
		{
			if (_savesByShip == null || _savesByShipFrame != Time.frameCount)
			{
				_savesByShipFrame = Time.frameCount;
				_savesByShip = new Dictionary<string, List<JsonCondOwnerSave>>();
				foreach (JsonCondOwnerSave s in DataHandler.dictCOSaves.Values)
				{
					if (s == null || s.strID == null || string.IsNullOrEmpty(s.strRegIDLast))
					{
						continue;
					}
					List<JsonCondOwnerSave> list;
					if (!_savesByShip.TryGetValue(s.strRegIDLast, out list))
					{
						list = new List<JsonCondOwnerSave>();
						_savesByShip[s.strRegIDLast] = list;
					}
					list.Add(s);
				}
			}
			return _savesByShip;
		}

		private static bool UndockShipPrefix(Ship shipUs, Ship objShipThem, out HashSet<string> __state)
		{
			__state = null;
			if (HostTracking)
			{
				// GUIDockSys also calls this after a dock that didn't happen; nothing to relay then.
				if (shipUs != null && objShipThem != null && System.Linq.Enumerable.Contains(shipUs.GetAllDockedShips(), objShipThem))
				{
					__state = LocationSet();
				}
				return true;
			}
			if (ClientBlocking && shipUs != null && objShipThem != null)
			{
				HashSet<string> set = LocationSet();
				if (set.Contains(shipUs.strRegID) || set.Contains(objShipThem.strRegID))
				{
					Plugin.Log.LogInfo("Undock " + shipUs.strRegID + " from " + objShipThem.strRegID + " blocked: the host undocks");
					return false;
				}
			}
			return true;
		}

		private static void UndockShipPostfix(Ship shipUs, Ship objShipThem, bool bPushback, bool keepClearance, HashSet<string> __state)
		{
			if (__state == null || shipUs == null || objShipThem == null || !HostTracking)
			{
				return;
			}
			if (!__state.Contains(shipUs.strRegID) && !__state.Contains(objShipThem.strRegID))
			{
				return;
			}
			// Apart: the client doesn't have this pair docked as we do; nothing to undo there.
			if (Apart.HostActive && !(Apart.ClientHasLoaded(shipUs.strRegID) && Apart.ClientHasLoaded(objShipThem.strRegID)))
			{
				Plugin.Log.LogInfo("Undocked " + shipUs.strRegID + " from " + objShipThem.strRegID + ", not relayed (apart)");
				return;
			}
			string situ = (shipUs.objSS != null) ? Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonMapper.ToJson(shipUs.objSS.GetJSON()))) : "";
			Plugin.Net.Send("UNDOCK " + shipUs.strRegID + "|" + objShipThem.strRegID + "|" + (bPushback ? "1" : "0") + "|" + (keepClearance ? "1" : "0") + "|" + situ + "|" + StarSystem.fEpoch.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
			SaveSync.SuspendLocation();
			Plugin.Log.LogInfo("Undocked " + shipUs.strRegID + " from " + objShipThem.strRegID + ", sent");
		}

		// ---- Messages ------------------------------------------------------------------------------------------

		// Main thread. Returns false if the message isn't ours.
		public static bool OnMessage(string cmd, string arg)
		{
			switch (cmd)
			{
				case "UNDOCK":
					if (Plugin.Net.CurrentMode == NetSession.Mode.Client)
					{
						Reply(ApplyUndock(arg));
					}
					return true;
				case "DOCK_OK":
					if (Plugin.Net.CurrentMode == NetSession.Mode.Host)
					{
						Plugin.Log.LogInfo("Client docked/undocked too");
						SaveSync.ResetLocation();
					}
					return true;
				case "DOCK_FAIL":
					if (Plugin.Net.CurrentMode == NetSession.Mode.Host)
					{
						Plugin.Log.LogWarning("Client could not dock/undock (" + arg + "), resending the save");
						SaveSync.HostLateJoin();
					}
					return true;
				default:
					return false;
			}
		}

		// Main thread; the DOCK binary frame.
		public static void OnBinary(byte[] data)
		{
			if (Plugin.Net.CurrentMode != NetSession.Mode.Client)
			{
				return;
			}
			string error;
			try
			{
				DockPayload p = JsonMapper.ToObject<DockPayload>(Encoding.UTF8.GetString(Gunzip(data)));
				error = ApplyDock(p);
			}
			catch (Exception e)
			{
				error = e.GetType().Name + ": " + e.Message;
				Plugin.Log.LogError("Dock failed: " + e);
			}
			Reply(error);
		}

		private static void Reply(string error)
		{
			if (error == null)
			{
				Plugin.Net.Send("DOCK_OK");
			}
			else
			{
				Plugin.Log.LogWarning("Dock/undock failed: " + error);
				Plugin.Net.Send("DOCK_FAIL " + error);
			}
		}

		// ---- Client --------------------------------------------------------------------------------------------

		// Null on success, otherwise why not.
		private static string ApplyDock(DockPayload p)
		{
			if (!WorldSync.InGame || CrewSim.system == null)
			{
				return "not in game";
			}
			if (p.Ship != null && p.Newcomer != null)
			{
				string error = ReplaceShip(p);
				if (error != null)
				{
					return error;
				}
			}
			// Coming back after being apart: the host's ship as it is now, without the people we still have here (the
			// host's character waited in our place); they move aboard after docking.
			List<CondOwner> waiting = new List<CondOwner>();
			if (p.UsCopy != null && p.UsCopy.Ship != null)
			{
				if (p.UsCopy.Ship.aCrew != null)
				{
					foreach (JsonItem crew in p.UsCopy.Ship.aCrew)
					{
						CondOwner co;
						if (crew != null && crew.strID != null && DataHandler.mapCOs.TryGetValue(crew.strID, out co) && co != null && !co.bDestroyed)
						{
							waiting.Add(co);
						}
					}
				}
				Apart.FilterLive(p.UsCopy);
				string error = ReplaceShip(p.UsCopy);
				if (error != null)
				{
					return error;
				}
				Ship loaded = CrewSim.system.SpawnShip(p.UsCopy.Newcomer, Ship.Loaded.Full);
				if (loaded == null)
				{
					return "could not load " + p.UsCopy.Newcomer;
				}
				loaded.ToggleVis(bShow: true);
			}
			Ship us = CrewSim.system.GetShipByRegID(p.Us);
			if (us == null)
			{
				return "no ship " + p.Us;
			}
			Ship result;
			_applying = true;
			try
			{
				result = CrewSim.DockShip(us, p.Them, p.PortThem, p.PortUs);
			}
			finally
			{
				_applying = false;
			}
			if (result == null)
			{
				return "DockShip returned null";
			}
			foreach (CondOwner co in waiting)
			{
				if (co.ship != us)
				{
					CrewSim.MoveCO(co, us, bPlayerShip: false);
				}
			}
			Plugin.Log.LogInfo("Docked " + p.Us + " to " + p.Them + ((waiting.Count > 0) ? (", " + waiting.Count + " aboard again") : ""));
			return null;
		}

		// Swap our copy of the ship for the host's, the way CrewSim.DestroyAndReload rebuilds an unloaded ship.
		internal static string ReplaceShip(DockPayload p)
		{
			string reg = p.Newcomer;
			HashSet<string> set = LocationSet();
			if (set.Contains(reg))
			{
				// Already part of where we are; leave it to item sync.
				Plugin.Log.LogWarning("Not replacing " + reg + ": we're on it or docked to it");
				return null;
			}
			StarSystem system = CrewSim.system;
			string owner = system.GetShipOwner(reg);
			Ship old = system.GetShipByRegID(reg);
			if (old != null)
			{
				old.Destroy();
			}
			if (!string.IsNullOrEmpty(owner))
			{
				system.RegisterShipOwner(reg, owner);
			}
			if (p.COs != null)
			{
				foreach (JsonCondOwnerSave s in p.COs)
				{
					if (s == null || s.strID == null)
					{
						continue;
					}
					DataHandler.dictCOSaves[s.strID] = s;
					DataHandler.mapCOs.Remove(s.strID);
				}
			}
			GameObject go = new GameObject("goShip");
			GameObject playState = GameObject.Find("PlayState");
			if (playState != null)
			{
				go.transform.SetParent(playState.transform, worldPositionStays: false);
			}
			Ship ship = new Ship(go);
			ship.json = p.Ship;
			ship.InitShip(bTemplateOnly: false, Ship.Loaded.Shallow);
			system.dictShips[ship.strRegID] = ship;
			ship.ToggleVis(bShow: false, affectDocked: false);
			if (p.AI != null)
			{
				AIShipManager.AddAIToShip(ship, p.AI.enumAIType, p.AI.strATCLast, p.AI, gameLoad: true);
			}
			Plugin.Log.LogInfo("Replaced " + reg + " with the host's copy (" + ((p.COs != null) ? p.COs.Count : 0) + " items)");
			return null;
		}

		private static string ApplyUndock(string arg)
		{
			if (!WorldSync.InGame || CrewSim.system == null)
			{
				return "not in game";
			}
			string[] f = arg.Split('|');
			if (f.Length != 5 && f.Length != 6)
			{
				return "bad UNDOCK";
			}
			Ship us = CrewSim.system.GetShipByRegID(f[0]);
			Ship them = CrewSim.system.GetShipByRegID(f[1]);
			if (us == null || them == null)
			{
				return "no ship " + ((us == null) ? f[0] : f[1]);
			}
			// Our character stays on the other side: unloading that (the game's undock) would unload us. Only unlink the
			// two; the host's ship flies off loaded, and our place keeps running here (Apart).
			CondOwner mine = CrewClaim.ClientMine;
			Ship mineShip = (mine != null) ? mine.ship : null;
			bool stay = mineShip != null && mineShip != us && (mineShip == them || System.Linq.Enumerable.Contains(them.GetAllDockedShips(), mineShip)) && !OnUsSide(us, them, mineShip);
			if (stay)
			{
				us.ResetDockedCache();
				us.Undock(them);
				if (f[3] != "1")
				{
					us.Comms.Clearance = null;
					them.Comms.Clearance = null;
				}
				them.ResetDockedCache();
			}
			else
			{
				_applying = true;
				try
				{
					CrewSim.UndockShip(us, them, f[2] == "1", f[3] == "1");
				}
				finally
				{
					_applying = false;
				}
			}
			// Where the host's ship ended up after the pushback.
			if (f[4].Length > 0 && us.objSS != null)
			{
				JsonShipSitu jss = JsonMapper.ToObject<JsonShipSitu>(Encoding.UTF8.GetString(Convert.FromBase64String(f[4])));
				us.objSS.CopyFrom(new ShipSitu(jss), bKinematicsOnly: true);
				// Carried on from the host's epoch to ours (see ShipSync).
				double hostEpoch;
				if (f.Length == 6 && double.TryParse(f[5], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out hostEpoch))
				{
					double dt = StarSystem.fEpoch - hostEpoch;
					if (dt != 0.0 && Math.Abs(dt) < 120.0)
					{
						us.objSS.TimeAdvance(dt);
					}
				}
				us.UpdateDockedShips(us.objSS);
			}
			Plugin.Log.LogInfo("Undocked " + f[0] + " from " + f[1] + (stay ? " (we stay)" : ""));
			return null;
		}

		// Whether this ship stays docked to us (not through them) once they're undocked.
		private static bool OnUsSide(Ship us, Ship them, Ship ship)
		{
			foreach (KeyValuePair<string, Ship> kv in us.GetDockedShipsAndPortIDs())
			{
				if (kv.Value == ship && kv.Value != them)
				{
					return true;
				}
			}
			return false;
		}

		// ---- Helpers -------------------------------------------------------------------------------------------

		internal static byte[] Gzip(byte[] raw)
		{
			using (MemoryStream ms = new MemoryStream())
			{
				using (GZipStream gz = new GZipStream(ms, CompressionMode.Compress))
				{
					gz.Write(raw, 0, raw.Length);
				}
				return ms.ToArray();
			}
		}

		internal static byte[] Gunzip(byte[] data)
		{
			using (MemoryStream src = new MemoryStream(data))
			using (GZipStream gz = new GZipStream(src, CompressionMode.Decompress))
			using (MemoryStream dst = new MemoryStream())
			{
				gz.CopyTo(dst);
				return dst.ToArray();
			}
		}
	}
}
