using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace OstranautsMP
{
	// The CHAT app on the PDA: the PDA messages ("/pda ..." in the quick chat, the main menu chat, notices).
	//
	// The page is a copy of the PDA's Notes page (its frame and title, "CHAT"); its text field is hidden and the
	// messages are drawn over that area, like a messenger: the sender's avatar (their profile's portrait), their colour
	// and name, the in-game time (real time for messages sent from the menus), the text and a button to delete it; a
	// line between messages more than 5 minutes apart (in-game minutes when both have an in-game time from the same
	// game, else real ones), and "Delete chat" for all of them. Nothing is typed here.
	// The messages and avatars are kept with the profile on this computer (PdaLog).
	//
	// The page is opened by GUIPDA.OpenApp("mpchat") (see Chat): the PDA goes to its "Loading" state, which shows
	// none of its own pages, and this page is shown on top. Any other state closes it.
	public static class PdaChat
	{
		// Seconds between two messages that get a line between them.
		private const double GapSeconds = 300.0;

		private static readonly FieldInfo NotesPage = AccessTools.Field(typeof(GUIPDA), "cgNotes");

		private static GUIPDA _builtFor;

		private static GameObject _page;

		private static CanvasGroup _cg;

		// The area the messages are drawn in: the Notes page's text field.
		private static RectTransform _area;

		private static bool _open;

		private static int _shownVersion = -1;

		private static Vector2 _scroll;

		private static GUIStyle _head;

		private static GUIStyle _body;

		private static GUIStyle _small;

		private static Texture2D _white;

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
			if (_open && (pda == null || pda.State != GUIPDA.UIState.Loading))
			{
				Close();
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
			GameObject page = UnityEngine.Object.Instantiate(notes.gameObject, notes.transform.parent);
			page.name = "MPChatPage";
			TMP_InputField[] fields = page.GetComponentsInChildren<TMP_InputField>(true);
			if (fields.Length == 0)
			{
				UnityEngine.Object.Destroy(page);
				Plugin.Log.LogError("PDA chat: the Notes page has no text field");
				return false;
			}
			foreach (TMP_Text t in page.GetComponentsInChildren<TMP_Text>(true))
			{
				if (t.GetComponentInParent<TMP_InputField>() == null && t.text != null && t.text.ToUpperInvariant().Contains("NOTE"))
				{
					t.text = "CHAT";
				}
			}
			// The field's place is where the messages go; the field itself (and its typing events) goes away.
			TMP_InputField field = fields[0];
			GameObject area = new GameObject("MPChatArea", typeof(RectTransform));
			RectTransform areaRT = area.GetComponent<RectTransform>();
			RectTransform fieldRT = field.transform as RectTransform;
			areaRT.SetParent(fieldRT.parent, false);
			areaRT.anchorMin = fieldRT.anchorMin;
			areaRT.anchorMax = fieldRT.anchorMax;
			areaRT.pivot = fieldRT.pivot;
			areaRT.offsetMin = fieldRT.offsetMin;
			areaRT.offsetMax = fieldRT.offsetMax;
			foreach (TMP_InputField f in fields)
			{
				f.gameObject.SetActive(false);
			}
			_cg = page.GetComponent<CanvasGroup>();
			if (_cg == null)
			{
				_cg = page.AddComponent<CanvasGroup>();
			}
			CanvasManager.HideCanvasGroup(_cg);
			_page = page;
			_area = areaRT;
			_builtFor = pda;
			Plugin.Log.LogInfo("PDA chat page built");
			return true;
		}

		// The area on screen, in IMGUI coordinates.
		private static bool ScreenRect(out Rect rect)
		{
			rect = default(Rect);
			if (_area == null)
			{
				return false;
			}
			Canvas canvas = _area.GetComponentInParent<Canvas>();
			Camera cam = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay) ? canvas.worldCamera : null;
			Vector3[] corners = new Vector3[4];
			_area.GetWorldCorners(corners);
			Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
			Vector2 b = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
			rect = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Screen.height - Mathf.Max(a.y, b.y), Mathf.Max(a.x, b.x), Screen.height - Mathf.Min(a.y, b.y));
			return rect.width > 20f && rect.height > 20f;
		}

		private static void EnsureStyles()
		{
			if (_head != null)
			{
				return;
			}
			_head = new GUIStyle(GUI.skin.label);
			_head.richText = true;
			_head.fontSize = 17;
			_head.wordWrap = false;
			_body = new GUIStyle(GUI.skin.label);
			_body.richText = false;
			_body.fontSize = 19;
			_body.wordWrap = true;
			_body.normal.textColor = Color.white;
			_small = new GUIStyle(GUI.skin.button);
			_small.fontSize = 15;
			_small.padding = new RectOffset(4, 4, 1, 1);
			_white = new Texture2D(1, 1);
			_white.SetPixel(0, 0, Color.white);
			_white.Apply();
			_white.hideFlags = HideFlags.HideAndDontSave;
		}

		// Called from Plugin.OnGUI.
		public static void OnGUI()
		{
			Rect area;
			if (!_open || !ScreenRect(out area))
			{
				return;
			}
			GUISkin prev = MenuLook.Use();
			try
			{
				EnsureStyles();
				Draw(area);
			}
			finally
			{
				MenuLook.Restore(prev);
			}
			Chat.AddBlocking(area);
		}

		private static void Draw(Rect area)
		{
			List<Chat.Record> records = Chat.Records();
			if (Chat.RecordsVersion != _shownVersion)
			{
				_shownVersion = Chat.RecordsVersion;
				_scroll.y = float.MaxValue;
			}
			GUILayout.BeginArea(area);
			GUILayout.BeginHorizontal();
			GUILayout.Label(records.Count + " message(s)", _head);
			GUILayout.FlexibleSpace();
			if (records.Count > 0 && GUILayout.Button("Delete chat", _small))
			{
				Chat.ClearRecords();
			}
			GUILayout.EndHorizontal();
			_scroll = GUILayout.BeginScrollView(_scroll);
			string delete = null;
			for (int i = 0; i < records.Count; i++)
			{
				Chat.Record r = records[i];
				if (i > 0 && Gap(records[i - 1], r))
				{
					GUILayout.Space(4f);
					Rect line = GUILayoutUtility.GetRect(10f, 1f, GUILayout.ExpandWidth(true), GUILayout.Height(1f));
					Color saved = GUI.color;
					GUI.color = new Color(1f, 1f, 1f, 0.35f);
					GUI.DrawTexture(line, _white);
					GUI.color = saved;
					GUILayout.Space(4f);
				}
				string time = !double.IsNaN(r.Epoch) ? MathUtils.GetUTCFromS(r.Epoch) : r.Clock;
				string hex = ColorUtility.ToHtmlStringRGB(r.Color);
				GUILayout.BeginHorizontal();
				// The portrait, with a strip of the sender's colour.
				Rect pic = GUILayoutUtility.GetRect(AvatarSize, AvatarSize, GUILayout.Width(AvatarSize), GUILayout.Height(AvatarSize));
				Texture2D face = r.System ? null : PdaLog.AvatarOf(r.ProfileId);
				Color keep = GUI.color;
				GUI.color = r.Color;
				GUI.DrawTexture(new Rect(pic.x, pic.y, 3f, pic.height), _white);
				GUI.color = keep;
				if (face != null)
				{
					GUI.DrawTexture(new Rect(pic.x + 5f, pic.y, pic.width - 5f, pic.height), face, ScaleMode.ScaleToFit);
				}
				GUILayout.Space(6f);
				GUILayout.BeginVertical();
				GUILayout.BeginHorizontal();
				string who = r.System ? "<i>notice</i>" : ("<b>" + Esc(r.Name) + "</b>");
				GUILayout.Label("<color=#" + hex + ">" + who + "</color>  <color=#999999>" + time + "</color>", _head);
				GUILayout.FlexibleSpace();
				if (GUILayout.Button("x", _small, GUILayout.Width(26f)))
				{
					delete = r.Id;
				}
				GUILayout.EndHorizontal();
				Color c = GUI.color;
				if (r.System || r.Action)
				{
					GUI.color = r.System ? new Color(0.75f, 0.75f, 0.75f) : r.Color;
				}
				GUILayout.Label(r.Action ? ("* " + r.Name + " " + r.Text) : r.Text, _body);
				GUI.color = c;
				GUILayout.EndVertical();
				GUILayout.EndHorizontal();
				GUILayout.Space(10f);
			}
			GUILayout.EndScrollView();
			GUILayout.EndArea();
			if (delete != null)
			{
				Chat.DeleteRecord(delete);
			}
		}

		private const float AvatarSize = 60f;

		// In-game time when both have it and it went forward (an earlier time is another game), else real time.
		private static bool Gap(Chat.Record a, Chat.Record b)
		{
			if (!double.IsNaN(a.Epoch) && !double.IsNaN(b.Epoch))
			{
				double d = b.Epoch - a.Epoch;
				return d < 0.0 || d > GapSeconds;
			}
			return (b.Ticks - a.Ticks) / (double)TimeSpan.TicksPerSecond > GapSeconds;
		}

		private static string Esc(string s)
		{
			return (s ?? "").Replace("<", "<​");
		}
	}
}
