using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OstranautsMP
{
	// Host -> client conditions of every person and every room on the loaded ships.
	// People: needs, stats, injuries, moods. Rooms: gas, pressure, temperature and the rest of the room's stats.
	//
	// Protocol (host -> client), twice per second, only values that changed since the last send:
	//   CONDS <key>:<cond>=<value>,<cond>=<value>;<key>:...
	// <key> is a person's strID, or R#<shipRegID>#<lowest tile index> for a room. Room objects are rebuilt when a
	// ship loads, so their IDs aren't guaranteed to match between host and client; the tile layout is.
	// A removed condition is sent as 0. After the client reports READY, the host sends everything again.
	//
	// The client applies values with the owner's cond rules frozen, so thresholds, anti-conditions and the fatigue
	// coefficient don't run a second time on top of the host's results (those results are conditions too and arrive
	// in the same message). For rooms, the gas container's mol table is set to match the StatGasMol* conditions,
	// otherwise the client's gas simulation would recompute the conditions from its own mols.
	public static class CondSync
	{
		private const float Interval = 0.5f;

		private const double Epsilon = 1E-06;

		private const string RoomPrefix = "R#";

		private const string MolPrefix = "StatGasMol";

		private const string MolTotal = "StatGasMolTotal";

		private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

		// Host: last values sent, per key.
		private static readonly Dictionary<string, Dictionary<string, double>> Sent = new Dictionary<string, Dictionary<string, double>>();

		private static readonly HashSet<string> Seen = new HashSet<string>();

		private static readonly List<string> Removed = new List<string>();

		private static readonly StringBuilder Sb = new StringBuilder();

		// Client: room key -> room, rebuilt for each message.
		private static readonly Dictionary<string, Room> RoomsByKey = new Dictionary<string, Room>();

		private static readonly List<string> AppliedMols = new List<string>();

		private static float _next;

		public static void ResetSnapshot()
		{
			Sent.Clear();
		}

		public static void HostUpdate(float now)
		{
			if (now < _next)
			{
				return;
			}
			_next = now + Interval;
			List<Ship> ships = CrewSim.GetAllLoadedShips();
			if (ships == null)
			{
				return;
			}
			Sb.Length = 0;
			Seen.Clear();
			foreach (Ship ship in ships)
			{
				if (ship == null || ship.bDestroyed)
				{
					continue;
				}
				foreach (CondOwner co in ship.GetPeople(bAllowDocked: false))
				{
					if (co != null && !co.bDestroyed && co.strID != null && Seen.Add(co.strID))
					{
						AppendOwner(co.strID, co);
					}
				}
				if (ship.aRooms == null)
				{
					continue;
				}
				foreach (Room room in ship.aRooms)
				{
					string key = RoomKey(ship, room);
					if (key != null && Seen.Add(key))
					{
						AppendOwner(key, room.CO);
					}
				}
			}
			Removed.Clear();
			foreach (string key in Sent.Keys)
			{
				if (!Seen.Contains(key))
				{
					Removed.Add(key);
				}
			}
			foreach (string key in Removed)
			{
				Sent.Remove(key);
			}
			if (Sb.Length > 0)
			{
				Plugin.Net.Send("CONDS " + Sb);
			}
		}

		// Null for rooms that can't be keyed or don't hold a simulated atmosphere (space).
		private static string RoomKey(Ship ship, Room room)
		{
			if (room == null || room.CO == null || room.CO.bDestroyed || room.Void || room.aTiles == null || room.aTiles.Count == 0 || ship.strRegID == null)
			{
				return null;
			}
			int min = int.MaxValue;
			foreach (Tile t in room.aTiles)
			{
				if (t != null && t.Index < min)
				{
					min = t.Index;
				}
			}
			if (min == int.MaxValue)
			{
				return null;
			}
			return RoomPrefix + ship.strRegID + "#" + min.ToString(Inv);
		}

		private static void AppendOwner(string key, CondOwner co)
		{
			if (co.mapConds == null)
			{
				return;
			}
			Dictionary<string, double> last;
			if (!Sent.TryGetValue(key, out last))
			{
				last = new Dictionary<string, double>();
				Sent[key] = last;
			}
			bool any = false;
			foreach (KeyValuePair<string, Condition> kv in co.mapConds)
			{
				Condition c = kv.Value;
				if (c == null)
				{
					continue;
				}
				double old;
				if (!last.TryGetValue(kv.Key, out old) || Math.Abs(old - c.fCount) > Epsilon)
				{
					AppendCond(ref any, key, kv.Key, c.fCount);
					last[kv.Key] = c.fCount;
				}
			}
			Removed.Clear();
			foreach (string name in last.Keys)
			{
				if (!co.mapConds.ContainsKey(name))
				{
					Removed.Add(name);
				}
			}
			foreach (string name in Removed)
			{
				AppendCond(ref any, key, name, 0.0);
				last.Remove(name);
			}
		}

		private static void AppendCond(ref bool any, string key, string name, double value)
		{
			if (!any)
			{
				if (Sb.Length > 0)
				{
					Sb.Append(';');
				}
				Sb.Append(key).Append(':');
				any = true;
			}
			else
			{
				Sb.Append(',');
			}
			Sb.Append(name).Append('=').Append(value.ToString("R", Inv));
		}

		// Client side, main thread.
		public static void Apply(string arg)
		{
			bool roomsIndexed = false;
			foreach (string entry in arg.Split(';'))
			{
				int colon = entry.IndexOf(':');
				if (colon <= 0)
				{
					continue;
				}
				string key = entry.Substring(0, colon);
				CondOwner co = null;
				Room room = null;
				if (key.StartsWith(RoomPrefix))
				{
					if (!roomsIndexed)
					{
						IndexRooms();
						roomsIndexed = true;
					}
					if (RoomsByKey.TryGetValue(key, out room))
					{
						co = room.CO;
					}
				}
				else
				{
					DataHandler.mapCOs.TryGetValue(key, out co);
				}
				if (co == null || co.bDestroyed || co.mapConds == null)
				{
					continue;
				}
				ApplyConds(co, entry.Substring(colon + 1), room != null);
			}
		}

		private static void IndexRooms()
		{
			RoomsByKey.Clear();
			List<Ship> ships = CrewSim.GetAllLoadedShips();
			if (ships == null)
			{
				return;
			}
			foreach (Ship ship in ships)
			{
				if (ship == null || ship.bDestroyed || ship.aRooms == null)
				{
					continue;
				}
				foreach (Room room in ship.aRooms)
				{
					string key = RoomKey(ship, room);
					if (key != null)
					{
						RoomsByKey[key] = room;
					}
				}
			}
		}

		private static void ApplyConds(CondOwner co, string conds, bool isRoom)
		{
			AppliedMols.Clear();
			bool freeze = co.bFreezeCondRules;
			co.bFreezeCondRules = true;
			try
			{
				foreach (string pair in conds.Split(','))
				{
					int eq = pair.IndexOf('=');
					double value;
					if (eq > 0 && double.TryParse(pair.Substring(eq + 1), NumberStyles.Float, Inv, out value))
					{
						string name = pair.Substring(0, eq);
						co.SetCondAmount(name, value);
						if (isRoom && name.StartsWith(MolPrefix) && name != MolTotal)
						{
							AppliedMols.Add(name);
						}
					}
				}
				if (AppliedMols.Count > 0)
				{
					SyncGasMols(co);
				}
			}
			catch (Exception e)
			{
				Plugin.Log.LogError("Applying conditions to " + co.strName + " failed: " + e.Message);
			}
			finally
			{
				co.bFreezeCondRules = freeze;
			}
		}

		// Make the room's mol table match its (now host-synced) StatGasMol* conditions, and drop pending local deltas.
		private static void SyncGasMols(CondOwner co)
		{
			GasContainer gc = co.GasContainer;
			if (gc == null || gc.mapGasMols1 == null)
			{
				return;
			}
			foreach (string name in AppliedMols)
			{
				gc.mapGasMols1[name] = co.GetCondAmount(name);
			}
			double total = 0.0;
			foreach (KeyValuePair<string, double> kv in gc.mapGasMols1)
			{
				if (kv.Key != MolTotal)
				{
					total += kv.Value;
				}
			}
			gc.mapGasMols1[MolTotal] = total;
			if (gc.mapDGasMols != null)
			{
				List<string> keys = new List<string>(gc.mapDGasMols.Keys);
				foreach (string k in keys)
				{
					gc.mapDGasMols[k] = 0.0;
				}
			}
			gc.fDGasTemp = 0.0;
		}
	}
}
