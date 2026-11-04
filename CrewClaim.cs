using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace OstranautsMP
{
	// Binds the client to one crew member. The client picks someone from the host's crew (not the host's main
	// character); the host confirms and from then on ignores client actions for anyone else. On the client,
	// selecting another person is blocked and GetSelectedCrew always returns the claimed character, so clicks, WASD,
	// the radial menu, the crew bar and visibility all work from that character.
	//
	// Protocol: client -> host CLAIM <id>; host -> client CLAIMED <id> or CLAIM_DENIED <reason>.
	public static class CrewClaim
	{
		private const int WindowId = 0x0B4F2;

		// Client.
		private static string _mine;

		private static bool _confirmed;

		private static string _status;

		private static bool _wasInGame;

		private static Rect _rect = new Rect(0f, 0f, 260f, 10f);

		// Host.
		private static string _claimed;

		public static void Patch(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(CrewSim), "SelectCO"), prefix: new HarmonyMethod(typeof(CrewClaim), nameof(SelectCOPrefix)));
			harmony.Patch(AccessTools.Method(typeof(CrewSim), "GetSelectedCrew"), postfix: new HarmonyMethod(typeof(CrewClaim), nameof(GetSelectedCrewPostfix)));
			harmony.Patch(AccessTools.Method(typeof(CondOwner), "AIIssueOrder"), prefix: new HarmonyMethod(typeof(CrewClaim), nameof(FreeWillPrefix)), finalizer: new HarmonyMethod(typeof(CrewClaim), nameof(FreeWillFinalizer)));
			harmony.Patch(AccessTools.Method(typeof(CrewSim), "SetBracketTarget"), prefix: new HarmonyMethod(typeof(CrewClaim), nameof(SetBracketTargetPrefix)));
			harmony.Patch(AccessTools.Method(typeof(CrewSim), "GetMouseOverCO"), postfix: new HarmonyMethod(typeof(CrewClaim), nameof(GetMouseOverCOPostfix)));
			harmony.Patch(AccessTools.Method(typeof(CrewSim), "MouseHandler"), prefix: new HarmonyMethod(typeof(CrewClaim), nameof(MouseHandlerPrefix)), finalizer: new HarmonyMethod(typeof(CrewClaim), nameof(MouseHandlerFinalizer)));
			harmony.Patch(AccessTools.Method(typeof(CrewSim), "Walk"), prefix: new HarmonyMethod(typeof(CrewClaim), nameof(WalkPrefix)));
			harmony.Patch(AccessTools.Method(typeof(CrewSim), "CycleCrew"), prefix: new HarmonyMethod(typeof(CrewClaim), nameof(CycleCrewPrefix)));
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
			if (bUnselect || co == null || !Locked || !co.HasCond("IsHuman"))
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
			foreach (CondOwner s in CrewSim.aSelected)
			{
				if (s != null)
				{
					s.Selected = false;
				}
			}
			CrewSim.aSelected.Clear();
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
					if (co != null && !co.bDestroyed && co != player && co.Company == player.Company && co.HasCond("IsHuman") && !list.Contains(co))
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
			foreach (CondOwner co in crew)
			{
				if (GUILayout.Button(co.strName))
				{
					_mine = co.strID;
					_status = "Waiting for host...";
					Plugin.Net.Send("CLAIM " + co.strID);
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
				case "CLAIMED":
					if (Plugin.Net.CurrentMode == NetSession.Mode.Client)
					{
						_mine = arg;
						_confirmed = true;
						_status = null;
						FocusMine();
						Plugin.Log.LogInfo("Playing as " + MineName);
					}
					return true;
				case "CLAIM_DENIED":
					if (Plugin.Net.CurrentMode == NetSession.Mode.Client)
					{
						_mine = null;
						_status = "Denied: " + arg;
					}
					return true;
				default:
					return false;
			}
		}

		private static void HostClaim(string id)
		{
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
			_claimed = id;
			Budget.MarkClientCharacter(co);
			Plugin.Net.Send("CLAIMED " + id);
			Plugin.Log.LogInfo("Client plays as " + co.strName);
		}
	}
}
