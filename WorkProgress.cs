using System;
using System.Globalization;
using HarmonyLib;
using Ostranauts.Components;

namespace OstranautsMP
{
	// The bar over a working person (repairing, installing, eating): the game starts it when that person's AI begins
	// an action (CondOwner -> ProgressBar.Activate). On the client the AI of host-run people doesn't run, and the
	// client's own predicted action may not match what the host does, so the host's bars are shown there as well.
	//
	// Protocol (host -> client):
	//   PROG <personID>|<interaction>|<targetID>|<duration s>|<elapsed s>|<long bar: current>|<long bar: max>   a bar starts
	// Each person has their own clock (CondOwner.Clock), anchored differently on each machine, so the bar's start is
	// sent as time already elapsed, not as a clock reading. The long bar (work left on the target) comes from the
	// host as well: the target's progress condition may not have reached the client yet.
	//   PROG <personID>|                                                        it's cancelled
	public static class WorkProgress
	{
		private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

		private static readonly AccessTools.FieldRef<ProgressBar, CondOwner> BarUs = AccessTools.FieldRefAccess<ProgressBar, CondOwner>("_COUs");

		private static readonly AccessTools.FieldRef<ProgressBar, double> BarStart = AccessTools.FieldRefAccess<ProgressBar, double>("_shortBarStartingEpoch");

		private static readonly AccessTools.FieldRef<CondOwner, ProgressBar> PersonBar = AccessTools.FieldRefAccess<CondOwner, ProgressBar>("progressBar");

		private static readonly AccessTools.FieldRef<ProgressBar, float> LongNow = AccessTools.FieldRefAccess<ProgressBar, float>("_longBarCurrentLength");

		private static readonly AccessTools.FieldRef<ProgressBar, float> LongMax = AccessTools.FieldRefAccess<ProgressBar, float>("_longBarLengthMax");

		private static readonly AccessTools.FieldRef<ProgressBar, bool> LongOn = AccessTools.FieldRefAccess<ProgressBar, bool>("_longBarState");

		// Client: inside a bar we start for the host.
		private static bool _applying;

		public static void Patch(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(ProgressBar), "Activate"), postfix: new HarmonyMethod(typeof(WorkProgress), nameof(ActivatePostfix)));
			harmony.Patch(AccessTools.Method(typeof(ProgressBar), "DeactivateImmediate"), prefix: new HarmonyMethod(typeof(WorkProgress), nameof(DeactivatePrefix)));
		}

		private static bool HostSending => Plugin.Net.CurrentMode == NetSession.Mode.Host && Plugin.Net.Connected && WorldSync.InGame;

		private static void ActivatePostfix(ProgressBar __instance, float duration, Interaction interaction)
		{
			if (!HostSending || interaction == null || interaction.objUs == null || interaction.strName == null || duration > 60f || !Apart.HostSends(interaction.objUs))
			{
				return;
			}
			CondOwner us = interaction.objUs;
			double elapsed = us.Clock.Epoch - BarStart(__instance);
			bool longOn = LongOn(__instance);
			Plugin.Net.Send("PROG " + us.strID + "|" + interaction.strName + "|" + ((interaction.objThem != null) ? interaction.objThem.strID : "") + "|" + duration.ToString("R", Inv) + "|" + elapsed.ToString("R", Inv) + "|" + (longOn ? LongNow(__instance).ToString("R", Inv) : "") + "|" + (longOn ? LongMax(__instance).ToString("R", Inv) : ""));
		}

		private static void DeactivatePrefix(ProgressBar __instance)
		{
			if (!HostSending)
			{
				return;
			}
			CondOwner us = BarUs(__instance);
			if (us != null && us.strID != null && Apart.HostSends(us))
			{
				Plugin.Net.Send("PROG " + us.strID + "|");
			}
		}

		// Main thread. Returns false if the message isn't ours.
		public static bool OnMessage(string cmd, string arg)
		{
			if (cmd != "PROG")
			{
				return false;
			}
			if (Plugin.Net.CurrentMode != NetSession.Mode.Client || !WorldSync.InGame || _applying)
			{
				return true;
			}
			string[] f = arg.Split('|');
			CondOwner us;
			if (f.Length < 2 || !DataHandler.mapCOs.TryGetValue(f[0], out us) || us == null || us.bDestroyed)
			{
				return true;
			}
			ProgressBar bar = PersonBar(us);
			if (bar == null)
			{
				return true;
			}
			_applying = true;
			try
			{
				if (f.Length < 5 || f[1].Length == 0)
				{
					bar.DeactivateImmediate();
					return true;
				}
				Interaction ia = DataHandler.GetInteraction(f[1]);
				float duration;
				double elapsed;
				if (ia == null || !float.TryParse(f[3], NumberStyles.Float, Inv, out duration) || !double.TryParse(f[4], NumberStyles.Float, Inv, out elapsed))
				{
					return true;
				}
				float longNow = 0f;
				float longMax = 0f;
				bool hasLong = f.Length >= 7 && float.TryParse(f[5], NumberStyles.Float, Inv, out longNow) && float.TryParse(f[6], NumberStyles.Float, Inv, out longMax) && longMax > 0f;
				CondOwner them = null;
				if (f[2].Length > 0 && DataHandler.mapCOs.TryGetValue(f[2], out them) && them != null && them.bDestroyed)
				{
					them = null;
				}
				ia.objUs = us;
				ia.objThem = them;
				bar.Activate(duration, hasLong, ia);
				// From when the host started it, on this machine's clock for that person, so both bars fill together.
				BarStart(bar) = us.Clock.Epoch - Math.Max(0.0, elapsed);
				if (hasLong)
				{
					LongNow(bar) = longNow;
					LongMax(bar) = longMax;
					// Our copy of the target's condition may say the work is done already, which turns the bar off.
					if (!LongOn(bar))
					{
						Traverse t = Traverse.Create(bar);
						t.Property("ShortBarState").SetValue(true);
						t.Property("LongBarState").SetValue(true);
					}
				}
			}
			catch (Exception e)
			{
				Plugin.Log.LogWarning("Progress bar for " + us.strName + " failed: " + e.Message);
			}
			finally
			{
				_applying = false;
			}
			return true;
		}
	}
}
