using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using LitJson;
using Ostranauts.Core;
using UnityEngine;

namespace OstranautsMP
{
	// Players in different places.
	//
	// The game keeps only the place around the selected character loaded; leaving it (transit, undocking) unloads it
	// into data (CrewSim.SaveToShallow). When the host leaves the client's place, the host's copy of it freezes, so the
	// client takes it over: its people's AI runs on the client, the client's orders are carried out there and nothing
	// about it is sent to the host. The host keeps everything else (story, bills, space) and keeps sending only what
	// the client has loaded.
	//
	// The host's frozen copy is replaced by the client's (DockSync.ReplaceShip) every few minutes, on request ("/sync"
	// in chat), and right before the host's game loads one of those ships again (StarSystem.SpawnShip), so coming back
	// finds it as the client left it. People and items that are live on the receiving side (the host's character and whoever came
	// with it) are never taken from the other side's copy.
	//
	// While apart: the host can't speed time up or skip time without the client agreeing; the client can't transit;
	// NPCs don't move into or out of the client's place on the host.
	//
	// Protocol:
	//   LOC <charShip>|<location ships>|<loaded ships>     client -> host, on change and every few seconds
	//   APART 1|<client's ships>|<ghost person IDs>        host -> client; APART 0 when together again
	//   SNAPREQ <id>                                       host -> client
	//   BIN SNAP <gzip JSON SnapPayload>                   client -> host
	//   WARPASK <kind> / WARPOK <kind> / WARPNO <kind>     kind: x<multiplier> or ffwd
	//   MONEYADD <characterID>|<delta>                     host -> client: money the client's character got there
	public static class Apart
	{
		public class SnapPayload
		{
			public int Id;

			public List<DockSync.DockPayload> Ships;
		}

		private const float LocInterval = 0.5f;

		private const float LocRepeat = 5f;

		private const int FetchTimeoutMs = 20000;

		private static ConfigEntry<float> _snapshotMinutes;

		// ---- Host state ----

		internal static bool HostActive;

		private static string _clientShip;

		private static string _clientSig;

		private static readonly HashSet<string> ClientLocation = new HashSet<string>();

		private static readonly HashSet<string> ClientLoaded = new HashSet<string>();

		// The client's ships that are unloaded here.
		private static readonly HashSet<string> Owned = new HashSet<string>();

		private static float _nextSnapshot;

		private static int _snapId;

		private static float _lastFetch = -100f;

		private static bool _fetching;

		private static string _pendingWarp;

		// The client's character's money as last seen here, to pass on what changes while apart (gifts, salary).
		private static CondOwner _moneyFor;

		private static double _moneyBase;

		private static bool _warpBypass;

		internal static string HostNotice;

		// ---- Client state ----

		private static bool _clientOn;

		private static readonly HashSet<string> ClientOwned = new HashSet<string>();

		private static readonly HashSet<string> Ghosts = new HashSet<string>();

		private static readonly Dictionary<string, List<Renderer>> Hidden = new Dictionary<string, List<Renderer>>();

		private static string _sentLoc;

		private static float _nextLoc;

		private static float _locSentAt;

		private static string _warpAsk;

		private static Rect _askRect = new Rect(0f, 0f, 360f, 10f);

		private const int AskWindowId = 0x4D5041;

		internal static bool ClientActive => _clientOn && WorldSync.ClientFollowsHost && WorldSync.InGame;

		private static bool IsHost => Plugin.Net.CurrentMode == NetSession.Mode.Host && Plugin.Net.Connected && WorldSync.InGame;

		public static void Init(ConfigFile config)
		{
			_snapshotMinutes = config.Bind("General", "ApartSyncMinutes", 3f, "While apart, how often (minutes) the host takes a copy of the client's place. 0 = only on return and on request.");
		}

		public static void Patch(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(StarSystem), "SpawnShip", new Type[] { typeof(string), typeof(Ship.Loaded) }), prefix: new HarmonyMethod(typeof(Apart), nameof(SpawnShipPrefix)));
			harmony.Patch(AccessTools.Method(typeof(CrewSim), "MoveCO"), prefix: new HarmonyMethod(typeof(Apart), nameof(MoveCOPrefix)));
			harmony.Patch(AccessTools.Method(typeof(CrewSim), "TimeScaleMult"), prefix: new HarmonyMethod(typeof(Apart), nameof(TimeScaleMultPrefix)));
			harmony.Patch(AccessTools.Method(typeof(CrewSim), "ToggleSFF"), prefix: new HarmonyMethod(typeof(Apart), nameof(ToggleSFFPrefix)));
			harmony.Patch(AccessTools.Method(typeof(CrewSim), "QueueForTransit", new Type[] { typeof(JsonTransitConnection), typeof(List<CondOwner>), typeof(Action<JsonTransitConnection, List<CondOwner>>) }), prefix: new HarmonyMethod(typeof(Apart), nameof(QueueForTransitPrefix)));
			HarmonyMethod noSave = new HarmonyMethod(typeof(Apart), nameof(NoClientSavePrefix));
			harmony.Patch(AccessTools.Method(typeof(LoadManager), "AutoSave"), prefix: noSave);
			harmony.Patch(AccessTools.Method(typeof(LoadManager), "SaveGame"), prefix: noSave);
		}

		// ---- Shared helpers ----

		// Live here and on a loaded ship (directly or carried). People on unloaded ships are live objects too (the game
		// keeps them for NPC travel), but those are exactly what the other side's copy replaces.
		private static bool IsLive(string id)
		{
			CondOwner co;
			if (id == null || !DataHandler.mapCOs.TryGetValue(id, out co) || co == null || co.bDestroyed)
			{
				return false;
			}
			Ship s = RootShip(co);
			return s != null && !s.bDestroyed && s.LoadState >= Ship.Loaded.Edit;
		}

		// Drop from another machine's copy of a ship everyone and everything that is live here on a loaded ship.
		internal static int FilterLive(DockSync.DockPayload p)
		{
			HashSet<string> live = new HashSet<string>();
			if (p.COs != null)
			{
				List<JsonCondOwnerSave> kept = new List<JsonCondOwnerSave>();
				foreach (JsonCondOwnerSave s in p.COs)
				{
					if (s == null || s.strID == null)
					{
						continue;
					}
					if (IsLive(s.strID))
					{
						live.Add(s.strID);
						continue;
					}
					kept.Add(s);
				}
				p.COs = kept;
			}
			if (p.Ship != null && live.Count > 0)
			{
				p.Ship.aCrew = Strip(p.Ship.aCrew, live);
				p.Ship.aItems = Strip(p.Ship.aItems, live);
			}
			return live.Count;
		}

		private static JsonItem[] Strip(JsonItem[] items, HashSet<string> drop)
		{
			if (items == null)
			{
				return null;
			}
			List<JsonItem> kept = new List<JsonItem>();
			foreach (JsonItem it in items)
			{
				if (it != null && it.strID != null && drop.Contains(it.strID))
				{
					continue;
				}
				kept.Add(it);
			}
			return kept.ToArray();
		}

		private static string Ids(IEnumerable<string> ids)
		{
			List<string> list = new List<string>(ids);
			list.Sort(StringComparer.Ordinal);
			return string.Join(",", list.ToArray());
		}

		private static void ReadIds(string s, HashSet<string> into)
		{
			into.Clear();
			foreach (string id in s.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
			{
				into.Add(id);
			}
		}

		// The ship an item or person is on, through containers and slots.
		private static Ship RootShip(CondOwner co)
		{
			CondOwner top = co;
			for (int i = 0; i < 64 && top != null; i++)
			{
				CondOwner next = (top.coStackHead != null) ? top.coStackHead : top.objCOParent;
				if (next == null)
				{
					break;
				}
				top = next;
			}
			return (top != null && top.ship != null) ? top.ship : co.ship;
		}

		// ---- Host: what the client gets ----

		// Host: whether to send the client anything about this ship's contents.
		internal static bool HostSendsShip(string reg)
		{
			return !HostActive || (reg != null && ClientLoaded.Contains(reg));
		}

		internal static bool HostSends(CondOwner co)
		{
			if (!HostActive || co == null)
			{
				return true;
			}
			Ship s = RootShip(co);
			return s != null && ClientLoaded.Contains(s.strRegID);
		}

		// Host: the client is where the host is (the client's place is the host's place).
		internal static bool ClientIsAt(string sig)
		{
			return _clientSig != null && _clientSig == sig;
		}

		// Host: the client has this ship loaded.
		internal static bool ClientHasLoaded(string reg)
		{
			return reg != null && ClientLoaded.Contains(reg);
		}

		// Host, while apart: a dock is relayed only when it brings the host back to the client's place.
		internal static bool RelayDock(string them)
		{
			return !HostActive || (them != null && ClientLocation.Contains(them));
		}

		// ---- Every frame ----

		public static void Update(float now)
		{
			NetSession net = Plugin.Net;
			if (!net.Connected || !WorldSync.InGame)
			{
				if (!net.Connected)
				{
					ResetHost();
					_clientOn = false;
					_sentLoc = null;
					_warpAsk = null;
				}
				return;
			}
			if (net.CurrentMode == NetSession.Mode.Host)
			{
				HostUpdate(now);
			}
			else if (WorldSync.ClientFollowsHost)
			{
				ClientUpdate(now);
			}
		}

		private static void ResetHost()
		{
			HostActive = false;
			_clientShip = null;
			_clientSig = null;
			ClientLocation.Clear();
			ClientLoaded.Clear();
			Owned.Clear();
			_pendingWarp = null;
			HostNotice = null;
		}

		// Client: a save from the host was just loaded; whatever we ran on our own is in it or gone.
		internal static void OnClientLoaded()
		{
			_clientOn = false;
			ClientOwned.Clear();
			Ghosts.Clear();
			Hidden.Clear();
			_sentLoc = null;
			_warpAsk = null;
		}

		// Host: the client has just loaded a save; wait for its LOC.
		internal static void OnClientReady()
		{
			ResetHost();
		}

		private static void HostUpdate(float now)
		{
			if (_clientShip == null || CrewSim.system == null)
			{
				return;
			}
			HashSet<string> loaded = new HashSet<string>();
			List<Ship> ships = CrewSim.GetAllLoadedShips();
			if (ships != null)
			{
				foreach (Ship s in ships)
				{
					if (s != null && !s.bDestroyed && s.strRegID != null && s.LoadState >= Ship.Loaded.Edit)
					{
						loaded.Add(s.strRegID);
					}
				}
			}
			bool apart = !loaded.Contains(_clientShip) && CrewSim.system.GetShipByRegID(_clientShip) != null;
			HashSet<string> owned = new HashSet<string>();
			if (apart)
			{
				foreach (string reg in ClientLocation)
				{
					if (!loaded.Contains(reg))
					{
						owned.Add(reg);
					}
				}
				owned.Add(_clientShip);
			}
			if (apart != HostActive || !owned.SetEquals(Owned))
			{
				bool started = apart && !HostActive;
				HostActive = apart;
				Owned.Clear();
				Owned.UnionWith(owned);
				if (apart)
				{
					List<string> ghosts = new List<string>();
					if (ships != null)
					{
						foreach (Ship s in ships)
						{
							if (s == null || s.bDestroyed)
							{
								continue;
							}
							foreach (CondOwner co in s.GetPeople(bAllowDocked: false))
							{
								if (co != null && !co.bDestroyed && co.strID != null)
								{
									ghosts.Add(co.strID);
								}
							}
						}
					}
					Plugin.Net.Send("APART 1|" + Ids(Owned) + "|" + Ids(ghosts));
					if (started)
					{
						Plugin.Log.LogInfo("Apart: the client stays on " + Ids(Owned));
						Chat.AddSystem((Chat.PeerName ?? "The other player") + " stays behind; their place keeps running on their side");
						_nextSnapshot = now + SnapshotInterval();
						if (Time.timeScale > 1f)
						{
							_warpBypass = true;
							try
							{
								CrewSim.ResetTimeScale();
							}
							finally
							{
								_warpBypass = false;
							}
						}
					}
				}
				else
				{
					Plugin.Net.Send("APART 0");
					Plugin.Log.LogInfo("Apart: together again");
					Chat.AddSystem("Together again");
					_pendingWarp = null;
				}
			}
			HostWatchMoney();
			if (HostActive)
			{
				float interval = SnapshotInterval();
				if (interval > 0f && now >= _nextSnapshot)
				{
					_nextSnapshot = now + interval;
					RequestSnapshot();
				}
				HostNotice = "apart" + ((_pendingWarp != null) ? (", waiting for " + (Chat.PeerName ?? "the other player") + " to agree on time") : "");
			}
			else
			{
				HostNotice = null;
			}
		}

		// Host, while apart: the client's character here is a copy the client's own will replace; money it gets here
		// (a gift, salary) is passed on so the client's side has it too.
		private static void HostWatchMoney()
		{
			CondOwner cc = HostActive ? CrewClaim.HostClaimed : null;
			if (cc == null)
			{
				_moneyFor = null;
				return;
			}
			double m = cc.GetCondAmount(Ledger.CURRENCY);
			if (cc != _moneyFor)
			{
				_moneyFor = cc;
				_moneyBase = m;
				return;
			}
			if (m != _moneyBase)
			{
				double delta = m - _moneyBase;
				_moneyBase = m;
				Plugin.Net.Send("MONEYADD " + cc.strID + "|" + delta.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
				Plugin.Log.LogInfo("Apart: passed on " + delta.ToString("n", System.Globalization.CultureInfo.InvariantCulture) + " to " + cc.strName);
			}
		}

		private static float SnapshotInterval()
		{
			float m = (_snapshotMinutes != null) ? _snapshotMinutes.Value : 3f;
			return (m > 0f) ? (m * 60f) : 0f;
		}

		// "/sync" in chat: the host takes the client's place now.
		internal static void SyncCommand()
		{
			if (Plugin.Net.CurrentMode != NetSession.Mode.Host)
			{
				Chat.AddSystem("/sync: only the host can do that");
			}
			else if (!HostActive)
			{
				Chat.AddSystem("/sync: you're in the same place, nothing to sync");
			}
			else
			{
				RequestSnapshot();
				Chat.AddSystem("/sync: taking " + (Chat.PeerName ?? "the other player") + "'s place");
			}
		}

		// Host: ask for a copy of the client's place; it's applied when it arrives (OnBinary).
		internal static void RequestSnapshot()
		{
			if (!IsHost)
			{
				return;
			}
			_snapId++;
			Plugin.Net.Send("SNAPREQ " + _snapId);
			Plugin.Log.LogInfo("Apart: asked for the client's place (" + _snapId + ")");
		}

		// Host, before its game loads a ship: if it's one of the client's, fetch the client's copy first.
		private static void SpawnShipPrefix(string strRegID, Ship.Loaded nLoad)
		{
			if (!HostActive || _fetching || nLoad < Ship.Loaded.Edit || strRegID == null || !Owned.Contains(strRegID) || !IsHost)
			{
				return;
			}
			Ship s = CrewSim.system.GetShipByRegID(strRegID);
			if (s == null || s.LoadState >= Ship.Loaded.Edit || Time.unscaledTime - _lastFetch < 10f)
			{
				return;
			}
			_fetching = true;
			try
			{
				_snapId++;
				int want = _snapId;
				Plugin.Net.Send("SNAPREQ " + want);
				Plugin.Log.LogInfo("Apart: loading " + strRegID + ", waiting for the client's copy (" + want + ")");
				System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
				while (sw.ElapsedMilliseconds < FetchTimeoutMs)
				{
					byte[] data = Plugin.Net.WaitBinary("SNAP", (int)(FetchTimeoutMs - sw.ElapsedMilliseconds));
					if (data == null)
					{
						break;
					}
					SnapPayload p = Parse(data);
					if (p != null && p.Id >= want)
					{
						Apply(p);
						_lastFetch = Time.unscaledTime;
						return;
					}
				}
				Plugin.Log.LogError("Apart: no copy of the client's place in time, loading the last one");
				Chat.AddSystem("Could not get the latest state of " + strRegID + "; using the copy from the last sync");
			}
			catch (Exception e)
			{
				Plugin.Log.LogError("Apart: fetching the client's place failed: " + e);
			}
			finally
			{
				_fetching = false;
			}
		}

		// Host: NPCs don't walk into or out of the client's place here; the client runs it.
		private static bool MoveCOPrefix(CondOwner objCO, Ship objShipNew)
		{
			if (!HostActive || objCO == null || objShipNew == null || objCO == CrewSim.coPlayer)
			{
				return true;
			}
			bool into = Owned.Contains(objShipNew.strRegID) && objShipNew.LoadState < Ship.Loaded.Edit;
			bool outOf = objCO.ship != null && Owned.Contains(objCO.ship.strRegID) && objCO.ship.LoadState < Ship.Loaded.Edit;
			if (into || outOf)
			{
				Plugin.Log.LogInfo("Apart: kept " + objCO.strName + " where they are (" + (into ? "into " : "out of ") + "the client's place)");
				return false;
			}
			return true;
		}

		// Host: speeding up while apart needs the client's OK.
		private static bool TimeScaleMultPrefix(float fMultiplier)
		{
			if (!HostActive || _warpBypass || fMultiplier <= 1f || Plugin.Net.CurrentMode != NetSession.Mode.Host)
			{
				return true;
			}
			AskWarp("x" + fMultiplier.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
			return false;
		}

		// Host: opening the skip-time screen while apart needs the client's OK; closing it doesn't.
		private static bool ToggleSFFPrefix()
		{
			if (!HostActive || _warpBypass || Plugin.Net.CurrentMode != NetSession.Mode.Host)
			{
				return true;
			}
			Ostranauts.Core.Models.Tuple<string, CondOwner> cur = CrewSim.tplCurrentUI;
			if (cur != null && cur.Item1 == "FFWD")
			{
				return true;
			}
			AskWarp("ffwd");
			return false;
		}

		private static void AskWarp(string kind)
		{
			if (_pendingWarp == kind)
			{
				return;
			}
			_pendingWarp = kind;
			Plugin.Net.Send("WARPASK " + kind);
			Chat.AddSystem("Asked " + (Chat.PeerName ?? "the other player") + " to agree: " + WarpText(kind));
		}

		private static string WarpText(string kind)
		{
			return (kind == "ffwd") ? "skip time" : ("speed up time " + kind);
		}

		private static void HostWarpAnswer(string kind, bool ok)
		{
			if (_pendingWarp != kind)
			{
				return;
			}
			_pendingWarp = null;
			if (!ok)
			{
				Chat.AddSystem((Chat.PeerName ?? "The other player") + " declined: " + WarpText(kind));
				return;
			}
			_warpBypass = true;
			try
			{
				if (kind == "ffwd")
				{
					CrewSim.ToggleSFF();
				}
				else
				{
					float mult;
					if (float.TryParse(kind.Substring(1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out mult))
					{
						CrewSim.TimeScaleMult(mult);
					}
				}
			}
			finally
			{
				_warpBypass = false;
			}
		}

		// Client: no saves of our own; the host's save is the real one, and saving a big world on top of it can run
		// the game out of memory.
		private static bool NoClientSavePrefix()
		{
			if (Plugin.Net.CurrentMode == NetSession.Mode.Client && Plugin.Net.Connected)
			{
				Plugin.Log.LogInfo("Save skipped: the host saves the game");
				return false;
			}
			return true;
		}

		// Client, while apart: our character stays in its place.
		private static bool QueueForTransitPrefix()
		{
			if (!ClientActive)
			{
				return true;
			}
			Chat.AddSystem("Can't travel while apart from the host");
			return false;
		}

		// ---- Host: applying the client's copy ----

		private static SnapPayload Parse(byte[] data)
		{
			try
			{
				return JsonMapper.ToObject<SnapPayload>(Encoding.UTF8.GetString(DockSync.Gunzip(data)));
			}
			catch (Exception e)
			{
				Plugin.Log.LogError("Apart: bad copy of the client's place: " + e.Message);
				return null;
			}
		}

		// Host, main thread; a SNAP that wasn't waited for.
		public static void OnBinary(byte[] data)
		{
			if (Plugin.Net.CurrentMode != NetSession.Mode.Host || !WorldSync.InGame)
			{
				return;
			}
			SnapPayload p = Parse(data);
			if (p != null)
			{
				Apply(p);
			}
		}

		private static void Apply(SnapPayload p)
		{
			if (p.Ships == null)
			{
				return;
			}
			// Item saves kept for each ship, to drop the ones the client no longer has (eaten, destroyed).
			Dictionary<string, List<string>> stored = new Dictionary<string, List<string>>();
			foreach (KeyValuePair<string, JsonCondOwnerSave> kv in DataHandler.dictCOSaves)
			{
				JsonCondOwnerSave s = kv.Value;
				if (s == null || string.IsNullOrEmpty(s.strRegIDLast))
				{
					continue;
				}
				List<string> list;
				if (!stored.TryGetValue(s.strRegIDLast, out list))
				{
					list = new List<string>();
					stored[s.strRegIDLast] = list;
				}
				list.Add(kv.Key);
			}
			int ships = 0;
			int items = 0;
			foreach (DockSync.DockPayload ship in p.Ships)
			{
				if (ship == null || ship.Newcomer == null || ship.Ship == null)
				{
					continue;
				}
				Ship here = CrewSim.system.GetShipByRegID(ship.Newcomer);
				if (here == null || here.LoadState >= Ship.Loaded.Edit)
				{
					// Unknown here, or loaded (the host is there): never overwrite what the host runs.
					continue;
				}
				FilterLive(ship);
				HashSet<string> incoming = new HashSet<string>();
				if (ship.COs != null)
				{
					foreach (JsonCondOwnerSave s in ship.COs)
					{
						incoming.Add(s.strID);
					}
				}
				List<string> old;
				if (stored.TryGetValue(ship.Newcomer, out old))
				{
					foreach (string id in old)
					{
						if (!incoming.Contains(id) && !IsLive(id))
						{
							DataHandler.dictCOSaves.Remove(id);
						}
					}
				}
				string error = DockSync.ReplaceShip(ship);
				if (error != null)
				{
					Plugin.Log.LogWarning("Apart: could not take " + ship.Newcomer + ": " + error);
					continue;
				}
				ships++;
				items += (ship.COs != null) ? ship.COs.Count : 0;
			}
			Plugin.Log.LogInfo("Apart: took the client's place (" + p.Id + "): " + ships + " ships, " + items + " items");
		}

		// ---- Client ----

		private static void ClientUpdate(float now)
		{
			if (now >= _nextLoc)
			{
				_nextLoc = now + LocInterval;
				string loc = LocLine();
				if (loc != null && (loc != _sentLoc || now - _locSentAt >= LocRepeat))
				{
					_sentLoc = loc;
					_locSentAt = now;
					Plugin.Net.Send("LOC " + loc);
				}
				UpdateGhosts();
			}
		}

		private static string LocLine()
		{
			Ship cur = CrewSim.shipCurrentLoaded;
			if (cur == null || cur.strRegID == null)
			{
				return null;
			}
			CondOwner mine = CrewClaim.ClientMine;
			string charShip = (mine != null && mine.ship != null) ? mine.ship.strRegID : cur.strRegID;
			List<string> location = new List<string>();
			location.Add(cur.strRegID);
			foreach (Ship s in cur.GetAllDockedShips())
			{
				if (s != null && s.strRegID != null && !location.Contains(s.strRegID))
				{
					location.Add(s.strRegID);
				}
			}
			List<string> loaded = new List<string>();
			List<Ship> ships = CrewSim.GetAllLoadedShips();
			if (ships != null)
			{
				foreach (Ship s in ships)
				{
					if (s != null && !s.bDestroyed && s.strRegID != null && s.LoadState >= Ship.Loaded.Edit && !loaded.Contains(s.strRegID))
					{
						loaded.Add(s.strRegID);
					}
				}
			}
			return charShip + "|" + Ids(location) + "|" + Ids(loaded);
		}

		// A character was renamed (its ID is its name).
		internal static void Renamed(string oldId, string newId)
		{
			if (Ghosts.Remove(oldId))
			{
				Ghosts.Add(newId);
			}
			List<Renderer> off;
			if (Hidden.TryGetValue(oldId, out off))
			{
				Hidden.Remove(oldId);
				Hidden[newId] = off;
			}
		}

		// Client: whether our side runs this person (AI, needs).
		internal static bool ClientSimulates(CondOwner co)
		{
			return ClientActive && co != null && co.ship != null && co.strID != null && ClientOwned.Contains(co.ship.strRegID) && !Ghosts.Contains(co.strID);
		}

		// Client: people who are with the host, left behind in our copy of the place. Hidden and still until the host
		// comes back.
		private static void UpdateGhosts()
		{
			foreach (string id in Ghosts)
			{
				CondOwner co;
				if (!DataHandler.mapCOs.TryGetValue(id, out co) || co == null || co.bDestroyed)
				{
					continue;
				}
				bool hide = ClientActive && co.ship != null && ClientOwned.Contains(co.ship.strRegID);
				SetHidden(co, hide);
			}
			if (!ClientActive && Hidden.Count > 0)
			{
				ShowAll();
			}
		}

		private static void SetHidden(CondOwner co, bool hide)
		{
			bool hidden = Hidden.ContainsKey(co.strID);
			if (hide == hidden)
			{
				return;
			}
			if (hide)
			{
				List<Renderer> off = new List<Renderer>();
				foreach (Renderer r in co.GetComponentsInChildren<Renderer>(true))
				{
					if (r != null && r.enabled)
					{
						r.enabled = false;
						off.Add(r);
					}
				}
				Hidden[co.strID] = off;
				co.Visible = false;
			}
			else
			{
				Show(co.strID);
			}
		}

		private static void Show(string id)
		{
			List<Renderer> off;
			if (!Hidden.TryGetValue(id, out off))
			{
				return;
			}
			Hidden.Remove(id);
			CondOwner co;
			if (DataHandler.mapCOs.TryGetValue(id, out co) && co != null && !co.bDestroyed)
			{
				co.Visible = true;
			}
			foreach (Renderer r in off)
			{
				if (r != null)
				{
					r.enabled = true;
				}
			}
		}

		private static void ShowAll()
		{
			foreach (string id in new List<string>(Hidden.Keys))
			{
				Show(id);
			}
		}

		// Client: the ships of our place, as the host would store them unloaded.
		private static void SendSnapshot(int id)
		{
			SnapPayload p = new SnapPayload();
			p.Id = id;
			p.Ships = new List<DockSync.DockPayload>();
			Ship cur = CrewSim.shipCurrentLoaded;
			if (cur != null)
			{
				List<Ship> ships = new List<Ship>();
				ships.Add(cur);
				foreach (Ship s in cur.GetAllDockedShips())
				{
					if (s != null && !ships.Contains(s))
					{
						ships.Add(s);
					}
				}
				foreach (Ship s in ships)
				{
					if (s.bDestroyed || s.strRegID == null || (ClientOwned.Count > 0 && !ClientOwned.Contains(s.strRegID)))
					{
						continue;
					}
					DockSync.DockPayload d = new DockSync.DockPayload();
					d.Newcomer = s.strRegID;
					DockSync.Snapshot(s, d);
					p.Ships.Add(d);
				}
			}
			byte[] data = DockSync.Gzip(Encoding.UTF8.GetBytes(JsonMapper.ToJson(p)));
			Plugin.Net.SendBinary("SNAP", data, 0, data.Length);
			Plugin.Log.LogInfo("Apart: sent our place (" + id + "): " + p.Ships.Count + " ships, " + data.Length + " bytes");
		}

		// ---- Messages ----

		// Main thread. Returns false if the message isn't ours.
		public static bool OnMessage(string cmd, string arg)
		{
			NetSession.Mode mode = Plugin.Net.CurrentMode;
			switch (cmd)
			{
				case "LOC":
					if (mode == NetSession.Mode.Host)
					{
						string[] f = arg.Split('|');
						if (f.Length == 3)
						{
							_clientShip = f[0];
							_clientSig = f[1];
							ReadIds(f[1], ClientLocation);
							ReadIds(f[2], ClientLoaded);
						}
					}
					return true;
				case "APART":
					if (mode == NetSession.Mode.Client)
					{
						string[] f = arg.Split('|');
						bool on = f[0] == "1";
						if (on && f.Length == 3)
						{
							ReadIds(f[1], ClientOwned);
							ReadIds(f[2], Ghosts);
							if (!_clientOn)
							{
								Chat.AddSystem((Chat.PeerName ?? "The host") + " left; your place now runs on your side");
							}
						}
						else if (!on)
						{
							if (_clientOn)
							{
								Chat.AddSystem("Together again");
							}
							ClientOwned.Clear();
							_warpAsk = null;
						}
						_clientOn = on;
						UpdateGhosts();
						if (!on)
						{
							Ghosts.Clear();
						}
						Plugin.Log.LogInfo("Apart: " + (on ? ("on, our ships " + f[1]) : "off"));
					}
					return true;
				case "MONEYADD":
					if (mode == NetSession.Mode.Client && WorldSync.InGame)
					{
						string[] f = arg.Split('|');
						CondOwner mine = CrewClaim.ClientMine;
						double delta;
						if (f.Length == 2 && mine != null && mine.strID == f[0] && double.TryParse(f[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out delta))
						{
							mine.AddCondAmount(Ledger.CURRENCY, delta);
							Plugin.Log.LogInfo("Apart: got " + delta.ToString("n", System.Globalization.CultureInfo.InvariantCulture) + " from the host's side");
						}
					}
					return true;
				case "SNAPREQ":
					if (mode == NetSession.Mode.Client && WorldSync.InGame)
					{
						int id;
						int.TryParse(arg, out id);
						try
						{
							SendSnapshot(id);
						}
						catch (Exception e)
						{
							Plugin.Log.LogError("Apart: could not copy our place: " + e);
						}
					}
					return true;
				case "WARPASK":
					if (mode == NetSession.Mode.Client)
					{
						_warpAsk = arg;
						Chat.AddSystem((Chat.PeerName ?? "The host") + " wants to " + WarpText(arg));
					}
					return true;
				case "WARPOK":
				case "WARPNO":
					if (mode == NetSession.Mode.Host)
					{
						HostWarpAnswer(arg, cmd == "WARPOK");
					}
					return true;
				default:
					return false;
			}
		}

		// ---- Client: the time question ----

		public static void OnGUI()
		{
			if (_warpAsk == null || !ClientActive)
			{
				return;
			}
			if (_askRect.x == 0f)
			{
				_askRect.x = (Screen.width - _askRect.width) / 2f;
				_askRect.y = 120f;
			}
			_askRect = GUILayout.Window(AskWindowId, _askRect, DrawAsk, "TIME", MenuLook.Window);
			Chat.AddBlocking(_askRect);
		}

		private static void DrawAsk(int id)
		{
			GUISkin prev = MenuLook.Use();
			try
			{
				string kind = _warpAsk;
				if (kind == null)
				{
					return;
				}
				GUILayout.Label((Chat.PeerName ?? "The host") + " wants to " + WarpText(kind) + ".");
				GUILayout.BeginHorizontal();
				if (GUILayout.Button("Agree"))
				{
					Plugin.Net.Send("WARPOK " + kind);
					_warpAsk = null;
				}
				if (GUILayout.Button("Decline"))
				{
					Plugin.Net.Send("WARPNO " + kind);
					_warpAsk = null;
				}
				GUILayout.EndHorizontal();
				GUI.DragWindow();
			}
			finally
			{
				MenuLook.Restore(prev);
			}
		}
	}
}
