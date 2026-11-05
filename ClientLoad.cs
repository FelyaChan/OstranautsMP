using System;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace OstranautsMP
{
	// Host: from sending a save to the client (SaveSync.HostSend) until the client is in the game (READY), the game
	// stays paused (unpausing is refused) and a panel at the top shows how far the client got: sending the save, then
	// the client's own loading screen (LoadingScreen.SetProgressBar, which it reports). Then the panel goes away and the
	// game can be unpaused. Disconnecting ends the wait too.
	//
	// Protocol: LOADP <fraction 0..1>|<stage text>       client -> host, while its loading screen moves
	public static class ClientLoad
	{
		// How often the client reports while the fraction stays the same (stage text only).
		private const float ReportInterval = 0.25f;

		private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

		internal static bool Waiting;

		private static float _fraction;

		private static string _stage;

		// Client.
		private static float _nextReport;

		private static float _reported = -1f;

		private static GUIStyle _title;

		private static Texture2D _white;

		public static void Patch(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(Ostranauts.LoadingScreen), "SetProgressBar"), postfix: new HarmonyMethod(typeof(ClientLoad), nameof(ProgressPostfix)));
		}

		// Host: a save is going to the client.
		internal static void Begin()
		{
			Waiting = true;
			_fraction = 0f;
			_stage = null;
			Plugin.Log.LogInfo("Waiting for the client to load; the game stays paused");
		}

		// Host: the client is in the game, or gone.
		internal static void End()
		{
			if (Waiting)
			{
				Plugin.Log.LogInfo("Client loading done; the game can be unpaused");
			}
			Waiting = false;
		}

		// Host: unpausing is refused while waiting.
		internal static bool BlocksUnpause(bool value)
		{
			return !value && Waiting && Plugin.Net.CurrentMode == NetSession.Mode.Host;
		}

		// Host, every frame in the game: keep it paused (a save loaded from the menu starts unpaused).
		internal static void HostUpdate()
		{
			if (Waiting && !CrewSim.Paused)
			{
				CrewSim.Paused = true;
			}
		}

		// Client: the loading screen moved.
		private static void ProgressPostfix(float totalAmount, string textToDisplay)
		{
			if (Plugin.Net.CurrentMode != NetSession.Mode.Client || !Plugin.Net.Connected)
			{
				return;
			}
			float now = Time.realtimeSinceStartup;
			if (totalAmount == _reported && now < _nextReport)
			{
				return;
			}
			_reported = totalAmount;
			_nextReport = now + ReportInterval;
			Plugin.Net.Send("LOADP " + totalAmount.ToString("R", Inv) + "|" + (textToDisplay ?? "").Replace('\n', ' ').Replace('\r', ' '));
		}

		// Main thread. Returns false if the message isn't ours.
		public static bool OnMessage(string cmd, string arg)
		{
			if (cmd != "LOADP")
			{
				return false;
			}
			if (Plugin.Net.CurrentMode != NetSession.Mode.Host || !Waiting)
			{
				return true;
			}
			string[] f = arg.Split(new char[] { '|' }, 2);
			float v;
			if (float.TryParse(f[0], NumberStyles.Float, Inv, out v))
			{
				_fraction = Mathf.Clamp01(v);
			}
			if (f.Length == 2 && f[1].Length > 0)
			{
				_stage = f[1];
			}
			return true;
		}

		// Host, from Plugin.OnGUI.
		public static void OnGUI()
		{
			if (!Waiting || Plugin.Net.CurrentMode != NetSession.Mode.Host || !WorldSync.InGame)
			{
				return;
			}
			// The save first (half the bar), then the client's loading screen.
			string sending = SaveSync.Progress;
			float overall;
			string stage;
			if (sending != null)
			{
				overall = 0.5f * SaveSync.SendFraction;
				stage = sending;
			}
			else
			{
				overall = 0.5f + 0.5f * _fraction;
				stage = "Loading: " + (_stage ?? "starting");
			}
			GUISkin prev = MenuLook.Use();
			try
			{
				if (_title == null)
				{
					_title = new GUIStyle(GUI.skin.label);
					_title.fontSize = 18;
					_title.fontStyle = FontStyle.Bold;
					_title.alignment = TextAnchor.MiddleCenter;
					_white = new Texture2D(1, 1);
					_white.SetPixel(0, 0, Color.white);
					_white.Apply();
					_white.hideFlags = HideFlags.HideAndDontSave;
				}
				float w = Mathf.Min(460f, Screen.width - 40f);
				Rect box = new Rect((Screen.width - w) / 2f, 40f, w, 104f);
				GUI.Box(box, GUIContent.none);
				string who = Chat.PeerName ?? "the other player";
				GUI.Label(new Rect(box.x + 12f, box.y + 8f, box.width - 24f, 26f), "Waiting for " + who + " to load", _title);
				GUI.Label(new Rect(box.x + 12f, box.y + 36f, box.width - 24f, 24f), stage + "  (" + Mathf.RoundToInt(overall * 100f) + "%)");
				Rect bar = new Rect(box.x + 12f, box.y + 70f, box.width - 24f, 18f);
				Color c = GUI.color;
				GUI.color = new Color(0f, 0f, 0f, 0.6f);
				GUI.DrawTexture(bar, _white);
				GUI.color = new Color(0.17f, 0.71f, 0.83f);
				GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * overall, bar.height), _white);
				GUI.color = c;
				Chat.AddBlocking(box);
			}
			finally
			{
				MenuLook.Restore(prev);
			}
		}
	}
}
