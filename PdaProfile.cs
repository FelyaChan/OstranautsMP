using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace OstranautsMP
{
	// The PROFILE app on the PDA: a player's portrait, name, skills, training progress and biography. Tabs at the top:
	// "Me" and the other player. The biography is written here (own profile only) and kept with the multiplayer
	// profile (Profile.Data.Bio); the other player's is sent over.
	//
	// The page is built like the CHAT app's (PdaChat): a copy of the PDA's Notes page with its text field hidden, drawn
	// over with IMGUI; opened by GUIPDA.OpenApp(AppName) (see Chat.OpenAppPrefix).
	//
	// Protocol: BIO <base64 UTF-8 text>          both ways, on connect and when it changes
	public static class PdaProfile
	{
		internal const string AppName = "mpprofile";

		private const int MaxBio = 2000;

		private const float PortraitSize = 128f;

		private static readonly FieldInfo NotesPage = AccessTools.Field(typeof(GUIPDA), "cgNotes");

		private static GUIPDA _builtFor;

		private static GameObject _page;

		private static CanvasGroup _cg;

		private static RectTransform _area;

		private static bool _open;

		// 0: me, 1: the other player.
		private static int _who;

		private static Vector2 _scroll;

		private static string _peerBio;

		// Editing the own biography: the text being typed.
		private static string _draft;

		// Changes to what's shown wait for the next Layout pass, so IMGUI's layout doesn't change between Layout and
		// Repaint (which throws). -1 / null: nothing pending.
		private static int _pendingWho = -1;

		private static bool _pendingEdit;

		private static bool _pendingDone;

		private static GUIStyle _title;

		private static GUIStyle _head;

		private static GUIStyle _body;

		private static GUIStyle _small;

		private static GUIStyle _area2;

		private static Texture2D _white;

		internal static bool IsOpen => _open;

		// The biography is being typed: the game's hotkeys stay quiet (Chat.Update).
		internal static bool Editing => _open && _draft != null;

		internal static void Register()
		{
			if (DataHandler.dictPDAAppIcons == null || DataHandler.dictPDAAppIcons.ContainsKey(AppName))
			{
				return;
			}
			JsonPDAAppIcon app = new JsonPDAAppIcon();
			app.strName = AppName;
			app.strFriendlyName = "PROFILE";
			app.strIcon = "pda_apps/IcoPDARoster";
			app.bHidden = false;
			DataHandler.dictPDAAppIcons[AppName] = app;
		}

		// ---- Biography over the network -----------------------------------------------------------------------

		internal static void SendBio()
		{
			if (!Plugin.Net.Connected || Profile.Current == null)
			{
				return;
			}
			Plugin.Net.Send("BIO " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Profile.Current.Bio ?? "")));
		}

		public static bool OnMessage(string cmd, string arg)
		{
			if (cmd != "BIO")
			{
				return false;
			}
			try
			{
				_peerBio = Encoding.UTF8.GetString(Convert.FromBase64String(arg ?? ""));
			}
			catch (Exception e)
			{
				Plugin.Log.LogWarning("Bad BIO: " + e.Message);
			}
			return true;
		}

		// ---- Page ---------------------------------------------------------------------------------------------

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
			_draft = null;
			return true;
		}

		public static void Close()
		{
			if (_open && _cg != null)
			{
				CanvasManager.HideCanvasGroup(_cg);
			}
			_open = false;
			_draft = null;
		}

		public static void Update()
		{
			GUIPDA pda = GUIPDA.instance;
			if (_page != null && (pda == null || pda != _builtFor))
			{
				_page = null;
				_open = false;
				_draft = null;
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
				Plugin.Log.LogError("PDA profile: the PDA has no Notes page to build from");
				return false;
			}
			GameObject page = UnityEngine.Object.Instantiate(notes.gameObject, notes.transform.parent);
			page.name = "MPProfilePage";
			TMP_InputField[] fields = page.GetComponentsInChildren<TMP_InputField>(true);
			if (fields.Length == 0)
			{
				UnityEngine.Object.Destroy(page);
				Plugin.Log.LogError("PDA profile: the Notes page has no text field");
				return false;
			}
			foreach (TMP_Text t in page.GetComponentsInChildren<TMP_Text>(true))
			{
				if (t.GetComponentInParent<TMP_InputField>() == null && t.text != null && t.text.ToUpperInvariant().Contains("NOTE"))
				{
					t.text = "PROFILE";
				}
			}
			TMP_InputField field = fields[0];
			GameObject area = new GameObject("MPProfileArea", typeof(RectTransform));
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
			return true;
		}

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
			_title = new GUIStyle(GUI.skin.label);
			_title.richText = true;
			_title.fontSize = 24;
			_title.fontStyle = FontStyle.Bold;
			_head = new GUIStyle(GUI.skin.label);
			_head.richText = true;
			_head.fontSize = 17;
			_head.wordWrap = false;
			_body = new GUIStyle(GUI.skin.label);
			_body.fontSize = 17;
			_body.wordWrap = true;
			_body.normal.textColor = Color.white;
			_small = new GUIStyle(GUI.skin.button);
			_small.fontSize = 16;
			_small.padding = new RectOffset(8, 8, 3, 3);
			_area2 = new GUIStyle(GUI.skin.textArea);
			_area2.fontSize = 17;
			_area2.wordWrap = true;
			_white = new Texture2D(1, 1);
			_white.SetPixel(0, 0, Color.white);
			_white.Apply();
			_white.hideFlags = HideFlags.HideAndDontSave;
		}

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

		// ---- Content ------------------------------------------------------------------------------------------

		private static CondOwner Character(bool me)
		{
			if (!WorldSync.InGame)
			{
				return null;
			}
			bool client = Plugin.Net.CurrentMode == NetSession.Mode.Client;
			if (me)
			{
				return client ? CrewClaim.ClientMine : CrewSim.coPlayer;
			}
			return client ? CrewSim.coPlayer : CrewClaim.HostClaimed;
		}

		private static string Friendly(string cond, string prefix)
		{
			Condition c = DataHandler.GetCond(cond);
			string name = (c != null && !string.IsNullOrEmpty(c.strNameFriendly)) ? c.strNameFriendly : cond;
			if (prefix != null && name.StartsWith(prefix, StringComparison.Ordinal))
			{
				name = name.Substring(prefix.Length);
			}
			return name;
		}

		private static void Draw(Rect area)
		{
			if (Event.current.type == EventType.Layout)
			{
				if (_pendingWho >= 0)
				{
					_who = _pendingWho;
					_draft = null;
					_pendingWho = -1;
				}
				if (_pendingEdit)
				{
					_draft = (Profile.Current != null) ? (Profile.Current.Bio ?? "") : null;
					_pendingEdit = false;
				}
				if (_pendingDone)
				{
					_draft = null;
					_pendingDone = false;
				}
			}
			bool peer = Plugin.Net.Connected && Chat.PeerName != null;
			if (!peer && _who != 0 && Event.current.type == EventType.Layout)
			{
				_who = 0;
				_draft = null;
			}
			bool me = _who == 0;
			CondOwner co = Character(me);
			GUILayout.BeginArea(area);
			GUILayout.BeginHorizontal();
			if (GUILayout.Toggle(me, "Me", _small) && !me)
			{
				_pendingWho = 0;
			}
			if (peer && GUILayout.Toggle(!me, Chat.PeerName, _small) && me)
			{
				_pendingWho = 1;
			}
			GUILayout.FlexibleSpace();
			GUILayout.EndHorizontal();
			GUILayout.Space(8f);
			_scroll = GUILayout.BeginScrollView(_scroll);
			// Portrait and name.
			GUILayout.BeginHorizontal();
			Rect pic = GUILayoutUtility.GetRect(PortraitSize, PortraitSize, GUILayout.Width(PortraitSize), GUILayout.Height(PortraitSize));
			Texture2D face = me ? Profile.Avatar : PdaLog.AvatarOf(PdaLog.PeerProfileId);
			Color keep = GUI.color;
			GUI.color = new Color(0f, 0f, 0f, 0.45f);
			GUI.DrawTexture(pic, _white);
			GUI.color = keep;
			if (face != null)
			{
				GUI.DrawTexture(pic, face, ScaleMode.ScaleToFit);
			}
			GUILayout.Space(12f);
			GUILayout.BeginVertical();
			string name = (co != null) ? co.strName : (me ? ((Profile.Current != null) ? Profile.Current.Name : null) : Chat.PeerName);
			GUILayout.Label(name ?? "—", _title);
			GUILayout.Label(me ? "<color=#999999>you</color>" : "<color=#999999>the other player</color>", _head);
			GUILayout.EndVertical();
			GUILayout.EndHorizontal();
			GUILayout.Space(12f);
			DrawSkills(co);
			GUILayout.Space(12f);
			DrawBio(me);
			GUILayout.EndScrollView();
			GUILayout.EndArea();
		}

		private static void DrawSkills(CondOwner co)
		{
			GUILayout.Label("<b>SKILLS</b>", _head);
			if (co == null || co.mapConds == null)
			{
				GUILayout.Label("Not in the game.", _body);
				return;
			}
			List<string> skills = new List<string>();
			SortedDictionary<string, double> training = new SortedDictionary<string, double>();
			foreach (KeyValuePair<string, Condition> kv in co.mapConds)
			{
				if (kv.Value == null || kv.Value.fCount <= 0.0)
				{
					continue;
				}
				if (kv.Key.StartsWith("Skill", StringComparison.Ordinal))
				{
					skills.Add(Friendly(kv.Key, "Skilled in "));
				}
				else if (kv.Key.StartsWith("StatTraining", StringComparison.Ordinal))
				{
					training[Friendly(kv.Key, "Training: ")] = kv.Value.fCount;
				}
			}
			skills.Sort(StringComparer.Ordinal);
			GUILayout.Label((skills.Count > 0) ? string.Join(", ", skills.ToArray()) : "None yet.", _body);
			GUILayout.Space(10f);
			GUILayout.Label("<b>TRAINING</b>", _head);
			if (training.Count == 0)
			{
				GUILayout.Label("Nothing trained yet.", _body);
				return;
			}
			foreach (KeyValuePair<string, double> t in training)
			{
				float v = Mathf.Clamp((float)t.Value, 0f, 100f);
				GUILayout.BeginHorizontal();
				GUILayout.Label(t.Key, _body, GUILayout.Width(260f));
				Rect bar = GUILayoutUtility.GetRect(100f, 16f, GUILayout.ExpandWidth(true), GUILayout.Height(16f));
				bar.y += 4f;
				Color keep = GUI.color;
				GUI.color = new Color(0f, 0f, 0f, 0.5f);
				GUI.DrawTexture(bar, _white);
				GUI.color = (v >= 100f) ? new Color(0.4f, 0.85f, 0.45f) : new Color(0.17f, 0.71f, 0.83f);
				GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * v / 100f, bar.height), _white);
				GUI.color = keep;
				GUILayout.Label(Mathf.FloorToInt(v) + "%", _head, GUILayout.Width(52f));
				GUILayout.EndHorizontal();
			}
		}

		private static void DrawBio(bool me)
		{
			GUILayout.BeginHorizontal();
			GUILayout.Label("<b>BIOGRAPHY</b>", _head);
			GUILayout.FlexibleSpace();
			if (me && Profile.Current != null)
			{
				if (_draft == null)
				{
					if (GUILayout.Button("Edit", _small))
					{
						_pendingEdit = true;
					}
				}
				else
				{
					if (GUILayout.Button("Save", _small))
					{
						Profile.SetBio(_draft.Trim());
						SendBio();
						_pendingDone = true;
					}
					if (GUILayout.Button("Cancel", _small))
					{
						_pendingDone = true;
					}
				}
			}
			GUILayout.EndHorizontal();
			if (me && _draft != null)
			{
				_draft = GUILayout.TextArea(_draft, MaxBio, _area2, GUILayout.MinHeight(140f));
				return;
			}
			string bio = me ? ((Profile.Current != null) ? Profile.Current.Bio : null) : _peerBio;
			if (string.IsNullOrEmpty(bio))
			{
				GUILayout.Label("<color=#999999>Nothing written.</color>", _head);
			}
			else
			{
				GUILayout.Label(bio, _body);
			}
		}
	}
}
