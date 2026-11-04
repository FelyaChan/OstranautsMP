using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Ostranauts.ShipGUIs.Trade;
using Ostranauts.UI.Finance;
using UnityEngine;

namespace OstranautsMP
{
	// Money for the client's character.
	//
	// Every person has their own StatUSD; the client's character's wallet reaches the client through CondSync. Crew
	// salary is the game's own: the PaySalary condition and a daily "Salary" line in the Ledger, which the host pays
	// in the finance screen. The game takes the money from the host but gives it to no one; for the client's
	// character the payment is credited to their wallet (Ledger.PayLI).
	//
	// The host's BUDGET app (PdaBudget) sets the daily salary (replacing the repeating Salary line, as the game does
	// when a crew member renegotiates) and gives money (host's wallet -> character's wallet, recorded in the Ledger).
	// The client's BUDGET app shows their wallet, salary and ledger lines; the host sends that text (BUDGET).
	// The client's cash button at the bottom shows the client's character's money, not the host's.
	//
	// A character a client has ever played gets the hidden condition IsMPClientChar (saved with the game). They can't
	// quit: interactions starting SOCHireQuit / ACTQuit with them as the actor don't start or take effect.
	//
	// Trading by the client: GUITrade.OnBuy / OnSell run as player input, so item changes go to the host through
	// ItemSync; the money moved (buyer and trader) and the ledger record are sent separately (MONEY, LEDGERREC).
	//
	// Protocol:
	//   BUDGET <base64 text>                                        host -> client
	//   MONEY <coID>|<delta>|<coID>|<delta>|<currency>              client -> host
	//   LEDGERREC <coID>|<base64 other party>|<amount>|<base64 description>|<currency>   client -> host
	public static class Budget
	{
		internal const string MarkCond = "IsMPClientChar";

		private const string AppName = "mpbudget";

		private const float SendInterval = 2f;

		private const int MaxLedgerLines = 25;

		private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

		private static readonly FieldInfo Payees = AccessTools.Field(typeof(Ledger), "dictPayees");

		private static readonly FieldInfo Payors = AccessTools.Field(typeof(Ledger), "dictPayors");

		private static readonly FieldInfo Daily = AccessTools.Field(typeof(Ledger), "aDaily");

		// Host.
		private static float _nextSend;

		private static string _lastSent;

		// Client: the text from the host, and the wallet value last shown.
		internal static string ClientText = "";

		internal static int ClientVersion;

		private static double _lastWallet = double.NaN;

		// Client, inside a relayed trade.
		private static int _inTrade;

		public static void Patch(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(GUIPDA), "OpenApp"), prefix: new HarmonyMethod(typeof(Budget), nameof(OpenAppPrefix)));
			harmony.Patch(AccessTools.Method(typeof(CondOwner), "AIIssueOrder"), prefix: new HarmonyMethod(typeof(Budget), nameof(NoQuitOrder)));
			harmony.Patch(AccessTools.Method(typeof(Interaction), "ApplyEffects"), prefix: new HarmonyMethod(typeof(Budget), nameof(NoQuitEffects)));
			harmony.Patch(AccessTools.Method(typeof(Ledger), "PayLI"), prefix: new HarmonyMethod(typeof(Budget), nameof(PayLIPrefix)), postfix: new HarmonyMethod(typeof(Budget), nameof(PayLIPostfix)));
			harmony.Patch(AccessTools.Method(typeof(GUICashButton), "UpdateCashButton"), prefix: new HarmonyMethod(typeof(Budget), nameof(CashButtonPrefix)));
			HarmonyMethod tradeIn = new HarmonyMethod(typeof(Budget), nameof(TradePrefix));
			HarmonyMethod tradeOut = new HarmonyMethod(typeof(Budget), nameof(TradeFinalizer));
			harmony.Patch(AccessTools.Method(typeof(GUITrade), "OnBuy"), prefix: tradeIn, finalizer: tradeOut);
			harmony.Patch(AccessTools.Method(typeof(GUITrade), "OnSell"), prefix: tradeIn, finalizer: tradeOut);
			harmony.Patch(AccessTools.Method(typeof(Ledger), "RecordTransaction"), postfix: new HarmonyMethod(typeof(Budget), nameof(RecordTransactionPostfix)));
			harmony.Patch(AccessTools.Method(typeof(CrewSim), "ToggleFinances"), prefix: new HarmonyMethod(typeof(Budget), nameof(ToggleFinancesPrefix)));
		}

		// The finance screen is the host's company books; on the client the cash button opens BUDGET instead.
		private static bool ToggleFinancesPrefix()
		{
			if (!IsClient)
			{
				return true;
			}
			GUIPDA.OpenApp(AppName);
			return false;
		}

		private static bool IsClient => WorldSync.ClientFollowsHost && WorldSync.InGame;

		private static bool IsHost => Plugin.Net.CurrentMode == NetSession.Mode.Host && Plugin.Net.Connected && WorldSync.InGame;

		// ---- Data ----------------------------------------------------------------------------------------------

		// Our condition and app, once the game's data is loaded (before a save is, so the condition loads with it).
		private static void EnsureData()
		{
			if (DataHandler.dictConds != null && !DataHandler.dictConds.ContainsKey(MarkCond))
			{
				JsonCond c = new JsonCond();
				c.strName = MarkCond;
				c.strNameFriendly = "Multiplayer character";
				c.strDesc = "Played by a multiplayer client.";
				c.strColor = "Neutral";
				c.nDisplaySelf = 0;
				c.nDisplayOther = 0;
				DataHandler.dictConds[MarkCond] = c;
			}
			if (DataHandler.dictPDAAppIcons != null && !DataHandler.dictPDAAppIcons.ContainsKey(AppName))
			{
				JsonPDAAppIcon app = new JsonPDAAppIcon();
				app.strName = AppName;
				app.strFriendlyName = "BUDGET";
				app.strIcon = "pda_apps/IcoPDABounties";
				app.bHidden = false;
				DataHandler.dictPDAAppIcons[AppName] = app;
			}
			if (DataHandler.dictStrings != null && !DataHandler.dictStrings.ContainsKey("GUI_PDA_BUTTON_MPBUDGET"))
			{
				DataHandler.dictStrings["GUI_PDA_BUTTON_MPBUDGET_TITLE"] = "BUDGET";
				DataHandler.dictStrings["GUI_PDA_BUTTON_MPBUDGET"] = "Wallet, salary and ledger of the multiplayer character.";
				DataHandler.dictStrings["GUI_PDA_BUTTON_MPCHAT_TITLE"] = "CHAT";
				DataHandler.dictStrings["GUI_PDA_BUTTON_MPCHAT"] = "Chat with the other player.";
			}
		}

		internal static void MarkClientCharacter(CondOwner co)
		{
			EnsureData();
			if (co != null && co.GetCondAmount(MarkCond) < 1.0)
			{
				co.SetCondAmount(MarkCond, 1.0);
				Plugin.Log.LogInfo(co.strName + " is now a multiplayer character");
			}
		}

		private static bool IsMarked(CondOwner co)
		{
			return co != null && co.mapConds != null && co.HasCond(MarkCond);
		}

		// ---- No quitting ---------------------------------------------------------------------------------------

		private static bool IsQuit(Interaction ia)
		{
			return ia != null && ia.strName != null && (ia.strName.StartsWith("SOCHireQuit", StringComparison.Ordinal) || ia.strName.StartsWith("ACTQuit", StringComparison.Ordinal));
		}

		private static bool NoQuitOrder(CondOwner __instance, Interaction objInt, ref bool __result)
		{
			if (IsQuit(objInt) && IsMarked(__instance))
			{
				Plugin.Log.LogInfo(__instance.strName + " won't quit (" + objInt.strName + "): multiplayer character");
				__result = false;
				return false;
			}
			return true;
		}

		private static bool NoQuitEffects(Interaction __instance)
		{
			if (IsQuit(__instance) && IsMarked(__instance.objUs))
			{
				Plugin.Log.LogInfo(__instance.objUs.strName + " won't quit (" + __instance.strName + "): multiplayer character");
				return false;
			}
			return true;
		}

		// ---- Salary payments -----------------------------------------------------------------------------------

		private static void PayLIPrefix(LedgerLI li, out bool __state)
		{
			__state = li != null && li.Paid;
		}

		private static void PayLIPostfix(LedgerLI li, bool __state)
		{
			if (__state || li == null || !li.Paid || !IsHost || li.strCurrency != Ledger.CURRENCY)
			{
				return;
			}
			CondOwner co = CrewSim.coPlayer;
			if (co == null || li.strPayor != co.strName)
			{
				return;
			}
			CondOwner payee = FindMarked(li.strPayee);
			if (payee != null)
			{
				payee.AddCondAmount(Ledger.CURRENCY, li.fAmount);
				Plugin.Log.LogInfo("Paid " + li.fAmount.ToString("n", Inv) + " to " + payee.strName + " (" + li.strDesc + ")");
			}
		}

		// A multiplayer character on a loaded ship with this name (ledger lines name people, not IDs).
		private static CondOwner FindMarked(string name)
		{
			List<Ship> ships = CrewSim.GetAllLoadedShips();
			if (ships == null || name == null)
			{
				return null;
			}
			foreach (Ship ship in ships)
			{
				if (ship == null || ship.bDestroyed)
				{
					continue;
				}
				foreach (CondOwner p in ship.GetPeople(bAllowDocked: false))
				{
					if (p != null && p.strName == name && IsMarked(p))
					{
						return p;
					}
				}
			}
			return null;
		}

		// ---- Host actions (from the BUDGET page) ---------------------------------------------------------------

		internal static double SalaryOf(CondOwner co)
		{
			LedgerLI li = SalaryLine(co);
			return (li != null) ? li.fAmount : co.GetCondAmount("PaySalary");
		}

		private static LedgerLI SalaryLine(CondOwner co)
		{
			CondOwner boss = CrewSim.coPlayer;
			List<LedgerLI> daily = Daily.GetValue(null) as List<LedgerLI>;
			if (daily == null || boss == null)
			{
				return null;
			}
			foreach (LedgerLI li in daily)
			{
				if (li.strPayee == co.strName && li.strPayor == boss.strName && li.strDesc != null && li.strDesc.Contains("Salary"))
				{
					return li;
				}
			}
			return null;
		}

		// Null on success, otherwise what's wrong.
		internal static string SetSalary(string text)
		{
			CondOwner co = CrewClaim.HostClaimed;
			CondOwner boss = CrewSim.coPlayer;
			float amount;
			if (co == null || boss == null)
			{
				return "No client character";
			}
			if (!ParseMoney(text, out amount) || amount < 0f)
			{
				return "Enter an amount per day";
			}
			// Only the repeating line; salary already owed stays owed.
			LedgerLI old;
			for (int i = 0; i < 10 && (old = SalaryLine(co)) != null; i++)
			{
				Ledger.RemoveLI(old);
			}
			co.SetCondAmount("PaySalary", amount);
			if (amount > 0f)
			{
				Ledger.AddLI(new LedgerLI(co.strName, boss.strName, amount, "Salary", Ledger.CURRENCY, StarSystem.fEpoch, LedgerLI.Frequency.Daily));
			}
			Plugin.Log.LogInfo("Salary of " + co.strName + " set to " + amount.ToString("n", Inv) + "/day");
			_lastSent = null;
			return null;
		}

		internal static string GiveMoney(string text)
		{
			CondOwner co = CrewClaim.HostClaimed;
			CondOwner boss = CrewSim.coPlayer;
			float amount;
			if (co == null || boss == null)
			{
				return "No client character";
			}
			if (!ParseMoney(text, out amount) || amount <= 0f)
			{
				return "Enter an amount";
			}
			if (boss.GetCondAmount(Ledger.CURRENCY) < amount)
			{
				return "Not enough money";
			}
			boss.AddCondAmount(Ledger.CURRENCY, 0f - amount);
			co.AddCondAmount(Ledger.CURRENCY, amount);
			Ledger.RecordTransaction(boss, co.FriendlyName, 0f - amount, "Transfer");
			Plugin.Log.LogInfo("Gave " + amount.ToString("n", Inv) + " to " + co.strName);
			_lastSent = null;
			return null;
		}

		// "150", "150.5", "$1 000", "1,000.50"; a comma is the decimal mark only when 1-2 digits end the text ("150,5").
		private static bool ParseMoney(string text, out float amount)
		{
			string t = (text ?? "").Trim().Replace("$", "").Replace(" ", "").Replace("\u00A0", "");
			int comma = t.LastIndexOf(',');
			if (comma >= 0 && t.IndexOf('.') < 0 && t.Length - comma - 1 >= 1 && t.Length - comma - 1 <= 2 && t.IndexOf(',') == comma)
			{
				t = t.Substring(0, comma) + "." + t.Substring(comma + 1);
			}
			t = t.Replace(",", "");
			return float.TryParse(t, NumberStyles.Float, Inv, out amount) && !float.IsNaN(amount) && !float.IsInfinity(amount);
		}

		// ---- Texts ---------------------------------------------------------------------------------------------

		internal static string Money(double v)
		{
			return "$" + v.ToString("n", Inv);
		}

		// Salary and the ledger lines that name this character, newest first.
		internal static string LedgerText(CondOwner co)
		{
			StringBuilder sb = new StringBuilder();
			sb.Append("Salary: ").Append(Money(SalaryOf(co))).Append(" / day\n\nLEDGER\n");
			List<LedgerLI> lines = new List<LedgerLI>();
			Collect(Payees, co.strName, lines);
			Collect(Payors, co.strName, lines);
			lines.Sort((LedgerLI a, LedgerLI b) => b.fTime.CompareTo(a.fTime));
			if (lines.Count == 0)
			{
				sb.Append("(nothing yet)");
			}
			for (int i = 0; i < lines.Count && i < MaxLedgerLines; i++)
			{
				LedgerLI li = lines[i];
				bool incoming = li.strPayee == co.strName;
				sb.Append(MathUtils.GetUTCFromS(li.fTime)).Append("  ");
				sb.Append(incoming ? "+" : "-").Append(Money(li.fAmount)).Append("  ");
				sb.Append(li.strDesc).Append(incoming ? " from " : " to ").Append(incoming ? li.strPayor : li.strPayee);
				sb.Append(li.Paid ? "" : "  (unpaid)").Append('\n');
			}
			return sb.ToString();
		}

		private static void Collect(FieldInfo field, string name, List<LedgerLI> into)
		{
			Dictionary<string, List<LedgerLI>> dict = field.GetValue(null) as Dictionary<string, List<LedgerLI>>;
			List<LedgerLI> list;
			if (dict != null && name != null && dict.TryGetValue(name, out list) && list != null)
			{
				foreach (LedgerLI li in list)
				{
					if (li != null && !into.Contains(li))
					{
						into.Add(li);
					}
				}
			}
		}

		// ---- Update and messages -------------------------------------------------------------------------------

		// Called from Plugin.Update.
		public static void Update(float now)
		{
			EnsureData();
			PdaBudget.Update();
			if (IsHost && now >= _nextSend)
			{
				_nextSend = now + SendInterval;
				CondOwner co = CrewClaim.HostClaimed;
				if (co != null)
				{
					string text = LedgerText(co);
					if (text != _lastSent)
					{
						_lastSent = text;
						Plugin.Net.Send("BUDGET " + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)));
					}
				}
			}
			if (!Plugin.Net.Connected)
			{
				_lastSent = null;
			}
			if (IsClient)
			{
				// The cash button only redraws on ledger events; the wallet changes through CondSync.
				CondOwner mine = CrewClaim.ClientMine;
				double wallet = (mine != null) ? mine.GetCondAmount(Ledger.CURRENCY) : double.NaN;
				if (!wallet.Equals(_lastWallet))
				{
					_lastWallet = wallet;
					ClientVersion++;
					Ledger.OnLedgerRefresh.Invoke(arg0: true);
				}
			}
		}

		// Main thread. Returns false if the message isn't ours.
		public static bool OnMessage(string cmd, string arg)
		{
			switch (cmd)
			{
				case "BUDGET":
					if (Plugin.Net.CurrentMode == NetSession.Mode.Client)
					{
						ClientText = Encoding.UTF8.GetString(Convert.FromBase64String(arg));
						ClientVersion++;
					}
					return true;
				case "MONEY":
					if (IsHost)
					{
						HostMoney(arg);
					}
					return true;
				case "LEDGERREC":
					if (IsHost)
					{
						HostLedgerRecord(arg);
					}
					return true;
				default:
					return false;
			}
		}

		private static void HostMoney(string arg)
		{
			string[] f = arg.Split('|');
			if (f.Length != 5)
			{
				return;
			}
			for (int i = 0; i < 4; i += 2)
			{
				CondOwner co;
				double delta;
				if (f[i].Length > 0 && DataHandler.mapCOs.TryGetValue(f[i], out co) && co != null && double.TryParse(f[i + 1], NumberStyles.Float, Inv, out delta) && delta != 0.0)
				{
					co.AddCondAmount(f[4], delta);
					Plugin.Log.LogInfo("Client trade: " + co.strName + " " + delta.ToString("n", Inv) + " " + f[4]);
				}
			}
			_lastSent = null;
		}

		private static void HostLedgerRecord(string arg)
		{
			string[] f = arg.Split('|');
			CondOwner co;
			double amount;
			if (f.Length != 5 || !DataHandler.mapCOs.TryGetValue(f[0], out co) || co == null || !double.TryParse(f[2], NumberStyles.Float, Inv, out amount))
			{
				return;
			}
			string other = Encoding.UTF8.GetString(Convert.FromBase64String(f[1]));
			string desc = Encoding.UTF8.GetString(Convert.FromBase64String(f[3]));
			Ledger.RecordTransaction(co, other, amount, desc, (f[4].Length > 0) ? f[4] : null);
		}

		// ---- PDA app -------------------------------------------------------------------------------------------

		private static bool OpenAppPrefix(string appName)
		{
			if (appName != AppName)
			{
				PdaBudget.Close();
				return true;
			}
			GUIPDA pda = GUIPDA.instance;
			if (pda != null && !PdaBudget.Toggle(pda))
			{
				Plugin.Log.LogWarning("PDA budget could not be opened");
			}
			return false;
		}

		// ---- Client: cash button and trading -------------------------------------------------------------------

		private static readonly MethodInfo AnimateCash = AccessTools.Method(typeof(GUICashButton), "AnimateCash");

		// The same as the game's, with the client's character's wallet instead of the host's.
		private static bool CashButtonPrefix(GUICashButton __instance, bool animate)
		{
			CondOwner mine = IsClient ? CrewClaim.ClientMine : null;
			if (mine == null)
			{
				return true;
			}
			Traverse t = Traverse.Create(__instance);
			double cash = MathUtils.Round(mine.GetCondAmount(Ledger.CURRENCY), 2);
			t.Field("fCashNew").SetValue(cash);
			if (!animate)
			{
				t.Field("fCashLast").SetValue(cash);
				TMPro.TMP_Text txt = t.Field("txtCash").GetValue<TMPro.TMP_Text>();
				if (txt != null)
				{
					txt.text = "$" + cash.ToString("n");
				}
			}
			else if (!t.Field("cashRoutineActive").GetValue<bool>() && AnimateCash != null && __instance.isActiveAndEnabled)
			{
				t.Field("cashRoutine").SetValue(__instance.StartCoroutine((System.Collections.IEnumerator)AnimateCash.Invoke(__instance, null)));
			}
			return false;
		}

		public class TradeState
		{
			public CondOwner User;

			public CondOwner Trader;

			public string Currency;

			public double UserBefore;

			public double TraderBefore;
		}

		private static void TradePrefix(GUITrade __instance, out TradeState __state)
		{
			__state = null;
			if (!IsClient)
			{
				return;
			}
			Traverse t = Traverse.Create(__instance);
			TradeState s = new TradeState();
			s.User = t.Field("_coUser").GetValue<CondOwner>();
			s.Trader = t.Property("COSelf").GetValue<CondOwner>();
			s.Currency = t.Field("_selectedCurrency").GetValue<string>() ?? Ledger.CURRENCY;
			s.UserBefore = (s.User != null) ? s.User.GetCondAmount(s.Currency) : 0.0;
			s.TraderBefore = (s.Trader != null) ? s.Trader.GetCondAmount(s.Currency) : 0.0;
			__state = s;
			// Item changes made by the trade go to the host like the player's own inventory moves.
			OrderSync.EnterInput();
			_inTrade++;
		}

		private static Exception TradeFinalizer(Exception __exception, TradeState __state)
		{
			if (__state == null)
			{
				return __exception;
			}
			_inTrade--;
			OrderSync.LeaveInput();
			double du = (__state.User != null) ? (__state.User.GetCondAmount(__state.Currency) - __state.UserBefore) : 0.0;
			double dt = (__state.Trader != null) ? (__state.Trader.GetCondAmount(__state.Currency) - __state.TraderBefore) : 0.0;
			if (du != 0.0 || dt != 0.0)
			{
				Plugin.Net.Send("MONEY " + ((__state.User != null) ? __state.User.strID : "") + "|" + du.ToString("R", Inv) + "|" + ((__state.Trader != null) ? __state.Trader.strID : "") + "|" + dt.ToString("R", Inv) + "|" + __state.Currency);
				Plugin.Log.LogInfo("Trade sent to host: " + du.ToString("n", Inv) + " " + __state.Currency);
			}
			return __exception;
		}

		private static void RecordTransactionPostfix(CondOwner COSelf, string COThemFriendlyName, double dfReceived, string description, string currency)
		{
			if (_inTrade <= 0 || COSelf == null)
			{
				return;
			}
			Plugin.Net.Send("LEDGERREC " + COSelf.strID + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(COThemFriendlyName ?? "")) + "|" + dfReceived.ToString("R", Inv) + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(description ?? "")) + "|" + (currency ?? ""));
		}
	}
}
