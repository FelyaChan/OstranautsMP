using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Ostranauts.InputControl;
using UnityEngine;

namespace OstranautsMP
{
	// Client -> host player actions.
	//
	// Radial menu and quick bar: RadialContextMenuObject.ProcessInteraction is relayed as a whole (PROCESS). The host
	// runs the same function with the client's character selected, so whatever the menu does there happens on the
	// host: an order, a work task (duty actions outside the work shift become tasks, not orders), AI mode switches,
	// cancelling a task. Orders issued inside it aren't relayed separately.
	//
	// Clicks and WASD: these end in CondOwner.AIIssueOrder, which is relayed (ORDER).
	//
	// In both cases the client also runs the action locally as a prediction (footprints, local walk); position,
	// animation and items still come from the host. Orders from the client's own AI (WorkManager, pledges) are not
	// sent.
	//
	// Inventory screen: it moves items directly, not through orders. Those moves are relayed by ItemSync while
	// InUserInput is true; the inventory UI methods are part of the input scope for that reason.
	//
	// Protocol (client -> host):
	//   ORDER <us>|<target>|<interaction>|<playerOrdered 0|1>|<hasTile 0|1>|<tileX>|<tileY>|<posX>|<posY>
	//   PROCESS <us>|<target>|<interaction>
	public static class OrderSync
	{
		private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

		// Per-frame UI callbacks; they run every frame whether or not the player does anything.
		private static readonly HashSet<string> NotInput = new HashSet<string> { "Update", "LateUpdate", "FixedUpdate", "Awake", "Start", "OnEnable", "OnDisable", "OnDestroy", "OnPointerEnter", "OnPointerExit", "OnGUI" };

		// > 0 while the game is handling player input (clicks, WASD, radial menu, inventory screen).
		private static int _userInput;

		// > 0 inside a ProcessInteraction that was relayed as PROCESS.
		private static int _inProcess;

		internal static bool InUserInput => _userInput > 0;

		// Host: inside a relayed client order or menu action (the client's character is selected for the call).
		internal static bool HostRelaying;

		public static void Patch(Harmony harmony)
		{
			HarmonyMethod enter = new HarmonyMethod(typeof(OrderSync), nameof(EnterInput));
			HarmonyMethod exit = new HarmonyMethod(typeof(OrderSync), nameof(ExitInput));
			harmony.Patch(AccessTools.Method(typeof(CrewSim), "MouseHandler"), prefix: enter, finalizer: exit);
			harmony.Patch(AccessTools.Method(typeof(CrewSim), "Walk"), prefix: enter, finalizer: exit);
			harmony.Patch(AccessTools.Method(typeof(CommandMovePlayer), "Execute"), prefix: enter, finalizer: exit);
			harmony.Patch(AccessTools.Method(typeof(RadialContextMenuObject), "ProcessInteraction", new Type[] { typeof(Interaction) }), prefix: new HarmonyMethod(typeof(OrderSync), nameof(ProcessPrefix)), finalizer: new HarmonyMethod(typeof(OrderSync), nameof(ProcessFinalizer)));
			harmony.Patch(AccessTools.Method(typeof(CondOwner), "AIIssueOrder"), prefix: new HarmonyMethod(typeof(OrderSync), nameof(AIIssueOrderPrefix)), postfix: new HarmonyMethod(typeof(OrderSync), nameof(AIIssueOrderPostfix)));
			int patched = 0;
			foreach (Type t in typeof(Ostranauts.Inventory.GUIInventory).Assembly.GetTypes())
			{
				if (t.Namespace != "Ostranauts.Inventory" || t.IsInterface || t.IsGenericTypeDefinition)
				{
					continue;
				}
				foreach (MethodInfo m in AccessTools.GetDeclaredMethods(t))
				{
					if (m.IsAbstract || m.IsGenericMethodDefinition || m.IsSpecialName || m.ContainsGenericParameters || NotInput.Contains(m.Name) || m.GetMethodBody() == null)
					{
						continue;
					}
					try
					{
						harmony.Patch(m, prefix: enter, finalizer: exit);
						patched++;
					}
					catch (Exception e)
					{
						Plugin.Log.LogWarning("Could not patch " + t.Name + "." + m.Name + ": " + e.Message);
					}
				}
			}
			Plugin.Log.LogInfo("Inventory input scope: " + patched + " methods");
		}

		// Apart from the host, our orders are only carried out here.
		private static bool ClientActive => WorldSync.ClientFollowsHost && WorldSync.InGame && !Apart.ClientActive;

		internal static void EnterInput()
		{
			_userInput++;
		}

		internal static void LeaveInput()
		{
			_userInput--;
		}

		private static Exception ExitInput(Exception __exception)
		{
			_userInput--;
			return __exception;
		}

		// Client bound to a character: actions of other people aren't run, not even locally.
		private static bool BlockedOnClient(CondOwner us)
		{
			if (Death.ClientDead)
			{
				// Dead: only watching.
				return us != null && us.HasCond("IsHuman");
			}
			CondOwner mine = CrewClaim.ClientMine;
			return mine != null && us != null && us != mine && us.HasCond("IsHuman");
		}

		private static bool ProcessPrefix(Interaction interaction, out bool __state)
		{
			_userInput++;
			__state = false;
			if (interaction != null && BlockedOnClient(interaction.objUs))
			{
				return false;
			}
			// Placing an install/paint job opens a placement mode; that stays on the client.
			if (!ClientActive || interaction == null || interaction.objUs == null || interaction.strStartInstall != null)
			{
				return true;
			}
			Plugin.Net.Send("PROCESS " + interaction.objUs.strID + "|" + ((interaction.objThem != null) ? interaction.objThem.strID : "") + "|" + interaction.strName);
			Plugin.Log.LogInfo("Action sent: " + interaction.objUs.strName + " " + interaction.strName + NeedsDiag(interaction.objUs));
			_inProcess++;
			__state = true;
			return true;
		}

		private static Exception ProcessFinalizer(Exception __exception, bool __state)
		{
			_userInput--;
			if (__state)
			{
				_inProcess--;
			}
			return __exception;
		}

		private static bool AIIssueOrderPrefix(CondOwner __instance)
		{
			return _userInput <= 0 || !BlockedOnClient(__instance);
		}

		// Sent only if the order went through here too, so the host doesn't do what the client couldn't.
		private static void AIIssueOrderPostfix(bool __result, CondOwner __instance, CondOwner coTarget, Interaction objInt, bool bPlayerOrdered, Tile til, float fPosX, float fPosY)
		{
			// Inside a relayed menu action the host issues this order itself.
			if (!__result || _userInput <= 0 || _inProcess > 0 || !ClientActive)
			{
				return;
			}
			string tileX = "0";
			string tileY = "0";
			bool hasTile = til != null && til.tf != null;
			if (hasTile)
			{
				tileX = til.tf.position.x.ToString("R", Inv);
				tileY = til.tf.position.y.ToString("R", Inv);
			}
			string line = "ORDER " + __instance.strID + "|" + ((coTarget != null) ? coTarget.strID : "") + "|" + ((objInt != null) ? objInt.strName : "") + "|" + (bPlayerOrdered ? "1" : "0") + "|" + (hasTile ? "1" : "0") + "|" + tileX + "|" + tileY + "|" + fPosX.ToString("R", Inv) + "|" + fPosY.ToString("R", Inv);
			Plugin.Net.Send(line);
			if (objInt != null)
			{
				Plugin.Log.LogInfo("Order sent: " + __instance.strName + " " + objInt.strName);
			}
		}

		// TEMP diagnostics (remove after the satiety fix): needs values and their hidden level flags (Dc...).
		private static string NeedsDiag(CondOwner co)
		{
			if (co == null || co.mapConds == null)
			{
				return "";
			}
			System.Text.StringBuilder sb = new System.Text.StringBuilder(" [");
			foreach (string stat in new string[] { "StatSatiety", "StatHydration" })
			{
				sb.Append(stat).Append('=').Append(co.GetCondAmount(stat).ToString("0.###", Inv)).Append(' ');
			}
			foreach (string name in co.mapConds.Keys)
			{
				if (name.StartsWith("DcSatiety", StringComparison.Ordinal) || name.StartsWith("DcHydration", StringComparison.Ordinal))
				{
					sb.Append(name).Append('=').Append(co.GetCondAmount(name).ToString("0.###", Inv)).Append(' ');
				}
			}
			sb.Append("frozen rules: ").Append(co.bFreezeCondRules).Append(']');
			return sb.ToString();
		}

		// Host side, main thread. Returns false if the message isn't ours.
		public static bool OnMessage(string cmd, string arg)
		{
			if (cmd != "ORDER" && cmd != "PROCESS")
			{
				return false;
			}
			if (Plugin.Net.CurrentMode != NetSession.Mode.Host || !WorldSync.InGame)
			{
				return true;
			}
			try
			{
				if (cmd == "ORDER")
				{
					Execute(arg.Split('|'));
				}
				else
				{
					Process(arg.Split('|'));
				}
			}
			catch (Exception e)
			{
				Plugin.Log.LogError(cmd + " failed: " + e);
			}
			return true;
		}

		private static bool Resolve(string[] f, out CondOwner us, out CondOwner them)
		{
			them = null;
			if (!DataHandler.mapCOs.TryGetValue(f[0], out us) || us == null || us.bDestroyed)
			{
				Plugin.Log.LogWarning("Action for unknown character " + f[0]);
				return false;
			}
			if (!CrewClaim.AllowedForClient(us))
			{
				Plugin.Log.LogInfo("Ignored client action for " + us.strName + ": not the client's character");
				return false;
			}
			if (f[1].Length > 0 && (!DataHandler.mapCOs.TryGetValue(f[1], out them) || them == null || them.bDestroyed))
			{
				Plugin.Log.LogWarning("Action target not found on host: " + f[1]);
				return false;
			}
			return true;
		}

		// Run the radial menu action as if the host had clicked it with the client's character selected.
		private static void Process(string[] f)
		{
			if (f.Length != 3)
			{
				Plugin.Log.LogWarning("Bad PROCESS: " + string.Join("|", f));
				return;
			}
			CondOwner us;
			CondOwner them;
			if (!Resolve(f, out us, out them))
			{
				return;
			}
			Interaction ia = DataHandler.GetInteraction(f[2]);
			if (ia == null)
			{
				Plugin.Log.LogWarning("Unknown interaction on host: " + f[2]);
				return;
			}
			if (ia.strStartInstall != null)
			{
				return;
			}
			ia.objUs = us;
			ia.objThem = them;
			Plugin.Log.LogInfo("Action received: " + us.strName + " " + f[2] + NeedsDiag(us));
			// Resume/cancel task are menu wrappers: the game doesn't check their own conditions, so neither do we.
			if (them != null && f[2] != "ACTResumeTaskThem" && f[2] != "ACTCancelTaskThem")
			{
				// Same check the menu makes; log why it fails here, the game only tells the character's message log.
				bool chanceSkip = CondTrigger.bChanceSkip;
				CondTrigger.bChanceSkip = true;
				bool ok = ia.Triggered(us, them);
				CondTrigger.bChanceSkip = chanceSkip;
				if (!ok)
				{
					Plugin.Log.LogWarning("Client action fails on host: " + us.strName + " " + f[2] + ": " + ia.FailReasons(bUsThem: true, bItems: true, bDebug: false));
				}
			}
			// ProcessInteraction uses the selected crew (manual AI mode, quick bar); select the client's character for the call.
			List<CondOwner> selection = new List<CondOwner>(CrewSim.aSelected);
			CrewSim.aSelected.Clear();
			CrewSim.aSelected.Add(us);
			HostRelaying = true;
			try
			{
				RadialContextMenuObject.ProcessInteraction(ia);
			}
			finally
			{
				HostRelaying = false;
				CrewSim.aSelected.Clear();
				CrewSim.aSelected.AddRange(selection);
			}
			Plugin.Log.LogInfo("Action from client: " + us.strName + " " + f[2]);
		}

		private static void Execute(string[] f)
		{
			if (f.Length != 9)
			{
				Plugin.Log.LogWarning("Bad order: " + string.Join("|", f));
				return;
			}
			CondOwner us;
			CondOwner them;
			if (!Resolve(f, out us, out them))
			{
				return;
			}
			bool playerOrdered = f[3] == "1";
			float posX = float.Parse(f[7], NumberStyles.Float, Inv);
			float posY = float.Parse(f[8], NumberStyles.Float, Inv);
			Tile til = null;
			if (f[4] == "1" && us.ship != null)
			{
				til = us.ship.GetTileAtWorldCoords1(float.Parse(f[5], NumberStyles.Float, Inv), float.Parse(f[6], NumberStyles.Float, Inv), bAllowDocked: true);
			}
			Interaction ia = null;
			if (f[2].Length > 0)
			{
				ia = DataHandler.GetInteraction(f[2]);
				if (ia == null)
				{
					Plugin.Log.LogWarning("Unknown interaction on host: " + f[2]);
					return;
				}
				ia.objUs = us;
				ia.objThem = them;
				// Same check the radial menu runs before ordering.
				bool chanceSkip = CondTrigger.bChanceSkip;
				CondTrigger.bChanceSkip = true;
				bool ok = them == null || ia.Triggered(us, them);
				CondTrigger.bChanceSkip = chanceSkip;
				if (!ok)
				{
					Plugin.Log.LogInfo("Order rejected on host: " + us.strName + " " + f[2]);
					return;
				}
			}
			bool issued;
			HostRelaying = true;
			try
			{
				issued = us.AIIssueOrder(them, ia, playerOrdered, til, posX, posY);
			}
			finally
			{
				HostRelaying = false;
			}
			if (issued && playerOrdered)
			{
				// What CrewSim.AIManual(true) does for the selected crew, so the host's AI doesn't take the character back.
				if (!us.HasCond("IsAIManual"))
				{
					us.AddCondAmount("IsAIManual", 1.0);
					us.ZeroCondAmount("IsFollowCommand");
					PlayerMarker.AddMarker(us);
				}
			}
			if (ia != null || !issued)
			{
				Plugin.Log.LogInfo("Order from client: " + us.strName + " " + ((ia != null) ? ia.strName : ((til != null) ? "walk" : "?")) + (issued ? "" : " (not issued)"));
			}
		}
	}
}
