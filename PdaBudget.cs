using System.Reflection;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OstranautsMP
{
	// The BUDGET app on the PDA, built like the chat page (PdaChat) from a copy of the PDA's Notes page: its text
	// field becomes a read-only sheet (wallets, salary, ledger lines). On the host two one-line fields with a button
	// each are added along the bottom: daily salary (SET) and money to give (GIVE). The client's page is the sheet only.
	public static class PdaBudget
	{
		private const float RowHeight = 34f;

		private const float Gap = 6f;

		private const float ButtonWidth = 80f;

		private static readonly FieldInfo NotesPage = AccessTools.Field(typeof(GUIPDA), "cgNotes");

		private static readonly FieldInfo SortButton = AccessTools.Field(typeof(GUIPDA), "btnSort");

		private static GUIPDA _builtFor;

		private static bool _builtAsHost;

		private static GameObject _page;

		private static CanvasGroup _cg;

		private static TMP_InputField _sheet;

		private static TMP_InputField _salary;

		private static TMP_InputField _give;

		private static bool _open;

		private static string _status;

		private static float _nextRefresh;

		private static bool IsHost => Plugin.Net.CurrentMode == NetSession.Mode.Host;

		public static bool Toggle(GUIPDA pda)
		{
			if (_open)
			{
				Close();
				pda.State = GUIPDA.UIState.Closed;
				return true;
			}
			if (!EnsureBuilt(pda))
			{
				return false;
			}
			pda.State = GUIPDA.UIState.Loading;
			CanvasManager.ShowCanvasGroup(_cg);
			_page.transform.SetAsLastSibling();
			_open = true;
			_status = null;
			Refresh();
			return true;
		}

		public static void Close()
		{
			if (_open && _cg != null)
			{
				CanvasManager.HideCanvasGroup(_cg);
			}
			_open = false;
		}

		// Main thread, every frame (from Budget.Update).
		public static void Update()
		{
			GUIPDA pda = GUIPDA.instance;
			if (_page != null && (pda == null || pda != _builtFor || _builtAsHost != IsHost))
			{
				if (_page != null && pda == _builtFor)
				{
					Object.Destroy(_page);
				}
				_page = null;
				_open = false;
			}
			if (!_open)
			{
				return;
			}
			if (pda == null || pda.State != GUIPDA.UIState.Loading)
			{
				Close();
				return;
			}
			if (Time.unscaledTime >= _nextRefresh)
			{
				Refresh();
			}
		}

		private static void Refresh()
		{
			_nextRefresh = Time.unscaledTime + 0.5f;
			if (_sheet == null)
			{
				return;
			}
			StringBuilder sb = new StringBuilder();
			if (IsHost)
			{
				CondOwner co = CrewClaim.HostClaimed;
				CondOwner boss = CrewSim.coPlayer;
				if (boss != null)
				{
					sb.Append("Your cash: ").Append(Budget.Money(boss.GetCondAmount(Ledger.CURRENCY))).Append("\n\n");
				}
				if (co == null)
				{
					sb.Append("No client character yet.");
				}
				else
				{
					sb.Append(co.strName);
					if (Chat.PeerName != null)
					{
						sb.Append("  (").Append(Chat.PeerName).Append(')');
					}
					sb.Append("\nWallet: ").Append(Budget.Money(co.GetCondAmount(Ledger.CURRENCY))).Append('\n');
					sb.Append(Budget.LedgerText(co));
				}
				if (_status != null)
				{
					sb.Insert(0, _status + "\n\n");
				}
			}
			else
			{
				CondOwner mine = CrewClaim.ClientMine;
				if (mine == null)
				{
					sb.Append("No character chosen yet.");
				}
				else
				{
					sb.Append(mine.strName).Append("\nWallet: ").Append(Budget.Money(mine.GetCondAmount(Ledger.CURRENCY))).Append('\n');
					sb.Append(Budget.ClientText);
				}
			}
			string text = sb.ToString();
			if (_sheet.text != text)
			{
				_sheet.text = text;
				// Lay the text out now: the field resizes its scrollbar while the text is built, and during the canvas
				// rebuild that's refused ("already inside a graphic rebuild loop").
				_sheet.textComponent.ForceMeshUpdate();
			}
		}

		private static bool EnsureBuilt(GUIPDA pda)
		{
			if (_page != null && _builtFor == pda && _builtAsHost == IsHost)
			{
				return true;
			}
			CanvasGroup notes = NotesPage.GetValue(pda) as CanvasGroup;
			if (notes == null)
			{
				Plugin.Log.LogError("PDA budget: the PDA has no Notes page to build from");
				return false;
			}
			GameObject page = Object.Instantiate(notes.gameObject, notes.transform.parent);
			page.name = "MPBudgetPage";
			TMP_InputField[] fields = page.GetComponentsInChildren<TMP_InputField>(true);
			if (fields.Length == 0)
			{
				Object.Destroy(page);
				Plugin.Log.LogError("PDA budget: the Notes page has no text field");
				return false;
			}
			TMP_InputField sheet = fields[0];
			RectTransform sheetRT = sheet.transform as RectTransform;
			Vector2 min = sheetRT.offsetMin;
			Vector2 max = sheetRT.offsetMax;
			bool host = IsHost;
			TMP_InputField salary = null;
			TMP_InputField give = null;
			if (host)
			{
				// Copied before the sheet loses its typing events. Bottom row: GIVE, the row above it: SET.
				Button template = SortButton.GetValue(pda) as Button;
				give = AddRow(sheet, template, "Money to give", "GIVE", 0, min, max, OnGive);
				salary = AddRow(sheet, template, "Salary per day", "SET", 1, min, max, OnSetSalary);
				sheetRT.offsetMin = new Vector2(min.x, min.y + 2f * (RowHeight + Gap));
			}
			foreach (TMP_Text t in page.GetComponentsInChildren<TMP_Text>(true))
			{
				if (t.GetComponentInParent<TMP_InputField>() == null && t.text != null && t.text.ToUpperInvariant().Contains("NOTE"))
				{
					t.text = "BUDGET";
				}
			}
			sheet.readOnly = true;
			sheet.onSelect = new TMP_InputField.SelectionEvent();
			sheet.onDeselect = new TMP_InputField.SelectionEvent();
			sheet.onEndEdit = new TMP_InputField.SubmitEvent();
			sheet.onValueChanged = new TMP_InputField.OnChangeEvent();
			sheet.characterLimit = 0;
			sheet.text = "";
			_cg = page.GetComponent<CanvasGroup>();
			if (_cg == null)
			{
				_cg = page.AddComponent<CanvasGroup>();
			}
			CanvasManager.HideCanvasGroup(_cg);
			_page = page;
			_sheet = sheet;
			_salary = salary;
			_give = give;
			_builtFor = pda;
			_builtAsHost = host;
			Plugin.Log.LogInfo("PDA budget page built (" + (host ? "host" : "client") + ")");
			return true;
		}

		// A one-line copy of the sheet field with a button at its right, <row> rows up from the sheet's bottom edge.
		private static TMP_InputField AddRow(TMP_InputField sheet, Button template, string placeholder, string label, int row, Vector2 min, Vector2 max, UnityEngine.Events.UnityAction onClick)
		{
			RectTransform sheetRT = sheet.transform as RectTransform;
			GameObject go = Object.Instantiate(sheet.gameObject, sheet.transform.parent);
			go.name = "MPBudget" + label;
			TMP_InputField f = go.GetComponent<TMP_InputField>();
			f.lineType = TMP_InputField.LineType.SingleLine;
			// The copy still points at the sheet's scrollbar.
			f.verticalScrollbar = null;
			f.characterLimit = 20;
			f.readOnly = false;
			f.text = "";
			f.onSubmit.AddListener((string s) => onClick());
			TMP_Text ph = f.placeholder as TMP_Text;
			if (ph != null)
			{
				ph.text = placeholder;
			}
			float y = min.y + row * (RowHeight + Gap);
			float bw = (template != null) ? ButtonWidth : 0f;
			RectTransform rt = go.transform as RectTransform;
			rt.anchorMin = new Vector2(sheetRT.anchorMin.x, sheetRT.anchorMin.y);
			rt.anchorMax = new Vector2(sheetRT.anchorMax.x, sheetRT.anchorMin.y);
			rt.pivot = new Vector2(0.5f, 0f);
			rt.offsetMin = new Vector2(min.x, y);
			rt.offsetMax = new Vector2(max.x - bw - ((bw > 0f) ? Gap : 0f), y + RowHeight);
			if (template != null)
			{
				GameObject b = Object.Instantiate(template.gameObject, sheet.transform.parent);
				b.name = "MPBudget" + label + "Button";
				Button btn = b.GetComponent<Button>();
				btn.onClick = new Button.ButtonClickedEvent();
				btn.onClick.AddListener(onClick);
				TMP_Text t = b.GetComponentInChildren<TMP_Text>(true);
				if (t != null)
				{
					t.text = label;
				}
				RectTransform brt = b.transform as RectTransform;
				brt.anchorMin = new Vector2(sheetRT.anchorMax.x, sheetRT.anchorMin.y);
				brt.anchorMax = new Vector2(sheetRT.anchorMax.x, sheetRT.anchorMin.y);
				brt.pivot = new Vector2(1f, 0f);
				brt.offsetMin = new Vector2(max.x - bw, y);
				brt.offsetMax = new Vector2(max.x, y + RowHeight);
			}
			return f;
		}

		private static void OnSetSalary()
		{
			if (_salary == null)
			{
				return;
			}
			string error = Budget.SetSalary(_salary.text);
			_status = error ?? "Salary set.";
			if (error == null)
			{
				_salary.text = "";
			}
			Refresh();
		}

		private static void OnGive()
		{
			if (_give == null)
			{
				return;
			}
			string error = Budget.GiveMoney(_give.text);
			_status = error ?? "Money given.";
			if (error == null)
			{
				_give.text = "";
			}
			Refresh();
		}
	}
}
