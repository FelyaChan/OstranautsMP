using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Ostranauts.UI.Quickbar.Models;

namespace OstranautsMP
{
	// Whose locker: a player marks an installed container (locker, cabinet, fridge) as theirs with "Make it mine" and
	// gives it up with "Give it up". It's only a label: the name shows "(owner)", nobody is kept out.
	//
	// The owner is kept in the item's GUI property map MPOwner (id, name), which the game saves with the ship and
	// copies when the item changes kind. The action is applied where it runs: on the host (the client's click is
	// relayed as usual), or on the client while apart from the host (its place's copy carries it back).
	//
	// Protocol: OWNER <itemID>|<ownerID or empty>|<base64 owner name>       host -> client
	public static class Ownership
	{
		private const string Gpm = "MPOwner";

		private const string Claim = "MPLockerClaim";

		private const string Release = "MPLockerRelease";

		private static bool _ready;

		public static void Patch(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(CrewSim), "GetAvailActionsForCO"), postfix: new HarmonyMethod(typeof(Ownership), nameof(AvailActionsPostfix)));
			harmony.Patch(AccessTools.Method(typeof(Interaction), "ApplyEffects"), postfix: new HarmonyMethod(typeof(Ownership), nameof(ApplyEffectsPostfix)));
			harmony.Patch(AccessTools.PropertyGetter(typeof(CondOwner), "FriendlyName"), postfix: new HarmonyMethod(typeof(Ownership), nameof(FriendlyNamePostfix)));
		}

		private static bool Multiplayer => Plugin.Net.Connected && WorldSync.InGame;

		private static bool IsHost => Plugin.Net.CurrentMode == NetSession.Mode.Host && Multiplayer;

		private static void Ensure()
		{
			if (_ready || DataHandler.dictInteractions == null || !DataHandler.dictInteractions.ContainsKey("SOCBlank"))
			{
				return;
			}
			foreach (JsonInteraction j in new JsonInteraction[] { PlayerInteractions.Make(Claim, "Make it mine", "Use", false, "Use", "Other"), PlayerInteractions.Make(Release, "Give it up", "Use", false, "Use", "Other") })
			{
				if (j == null)
				{
					return;
				}
				j.fDuration = 1E-05;
				// From anywhere: no walking over to it.
				j.strTargetPoint = "remote";
			}
			_ready = true;
		}

		private static bool IsLocker(CondOwner co)
		{
			return co != null && !co.bDestroyed && co.objContainer != null && !co.IsHumanOrRobot && co.HasCond("IsInstalled");
		}

		private static Dictionary<string, string> Owner(CondOwner co)
		{
			Dictionary<string, string> d;
			if (co == null || co.mapGUIPropMaps == null || !co.mapGUIPropMaps.TryGetValue(Gpm, out d) || d == null || !d.ContainsKey("id"))
			{
				return null;
			}
			return d;
		}

		private static void Add(List<AvailableActionDTO> list, string name, CondOwner us, CondOwner them)
		{
			Interaction ia = DataHandler.GetInteraction(name);
			if (ia == null)
			{
				return;
			}
			ia.objUs = us;
			ia.objThem = them;
			ia.bManual = true;
			Interaction current = us.GetInteractionCurrent();
			AvailableActionDTO dto = new AvailableActionDTO(ia, false);
			dto.IsClickable = current == null || current.strName == "Walk";
			list.Add(dto);
		}

		private static void AvailActionsPostfix(List<AvailableActionDTO> __result, CondOwner coUs, CondOwner coTarget)
		{
			if (__result == null || !Multiplayer || !IsLocker(coTarget) || !PlayerInteractions.IsPlayerChar(coUs))
			{
				return;
			}
			Ensure();
			if (!_ready)
			{
				return;
			}
			Dictionary<string, string> owner = Owner(coTarget);
			if (owner == null)
			{
				Add(__result, Claim, coUs, coTarget);
			}
			else if (owner["id"] == coUs.strID)
			{
				Add(__result, Release, coUs, coTarget);
			}
		}

		private static void ApplyEffectsPostfix(Interaction __instance, bool isCancelIa)
		{
			if (isCancelIa || __instance == null || (__instance.strName != Claim && __instance.strName != Release) || !(IsHost || Apart.ClientActive))
			{
				return;
			}
			CondOwner us = __instance.objUs;
			CondOwner them = __instance.objThem;
			if (us == null || !IsLocker(them))
			{
				return;
			}
			Dictionary<string, string> owner = Owner(them);
			if (__instance.strName == Claim && owner == null)
			{
				Set(them, us.strID, us.FriendlyName);
			}
			else if (__instance.strName == Release && owner != null && owner["id"] == us.strID)
			{
				Set(them, null, null);
			}
			else
			{
				return;
			}
			if (IsHost)
			{
				Plugin.Net.Send("OWNER " + them.strID + "|" + (Owner(them) != null ? us.strID : "") + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes((Owner(them) != null) ? us.FriendlyName : "")));
			}
		}

		// A character was renamed (its ID is its name): their lockers follow.
		internal static void Renamed(string oldId, string newId)
		{
			foreach (CondOwner co in DataHandler.mapCOs.Values)
			{
				Dictionary<string, string> owner = Owner(co);
				if (owner != null && owner["id"] == oldId)
				{
					owner["id"] = newId;
					owner["name"] = newId;
				}
			}
		}

		internal static bool OwnedBy(CondOwner co, CondOwner person)
		{
			Dictionary<string, string> owner = Owner(co);
			return owner != null && person != null && owner["id"] == person.strID;
		}

		private static void Set(CondOwner item, string id, string name)
		{
			if (item.mapGUIPropMaps == null)
			{
				return;
			}
			if (id == null)
			{
				item.mapGUIPropMaps.Remove(Gpm);
				Plugin.Log.LogInfo(item.strName + " has no owner now");
				return;
			}
			Dictionary<string, string> d = new Dictionary<string, string>();
			d["id"] = id;
			d["name"] = name ?? "";
			item.mapGUIPropMaps[Gpm] = d;
			Plugin.Log.LogInfo(item.strName + " belongs to " + name);
		}

		// "Locker (Alejandro Riley)": the owner's current name, or the one saved with it if they aren't loaded here.
		private static void FriendlyNamePostfix(CondOwner __instance, ref string __result)
		{
			Dictionary<string, string> owner = Owner(__instance);
			if (owner == null)
			{
				return;
			}
			CondOwner person;
			string name = (DataHandler.mapCOs.TryGetValue(owner["id"], out person) && person != null && !person.bDestroyed && person != __instance) ? person.FriendlyName : null;
			if (string.IsNullOrEmpty(name))
			{
				owner.TryGetValue("name", out name);
			}
			if (!string.IsNullOrEmpty(name))
			{
				__result = __result + " (" + name + ")";
			}
		}

		// Main thread. Returns false if the message isn't ours.
		public static bool OnMessage(string cmd, string arg)
		{
			if (cmd != "OWNER")
			{
				return false;
			}
			if (Plugin.Net.CurrentMode != NetSession.Mode.Client || !WorldSync.InGame)
			{
				return true;
			}
			string[] f = arg.Split('|');
			CondOwner item;
			if (f.Length != 3 || !DataHandler.mapCOs.TryGetValue(f[0], out item) || item == null || item.bDestroyed)
			{
				return true;
			}
			Set(item, (f[1].Length > 0) ? f[1] : null, Encoding.UTF8.GetString(Convert.FromBase64String(f[2])));
			return true;
		}
	}
}
