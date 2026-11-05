using UnityEngine;

namespace OstranautsMP
{
	// A message from the other player in the PDA chat, while the chat isn't open: a box in the bottom right corner for
	// a few seconds (their avatar, name and the start of the text) and the game's social message sound. Clicking it
	// opens the chat. In the game only; a new message replaces the box.
	public static class PdaNotify
	{
		private const float Seconds = 5f;

		private const float Fade = 0.5f;

		private const int MaxChars = 90;

		private const float AvatarSize = 60f;

		private static string _profileId;

		private static string _name;

		private static string _text;

		private static Color _color;

		private static float _until;

		private static GUIStyle _head;

		private static GUIStyle _body;

		private static GUIStyle _click;

		private static Texture2D _white;

		// From Chat, for a PDA message that came from the other player.
		internal static void Show(string profileId, string name, string text, Color color, bool action)
		{
			if (!WorldSync.InGame || PdaChat.IsOpen)
			{
				return;
			}
			_profileId = profileId;
			_name = name;
			_text = action ? ("* " + name + " " + text) : text;
			if (_text.Length > MaxChars)
			{
				_text = _text.Substring(0, MaxChars) + "…";
			}
			_color = color;
			_until = Time.unscaledTime + Seconds;
			try
			{
				AudioManager.am.PlayAudioEmitter("UIMessageLogSocial", bLoop: false);
			}
			catch
			{
			}
		}

		// Called from Plugin.OnGUI.
		public static void OnGUI()
		{
			float left = _until - Time.unscaledTime;
			if (left <= 0f || !WorldSync.InGame)
			{
				return;
			}
			if (PdaChat.IsOpen)
			{
				_until = 0f;
				return;
			}
			GUISkin prev = MenuLook.Use();
			try
			{
				EnsureStyles();
				Color keep = GUI.color;
				GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(left / Fade));
				Rect box = new Rect(Screen.width - 400f - 20f, Screen.height - 96f - 170f, 400f, 96f);
				GUI.Box(box, GUIContent.none);
				Rect pic = new Rect(box.x + 10f, box.y + 18f, AvatarSize, AvatarSize);
				Color c = GUI.color;
				GUI.color = new Color(_color.r, _color.g, _color.b, c.a);
				GUI.DrawTexture(new Rect(pic.x, pic.y, 3f, pic.height), _white);
				GUI.color = c;
				Texture2D face = PdaLog.AvatarOf(_profileId);
				if (face != null)
				{
					GUI.DrawTexture(new Rect(pic.x + 5f, pic.y, pic.width - 5f, pic.height), face, ScaleMode.ScaleToFit);
				}
				float tx = pic.xMax + 10f;
				string hex = ColorUtility.ToHtmlStringRGB(_color);
				GUI.Label(new Rect(tx, box.y + 8f, box.xMax - tx - 10f, 24f), "<color=#" + hex + "><b>" + Esc(_name) + "</b></color>  <color=#999999>PDA</color>", _head);
				GUI.Label(new Rect(tx, box.y + 32f, box.xMax - tx - 10f, box.height - 38f), _text, _body);
				GUI.color = keep;
				if (GUI.Button(box, GUIContent.none, _click))
				{
					_until = 0f;
					GUIPDA.OpenApp("mpchat");
				}
				Chat.AddBlocking(box);
			}
			finally
			{
				MenuLook.Restore(prev);
			}
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
			_body.fontSize = 17;
			_body.wordWrap = true;
			_body.normal.textColor = Color.white;
			_click = new GUIStyle();
			_white = new Texture2D(1, 1);
			_white.SetPixel(0, 0, Color.white);
			_white.Apply();
			_white.hideFlags = HideFlags.HideAndDontSave;
		}

		private static string Esc(string s)
		{
			return (s ?? "").Replace("<", "<​");
		}
	}
}
