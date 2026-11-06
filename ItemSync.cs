using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using LitJson;
using UnityEngine;

namespace OstranautsMP
{
	// Items: where every item is (floor, slot, container, stack) and its state. Covers picking up, dropping,
	// equipping, moving between containers, stacking, using (eating, charging, tools) and building: walls, floors and
	// doors are items on the floor too, and the receiver rebuilds its rooms by itself (Ship.AddCO/RemoveCO set
	// bCheckRooms, and StarSystem.UpdateShip then calls CreateRooms).
	//
	// Host: every low-level move (Slots.SlotItem/UnSlotItem, Container.AddCOSimple/RemoveCOSimple,
	// CondOwner.StackCO/PopHeadFromStack, Ship.AddCO/RemoveCO), every interaction's effects and every Destroy mark the
	// items involved. Client: the same, but only inside player input (inventory screen, clicks), which the host can't
	// reproduce from an order. At the end of the frame each marked item is reduced to its stack head, and one message
	// per stack says where it ended up.
	//
	// Protocol (both directions):
	//   ITEM F|<shipRegID>|<x>|<y>|<rot>|<entries>       on the floor
	//   ITEM S|<parentID>|<slot>|<entries>               in a slot of <parentID>
	//   ITEM C|<parentID>|<gridX>|<gridY>|<entries>      in the container of <parentID>
	//   GONE <id>                                        destroyed
	//   NEED <id>                                        "I don't have this item, send it in full"
	// <entries> = the head, then each stack member, joined by ',', each one of:
	//   S<base64 JsonCondOwnerSave>    full save: only for items the other side may not have (created since the save
	//                                  was shared, or asked for with NEED)
	//   T<id>~<cond>=<value>^...       its conditions changed (it was used)
	//   I<id>                          just its place
	// An entry for an item the receiver doesn't have makes it send NEED and retry the message for a few seconds.
	//
	// Receiver: create missing items from their saves, update the state of existing ones (without recreating them,
	// so a bag keeps its contents), rebuild the stack the way the game does after loading (CondOwner.PostGameLoad),
	// and move the head with the same calls the game uses when loading a ship (SlotItem, AddCOSimple, AddCO).
	public static class ItemSync
	{
		private const float RetryWindow = 5f;

		private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

		// Ship.InitShip spawns and slots thousands of items while a ship loads; Ship.Destroy (unloading a station when
		// undocking) and CrewSim.EmptyScene (leaving the game) destroy them all. None of those are changes to send.
		private static int _suppressDepth;

		// > 0 while we apply the other side's message, so objects we create for it don't count as new here.
		private static int _applyingDepth;

		// Items touched this frame, by ID.
		private static readonly Dictionary<string, CondOwner> Dirty = new Dictionary<string, CondOwner>();

		// Of those, the ones whose conditions changed (used in an interaction).
		private static readonly HashSet<string> StateDirty = new HashSet<string>();

		// Items the other side may not have: created here since the save was shared, or asked for with NEED.
		private static readonly HashSet<string> Fresh = new HashSet<string>();

		private static readonly Dictionary<string, CondOwner> Units = new Dictionary<string, CondOwner>();

		private static readonly List<CondOwner> Ordered = new List<CondOwner>();

		private struct Pending
		{
			public string Arg;

			public float Since;
		}

		// Messages waiting for an item or parent that doesn't exist here yet.
		private static readonly List<Pending> Waiting = new List<Pending>();

		private static readonly List<Pending> Retry = new List<Pending>();

		private static readonly Dictionary<string, double> TargetConds = new Dictionary<string, double>();

		private static readonly List<string> ToZero = new List<string>();

		private static readonly Dictionary<string, float> NeedSent = new Dictionary<string, float>();

		public static void Patch(Harmony harmony)
		{
			HarmonyMethod enter = new HarmonyMethod(typeof(ItemSync), nameof(EnterSuppress));
			HarmonyMethod exit = new HarmonyMethod(typeof(ItemSync), nameof(ExitSuppress));
			harmony.Patch(AccessTools.Method(typeof(Ship), "InitShip"), prefix: enter, finalizer: exit);
			harmony.Patch(AccessTools.Method(typeof(Ship), "Destroy"), prefix: enter, finalizer: exit);
			harmony.Patch(AccessTools.Method(typeof(CrewSim), "EmptyScene"), prefix: enter, finalizer: exit);
			harmony.Patch(AccessTools.Method(typeof(Slots), "SlotItem"), postfix: new HarmonyMethod(typeof(ItemSync), nameof(MarkCo)));
			harmony.Patch(AccessTools.Method(typeof(Slots), "UnSlotItem", new Type[] { typeof(string), typeof(CondOwner), typeof(bool) }), postfix: new HarmonyMethod(typeof(ItemSync), nameof(MarkResult)));
			harmony.Patch(AccessTools.Method(typeof(Container), "AddCOSimple"), postfix: new HarmonyMethod(typeof(ItemSync), nameof(MarkObjCO)));
			harmony.Patch(AccessTools.Method(typeof(Container), "RemoveCOSimple"), postfix: new HarmonyMethod(typeof(ItemSync), nameof(MarkObjCO)));
			harmony.Patch(AccessTools.Method(typeof(CondOwner), "StackCO"), postfix: new HarmonyMethod(typeof(ItemSync), nameof(MarkStackCO)));
			harmony.Patch(AccessTools.Method(typeof(CondOwner), "PopHeadFromStack"), postfix: new HarmonyMethod(typeof(ItemSync), nameof(MarkPopHead)));
			harmony.Patch(AccessTools.Method(typeof(Ship), "AddCO", new Type[] { typeof(CondOwner), typeof(bool), typeof(bool) }), postfix: new HarmonyMethod(typeof(ItemSync), nameof(MarkObjICO)));
			harmony.Patch(AccessTools.Method(typeof(Ship), "RemoveCO"), postfix: new HarmonyMethod(typeof(ItemSync), nameof(MarkObjCO)));
			harmony.Patch(AccessTools.Method(typeof(CondOwner), "Destroy"), prefix: new HarmonyMethod(typeof(ItemSync), nameof(DestroyPrefix)));
			harmony.Patch(AccessTools.Method(typeof(CondOwner), "ModeSwitch"), postfix: new HarmonyMethod(typeof(ItemSync), nameof(ModeSwitchPostfix)));
			harmony.Patch(AccessTools.Method(typeof(Interaction), "ApplyEffects"), postfix: new HarmonyMethod(typeof(ItemSync), nameof(ApplyEffectsPostfix)));
			harmony.Patch(AccessTools.Method(typeof(DataHandler), "GetCondOwner", new Type[] { typeof(string), typeof(string), typeof(string), typeof(bool), typeof(string), typeof(JsonCondOwnerSave), typeof(string), typeof(Transform) }), prefix: new HarmonyMethod(typeof(ItemSync), nameof(GetCondOwnerPrefix)), postfix: new HarmonyMethod(typeof(ItemSync), nameof(GetCondOwnerPostfix)));
		}

		private static void EnterSuppress()
		{
			_suppressDepth++;
		}

		private static Exception ExitSuppress(Exception __exception)
		{
			_suppressDepth--;
			return __exception;
		}

		// Host: every change. Client: only changes made directly by the player.
		// Client apart from the host: our place is ours alone, nothing about it goes to the host.
		private static bool Tracking => _suppressDepth <= 0 && _applyingDepth <= 0 && Plugin.Net.Connected && WorldSync.InGame && (Plugin.Net.CurrentMode == NetSession.Mode.Host || (WorldSync.ClientFollowsHost && OrderSync.InUserInput && !Apart.ClientActive));

		private static bool FlushAllowed => _suppressDepth <= 0 && Plugin.Net.Connected && WorldSync.InGame && (Plugin.Net.CurrentMode == NetSession.Mode.Host || WorldSync.ClientFollowsHost);

		// Physical items: not people (they have a pathfinder and sync through POS/CONDS), not rooms, not ship records.
		private static bool IsItem(CondOwner co)
		{
			return co != null && !co.bDestroyed && co.strID != null && co.Item != null && co.Pathfinder == null && !co.HasCond("IsRoom");
		}

		// Both sides now have the same world (host: the save was just sent; client: it was just loaded).
		public static void ResetKnown()
		{
			Fresh.Clear();
			NeedSent.Clear();
			Waiting.Clear();
		}

		private static void Mark(CondOwner co)
		{
			if (Tracking && IsItem(co))
			{
				Dirty[co.strID] = co;
			}
		}

		private static void MarkCo(CondOwner co)
		{
			Mark(co);
		}

		private static void MarkResult(CondOwner __result)
		{
			Mark(__result);
		}

		private static void MarkObjCO(CondOwner objCO)
		{
			Mark(objCO);
		}

		private static void MarkObjICO(CondOwner objICO)
		{
			Mark(objICO);
		}

		private static void MarkStackCO(CondOwner __instance, CondOwner objCO)
		{
			Mark(__instance);
			Mark(objCO);
		}

		private static void MarkPopHead(CondOwner __instance, CondOwner __result)
		{
			Mark(__instance);
			Mark(__result);
		}

		private static void MarkUsed(CondOwner co)
		{
			if (Tracking && IsItem(co))
			{
				Dirty[co.strID] = co;
				StateDirty.Add(co.strID);
			}
		}

		private static void ApplyEffectsPostfix(Interaction __instance)
		{
			MarkUsed(__instance.objUs);
			MarkUsed(__instance.objThem);
			MarkUsed(__instance.obj3rd);
		}

		// A new object (not one returned from mapCOs, not one we build for the other side) is unknown to the other side.
		private static void GetCondOwnerPrefix(string strName, JsonCondOwnerSave jcos, out bool __state)
		{
			__state = strName != null && jcos == null && DataHandler.mapCOs.ContainsKey(strName);
		}

		private static void GetCondOwnerPostfix(CondOwner __result, bool __state)
		{
			if (!__state && __result != null && __result.strID != null && _applyingDepth <= 0 && _suppressDepth <= 0 && Plugin.Net.Connected && WorldSync.InGame)
			{
				Fresh.Add(__result.strID);
			}
		}

		// An item turned into another kind (repaired, broken, switched on or off) keeps its ID but is a new object of
		// another type: the other side needs it in full, not just its place.
		private static void ModeSwitchPostfix(CondOwner coNew)
		{
			if (coNew != null && coNew.strID != null && Tracking && IsItem(coNew))
			{
				Fresh.Add(coNew.strID);
				Dirty[coNew.strID] = coNew;
			}
		}

		private static void DestroyPrefix(CondOwner __instance)
		{
			if (!Tracking || !IsItem(__instance))
			{
				return;
			}
			// The old object of a mode switch: its ID lives on in the new one, which is sent in full.
			CondOwner current;
			if (__instance.strID != null && DataHandler.mapCOs.TryGetValue(__instance.strID, out current) && current != null && current != __instance && !current.bDestroyed)
			{
				return;
			}
			// The stack it leaves changes too.
			if (__instance.coStackHead != null)
			{
				Mark(__instance.coStackHead);
			}
			// Only drop the pending update if it's this object; a replacement with the same ID (mode switch) still goes.
			CondOwner pending;
			if (Dirty.TryGetValue(__instance.strID, out pending) && pending == __instance)
			{
				Dirty.Remove(__instance.strID);
			}
			Fresh.Remove(__instance.strID);
			if (Apart.HostSends(__instance))
			{
				Plugin.Net.Send("GONE " + __instance.strID);
			}
		}

		// Main thread, every frame: send what was marked this frame.
		public static void Update()
		{
			if (Dirty.Count == 0)
			{
				return;
			}
			if (!FlushAllowed)
			{
				Dirty.Clear();
				StateDirty.Clear();
				return;
			}
			Units.Clear();
			foreach (CondOwner co in Dirty.Values)
			{
				if (co == null || co.bDestroyed)
				{
					continue;
				}
				CondOwner head = (co.coStackHead != null) ? co.coStackHead : co;
				if (IsItem(head) && Apart.HostSends(head))
				{
					Units[head.strID] = head;
				}
			}
			Dirty.Clear();
			// Parents first, so the receiver has the bag before what's inside it.
			Ordered.Clear();
			Ordered.AddRange(Units.Values);
			Ordered.Sort((CondOwner a, CondOwner b) => Depth(a).CompareTo(Depth(b)));
			foreach (CondOwner head in Ordered)
			{
				try
				{
					string location = Location(head);
					if (location != null)
					{
						Plugin.Net.Send("ITEM " + location + "|" + Entries(head));
					}
				}
				catch (Exception e)
				{
					Plugin.Log.LogError("Could not send item " + head.strName + ": " + e.Message);
				}
			}
			Ordered.Clear();
			Units.Clear();
			StateDirty.Clear();
		}

		private static int Depth(CondOwner co)
		{
			int d = 0;
			for (CondOwner p = co.objCOParent; p != null && d < 64; p = p.objCOParent)
			{
				d++;
			}
			return d;
		}

		// Null when the item is somewhere we don't sync (in transit, a trade screen, attached as a lot part).
		private static string Location(CondOwner co)
		{
			CondOwner parent = co.objCOParent;
			if (parent != null)
			{
				if (co.slotNow != null)
				{
					return "S|" + parent.strID + "|" + co.slotNow.strName;
				}
				if (parent.objContainer != null && parent.objContainer.Contains(co))
				{
					return "C|" + parent.strID + "|" + co.pairInventoryXY.x.ToString(Inv) + "|" + co.pairInventoryXY.y.ToString(Inv);
				}
				return null;
			}
			if (co.ship != null && co.ship.GetCOByID(co.strID) == co)
			{
				float rot = co.Item.fLastRotation;
				return "F|" + co.ship.strRegID + "|" + co.tf.position.x.ToString("R", Inv) + "|" + co.tf.position.y.ToString("R", Inv) + "|" + rot.ToString("R", Inv);
			}
			return null;
		}

		private static string Entries(CondOwner head)
		{
			StringBuilder sb = new StringBuilder();
			AppendEntry(sb, head);
			foreach (CondOwner member in head.aStack)
			{
				if (member != null && !member.bDestroyed)
				{
					sb.Append(',');
					AppendEntry(sb, member);
				}
			}
			return sb.ToString();
		}

		private static void AppendEntry(StringBuilder sb, CondOwner co)
		{
			if (Fresh.Remove(co.strID))
			{
				sb.Append('S').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonMapper.ToJson(co.GetJSONSave()))));
				return;
			}
			if (StateDirty.Contains(co.strID) && co.mapConds != null)
			{
				sb.Append('T').Append(co.strID).Append('~');
				string idCond = (co.objCondID != null) ? co.objCondID.strName : null;
				bool first = true;
				foreach (KeyValuePair<string, Condition> kv in co.mapConds)
				{
					if (kv.Value == null || kv.Key == idCond)
					{
						continue;
					}
					if (!first)
					{
						sb.Append('^');
					}
					sb.Append(kv.Key).Append('=').Append(kv.Value.fCount.ToString("R", Inv));
					first = false;
				}
				return;
			}
			sb.Append('I').Append(co.strID);
		}

		// Main thread. Returns false if the message isn't ours.
		public static bool OnMessage(string cmd, string arg, float now)
		{
			if (cmd != "ITEM" && cmd != "GONE" && cmd != "NEED" && cmd != "CHECK" && cmd != "RESYNC")
			{
				return false;
			}
			if (Plugin.Net.CurrentMode == NetSession.Mode.None || !WorldSync.InGame)
			{
				return true;
			}
			_applyingDepth++;
			try
			{
				switch (cmd)
				{
					case "GONE":
						Gone(arg);
						break;
					case "ITEM":
						if (!ApplyItem(arg))
						{
							Pending p;
							p.Arg = arg;
							p.Since = now;
							Waiting.Add(p);
						}
						break;
					case "NEED":
					{
						// The other side lacks this item; send it in full on the next flush.
						CondOwner co;
						if (DataHandler.mapCOs.TryGetValue(arg, out co) && IsItem(co))
						{
							Dirty[co.strID] = co;
							Fresh.Add(co.strID);
						}
						else if (co != null && !co.bDestroyed && co.Crew != null)
						{
							// A person their items belong to (PersonSync).
							PersonSync.HostSend(co);
						}
						break;
					}
					case "CHECK":
						if (Plugin.Net.CurrentMode == NetSession.Mode.Client)
						{
							ClientCheck(arg, now);
						}
						break;
					case "RESYNC":
						if (Plugin.Net.CurrentMode == NetSession.Mode.Host)
						{
							HostResync(arg);
						}
						break;
				}
			}
			catch (Exception e)
			{
				Plugin.Log.LogError(cmd + " failed: " + e);
			}
			finally
			{
				_applyingDepth--;
			}
			return true;
		}

		// Main thread, every frame: retry messages whose item or parent hadn't arrived.
		public static void RetryUpdate(float now)
		{
			if (Waiting.Count == 0)
			{
				return;
			}
			if (!Plugin.Net.Connected || !WorldSync.InGame)
			{
				Waiting.Clear();
				return;
			}
			Retry.AddRange(Waiting);
			Waiting.Clear();
			_applyingDepth++;
			try
			{
				foreach (Pending p in Retry)
				{
					bool done;
					try
					{
						done = ApplyItem(p.Arg);
					}
					catch (Exception e)
					{
						Plugin.Log.LogError("ITEM retry failed: " + e.Message);
						done = true;
					}
					if (done)
					{
						continue;
					}
					if (now - p.Since < RetryWindow)
					{
						Waiting.Add(p);
					}
					else
					{
						Plugin.Log.LogWarning("Dropped item update, item or parent never arrived: " + p.Arg.Substring(0, Math.Min(80, p.Arg.Length)));
					}
				}
			}
			finally
			{
				_applyingDepth--;
				Retry.Clear();
			}
		}

		// ---- Desync check -------------------------------------------------------------------------------------
		//
		// Every CheckInterval the host sends, per group, a hash of where every item in it is:
		//   CHECK <group>=<hash>:<count>;...
		// Groups: P~<personID> (everything a person carries) and S~<shipRegID> (everything else on that ship).
		// A group that differs on the client in two checks in a row (so not just an update in flight) is compared item by
		// item: the client sends its list, the host answers only the differences.
		//   client: RESYNC <group>|<id>=<place>\t<id>=<place>...
		//   host:   ITEM for each item that is missing or elsewhere on the client (full save only if missing),
		//           GONE for each item the client has and the host doesn't.

		private const float CheckInterval = 5f;

		private const int MaxResyncsPerCheck = 3;

		private static float _nextCheck;

		private static readonly Dictionary<string, int> Mismatches = new Dictionary<string, int>();

		private static string _notice;

		private static float _noticeUntil;

		// Shown in the status line for a while after a resync.
		internal static string Notice => (_notice != null && Time.unscaledTime < _noticeUntil) ? _notice : null;

		// Host, main thread, every frame.
		public static void HostCheck(float now)
		{
			if (Plugin.Net.CurrentMode != NetSession.Mode.Host || !FlushAllowed || now < _nextCheck)
			{
				return;
			}
			_nextCheck = now + CheckInterval;
			StringBuilder sb = new StringBuilder();
			foreach (KeyValuePair<string, List<CondOwner>> group in Scan())
			{
				if (sb.Length > 0)
				{
					sb.Append(';');
				}
				sb.Append(group.Key).Append('=').Append(Hash(group.Value)).Append(':').Append(group.Value.Count.ToString(Inv));
			}
			if (sb.Length > 0)
			{
				Plugin.Net.Send("CHECK " + sb);
			}
		}

		private static void ClientCheck(string arg, float now)
		{
			if (!FlushAllowed)
			{
				return;
			}
			Dictionary<string, List<CondOwner>> local = Scan();
			int requested = 0;
			foreach (string entry in arg.Split(';'))
			{
				int eq = entry.IndexOf('=');
				int colon = entry.LastIndexOf(':');
				if (eq <= 0 || colon <= eq)
				{
					continue;
				}
				string key = entry.Substring(0, eq);
				string hostHash = entry.Substring(eq + 1, colon - eq - 1);
				if (!HaveGroup(key))
				{
					// A ship or person that isn't loaded here: nothing to compare.
					Mismatches.Remove(key);
					continue;
				}
				List<CondOwner> mine;
				if (!local.TryGetValue(key, out mine))
				{
					mine = new List<CondOwner>();
				}
				if (Hash(mine) == hostHash)
				{
					Mismatches.Remove(key);
					continue;
				}
				int n;
				Mismatches.TryGetValue(key, out n);
				n++;
				if (n < 2 || requested >= MaxResyncsPerCheck)
				{
					Mismatches[key] = n;
					continue;
				}
				Mismatches.Remove(key);
				requested++;
				StringBuilder sb = new StringBuilder();
				foreach (CondOwner co in mine)
				{
					if (sb.Length > 0)
					{
						sb.Append('\t');
					}
					sb.Append(co.strID).Append('=').Append(Signature(co));
				}
				Plugin.Net.Send("RESYNC " + key + "|" + sb);
				Plugin.Log.LogWarning("Desync in " + key + " (host " + entry.Substring(colon + 1) + " items, here " + mine.Count + "), comparing");
			}
			if (requested > 0)
			{
				_notice = "DESYNC: resyncing " + requested + " group(s)";
				_noticeUntil = now + 10f;
			}
		}

		private static bool HaveGroup(string key)
		{
			string id = key.Substring(2);
			if (key.StartsWith("S~"))
			{
				Ship ship = (CrewSim.system != null) ? CrewSim.system.GetShipByRegID(id) : null;
				return ship != null && ship.LoadState == Ship.Loaded.Full;
			}
			CondOwner co;
			return DataHandler.mapCOs.TryGetValue(id, out co) && co != null && !co.bDestroyed;
		}

		private static void HostResync(string arg)
		{
			int bar = arg.IndexOf('|');
			if (bar <= 0)
			{
				return;
			}
			string key = arg.Substring(0, bar);
			Dictionary<string, string> theirs = new Dictionary<string, string>();
			foreach (string pair in arg.Substring(bar + 1).Split(new char[] { '\t' }, StringSplitOptions.RemoveEmptyEntries))
			{
				int eq = pair.IndexOf('=');
				if (eq > 0)
				{
					theirs[pair.Substring(0, eq)] = pair.Substring(eq + 1);
				}
			}
			List<CondOwner> group;
			if (!Scan().TryGetValue(key, out group))
			{
				group = new List<CondOwner>();
			}
			HashSet<string> ours = new HashSet<string>();
			int missing = 0;
			int moved = 0;
			int extra = 0;
			foreach (CondOwner co in group)
			{
				ours.Add(co.strID);
				string place;
				if (!theirs.TryGetValue(co.strID, out place))
				{
					Dirty[co.strID] = co;
					Fresh.Add(co.strID);
					missing++;
				}
				else if (place != Signature(co))
				{
					Dirty[co.strID] = co;
					moved++;
				}
			}
			foreach (string id in theirs.Keys)
			{
				if (ours.Contains(id))
				{
					continue;
				}
				CondOwner co;
				if (DataHandler.mapCOs.TryGetValue(id, out co) && IsItem(co))
				{
					// We have it, just somewhere else: send where.
					Dirty[id] = co;
					moved++;
				}
				else
				{
					Plugin.Net.Send("GONE " + id);
					extra++;
				}
			}
			Plugin.Log.LogWarning("Client desync in " + key + ": " + missing + " missing, " + moved + " misplaced, " + extra + " extra");
			_notice = "DESYNC: client resynced " + key + " (" + (missing + moved + extra) + " items)";
			_noticeUntil = Time.unscaledTime + 10f;
		}

		// All items on the loaded ships, grouped.
		private static Dictionary<string, List<CondOwner>> Scan()
		{
			Dictionary<string, List<CondOwner>> groups = new Dictionary<string, List<CondOwner>>();
			List<Ship> ships = CrewSim.GetAllLoadedShips();
			if (ships == null)
			{
				return groups;
			}
			HashSet<string> seen = new HashSet<string>();
			foreach (Ship ship in ships)
			{
				if (ship == null || ship.bDestroyed || ship.LoadState != Ship.Loaded.Full || ship.strRegID == null || (Plugin.Net.CurrentMode == NetSession.Mode.Host && !Apart.HostSendsShip(ship.strRegID)))
				{
					continue;
				}
				foreach (CondOwner co in ship.GetICOs1(null, bSubObjects: true, bAllowDocked: false, bAllowLocked: true))
				{
					if (!IsItem(co) || !seen.Add(co.strID))
					{
						continue;
					}
					string key = GroupKey(co, ship);
					List<CondOwner> list;
					if (!groups.TryGetValue(key, out list))
					{
						list = new List<CondOwner>();
						groups[key] = list;
					}
					list.Add(co);
				}
			}
			return groups;
		}

		private static string GroupKey(CondOwner co, Ship ship)
		{
			CondOwner top = co;
			for (int i = 0; i < 64; i++)
			{
				CondOwner next = (top.coStackHead != null) ? top.coStackHead : top.objCOParent;
				if (next == null)
				{
					break;
				}
				top = next;
			}
			if (top.Pathfinder != null)
			{
				return "P~" + top.strID;
			}
			return "S~" + ship.strRegID;
		}

		// Where an item is, coarse enough to match on both machines (floor position to half a tile).
		private static string Signature(CondOwner co)
		{
			if (co.coStackHead != null)
			{
				return "K" + co.coStackHead.strID;
			}
			CondOwner parent = co.objCOParent;
			if (parent != null)
			{
				if (co.slotNow != null)
				{
					return "S" + parent.strID + "/" + co.slotNow.strName;
				}
				return "C" + parent.strID + "/" + co.pairInventoryXY.x.ToString(Inv) + "," + co.pairInventoryXY.y.ToString(Inv);
			}
			return "F" + Mathf.RoundToInt(co.tf.position.x * 2f).ToString(Inv) + "," + Mathf.RoundToInt(co.tf.position.y * 2f).ToString(Inv);
		}

		private static string Hash(List<CondOwner> group)
		{
			List<string> entries = new List<string>(group.Count);
			foreach (CondOwner co in group)
			{
				entries.Add(co.strID + "=" + Signature(co));
			}
			entries.Sort(StringComparer.Ordinal);
			ulong h = 14695981039346656037UL;
			foreach (string e in entries)
			{
				foreach (char ch in e)
				{
					h ^= ch;
					h *= 1099511628211UL;
				}
				h ^= '\n';
				h *= 1099511628211UL;
			}
			return h.ToString("x16");
		}

		// ---- Applying -----------------------------------------------------------------------------------------

		private static void Gone(string id)
		{
			CondOwner co;
			if (!DataHandler.mapCOs.TryGetValue(id, out co) || co == null || co.bDestroyed)
			{
				return;
			}
			Unstack(co);
			co.RemoveFromCurrentHome(bForce: true);
			co.Destroy();
		}

		// Returns false when an item or the parent doesn't exist here yet (asked for with NEED; retry later).
		private static bool ApplyItem(string arg)
		{
			string[] f = arg.Split('|');
			if (f.Length < 2)
			{
				return true;
			}
			string entriesField = f[f.Length - 1];
			Ship ship = null;
			CondOwner parent = null;
			switch (f[0])
			{
				case "F":
					if (f.Length != 6)
					{
						return true;
					}
					ship = (CrewSim.system != null) ? CrewSim.system.GetShipByRegID(f[1]) : null;
					if (ship == null || ship.bDestroyed || ship.LoadState != Ship.Loaded.Full)
					{
						// Not a ship loaded here; nothing to show.
						return true;
					}
					break;
				case "S":
				case "C":
					if ((f[0] == "S" && f.Length != 4) || (f[0] == "C" && f.Length != 5))
					{
						return true;
					}
					if (!DataHandler.mapCOs.TryGetValue(f[1], out parent) || parent == null || parent.bDestroyed)
					{
						RequestItem(f[1]);
						return false;
					}
					if ((f[0] == "S" && parent.compSlots == null) || (f[0] == "C" && parent.objContainer == null))
					{
						return false;
					}
					break;
				default:
					return true;
			}
			List<CondOwner> unit = new List<CondOwner>();
			bool complete = true;
			foreach (string entry in entriesField.Split(','))
			{
				CondOwner co = Resolve(entry);
				if (co != null)
				{
					unit.Add(co);
				}
				else
				{
					complete = false;
				}
			}
			if (!complete)
			{
				return false;
			}
			if (unit.Count == 0)
			{
				return true;
			}
			CondOwner head = unit[0];
			bool rebuilt = RebuildStack(head, unit);
			if (!rebuilt && AtLocation(head, f, ship, parent))
			{
				return true;
			}
			Unstack(head);
			head.RemoveFromCurrentHome();
			switch (f[0])
			{
				case "F":
				{
					float x = float.Parse(f[2], NumberStyles.Float, Inv);
					float y = float.Parse(f[3], NumberStyles.Float, Inv);
					head.tf.position = new Vector3(x, y, head.tf.position.z);
					head.Item.fLastRotation = float.Parse(f[4], NumberStyles.Float, Inv);
					ship.AddCO(head, bTiles: true);
					break;
				}
				case "S":
					if (!SlotInto(parent, f[2], head))
					{
						Plugin.Log.LogWarning("Could not slot " + head.strName + " into " + parent.strName + ":" + f[2]);
					}
					break;
				case "C":
				{
					Container c = parent.objContainer;
					bool allow = c.bAllowStacking;
					c.bAllowStacking = false;
					c.AddCOSimple(head, new PairXY(int.Parse(f[2], Inv), int.Parse(f[3], Inv)));
					c.bAllowStacking = allow;
					break;
				}
			}
			return true;
		}

		// One entry of an ITEM message to the local object; null if we don't have it (NEED is sent).
		private static CondOwner Resolve(string entry)
		{
			if (entry.Length < 2)
			{
				return null;
			}
			char kind = entry[0];
			string body = entry.Substring(1);
			if (kind == 'S')
			{
				JsonCondOwnerSave save = JsonMapper.ToObject<JsonCondOwnerSave>(Encoding.UTF8.GetString(Convert.FromBase64String(body)));
				return GetOrCreate(save);
			}
			string id = body;
			string conds = null;
			if (kind == 'T')
			{
				int tilde = body.IndexOf('~');
				if (tilde < 0)
				{
					return null;
				}
				id = body.Substring(0, tilde);
				conds = body.Substring(tilde + 1);
			}
			CondOwner co;
			if (!DataHandler.mapCOs.TryGetValue(id, out co) || co == null || co.bDestroyed)
			{
				RequestItem(id);
				return null;
			}
			if (conds != null)
			{
				TargetConds.Clear();
				foreach (string pair in conds.Split(new char[] { '^' }, StringSplitOptions.RemoveEmptyEntries))
				{
					int eq = pair.IndexOf('=');
					double value;
					if (eq > 0 && double.TryParse(pair.Substring(eq + 1), NumberStyles.Float, Inv, out value))
					{
						TargetConds[pair.Substring(0, eq)] = value;
					}
				}
				SetConds(co);
			}
			return co;
		}

		// Like the inventory screen (bAuto false: this exact slot). If the slot holds something else here, that's stale:
		// it goes on the floor next to the owner, and its real place arrives in its own update (or a resync).
		private static bool SlotInto(CondOwner parent, string slotName, CondOwner head)
		{
			Slots slots = parent.compSlots;
			if (slots.SlotItem(slotName, head, bAuto: false))
			{
				return true;
			}
			Slot slot = slots.GetSlot(slotName);
			if (slot == null || slot.aCOs == null)
			{
				return false;
			}
			CondOwner root = parent.RootParent() ?? parent;
			bool evicted = false;
			foreach (CondOwner occupant in (CondOwner[])slot.aCOs.Clone())
			{
				if (occupant == null || occupant == head)
				{
					continue;
				}
				slot.compSlots.UnSlotItem(slot.strName, occupant, bForce: true);
				if (root.DropCO(occupant, bAllowLocked: false) != null)
				{
					Plugin.Log.LogWarning("Could not drop " + occupant.strName + " to free " + parent.strName + ":" + slotName);
				}
				evicted = true;
			}
			return evicted && slots.SlotItem(slotName, head, bAuto: false);
		}

		// Ask the other side for an item (or a parent: a bag, a suit) we don't have.
		private static void RequestItem(string id)
		{
			float last;
			float now = Time.unscaledTime;
			if (NeedSent.TryGetValue(id, out last) && now - last < 2f)
			{
				return;
			}
			NeedSent[id] = now;
			Plugin.Net.Send("NEED " + id);
		}

		private static CondOwner GetOrCreate(JsonCondOwnerSave save)
		{
			if (save == null || save.strID == null)
			{
				return null;
			}
			CondOwner co;
			if (DataHandler.mapCOs.TryGetValue(save.strID, out co) && co != null && !co.bDestroyed)
			{
				if (save.strCODef == null || co.strCODef == save.strCODef)
				{
					ApplyConds(co, save);
					return co;
				}
				// Turned into another kind on the other side (CondOwner.ModeSwitch): ours is the old kind, with the old
				// kind's actions. Rebuild it from the save.
				Plugin.Log.LogInfo("Item " + save.strID + " changed from " + co.strCODef + " to " + save.strCODef);
				Unstack(co);
				co.RemoveFromCurrentHome(bForce: true);
				co.Destroy();
			}
			if (save.strCODef == null)
			{
				return null;
			}
			// Same as the game spawning a saved object (Ship.CreatePart): register the save, then build from it.
			DataHandler.dictCOSaves[save.strID] = save;
			co = DataHandler.GetCondOwner(save.strCODef, save.strID, null, bLoot: false, null, save);
			if (co != null)
			{
				co.strID = save.strID;
			}
			return co;
		}

		// Existing item, full save received: bring its conditions to the save's values.
		private static void ApplyConds(CondOwner co, JsonCondOwnerSave save)
		{
			if (co.mapConds == null || save.aConds == null)
			{
				return;
			}
			TargetConds.Clear();
			foreach (string entry in save.aConds)
			{
				if (entry == "DEFAULT")
				{
					JsonCondOwner def = DataHandler.GetCondOwnerDef(co.strCODef);
					if (def != null && def.aStartingConds != null)
					{
						foreach (string s in def.aStartingConds)
						{
							ParseCond(s);
						}
					}
				}
				else
				{
					ParseCond(entry);
				}
			}
			SetConds(co);
		}

		// Make co's conditions exactly TargetConds (except its ID condition), cond rules frozen (like CondSync).
		private static void SetConds(CondOwner co)
		{
			if (co.mapConds == null)
			{
				return;
			}
			string idCond = (co.objCondID != null) ? co.objCondID.strName : null;
			ToZero.Clear();
			foreach (string name in co.mapConds.Keys)
			{
				if (!TargetConds.ContainsKey(name) && name != idCond)
				{
					ToZero.Add(name);
				}
			}
			bool freeze = co.bFreezeCondRules;
			co.bFreezeCondRules = true;
			try
			{
				foreach (KeyValuePair<string, double> kv in TargetConds)
				{
					if (kv.Key != idCond && co.GetCondAmount(kv.Key) != kv.Value)
					{
						co.SetCondAmount(kv.Key, kv.Value);
					}
				}
				foreach (string name in ToZero)
				{
					co.ZeroCondAmount(name);
				}
			}
			finally
			{
				co.bFreezeCondRules = freeze;
			}
		}

		// "Name=1.0x5" (coefficient x value) or "Name=5".
		private static void ParseCond(string s)
		{
			int eq = s.IndexOf('=');
			if (eq <= 0)
			{
				return;
			}
			string name = s.Substring(0, eq);
			string rest = s.Substring(eq + 1);
			int x = rest.IndexOf('x');
			double value;
			if (x >= 0)
			{
				double a;
				double b;
				if (!double.TryParse(rest.Substring(0, x), NumberStyles.Float, Inv, out a) || !double.TryParse(rest.Substring(x + 1), NumberStyles.Float, Inv, out b))
				{
					return;
				}
				value = a * b;
			}
			else if (!double.TryParse(rest, NumberStyles.Float, Inv, out value))
			{
				return;
			}
			TargetConds[name] = value;
		}

		// Make head's stack exactly unit[1..]. Returns true if it had to change (the head then needs re-placing).
		private static bool RebuildStack(CondOwner head, List<CondOwner> unit)
		{
			bool same = head.coStackHead == null && head.aStack.Count == unit.Count - 1;
			if (same)
			{
				for (int i = 1; i < unit.Count; i++)
				{
					if (unit[i].coStackHead != head)
					{
						same = false;
						break;
					}
				}
			}
			if (same)
			{
				return false;
			}
			// The game's own rebuild after loading (CondOwner.PostGameLoad): take members out of wherever they are,
			// then StackFromList with the head last.
			List<CondOwner> list = new List<CondOwner>();
			for (int i = 1; i < unit.Count; i++)
			{
				CondOwner m = unit[i];
				Unstack(m);
				m.RemoveFromCurrentHome();
				list.Add(m);
			}
			Unstack(head);
			head.RemoveFromCurrentHome();
			list.Add(head);
			CondOwner.StackFromList(list);
			return true;
		}

		// Take co out of the stack it belongs to, as a member; the old head stays where it is.
		private static void Unstack(CondOwner co)
		{
			CondOwner h = co.coStackHead;
			if (h == null)
			{
				return;
			}
			h.aStack.Remove(co);
			co.coStackHead = null;
			co.Visible = true;
			h.UpdateAppearance();
			co.UpdateAppearance();
		}

		private static bool AtLocation(CondOwner co, string[] f, Ship ship, CondOwner parent)
		{
			switch (f[0])
			{
				case "F":
				{
					if (co.objCOParent != null || co.ship != ship || ship.GetCOByID(co.strID) != co)
					{
						return false;
					}
					float x = float.Parse(f[2], NumberStyles.Float, Inv);
					float y = float.Parse(f[3], NumberStyles.Float, Inv);
					float rot = float.Parse(f[4], NumberStyles.Float, Inv);
					return Mathf.Abs(co.tf.position.x - x) < 0.01f && Mathf.Abs(co.tf.position.y - y) < 0.01f && Mathf.Abs(Mathf.DeltaAngle(co.Item.fLastRotation, rot)) < 0.5f;
				}
				case "S":
					return co.objCOParent == parent && co.slotNow != null && co.slotNow.strName == f[2];
				case "C":
					return co.objCOParent == parent && co.slotNow == null && co.pairInventoryXY.x == int.Parse(f[2], Inv) && co.pairInventoryXY.y == int.Parse(f[3], Inv);
				default:
					return false;
			}
		}
	}
}
