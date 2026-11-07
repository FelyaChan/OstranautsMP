using UnityEngine;

namespace OstranautsMP
{
	// A message in the middle of the top of the screen, for what a player must not miss: the connection lost (the game
	// is paused then, on both sides) or the other player having another version of the mod. Also in the chat.
	public static class Alert
	{
		private const float Seconds = 10f;

		private static string _text;

		private static float _until;

		private static GUIStyle _style;

		internal static void Show(string text)
		{
			_text = text;
			_until = Time.unscaledTime + Seconds;
			Plugin.Log.LogWarning("[Alert] " + text);
		}

		// The other player is gone (left, crashed, timed out, other version). In the game: paused, and said so.
		internal static void PeerLost(string who, string reason)
		{
			string text = who + " " + reason + ".";
			if (WorldSync.InGame)
			{
				try
				{
					CrewSim.Paused = true;
				}
				catch
				{
				}
				text += " The game is paused.";
			}
			Show(text);
		}

		// The other side's HELLO: another version of the mod can't play with this one.
		internal static bool VersionMismatch(string remote)
		{
			if (string.IsNullOrEmpty(remote) || remote == Plugin.Version)
			{
				return false;
			}
			Plugin.Log.LogError("Mod version mismatch: here " + Plugin.Version + ", the other player " + remote);
			return true;
		}

		public static void OnGUI()
		{
			if (_text == null || Time.unscaledTime > _until)
			{
				return;
			}
			GUISkin prev = MenuLook.Use();
			try
			{
				if (_style == null)
				{
					_style = new GUIStyle(GUI.skin.box);
					_style.fontSize = 20;
					_style.wordWrap = true;
					_style.alignment = TextAnchor.MiddleCenter;
					_style.padding = new RectOffset(16, 16, 12, 12);
				}
				float w = Mathf.Min(620f, Screen.width - 40f);
				float h = _style.CalcHeight(new GUIContent(_text), w) + 4f;
				Rect r = new Rect((Screen.width - w) / 2f, 150f, w, h);
				Color keep = GUI.color;
				GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01((_until - Time.unscaledTime) * 2f));
				if (GUI.Button(r, _text, _style))
				{
					_until = 0f;
				}
				GUI.color = keep;
				Chat.AddBlocking(r);
			}
			finally
			{
				MenuLook.Restore(prev);
			}
		}
	}
}
