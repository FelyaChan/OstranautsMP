using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using LitJson;
using UnityEngine;

namespace OstranautsMP
{
	// Binds the client to one crew member. The client picks someone from the host's crew (not the host's main
	// character); the host confirms and from then on ignores client actions for anyone else. On the client,
	// selecting another person is blocked and GetSelectedCrew always returns the claimed character, so clicks, WASD,
	// the radial menu, the crew bar and visibility all work from that character.
	//
	// The client's profile (Profile) comes with the claim: the host makes the crew member that character (looks,
	// name) and marks them as the profile's; a crew member another profile has is refused. The client applies the
	// same change when the host confirms.
	//
	// A new body: instead of a crew member, the client can have the host make a new one from its profile (looks, name,
	// skills) and no money, straight into the host's crew at a crew spawn point of the host's ship. One per profile:
	// while the profile has a crew member here, the client picks them; if they're somewhere the host isn't (a ship
	// not loaded on the host), the host says where. The host sends the new person over (PersonSync) and confirms as
	// usual.
	//
	// Protocol: client -> host CLAIM <id>|<profile ID>|<base64 JSON Mirror.Look, or empty>
	//                          NEWBODY <profile ID>|<base64 JSON Profile.Data>;
	//           host -> client CLAIMED <id as picked>|<id now> or CLAIM_DENIED <reason>.
	public static class CrewClaim
	{
		private const int WindowId = 0x0B4F2;

		// Client.
		private static string _mine;

		private static bool _confirmed;

		private static string _status;

		private static bool _wasInGame;

		private static Rect _rect = new Rect(0f, 0f, 260f, 10f);

		// Client: the profile sent with the claim, applied when the host confirms.
		private static Mirror.Look _claimLook;

		// Host.
		private static string _claimed;

		public static void Patch(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(CrewSim), "SelectCO"), prefix: new HarmonyMethod(typeof(CrewClaim), nameof(SelectCOPrefix)));
			harmony.Patch(AccessTools.Method(typeof(CrewSim), "GetSelectedCrew"), postfix: new HarmonyMethod(typeof(CrewClaim), nameof(GetSelectedCrewPostfix)));
			harmony.Patch(AccessTools.Method(typeof(CrewSim), "GetBracketTarget"), postfix: new HarmonyMethod(typeof(CrewClaim), nameof(GetBracketTargetPostfix)));
			harmony.Patch(AccessTools.Method(typeof(CondOwner), "AIIssueOrder"), prefix: new HarmonyMethod(typeof(CrewClaim), nameof(FreeWillPrefix)), finalizer: new HarmonyMethod(typeof(CrewClaim), nameof(FreeWillFinalizer)));
			harmony.Patch(AccessTools.Method(typeof(CrewSim), "SetBracketTarget"), prefix: new HarmonyMethod(typeof(CrewClaim), nameof(SetBracketTargetPrefix)));
			harmony.Patch(AccessTools.Method(typeof(CrewSim), "GetMouseOverCO"), postfix: new HarmonyMethod(typeof(CrewClaim), nameof(GetMouseOverCOPostfix)));
			harmony.Patch(AccessTools.Method(typeof(CrewSim), "MouseHandler"), prefix: new HarmonyMethod(typeof(CrewClaim), nameof(MouseHandlerPrefix)), finalizer: new HarmonyMethod(typeof(CrewClaim), nameof(MouseHandlerFinalizer)));
			harmony.Patch(AccessTools.Method(typeof(CrewSim), "Walk"), prefix: new HarmonyMethod(typeof(CrewClaim), nameof(WalkPrefix)));
			harmony.Patch(AccessTools.Method(typeof(CrewSim), "CycleCrew"), prefix: new HarmonyMethod(typeof(CrewClaim), nameof(CycleCrewPrefix)));
			harmony.Patch(AccessTools.Method(typeof(CondOwner), "QueueInteraction"), prefix: new HarmonyMethod(typeof(CrewClaim), nameof(NoClientUIOnHost)));
			harmony.Patch(AccessTools.Method(typeof(CrewSim), "RaiseUI"), prefix: new HarmonyMethod(typeof(CrewClaim), nameof(NotWhileRelaying)));
			harmony.Patch(AccessTools.Method(typeof(Ostranauts.InputControl.CommandInventory), "ToggleInventory"), prefix: new HarmonyMethod(typeof(CrewClaim), nameof(NotWhileRelaying)));
			Death.Patch(harmony);
		}

		// Host: the client's character's actions don't open windows here (they open on the client, who does them too).
		// The game raises an action's window for whoever does it, and for a crew member who isn't selected it first
		// switches the host to them (CrewSim.RaiseUI -> CycleCrew); when the action ends it closes the host's window.
		// Unless the host has selected that character themselves.
		private static void NoClientUIOnHost(CondOwner __instance, Interaction objInteraction)
		{
			if (objInteraction == null || Plugin.Net.CurrentMode != NetSession.Mode.Host || !Plugin.Net.Connected)
			{
				return;
			}
			CondOwner client = HostClaimed;
			if (client == null || __instance != client || (!OrderSync.HostRelaying && CrewSim.GetSelectedCrew() == client))
			{
				return;
			}
			objInteraction.strRaiseUI = null;
			objInteraction.strRaiseUIThem = null;
		}

		// Host: nothing opens while a client order is being carried out (the client's character is selected then).
		private static bool NotWhileRelaying()
		{
			return !OrderSync.HostRelaying;
		}

		// No switching crew on the client (Caps Lock, the crew bar cards, the game's own calls): switching takes over
		// what the next person is doing, e.g. the nav station the host is flying from.
		private static bool CycleCrewPrefix()
		{
			if (Plugin.Net.CurrentMode == NetSession.Mode.Client && Plugin.Net.Connected && WorldSync.InGame)
			{
				return false;
			}
			return true;
		}

		// Client: the claimed character, once confirmed and in game.
		internal static CondOwner ClientMine => Locked ? Mine : null;

		// Client: the claimed character's ID, and whether the host has confirmed it.
		internal static string MineId => _mine;

		internal static bool Confirmed => _confirmed;

		// Client: our character died (Death); no character until a new body.
		internal static void ClientLost()
		{
			_mine = null;
			_confirmed = false;
			_status = null;
			_claimLook = null;
		}

		// Host: the client's character died (Death); their orders are no longer taken.
		internal static void HostCharacterDied(string id)
		{
			if (_claimed != null && _claimed == id)
			{
				_claimed = null;
			}
		}

		// Host: the profile has a living character, here or on a ship that isn't loaded.
		internal static bool HasLivingCharacter(string profileId)
		{
			foreach (CondOwner co in DataHandler.mapCOs.Values)
			{
				if (co != null && !co.bDestroyed && co.bAlive && Profile.OwnerOf(co) == profileId)
				{
					return true;
				}
			}
			string shipName;
			return OwnedAway(profileId, out shipName) != null;
		}

		// The client's character on this machine (host: the claimed one; client: our own).
		private static bool IsClientCharacter(CondOwner co)
		{
			if (co == null || co.strID == null || !Plugin.Net.Connected)
			{
				return false;
			}
			if (Plugin.Net.CurrentMode == NetSession.Mode.Host)
			{
				return _claimed != null && co.strID == _claimed;
			}
			return Locked && co.strID == _mine;
		}

		// A crew member without IsPlayer may refuse a player order (AIIssueOrder: FreeWillPenalty, SeekSocialDeny),
		// and host and client can roll that differently. The client's character takes orders like the host's own:
		// IsPlayer is set for the duration of the call on both machines.
		private static void FreeWillPrefix(CondOwner __instance, bool bPlayerOrdered, out bool __state)
		{
			__state = false;
			if (!bPlayerOrdered || !IsClientCharacter(__instance) || __instance.HasCond("IsPlayer"))
			{
				return;
			}
			bool freeze = __instance.bFreezeCondRules;
			__instance.bFreezeCondRules = true;
			__instance.AddCondAmount("IsPlayer", 1.0);
			__instance.bFreezeCondRules = freeze;
			__state = true;
		}

		private static Exception FreeWillFinalizer(Exception __exception, CondOwner __instance, bool __state)
		{
			if (__state)
			{
				bool freeze = __instance.bFreezeCondRules;
				__instance.bFreezeCondRules = true;
				__instance.ZeroCondAmount("IsPlayer");
				__instance.bFreezeCondRules = freeze;
			}
			return __exception;
		}

		// Host: the character the client plays, if any.
		internal static CondOwner HostClaimed
		{
			get
			{
				CondOwner co;
				if (_claimed != null && DataHandler.mapCOs.TryGetValue(_claimed, out co) && co != null && !co.bDestroyed)
				{
					return co;
				}
				return null;
			}
		}

		private static CondOwner Mine
		{
			get
			{
				CondOwner co;
				if (_mine != null && DataHandler.mapCOs.TryGetValue(_mine, out co) && co != null && !co.bDestroyed)
				{
					return co;
				}
				return null;
			}
		}

		private static bool Locked => _confirmed && Plugin.Net.CurrentMode == NetSession.Mode.Client && Plugin.Net.Connected && WorldSync.InGame && Mine != null;

		// A character was renamed (its ID is its name): keep pointing at it.
		internal static void Renamed(string oldId, string newId)
		{
			if (_claimed == oldId)
			{
				_claimed = newId;
			}
			if (_mine == oldId)
			{
				_mine = newId;
			}
		}

		internal static string MineName
		{
			get
			{
				CondOwner co = _confirmed ? Mine : null;
				return (co != null) ? co.strName : null;
			}
		}

		// Other people can't be selected; objects still can.
		private static bool SelectCOPrefix(CondOwner co, bool bUnselect)
		{
			if (bUnselect || co == null || !co.HasCond("IsHuman"))
			{
				return true;
			}
			// Dead: only watching, nobody to select.
			if (Death.ClientDead)
			{
				return false;
			}
			if (!Locked)
			{
				return true;
			}
			return co == Mine;
		}

		// Clicking another person selects them and unselects everyone else (SetBracketTarget). The selection of them is
		// blocked above, so without this our own character would just end up unselected.
		private static bool SetBracketTargetPrefix(string strID)
		{
			if (!Locked || string.IsNullOrEmpty(strID))
			{
				return true;
			}
			CondOwner co;
			if (!DataHandler.mapCOs.TryGetValue(strID, out co) || co == null || !co.HasCond("IsHuman"))
			{
				return true;
			}
			return co == Mine;
		}

		// Inside MouseHandler, and whether the host's main character was under the mouse there.
		private static bool _inMouseHandler;

		private static bool _hidHost;

		private static void MouseHandlerPrefix()
		{
			_inMouseHandler = true;
			_hidHost = false;
		}

		private static Exception MouseHandlerFinalizer(Exception __exception)
		{
			_inMouseHandler = false;
			_hidHost = false;
			return __exception;
		}

		// The client's left clicks don't see the host's main character (no selecting them, no walking to the floor under
		// them). A right click still opens their menu, so the players can talk and hand things over.
		private static void GetMouseOverCOPostfix(List<CondOwner> __result)
		{
			CondOwner host = CrewSim.coPlayer;
			if (_inMouseHandler && CrewSim.objInstance != null && CrewSim.objInstance.GetMouseButtonUp(1))
			{
				return;
			}
			if (Locked && host != null && __result != null && __result.RemoveAll((CondOwner c) => c == host) > 0 && _inMouseHandler)
			{
				_hidHost = true;
			}
		}

		// A click on the host's character would otherwise fall through to the floor under them and walk there.
		private static bool WalkPrefix()
		{
			return !(_inMouseHandler && _hidHost);
		}

		// Nothing selected: a click on the floor (CrewSim.Walk) orders the bracket target, else coPlayer (the host's
		// character here, whose orders are blocked). Our character instead.
		private static void GetBracketTargetPostfix(ref CondOwner __result)
		{
			if (__result == null && Locked)
			{
				__result = Mine;
			}
		}

		private static void GetSelectedCrewPostfix(ref CondOwner __result)
		{
			if (Locked)
			{
				__result = Mine;
			}
		}

		// Host: may the client act as this character?
		internal static bool AllowedForClient(CondOwner us)
		{
			return us != null && us != CrewSim.coPlayer && _claimed != null && us.strID == _claimed;
		}

		public static void Update()
		{
			Death.Update(Time.unscaledTime);
			if (!Plugin.Net.Connected)
			{
				_claimed = null;
				_mine = null;
				_confirmed = false;
				_status = null;
				_wasInGame = false;
				return;
			}
			if (Plugin.Net.CurrentMode != NetSession.Mode.Client)
			{
				return;
			}
			bool inGame = WorldSync.InGame;
			// After every load, look at our character again.
			if (inGame && !_wasInGame && _confirmed)
			{
				FocusMine();
			}
			_wasInGame = inGame;
		}

		private static void FocusMine()
		{
			CondOwner co = Mine;
			if (co == null || CrewSim.objInstance == null)
			{
				return;
			}
			// Selected, so clicks order them (and the others are unselected).
			CrewSim.objInstance.SetBracketTarget(co.strID, bUpdateOnly: false);
			CrewSim.objInstance.CamCenter(co);
		}

		private static List<CondOwner> Candidates()
		{
			List<CondOwner> list = new List<CondOwner>();
			CondOwner player = CrewSim.coPlayer;
			List<Ship> ships = CrewSim.GetAllLoadedShips();
			if (player == null || player.Company == null || ships == null)
			{
				return list;
			}
			foreach (Ship ship in ships)
			{
				if (ship == null || ship.bDestroyed)
				{
					continue;
				}
				foreach (CondOwner co in ship.GetPeople(bAllowDocked: false))
				{
					if (co != null && !co.bDestroyed && co.bAlive && co != player && co.Company == player.Company && co.HasCond("IsHuman") && !list.Contains(co))
					{
						list.Add(co);
					}
				}
			}
			return list;
		}

		// Client: the picker, shown in game until a character is confirmed.
		public static void OnGUI()
		{
			if (Death.ClientDead && Plugin.Net.CurrentMode == NetSession.Mode.Client && Plugin.Net.Connected && WorldSync.InGame && WorldSync.ClientFollowsHost)
			{
				Death.OnGUI();
				return;
			}
			if (_confirmed || Plugin.Net.CurrentMode != NetSession.Mode.Client || !Plugin.Net.Connected || !WorldSync.InGame || !WorldSync.ClientFollowsHost)
			{
				return;
			}
			if (_rect.x == 0f)
			{
				_rect.x = (Screen.width - _rect.width) / 2f;
				_rect.y = 60f;
			}
			_rect = GUILayout.Window(WindowId, _rect, DrawPicker, "CHOOSE YOUR CHARACTER", MenuLook.Window);
			Chat.AddBlocking(_rect);
		}

		private static void DrawPicker(int id)
		{
			GUISkin prev = MenuLook.Use();
			try
			{
				DrawPickerContent(id);
			}
			finally
			{
				MenuLook.Restore(prev);
			}
		}

		private static void DrawPickerContent(int id)
		{
			List<CondOwner> crew = Candidates();
			if (crew.Count == 0)
			{
				GUILayout.Label("No crew members besides the host's character.");
			}
			string me = (Profile.Current != null) ? Profile.Current.Id : null;
			bool haveMine = false;
			foreach (CondOwner co in crew)
			{
				string owner = Profile.OwnerOf(co);
				if (owner != null && owner == me)
				{
					haveMine = true;
				}
				bool taken = owner != null && owner != me;
				GUI.enabled = !taken;
				if (GUILayout.Button(co.strName + (taken ? " (another player's)" : ((owner != null) ? " (yours)" : ""))))
				{
					_mine = co.strID;
					_status = "Waiting for host...";
					Mirror.Look look = Profile.ToLook();
					_claimLook = look;
					Plugin.Net.Send("CLAIM " + co.strID + "|" + (me ?? "") + "|" + ((look != null) ? Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonMapper.ToJson(look))) : ""));
				}
				GUI.enabled = true;
			}
			if (!haveMine && Profile.HasLooks && me != null)
			{
				GUILayout.Space(8f);
				if (GUILayout.Button("New body: " + Profile.Current.Name))
				{
					_mine = null;
					_claimLook = null;
					_status = "Waiting for host...";
					Plugin.Net.Send("NEWBODY " + me + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonMapper.ToJson(Profile.Current))));
				}
			}
			if (_status != null)
			{
				GUILayout.Label(_status);
			}
			GUI.DragWindow();
		}

		// Main thread. Returns false if the message isn't ours.
		public static bool OnMessage(string cmd, string arg)
		{
			switch (cmd)
			{
				case "CLAIM":
					if (Plugin.Net.CurrentMode == NetSession.Mode.Host)
					{
						HostClaim(arg);
					}
					return true;
				case "NEWBODY":
					if (Plugin.Net.CurrentMode == NetSession.Mode.Host)
					{
						HostNewBody(arg);
					}
					return true;
				case "CLAIMED":
					if (Plugin.Net.CurrentMode == NetSession.Mode.Client)
					{
						string[] f = arg.Split('|');
						CondOwner picked;
						if (_claimLook != null && DataHandler.mapCOs.TryGetValue(f[0], out picked) && picked != null && !picked.bDestroyed)
						{
							// What the host did to them: the same looks, and the name if it was free there.
							if (f.Length > 1)
							{
								_claimLook.NewId = f[1];
							}
							Mirror.Apply(picked, _claimLook);
							if (Profile.Current != null)
							{
								Profile.SetOwner(picked, Profile.Current.Id);
							}
						}
						_claimLook = null;
						_mine = (f.Length > 1) ? f[1] : f[0];
						_confirmed = true;
						_status = null;
						Death.ClientNewBody();
						FocusMine();
						Plugin.Log.LogInfo("Playing as " + MineName);
					}
					return true;
				case "CLAIM_DENIED":
					if (Plugin.Net.CurrentMode == NetSession.Mode.Client)
					{
						_mine = null;
						_status = "Denied: " + arg;
						Death.ClientDenied(arg);
					}
					return true;
				default:
					return Death.OnMessage(cmd, arg);
			}
		}

		private static void HostClaim(string arg)
		{
			string[] f = arg.Split('|');
			string id = f[0];
			string profileId = (f.Length > 1 && f[1].Length > 0) ? f[1] : null;
			Mirror.Look look = null;
			if (f.Length > 2 && f[2].Length > 0)
			{
				try
				{
					look = JsonMapper.ToObject<Mirror.Look>(Encoding.UTF8.GetString(Convert.FromBase64String(f[2])));
				}
				catch (Exception e)
				{
					Plugin.Log.LogWarning("Bad profile with the claim: " + e.Message);
				}
			}
			CondOwner co;
			CondOwner player = CrewSim.coPlayer;
			if (!WorldSync.InGame || !DataHandler.mapCOs.TryGetValue(id, out co) || co == null || co.bDestroyed)
			{
				Plugin.Net.Send("CLAIM_DENIED character not found on host");
				return;
			}
			if (co == player)
			{
				Plugin.Net.Send("CLAIM_DENIED that is the host's character");
				return;
			}
			if (player == null || co.Company != player.Company || !co.HasCond("IsHuman"))
			{
				Plugin.Net.Send("CLAIM_DENIED not in the host's crew");
				return;
			}
			string owner = Profile.OwnerOf(co);
			if (owner != null && owner != profileId)
			{
				Plugin.Net.Send("CLAIM_DENIED " + co.strName + " is another player's character");
				return;
			}
			if (profileId != null)
			{
				Profile.SetOwner(co, profileId);
			}
			if (look != null)
			{
				Mirror.Apply(co, look);
			}
			GivePda(co);
			_claimed = co.strID;
			Budget.MarkClientCharacter(co);
			Plugin.Net.Send("CLAIMED " + id + "|" + co.strID);
			Plugin.Log.LogInfo("Client plays as " + co.strName);
		}

		// Host: the client's character gets a working PDA on the wrist, as a new player's (the game's
		// ItmWristPDA01NewPlayer: a PDA with a random cartridge), so their vizor works. Crew members and new bodies
		// come with a locked one (ItmWristPDA01Locked), which the vizor doesn't take: it goes into their inventory
		// (or away if there's no room). Nothing changes if they already have a working one.
		private static void GivePda(CondOwner co)
		{
			try
			{
				CondTrigger working = DataHandler.GetCondTrigger("TIsWristSlotted");
				List<CondOwner> have = co.GetCOs(bAllowLocked: false, working);
				if (working == null || co.compSlots == null || (have != null && have.Count > 0))
				{
					return;
				}
				Loot loot = DataHandler.GetLoot("ItmWristPDA01NewPlayer");
				List<CondOwner> made = (loot != null) ? loot.GetCOLoot(co, bSuppressOverride: false) : null;
				if (made == null || made.Count == 0)
				{
					Plugin.Log.LogWarning("PDA: no ItmWristPDA01NewPlayer for " + co.strName);
					return;
				}
				// Whatever is on the wrist off it (a locked or broken PDA: a random person's can be on without IsWristSlotted).
				// Into the inventory only after the new one is on: the wrist is a hold slot, so AddCO would put it back there.
				List<CondOwner> off = new List<CondOwner>();
				Slot wrist = co.compSlots.GetSlot("wristL");
				if (wrist != null && wrist.aCOs != null)
				{
					foreach (CondOwner old in new List<CondOwner>(wrist.aCOs))
					{
						if (old == null)
						{
							continue;
						}
						Plugin.Log.LogInfo("PDA: took " + old.strCODef + " off the wrist of " + co.strName);
						co.compSlots.UnSlotItem(old, bForce: true);
						off.Add(old);
					}
				}
				CondTrigger cart = DataHandler.GetCondTrigger("TIsPDACartridge");
				CondOwner pda = null;
				foreach (CondOwner item in made)
				{
					if (item != null && item.mapSlotEffects != null && item.mapSlotEffects.Count > 0 && pda == null)
					{
						foreach (string key in item.mapSlotEffects.Keys)
						{
							if (co.compSlots.SlotItem(key, item))
							{
								pda = item;
								break;
							}
						}
						if (pda != null)
						{
							continue;
						}
					}
					if (item == null)
					{
						continue;
					}
					// A cartridge the loot gave separately goes into the PDA; anything else into the inventory.
					CondOwner into = (pda != null && cart != null && cart.Triggered(item)) ? pda : co;
					CondOwner rest = into.AddCO(item, bEquip: false, bOverflow: true, bIgnoreLocks: true);
					if (rest != null)
					{
						rest.Destroy();
					}
				}
				foreach (CondOwner old in off)
				{
					CondOwner left = co.AddCO(old, bEquip: false, bOverflow: true, bIgnoreLocks: true);
					if (left != null)
					{
						left.Destroy();
					}
				}
				Plugin.Log.LogInfo("PDA: " + co.strName + (pda != null ? " got a working PDA" : " got no PDA slotted"));
			}
			catch (Exception e)
			{
				Plugin.Log.LogError("PDA for " + co.strName + ": " + e);
			}
		}

		// Host: a new crew member made from the client's profile.
		private static void HostNewBody(string arg)
		{
			int bar = arg.IndexOf('|');
			string profileId = (bar > 0) ? arg.Substring(0, bar) : null;
			Profile.Data data = null;
			if (bar > 0)
			{
				try
				{
					data = JsonMapper.ToObject<Profile.Data>(Encoding.UTF8.GetString(Convert.FromBase64String(arg.Substring(bar + 1))));
				}
				catch (Exception e)
				{
					Plugin.Log.LogWarning("Bad profile with NEWBODY: " + e.Message);
				}
			}
			CondOwner player = CrewSim.coPlayer;
			if (!WorldSync.InGame || player == null || player.Company == null || CrewSim.objInstance == null)
			{
				Plugin.Net.Send("CLAIM_DENIED the host isn't in the game");
				return;
			}
			if (string.IsNullOrEmpty(profileId) || data == null || data.Face == null || data.Face.Length == 0)
			{
				Plugin.Net.Send("CLAIM_DENIED no character in the profile");
				return;
			}
			if (Apart.HostActive)
			{
				Plugin.Net.Send("CLAIM_DENIED the host is away from your place");
				return;
			}
			foreach (CondOwner other in DataHandler.mapCOs.Values)
			{
				// A dead body of theirs doesn't count: they get a new one.
				if (other != null && !other.bDestroyed && other.bAlive && Profile.OwnerOf(other) == profileId)
				{
					Plugin.Net.Send("CLAIM_DENIED you already have " + other.strName + " here");
					return;
				}
			}
			string shipName;
			string away = OwnedAway(profileId, out shipName);
			if (away != null)
			{
				Plugin.Net.Send("CLAIM_DENIED your character " + away + " is on " + shipName);
				return;
			}
			// After a death (Death): only in port, with a name nobody has, and with the last body's training.
			string lastBody;
			Dictionary<string, string> lastDeath = Death.LastDeath(profileId, out lastBody);
			if (lastDeath != null)
			{
				// The client may not know yet (asked from the pick window after a reconnect): the death window.
				if (lastBody != null)
				{
					Plugin.Net.Send("DIED " + lastBody);
				}
				if (!Death.InPort())
				{
					Plugin.Net.Send("CLAIM_DENIED the host's ship isn't in port");
					return;
				}
				string name = (data.Name ?? "").Trim();
				string why = Mirror.NameProblem(name, null);
				if (why != null && !DataHandler.mapCOs.ContainsKey(name))
				{
					Plugin.Net.Send("CLAIM_DENIED name: " + why);
					return;
				}
				if (!Death.NameFree(name))
				{
					string free = Death.FreeName(Death.NextName(name));
					Plugin.Net.Send("NAMETAKEN " + free);
					Plugin.Net.Send("CLAIM_DENIED the name " + name + " is taken, try " + free);
					return;
				}
				data.Name = name;
			}
			// The game's own new crew member (the console's makecrew): a random person at a crew spawn tile of the
			// host's ship, with their own random skills, traits and things.
			CondOwner co = CrewSim.objInstance.GetRandomCrew();
			if (co == null)
			{
				Plugin.Net.Send("CLAIM_DENIED the host couldn't make a new body");
				return;
			}
			JoinCrew(co, player);
			SetSkills(co, data.Skills);
			// The profile's character, not a random person: none of the game's random training, only the last
			// body's (after a death).
			Death.ResetTraining(co);
			if (lastDeath != null)
			{
				Death.ApplyTraining(co, lastDeath);
			}
			// Starts with an empty wallet (the game gave them random money); the host gives money and sets a salary in
			// the BUDGET app.
			co.ZeroCondAmount(Ledger.CURRENCY);
			Profile.SetOwner(co, profileId);
			GivePda(co);
			Mirror.Look look = new Mirror.Look();
			look.NewId = data.Name;
			look.Face = data.Face;
			look.Body = data.Body;
			look.Skin = data.Skin;
			look.Gender = data.Gender;
			look.Attract = data.Attract;
			Mirror.Apply(co, look);
			_claimed = co.strID;
			Budget.MarkClientCharacter(co);
			PersonSync.HostSend(co);
			Plugin.Net.Send("CLAIMED " + co.strID + "|" + co.strID);
			Plugin.Log.LogInfo("New body for the client: " + co.strName);
		}

		// Whether a person of an unloaded ship is alive: their live object if the game keeps one (a shallow ship),
		// else their saved data in the ship's; alive if neither says.
		private static bool AliveAway(Ship ship, string id)
		{
			CondOwner co;
			if (id != null && DataHandler.mapCOs.TryGetValue(id, out co) && co != null && !co.bDestroyed)
			{
				return co.bAlive;
			}
			if (ship.json.aCOs != null)
			{
				foreach (JsonCondOwnerSave s in ship.json.aCOs)
				{
					if (s != null && s.strID == id)
					{
						return s.bAlive;
					}
				}
			}
			return true;
		}

		// A crew member of this profile on a ship that isn't loaded here (a station the host left): the ship's data keeps
		// them with their GUI property maps, the profile's mark among them. Their ID, or null.
		private static string OwnedAway(string profileId, out string shipName)
		{
			shipName = null;
			if (CrewSim.system == null || CrewSim.system.dictShips == null)
			{
				return null;
			}
			foreach (Ship ship in CrewSim.system.dictShips.Values)
			{
				if (ship == null || ship.bDestroyed || ship.LoadState >= Ship.Loaded.Full || ship.json == null || ship.json.aCrew == null)
				{
					continue;
				}
				foreach (JsonItem it in ship.json.aCrew)
				{
					if (it == null || it.aGPMSettings == null)
					{
						continue;
					}
					foreach (JsonGUIPropMap g in it.aGPMSettings)
					{
						if (g == null || g.strName != Profile.Gpm || g.dictGUIPropMap == null)
						{
							continue;
						}
						for (int i = 0; i + 1 < g.dictGUIPropMap.Length; i += 2)
						{
							if (g.dictGUIPropMap[i] == "id" && g.dictGUIPropMap[i + 1] == profileId && AliveAway(ship, it.strID))
							{
								shipName = string.IsNullOrEmpty(ship.publicName) ? ship.strRegID : ship.publicName;
								return it.strID;
							}
						}
					}
				}
			}
			return null;
		}

		// Into the host's company like a hire (Hire.UpdateThem), without the contract: no signing bonus, salary or
		// death pay in the books. Salary is the host's to set in the BUDGET app.
		private static void JoinCrew(CondOwner co, CondOwner player)
		{
			JsonCompany company = player.Company;
			JsonCompanyRules rules = new JsonCompanyRules();
			rules.StartWorkdayAt(StarSystem.nUTCHour);
			company.mapRoster[co.strID] = rules;
			co.AddCondAmount("IsPlayerCrew", 1.0);
			co.Company = company;
			co.SetShipsOwned(player.GetShipsOwned());
		}

		// The profile's skills instead of the random ones: every skill the profile can pick (as in its editor) is
		// taken away, then the picked ones (up to Profile.MaxSkills) are given.
		private static void SetSkills(CondOwner co, string[] skills)
		{
			foreach (KeyValuePair<string, JsonCond> kv in DataHandler.dictConds)
			{
				if (kv.Value != null && kv.Key.StartsWith("Skill", StringComparison.Ordinal) && kv.Value.nDisplaySelf == 2 && co.HasCond(kv.Key))
				{
					co.ZeroCondAmount(kv.Key);
				}
			}
			if (skills == null)
			{
				return;
			}
			int n = 0;
			foreach (string skill in skills)
			{
				if (n >= Profile.MaxSkills)
				{
					break;
				}
				JsonCond jc;
				if (skill != null && DataHandler.dictConds.TryGetValue(skill, out jc) && jc != null && skill.StartsWith("Skill", StringComparison.Ordinal) && jc.nDisplaySelf == 2)
				{
					co.SetCondAmount(skill, 1.0);
					n++;
				}
			}
		}
	}
}
