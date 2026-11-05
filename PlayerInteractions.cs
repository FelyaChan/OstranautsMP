using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using LitJson;
using Ostranauts.UI.Quickbar.Models;
using UnityEngine;

namespace OstranautsMP
{
	// Interactions between the two players' characters, built from the game's own dialogue system.
	//
	// Dialogue replies: when A says something to B, B gets a ReplyThread (CondOwner.aReplies); B's reply options come
	// from it, and A sees "Wait for NPC Reply" until B answers. The list isn't a condition or an item, so nothing synced
	// it. Now the host sends every person's list when it changes (REPLIES) and the client never makes its own
	// (AddReplyThread is off there). The client's character answers by the player's choice, never by the host's AI
	// (PledgeReply is off for it while the client is connected).
	// The game's own dead end: when A and B wait for each other, the menu shows only "Wait" (it checks waiting before
	// offering your own replies). With the mod on, your reply options are shown as well.
	//
	// Between the two players' characters (host's main character, client's character):
	//   Give item in left hand / right hand  -> Accept item (needs a free hand) / Can't take it: hands full / Refuse
	//   Offer money                          -> Accept money -> the giver picks Give $10 / $100 / $250 / Never mind
	//   Follow                               -> the game's FollowCommand pledge; "Stop following" on yourself
	// On yourself: emotes (animations the game's own dialogue uses), Play dead / Get up.
	//
	// Play dead marks the character with IsMPPlayingDead and lays them down (idle animation Fallen). They are not
	// unconscious: only the drag check (TIsNotConsciousNotCarried, used by Drag alone) treats them as such, so the
	// other player can drag them; nothing else (stripping, jettisoning, combat on the helpless) sees them as helpless.
	// While playing dead they take no orders or queued actions except Get up and being dragged.
	//
	// Effects (moving the item, the money, the marks) are applied on the host; the client sees them through
	// ItemSync / CondSync and the host's animations.
	//
	// Protocol: REPLIES <personID>|<base64 JSON ReplyThread[]>     host -> client
	public static class PlayerInteractions
	{
		internal const string PlayingDead = "IsMPPlayingDead";

		private const string GiveLeft = "MPGiveLeft";

		private const string GiveRight = "MPGiveRight";

		private const string AcceptLeft = "MPGiveAcceptLeft";

		private const string AcceptRight = "MPGiveAcceptRight";

		private const string HandsFull = "MPGiveHandsFull";

		private const string Refuse = "MPRefuse";

		private const string OfferMoney = "MPOfferMoney";

		private const string AcceptMoney = "MPMoneyAccept";

		private const string MoneyPrefix = "MPMoneyGive";

		private const string NeverMind = "MPMoneyNever";

		private const string Follow = "MPFollow";

		private const string StopFollow = "MPFollowStop";

		private const string PlayDead = "MPEmotePlayDead";

		private const string GetUp = "MPEmoteGetUp";

		private static readonly int[] Amounts = { 10, 100, 250 };

		// Name, title, animation.
		private static readonly string[][] Emotes =
		{
			new[] { "MPEmoteNod", "Nod", "Yes" },
			new[] { "MPEmoteShake", "Shake head", "No" },
			new[] { "MPEmoteGesture", "Gesture", "Talk" },
			new[] { "MPEmoteClap", "Clap", "Clapping" },
			new[] { "MPEmoteAngry", "Get angry", "Angry" },
			new[] { "MPEmoteHandsUp", "Hands up", "Surrender" },
			new[] { "MPEmoteTrick", "Show a trick", "Burpee" },
			new[] { "MPEmoteSquat", "Squat", "Squat" },
			new[] { "MPEmoteGuard", "Guard stance", "Block" },
			new[] { "MPEmoteTablet", "Check tablet", "Tablet" },
			new[] { "MPEmoteLieDown", "Lie down", "Sleeping" }
		};

		private const float ReplyInterval = 0.5f;

		// Host: what was last sent, per person.
		private static readonly Dictionary<string, string> SentReplies = new Dictionary<string, string>();

		private static float _nextReplies;

		private static bool _applyingReplies;

		// Host: characters who just started playing dead; their queue is cleared on the next frame.
		private static readonly List<CondOwner> CancelNext = new List<CondOwner>();

		public static void Patch(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(CrewSim), "GetAvailActionsForCO"), postfix: new HarmonyMethod(typeof(PlayerInteractions), nameof(AvailActionsPostfix)));
			harmony.Patch(AccessTools.Method(typeof(Interaction), "ApplyEffects"), postfix: new HarmonyMethod(typeof(PlayerInteractions), nameof(ApplyEffectsPostfix)));
			harmony.Patch(AccessTools.Method(typeof(CondOwner), "AddReplyThread"), prefix: new HarmonyMethod(typeof(PlayerInteractions), nameof(AddReplyThreadPrefix)));
			harmony.Patch(AccessTools.Method(typeof(PledgeReply), "Finished"), prefix: new HarmonyMethod(typeof(PlayerInteractions), nameof(PledgeReplyPrefix)));
			harmony.Patch(AccessTools.Method(typeof(CondTrigger), "Triggered"), prefix: new HarmonyMethod(typeof(PlayerInteractions), nameof(CondTriggerPrefix)));
			harmony.Patch(AccessTools.Method(typeof(CondOwner), "AIIssueOrder"), prefix: new HarmonyMethod(typeof(PlayerInteractions), nameof(StillWhileDeadOrder)));
			harmony.Patch(AccessTools.Method(typeof(CondOwner), "QueueInteraction"), prefix: new HarmonyMethod(typeof(PlayerInteractions), nameof(StillWhileDeadQueue)));
		}

		private static bool Multiplayer => Plugin.Net.Connected && WorldSync.InGame;

		private static bool IsHost => Plugin.Net.CurrentMode == NetSession.Mode.Host && Multiplayer;

		// The host's main character and the client's character, on either machine.
		internal static bool IsPlayerChar(CondOwner co)
		{
			return co != null && !co.bDestroyed && (co == CrewSim.coPlayer || co == CrewClaim.HostClaimed || co == CrewClaim.ClientMine);
		}

		// ---- Data ----------------------------------------------------------------------------------------------

		private static bool _dataReady;

		internal static JsonInteraction Make(string name, string title, string anim, bool social, string group, string themType, params string[] inverse)
		{
			JsonInteraction template;
			if (!DataHandler.dictInteractions.TryGetValue("SOCBlank", out template))
			{
				return null;
			}
			JsonInteraction j = template.Clone();
			j.strName = name;
			j.strTitle = title;
			j.strDesc = "[us] " + title.ToLowerInvariant() + ".";
			j.strTooltip = null;
			j.strAnim = anim;
			j.strIdleAnim = null;
			j.bSocial = social;
			j.strActionGroup = group;
			j.strThemType = themType;
			j.aInverse = inverse;
			j.fDuration = social ? 0.0005 : 0.001;
			j.nLogging = social ? 1 : 0;
			j.strColor = social ? "Dialogue" : null;
			j.strBubble = social ? "BblTalk" : null;
			j.strTargetPoint = social ? "talk" : null;
			j.fTargetPointRange = social ? 2f : 0f;
			j.bRandomInverse = false;
			j.CTTestUs = null;
			j.CTTestThem = null;
			DataHandler.dictInteractions[name] = j;
			if (social && DataHandler.dictSocialStats != null && !DataHandler.dictSocialStats.ContainsKey(name))
			{
				DataHandler.dictSocialStats[name] = new SocialStats(name);
			}
			return j;
		}

		private static void EnsureData()
		{
			if (_dataReady || DataHandler.dictInteractions == null || DataHandler.dictConds == null || !DataHandler.dictInteractions.ContainsKey("SOCBlank"))
			{
				return;
			}
			if (!DataHandler.dictConds.ContainsKey(PlayingDead))
			{
				JsonCond c = new JsonCond();
				c.strName = PlayingDead;
				c.strNameFriendly = "Playing dead";
				c.strDesc = "[us] [is] pretending to be dead.";
				c.strColor = "Neutral";
				c.nDisplaySelf = 1;
				c.nDisplayOther = 0;
				DataHandler.dictConds[PlayingDead] = c;
			}
			Make(GiveLeft, "Give item in left hand", "Use", true, "Talk", "Other", AcceptLeft, HandsFull, Refuse);
			Make(GiveRight, "Give item in right hand", "Use", true, "Talk", "Other", AcceptRight, HandsFull, Refuse);
			Make(AcceptLeft, "Accept item", "Yes", true, "Talk", "Other").strBubble = "BblAffirmative";
			Make(AcceptRight, "Accept item", "Yes", true, "Talk", "Other").strBubble = "BblAffirmative";
			Make(HandsFull, "Can't take it: hands full", "No", true, "Talk", "Other");
			Make(Refuse, "Refuse", "No", true, "Talk", "Other");
			List<string> money = new List<string>();
			foreach (int a in Amounts)
			{
				money.Add(MoneyPrefix + a);
				Make(MoneyPrefix + a, "Give $" + a, "Use", true, "Talk", "Other").strBubble = "BblAffirmative";
			}
			money.Add(NeverMind);
			Make(NeverMind, "Never mind", "No", true, "Talk", "Other");
			Make(OfferMoney, "Offer money", "Talk", true, "Talk", "Other", AcceptMoney, Refuse);
			Make(AcceptMoney, "Accept money", "Yes", true, "Talk", "Other", money.ToArray()).strBubble = "BblAffirmative";
			// Follow: the game's own pledge, the way "Agree to follow" (SOCFollowCommandAllow) starts it.
			JsonInteraction follow = Make(Follow, "Follow", "Use", false, "Talk", "Other");
			follow.strPledgeAdd = "FollowCommand";
			follow.LootCTsUs = "CTSOCFollowCommandAllow";
			follow.fDuration = 1E-05;
			// From anywhere: no walking to them first (the pledge does the walking).
			follow.strTargetPoint = "remote";
			Make(StopFollow, "Stop following", "Use", false, "Talk", "Self").fDuration = 1E-05;
			foreach (string[] e in Emotes)
			{
				Make(e[0], e[1], e[2], false, "Emote", "Self");
			}
			Make(PlayDead, "Play dead", "Fallen", false, "Emote", "Self");
			Make(GetUp, "Get up", "Idle", false, "Emote", "Self");
			_dataReady = true;
			Plugin.Log.LogInfo("Player interactions added");
		}

		// ---- Menu ----------------------------------------------------------------------------------------------

		private static CondOwner HandItem(CondOwner co, string slot)
		{
			if (co == null || co.compSlots == null)
			{
				return null;
			}
			List<CondOwner> list = co.compSlots.GetCOs(slot, bAllowLocked: true);
			return (list != null && list.Count > 0) ? list[0] : null;
		}

		private static bool HandFree(CondOwner co, string slot)
		{
			return co != null && co.compSlots != null && co.compSlots.GetSlot(slot) != null && HandItem(co, slot) == null;
		}

		private static void Add(List<AvailableActionDTO> list, string name, CondOwner us, CondOwner them, bool reply, bool clickable)
		{
			Interaction ia = DataHandler.GetInteraction(name);
			if (ia == null)
			{
				return;
			}
			ia.objUs = us;
			ia.objThem = them;
			ia.bManual = true;
			AvailableActionDTO dto = new AvailableActionDTO(ia, reply);
			dto.IsClickable = clickable;
			list.Add(dto);
		}

		private static bool Has(List<AvailableActionDTO> list, string name)
		{
			foreach (AvailableActionDTO a in list)
			{
				if (a != null && a.Ia != null && a.Ia.strName == name)
				{
					return true;
				}
			}
			return false;
		}

		private static void AvailActionsPostfix(List<AvailableActionDTO> __result, CondOwner coUs, CondOwner coTarget)
		{
			if (__result == null || coUs == null || coTarget == null || !Multiplayer)
			{
				return;
			}
			EnsureData();
			if (!_dataReady)
			{
				return;
			}
			Interaction current = coUs.GetInteractionCurrent();
			bool clickable = current == null || current.strName == "Walk";
			// Replies that depend on the one answering.
			__result.RemoveAll((AvailableActionDTO a) => a != null && a.Ia != null && !ReplyAllowed(a.Ia.strName, coUs));
			if (coUs == coTarget)
			{
				if (!IsPlayerChar(coUs))
				{
					return;
				}
				if (coUs.HasCond(PlayingDead))
				{
					// Not while someone is dragging them: they'd stand up inside the other's drag slot.
					if (coUs.objCOParent == null)
					{
						Add(__result, GetUp, coUs, coUs, reply: false, clickable: true);
					}
					return;
				}
				if (coUs.HasCond("IsFollowCommand"))
				{
					Add(__result, StopFollow, coUs, coUs, reply: false, clickable);
				}
				foreach (string[] e in Emotes)
				{
					Add(__result, e[0], coUs, coUs, reply: false, clickable);
				}
				Add(__result, PlayDead, coUs, coUs, reply: false, clickable);
				return;
			}
			// The game's dead end: both wait for each other and only "Wait" is offered. Offer our replies too.
			if (Has(__result, "WaitReply"))
			{
				AddOwnReplies(__result, coUs, coTarget, clickable);
				return;
			}
			// Answering them comes first; offers again once the exchange is over.
			if (!IsPlayerChar(coUs) || !IsPlayerChar(coTarget) || coTarget.HasCond(PlayingDead) || __result.Exists((AvailableActionDTO a) => a != null && a.IsReply))
			{
				return;
			}
			if (HandItem(coUs, "heldL") != null)
			{
				Add(__result, GiveLeft, coUs, coTarget, reply: false, clickable);
			}
			if (HandItem(coUs, "heldR") != null)
			{
				Add(__result, GiveRight, coUs, coTarget, reply: false, clickable);
			}
			if (coUs.GetCondAmount(Ledger.CURRENCY) >= Amounts[0])
			{
				Add(__result, OfferMoney, coUs, coTarget, reply: false, clickable);
			}
			if (!coUs.HasCond("IsFollowCommand"))
			{
				Add(__result, Follow, coUs, coTarget, reply: false, clickable);
			}
		}

		private static bool ReplyAllowed(string name, CondOwner us)
		{
			if (name == AcceptLeft || name == AcceptRight)
			{
				return HandFree(us, "heldL") || HandFree(us, "heldR");
			}
			if (name == HandsFull)
			{
				return !HandFree(us, "heldL") && !HandFree(us, "heldR");
			}
			if (name != null && name.StartsWith(MoneyPrefix, StringComparison.Ordinal))
			{
				int amount;
				return int.TryParse(name.Substring(MoneyPrefix.Length), out amount) && us.GetCondAmount(Ledger.CURRENCY) >= amount;
			}
			return true;
		}

		// The game's own reply options (CrewSim.GetAvailActionsForCO, the coUs.aReplies part).
		private static void AddOwnReplies(List<AvailableActionDTO> list, CondOwner coUs, CondOwner coTarget, bool clickable)
		{
			if (coUs.aReplies == null)
			{
				return;
			}
			foreach (ReplyThread thread in coUs.aReplies.ToArray())
			{
				if (thread == null || thread.bDone || thread.jis == null || thread.strID != coTarget.strName)
				{
					continue;
				}
				Interaction asked = DataHandler.GetInteraction(thread.jis.strName, thread.jis, getTrackedObject: true);
				if (asked == null)
				{
					continue;
				}
				foreach (string inverse in asked.aInverse)
				{
					string[] parts = inverse.Split(',');
					Interaction reply = DataHandler.GetInteraction(parts[0]);
					if (parts[0] == "SOCBlank" || reply == null || Has(list, parts[0]))
					{
						continue;
					}
					asked.AssignReplyRoles(reply, parts, bNoSwap: false);
					reply.bManual = true;
					reply.strPlot = asked.strPlot;
					if (reply.objUs == coUs && ReplyAllowed(reply.strName, coUs) && reply.Triggered(coUs, coTarget, bStats: false, !reply.bSocial))
					{
						AvailableActionDTO dto = new AvailableActionDTO(reply, isReply: true);
						dto.IsClickable = clickable;
						list.Add(dto);
					}
				}
				DataHandler.ReleaseTrackedInteraction(asked);
			}
		}

		// ---- Effects (host) ------------------------------------------------------------------------------------

		private static void ApplyEffectsPostfix(Interaction __instance, bool isCancelIa)
		{
			// Also called when an action is cancelled; nothing happens then.
			if (isCancelIa || __instance == null || __instance.strName == null || !__instance.strName.StartsWith("MP", StringComparison.Ordinal) || !IsHost)
			{
				return;
			}
			CondOwner us = __instance.objUs;
			CondOwner them = __instance.objThem;
			if (us == null)
			{
				return;
			}
			try
			{
				switch (__instance.strName)
				{
					case AcceptLeft:
						HandOver(them, us, "heldL");
						break;
					case AcceptRight:
						HandOver(them, us, "heldR");
						break;
					case PlayDead:
						// Whatever else was queued is dropped next frame (not from inside this action).
						CancelNext.Add(us);
						us.SetCondAmount(PlayingDead, 1.0);
						us.strIdleAnim = "Fallen";
						us.SetAnimState(Interaction.dictAnims["Fallen"]);
						Plugin.Log.LogInfo(us.strName + " plays dead");
						break;
					case GetUp:
						us.ZeroCondAmount(PlayingDead);
						us.strIdleAnim = "Idle";
						us.SetAnimState(Interaction.dictAnims["Idle"]);
						Plugin.Log.LogInfo(us.strName + " gets up");
						break;
					case StopFollow:
						us.ZeroCondAmount("IsFollowCommand");
						break;
					default:
						if (__instance.strName.StartsWith(MoneyPrefix, StringComparison.Ordinal))
						{
							int amount;
							if (int.TryParse(__instance.strName.Substring(MoneyPrefix.Length), out amount))
							{
								GiveMoney(us, them, amount);
							}
						}
						break;
				}
			}
			catch (Exception e)
			{
				Plugin.Log.LogError(__instance.strName + " failed: " + e);
			}
		}

		// From the giver's hand into one of the receiver's free hands (the same side if it's free).
		private static void HandOver(CondOwner giver, CondOwner receiver, string slot)
		{
			CondOwner item = HandItem(giver, slot);
			if (giver == null || receiver == null || item == null)
			{
				Plugin.Log.LogWarning("Hand over: nothing in " + slot);
				return;
			}
			string other = (slot == "heldL") ? "heldR" : "heldL";
			string into = HandFree(receiver, slot) ? slot : (HandFree(receiver, other) ? other : null);
			if (into == null)
			{
				Plugin.Log.LogWarning("Hand over: " + receiver.strName + " has no free hand");
				return;
			}
			giver.compSlots.UnSlotItem(slot, item, bForce: true);
			if (!receiver.compSlots.SlotItem(into, item, bAuto: false))
			{
				// Back where it was rather than lost.
				giver.compSlots.SlotItem(slot, item, bAuto: false);
				Plugin.Log.LogWarning("Hand over: could not put " + item.strName + " into " + receiver.strName + "'s " + into);
				return;
			}
			Plugin.Log.LogInfo(giver.strName + " gave " + item.strName + " to " + receiver.strName);
		}

		private static void GiveMoney(CondOwner giver, CondOwner receiver, int amount)
		{
			if (giver == null || receiver == null || giver.GetCondAmount(Ledger.CURRENCY) < amount)
			{
				return;
			}
			giver.AddCondAmount(Ledger.CURRENCY, -amount);
			receiver.AddCondAmount(Ledger.CURRENCY, amount);
			Ledger.RecordTransaction(giver, receiver.FriendlyName, -amount, "Gift");
			Plugin.Log.LogInfo(giver.strName + " gave $" + amount + " to " + receiver.strName);
		}

		// ---- Play dead -----------------------------------------------------------------------------------------

		// Only the drag check treats someone playing dead as not conscious.
		private static bool CondTriggerPrefix(CondTrigger __instance, CondOwner objOwner, ref bool __result)
		{
			if (__instance == null || __instance.strName != "TIsNotConsciousNotCarried" || objOwner == null || !objOwner.HasCond(PlayingDead))
			{
				return true;
			}
			CondTrigger notCarried = DataHandler.GetCondTrigger("TIsNotCarriedNotInstalled");
			__result = notCarried == null || notCarried.Triggered(objOwner, null, false);
			return false;
		}

		private static bool AllowedWhileDead(Interaction ia)
		{
			return ia == null || ia.strName == null || ia.strName == GetUp || ia.strName.StartsWith("PickupDrag", StringComparison.Ordinal);
		}

		private static bool StillWhileDeadOrder(CondOwner __instance, Interaction objInt, ref bool __result)
		{
			if (__instance != null && __instance.HasCond(PlayingDead) && !AllowedWhileDead(objInt))
			{
				__result = false;
				return false;
			}
			return true;
		}

		private static bool StillWhileDeadQueue(CondOwner __instance, Interaction objInteraction, ref bool __result)
		{
			if (__instance != null && __instance.HasCond(PlayingDead) && !AllowedWhileDead(objInteraction))
			{
				__result = false;
				return false;
			}
			return true;
		}

		// ---- Replies -------------------------------------------------------------------------------------------

		// The client's character answers by the player's choice, not the host's AI.
		private static bool PledgeReplyPrefix(PledgeReply __instance, ref bool __result)
		{
			if (IsHost && __instance.Us != null && __instance.Us == CrewClaim.HostClaimed)
			{
				__result = false;
				return false;
			}
			return true;
		}

		// The client shows the host's reply threads only.
		private static bool AddReplyThreadPrefix()
		{
			return _applyingReplies || !(WorldSync.ClientFollowsHost && WorldSync.InGame) || Apart.ClientActive;
		}

		// Called from Plugin.Update.
		public static void Update(float now)
		{
			EnsureData();
			if (!Plugin.Net.Connected)
			{
				SentReplies.Clear();
				return;
			}
			if (CancelNext.Count > 0)
			{
				foreach (CondOwner co in CancelNext.ToArray())
				{
					if (co != null && !co.bDestroyed && co.HasCond(PlayingDead))
					{
						co.AICancelAll();
					}
				}
				CancelNext.Clear();
			}
			if (!IsHost || now < _nextReplies)
			{
				return;
			}
			_nextReplies = now + ReplyInterval;
			List<Ship> ships = CrewSim.GetAllLoadedShips();
			if (ships == null)
			{
				return;
			}
			HashSet<string> seen = new HashSet<string>();
			foreach (Ship ship in ships)
			{
				if (ship == null || ship.bDestroyed)
				{
					continue;
				}
				foreach (CondOwner p in ship.GetPeople(bAllowDocked: false))
				{
					if (p == null || p.strID == null || p.aReplies == null || !seen.Add(p.strID))
					{
						continue;
					}
					string sig = Signature(p.aReplies);
					string last;
					if (!SentReplies.TryGetValue(p.strID, out last))
					{
						last = "";
					}
					if (sig == last)
					{
						continue;
					}
					SentReplies[p.strID] = sig;
					string json = JsonMapper.ToJson(p.aReplies.ToArray());
					Plugin.Net.Send("REPLIES " + p.strID + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)));
				}
			}
		}

		private static string Signature(List<ReplyThread> list)
		{
			StringBuilder sb = new StringBuilder();
			foreach (ReplyThread r in list)
			{
				if (r != null)
				{
					sb.Append(r.strID).Append('|').Append((r.jis != null) ? r.jis.strName : "").Append('|').Append(r.fEpoch).Append('|').Append(r.bDone).Append(';');
				}
			}
			return sb.ToString();
		}

		// Host, on the client's READY: send every list again.
		internal static void ResetReplies()
		{
			SentReplies.Clear();
		}

		// Main thread. Returns false if the message isn't ours.
		public static bool OnMessage(string cmd, string arg)
		{
			if (cmd != "REPLIES")
			{
				return false;
			}
			if (!(WorldSync.ClientFollowsHost && WorldSync.InGame))
			{
				return true;
			}
			int bar = arg.IndexOf('|');
			CondOwner co;
			if (bar < 0 || !DataHandler.mapCOs.TryGetValue(arg.Substring(0, bar), out co) || co == null || co.aReplies == null)
			{
				return true;
			}
			try
			{
				ReplyThread[] threads = JsonMapper.ToObject<ReplyThread[]>(Encoding.UTF8.GetString(Convert.FromBase64String(arg.Substring(bar + 1))));
				_applyingReplies = true;
				co.aReplies.Clear();
				if (threads != null)
				{
					foreach (ReplyThread t in threads)
					{
						if (t != null && t.jis != null)
						{
							co.aReplies.Add(t);
						}
					}
				}
				if (co == CrewSim.GetSelectedCrew())
				{
					CondOwner.UpdateWaitingReplies.Invoke(co.aReplies);
				}
			}
			catch (Exception e)
			{
				Plugin.Log.LogWarning("Bad REPLIES for " + co.strName + ": " + e.Message);
			}
			finally
			{
				_applyingReplies = false;
			}
			return true;
		}
	}
}
