using System.Collections;
using HarmonyLib;

namespace OstranautsMP
{
	// Things the client doesn't decide for itself; the host's results reach it through the other sync modules.
	//
	// People's AI: every person (and robot) decides what to do next in CondOwner.EndTurnSteps (timed conditions,
	// pledges, needs, the action queue). On the client that doesn't run for anyone but the client's own character:
	// positions and animations come from WorldSync, conditions from CondSync, item changes from ItemSync. The client's
	// own character keeps it, since the client's orders are also carried out locally. While apart from the host, the
	// people of the client's place run here too (Apart).
	//
	// Story and random events: BeatManager.Update (tension/release events, autosave timer), BeatManager.RunEncounter
	// (both overloads) and PlotManager.CheckPlots don't run on the client.
	//
	// Bills: Ledger.ProcessRepeating (salaries, rent, mortgage turning into bills) doesn't run on the client; the
	// host's ledger is the real one, and the client sees its part in the BUDGET app.
	public static class ClientQuiet
	{
		public static void Patch(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(CondOwner), "EndTurnSteps"), prefix: new HarmonyMethod(typeof(ClientQuiet), nameof(EndTurnStepsPrefix)));
			harmony.Patch(AccessTools.Method(typeof(BeatManager), "Update"), prefix: new HarmonyMethod(typeof(ClientQuiet), nameof(Off)));
			HarmonyMethod offBool = new HarmonyMethod(typeof(ClientQuiet), nameof(OffFalse));
			foreach (System.Reflection.MethodInfo m in AccessTools.GetDeclaredMethods(typeof(BeatManager)))
			{
				if (m.Name == "RunEncounter")
				{
					harmony.Patch(m, prefix: offBool);
				}
			}
			harmony.Patch(AccessTools.Method(typeof(PlotManager), "CheckPlots"), prefix: offBool);
			harmony.Patch(AccessTools.Method(typeof(Ledger), "ProcessRepeating"), prefix: new HarmonyMethod(typeof(ClientQuiet), nameof(Off)));
		}

		private static bool Quiet => WorldSync.ClientFollowsHost && WorldSync.InGame;

		private static bool Off()
		{
			return !Quiet;
		}

		private static bool OffFalse(ref bool __result)
		{
			if (!Quiet)
			{
				return true;
			}
			__result = false;
			return false;
		}

		private static bool EndTurnStepsPrefix(CondOwner __instance, ref IEnumerator __result)
		{
			if (!Quiet || __instance == null || !__instance.IsHumanOrRobot || __instance == CrewClaim.ClientMine || Apart.ClientSimulates(__instance))
			{
				return true;
			}
			__result = Nothing();
			return false;
		}

		private static IEnumerator Nothing()
		{
			yield break;
		}
	}
}
