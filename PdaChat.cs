using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OstranautsMP
{
	// The chat app on the PDA, built from the game's own UI: a copy of the PDA's Notes page (its frame, title and
	// text field), the big field turned into a read-only log, a one-line copy of it for typing, and a copy of a PDA
	// button for SEND. The typing field keeps the Notes field's own events, so the game's CrewSim.StartTyping /
	// EndTyping run and its hotkeys stay quiet while typing.
	//
	// The page is opened by GUIPDA.OpenApp("mpchat") (see Chat): the PDA goes to its "Loading" state, which shows
	// none of its own pages, and this page is shown on top. Any other state closes it.
	public static class PdaChat
	{
		private const float InputHeight = 34f;

		private const float Gap = 6f;

		private const float SendWidth = 80f;

		private const int MaxShownLines = 60;

		private static readonly FieldInfo NotesPage = AccessTools.Field(typeof(GUIPDA), "cgNotes");

		private static readonly FieldInfo SortButton = AccessTools.Field(typeof(GUIPDA), "btnSort");

		private static GUIPDA _builtFor;

		private static GameObject _page;

		private static CanvasGroup _cg;

		private static TMP_InputField _log;

		private static TMP_InputField _input;

		private static bool _open;

		private static int _shownVersion = -1;

		internal static bool IsOpen => _open;

		// Returns false if the PDA's pages couldn't be used (then nothing changes).
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
			// Shows none of the PDA's own pages, but keeps the PDA slid open.
			pda.State = GUIPDA.UIState.Loading;
			CanvasManager.ShowCanvasGroup(_cg);
			_page.transform.SetAsLastSibling();
			_open = true;
			_shownVersion = -1;
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

		// Main thread, every frame.
		public static void Update()
		{
			GUIPDA pda = GUIPDA.instance;
			if (_page != null && (pda == null || pda != _builtFor))
			{
				// A new game scene has a new PDA; build again on next open.
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
			if (Chat.Version != _shownVersion)
			{
				Refresh();
			}
		}

		private static bool EnsureBuilt(GUIPDA pda)
		{
			if (_page != null && _builtFor == pda)
			{
				return true;
			}
			CanvasGroup notes = NotesPage.GetValue(pda) as CanvasGroup;
			if (notes == null)
			{
				Plugin.Log.LogError("PDA chat: the PDA has no Notes page to build from");
				return false;
			}
			GameObject page = Object.Instantiate(notes.gameObject, notes.transform.parent);
			page.name = "MPChatPage";
			TMP_InputField[] fields = page.GetComponentsInChildren<TMP_InputField>(true);
			if (fields.Length == 0)
			{
				Object.Destroy(page);
				Plugin.Log.LogError("PDA chat: the Notes page has no text field");
				return false;
			}
			TMP_InputField log = fields[0];
			// The typing field is copied before the log loses its events.
			GameObject inputGo = Object.Instantiate(log.gameObject, log.transform.parent);
			inputGo.name = "MPChatInput";
			TMP_InputField input = inputGo.GetComponent<TMP_InputField>();
			// Title.
			foreach (TMP_Text t in page.GetComponentsInChildren<TMP_Text>(true))
			{
				if (t.GetComponentInParent<TMP_InputField>() == null && t.text != null && t.text.ToUpperInvariant().Contains("NOTE"))
				{
					t.text = "CHAT";
				}
			}
			// Log: read-only, no typing events.
			log.readOnly = true;
			log.onSelect = new TMP_InputField.SelectionEvent();
			log.onDeselect = new TMP_InputField.SelectionEvent();
			log.onEndEdit = new TMP_InputField.SubmitEvent();
			log.onValueChanged = new TMP_InputField.OnChangeEvent();
			log.characterLimit = 0;
			log.text = "";
			// Input: one line, Enter sends.
			input.lineType = TMP_InputField.LineType.SingleLine;
			// The copy still points at the log's scrollbar.
			input.verticalScrollbar = null;
			input.characterLimit = 300;
			input.readOnly = false;
			input.text = "";
			input.onSubmit.AddListener(OnSubmit);
			// Send button: a copy of one of the PDA's buttons.
			Button template = SortButton.GetValue(pda) as Button;
			float sendWidth = 0f;
			RectTransform sendRT = null;
			if (template != null)
			{
				GameObject b = Object.Instantiate(template.gameObject, inputGo.transform.parent);
				b.name = "MPChatSend";
				Button btn = b.GetComponent<Button>();
				btn.onClick = new Button.ButtonClickedEvent();
				btn.onClick.AddListener(SendFromInput);
				TMP_Text label = b.GetComponentInChildren<TMP_Text>(true);
				if (label != null)
				{
					label.text = "SEND";
				}
				sendRT = b.transform as RectTransform;
				sendWidth = SendWidth;
			}
			// Layout: log on top, input (and SEND) along the log's old bottom edge.
			RectTransform logRT = log.transform as RectTransform;
			RectTransform inRT = inputGo.transform as RectTransform;
			Vector2 min = logRT.offsetMin;
			Vector2 max = logRT.offsetMax;
			inRT.anchorMin = new Vector2(logRT.anchorMin.x, logRT.anchorMin.y);
			inRT.anchorMax = new Vector2(logRT.anchorMax.x, logRT.anchorMin.y);
			inRT.pivot = new Vector2(0.5f, 0f);
			inRT.offsetMin = new Vector2(min.x, min.y);
			inRT.offsetMax = new Vector2(max.x - sendWidth - ((sendWidth > 0f) ? Gap : 0f), min.y + InputHeight);
			if (sendRT != null)
			{
				sendRT.anchorMin = new Vector2(logRT.anchorMax.x, logRT.anchorMin.y);
				sendRT.anchorMax = new Vector2(logRT.anchorMax.x, logRT.anchorMin.y);
				sendRT.pivot = new Vector2(1f, 0f);
				sendRT.offsetMin = new Vector2(max.x - sendWidth, min.y);
				sendRT.offsetMax = new Vector2(max.x, min.y + InputHeight);
			}
			logRT.offsetMin = new Vector2(min.x, min.y + InputHeight + Gap);
			_cg = page.GetComponent<CanvasGroup>();
			if (_cg == null)
			{
				_cg = page.AddComponent<CanvasGroup>();
			}
			CanvasManager.HideCanvasGroup(_cg);
			_page = page;
			_log = log;
			_input = input;
			_builtFor = pda;
			Plugin.Log.LogInfo("PDA chat page built");
			return true;
		}

		private static void Refresh()
		{
			if (_log == null)
			{
				return;
			}
			_shownVersion = Chat.Version;
			_log.text = Chat.Format(inGame: true, MaxShownLines);
			// Lay the text out now, not during the canvas rebuild (the field resizes its scrollbar while it does).
			_log.textComponent.ForceMeshUpdate();
			// Newest at the bottom: put the (invisible) caret at the end so the field scrolls there.
			_log.MoveTextEnd(shift: false);
		}

		private static void OnSubmit(string text)
		{
			SendFromInput();
		}

		private static void SendFromInput()
		{
			if (_input == null)
			{
				return;
			}
			Chat.SendText(_input.text);
			_input.text = "";
			_input.ActivateInputField();
		}
	}
}
