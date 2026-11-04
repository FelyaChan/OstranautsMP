using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace OstranautsMP
{
	// Chat between the two players, "joined / left / timed out" notices, speech bubbles and map pings.
	//
	// Main menu: a chat window opened from the Multiplayer window.
	// In game: a "CHAT" app on the PDA (PdaChat, built from the game's own UI). The app is an entry added to the PDA's
	// app list (DataHandler.dictPDAAppIcons) that GUIPDA.OpenApp is patched to handle. Lines carry the in-game time.
	// A message is typed out, like a typewriter, as "Name: text" in a bubble over the sender's character, then stays
	// for a few seconds; if the character is off screen, the bubble sits at the screen edge with a tail pointing at them.
	// The ~ key (Ё on a Russian layout, same physical key) drops a ping where the mouse points; both players see it.
	// Names in bubbles and pings are coloured by how the speaker's character is doing: green (fine) to red (bad),
	// from the worst condition they show (the game's own Good / Badish / Bad / BadVery colour classes).
	//
	// Protocol: NAME <name>; CHAT <characterID>|<epoch>|<text> (characterID and epoch empty when not in game);
	// MARK <x>|<y>|<characterID>|<base64 label>. A ping on a person or an item sits on it and names it.
	public static class Chat
	{
		private const string AppName = "mpchat";

		private const int MaxLines = 200;

		// How long a bubble stays after it's fully typed.
		private const float BubbleSeconds = 5f;

		private const float TypeCharsPerSecond = 30f;

		private const float MarkSeconds = 8f;

		private const string InputControl = "mp_chat_input";

		private struct Line
		{
			public double Epoch;

			public string Clock;

			public string Name;

			public string Text;

			public bool System;
		}

		private struct Bubble
		{
			public string CharacterId;

			public string Name;

			public string Text;

			public float Start;

			public float Until;
		}

		private struct Mark
		{
			public Vector3 Pos;

			public string Name;

			public string CharacterId;

			// What was pointed at (empty for a bare spot).
			public string Label;

			public float Until;
		}

		private static readonly List<Line> Lines = new List<Line>();

		private static readonly List<Bubble> Bubbles = new List<Bubble>();

		private static readonly List<Mark> Marks = new List<Mark>();

		private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

		internal static string PeerName;

		// Bumped on every new line, so the PDA page knows when to redraw.
		internal static int Version;

		private static bool _peerAnnounced;

		private static string _input = "";

		private static Vector2 _scroll;

		private static bool _scrollToEnd;

		// Main menu chat window.
		internal static bool MenuWindowOpen;

		private static Rect _menuRect = new Rect(0f, 0f, 420f, 320f);

		// Screen rects (IMGUI coordinates) the mouse must not click through, collected while drawing.
		private static readonly List<Rect> Blocking = new List<Rect>();

		private static readonly List<Rect> BlockingNext = new List<Rect>();

		private static int _blockingFrame = -1;

		// Keyboard focus is in our IMGUI text field (main menu window).
		private static bool _focused;

		private static bool _weSetTyping;

		private static Texture2D _white;

		private static GUIStyle _bubbleStyle;

		private static GUIStyle _markStyle;

		private static GUIStyle _speechStyle;

		public static void Patch(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(GUIPDA), "OpenApp"), prefix: new HarmonyMethod(typeof(Chat), nameof(OpenAppPrefix)));
			harmony.Patch(AccessTools.Method(typeof(CrewSim), "MouseHandler"), prefix: new HarmonyMethod(typeof(Chat), nameof(MouseHandlerPrefix)));
		}

		// ---- Network ------------------------------------------------------------------------------------------

		public static void OnConnected()
		{
			_peerAnnounced = false;
			PeerName = null;
			Plugin.Net.Send("NAME " + Plugin.PlayerName);
		}

		public static void OnPeerLost(string reason)
		{
			AddSystem(((PeerName != null) ? PeerName : "Player") + " " + reason);
			PeerName = null;
			_peerAnnounced = false;
		}

		// Main thread. Returns false if the message isn't ours.
		public static bool OnMessage(string cmd, string arg)
		{
			switch (cmd)
			{
				case "NAME":
				{
					string name = Clean(arg);
					if (!_peerAnnounced)
					{
						AddSystem(name + " joined");
						_peerAnnounced = true;
					}
					else if (name != PeerName)
					{
						AddSystem(PeerName + " is now " + name);
					}
					PeerName = name;
					return true;
				}
				case "CHAT":
				{
					string[] f = arg.Split(new char[] { '|' }, 3);
					if (f.Length != 3)
					{
						return true;
					}
					double epoch;
					if (!double.TryParse(f[1], NumberStyles.Float, Inv, out epoch))
					{
						epoch = double.NaN;
					}
					string from = (PeerName != null) ? PeerName : "Player";
					AddLine(from, f[2], epoch, false);
					AddBubble(f[0], from, f[2]);
					return true;
				}
				case "MARK":
				{
					string[] f = arg.Split('|');
					float x;
					float y;
					if (f.Length >= 3 && float.TryParse(f[0], NumberStyles.Float, Inv, out x) && float.TryParse(f[1], NumberStyles.Float, Inv, out y))
					{
						string label = "";
						if (f.Length >= 4 && f[3].Length > 0)
						{
							try
							{
								label = Encoding.UTF8.GetString(Convert.FromBase64String(f[3]));
							}
							catch (FormatException)
							{
							}
						}
						AddMark(new Vector3(x, y, 0f), (PeerName != null) ? PeerName : "Player", f[2], label);
					}
					return true;
				}
				default:
					return false;
			}
		}

		internal static void SendText(string raw)
		{
			string text = Clean(raw).Trim();
			if (text.Length == 0)
			{
				return;
			}
			CondOwner me = MyCharacter();
			double epoch = WorldSync.InGame ? StarSystem.fEpoch : double.NaN;
			string id = (me != null) ? me.strID : "";
			if (Plugin.Net.Connected)
			{
				Plugin.Net.Send("CHAT " + id + "|" + (double.IsNaN(epoch) ? "" : epoch.ToString("R", Inv)) + "|" + text);
			}
			else
			{
				AddSystem("Not connected");
			}
			AddLine(Plugin.PlayerName, text, epoch, false);
			AddBubble(id, Plugin.PlayerName, text);
		}

		// The character this player speaks as: the host's main character, or the client's claimed crew member.
		private static CondOwner MyCharacter()
		{
			if (!WorldSync.InGame)
			{
				return null;
			}
			if (Plugin.Net.CurrentMode == NetSession.Mode.Client)
			{
				return CrewClaim.ClientMine;
			}
			return CrewSim.coPlayer;
		}

		private static string Clean(string s)
		{
			return (s ?? "").Replace('\n', ' ').Replace('\r', ' ');
		}

		private static void AddSystem(string text)
		{
			AddLine(null, text, WorldSync.InGame ? StarSystem.fEpoch : double.NaN, true);
		}

		private static void AddLine(string name, string text, double epoch, bool system)
		{
			Line l;
			l.Epoch = epoch;
			l.Clock = DateTime.Now.ToString("HH:mm");
			l.Name = name;
			l.Text = text;
			l.System = system;
			Lines.Add(l);
			if (Lines.Count > MaxLines)
			{
				Lines.RemoveAt(0);
			}
			Version++;
			_scrollToEnd = true;
			Plugin.Log.LogInfo("[Chat] " + (system ? "* " : (name + ": ")) + text);
		}

		// The last <max> lines; in game with the in-game time they were sent at (real time for lines sent from menus).
		internal static string Format(bool inGame, int max)
		{
			StringBuilder sb = new StringBuilder();
			for (int i = Math.Max(0, Lines.Count - max); i < Lines.Count; i++)
			{
				Line l = Lines[i];
				string stamp = (inGame && !double.IsNaN(l.Epoch)) ? MathUtils.GetUTCFromS(l.Epoch) : l.Clock;
				if (sb.Length > 0)
				{
					sb.Append('\n');
				}
				sb.Append('[').Append(stamp).Append("] ");
				sb.Append(l.System ? ("* " + l.Text) : (l.Name + ": " + l.Text));
			}
			return sb.ToString();
		}

		private static void AddBubble(string characterId, string name, string text)
		{
			if (string.IsNullOrEmpty(characterId))
			{
				return;
			}
			Bubbles.RemoveAll((Bubble b) => b.CharacterId == characterId);
			Bubble nb;
			nb.CharacterId = characterId;
			nb.Name = name;
			nb.Text = text;
			nb.Start = Time.unscaledTime;
			nb.Until = nb.Start + (name.Length + 2 + text.Length) / TypeCharsPerSecond + BubbleSeconds;
			Bubbles.Add(nb);
		}

		private static void AddMark(Vector3 pos, string name, string characterId, string label)
		{
			Marks.RemoveAll((Mark m) => m.Name == name);
			Mark mk;
			mk.Pos = pos;
			mk.Name = name;
			mk.CharacterId = characterId;
			mk.Label = label ?? "";
			mk.Until = Time.unscaledTime + MarkSeconds;
			Marks.Add(mk);
		}

		// 0 = fine, 1 = very bad: the worst colour class among the conditions the character shows.
		private static float Badness(CondOwner co)
		{
			if (co == null || co.mapConds == null)
			{
				return 0f;
			}
			if (!co.bAlive || co.HasCond("Unconscious"))
			{
				return 1f;
			}
			float worst = 0f;
			foreach (Condition c in co.mapConds.Values)
			{
				if (c == null || c.nDisplaySelf <= 0 || c.fCount <= 0.0)
				{
					continue;
				}
				switch (c.strColor)
				{
					case "Badish":
						worst = Mathf.Max(worst, 0.35f);
						break;
					case "Bad":
						worst = Mathf.Max(worst, 0.7f);
						break;
					case "BadVery":
						return 1f;
				}
			}
			return worst;
		}

		private static readonly Color Fine = new Color(0.3f, 0.9f, 0.35f);

		private static readonly Color Uneasy = new Color(1f, 0.85f, 0.2f);

		private static readonly Color Dire = new Color(1f, 0.25f, 0.2f);

		private static string StateColorHex(string characterId)
		{
			CondOwner co = null;
			if (!string.IsNullOrEmpty(characterId))
			{
				DataHandler.mapCOs.TryGetValue(characterId, out co);
			}
			float b = Badness(co);
			Color c = (b < 0.5f) ? Color.Lerp(Fine, Uneasy, b * 2f) : Color.Lerp(Uneasy, Dire, (b - 0.5f) * 2f);
			return ColorUtility.ToHtmlStringRGB(c);
		}

		// IMGUI rich text has no escaping; a zero-width space after '<' keeps player text from being read as a tag.
		private static string NoTags(string s)
		{
			return s.Replace("<", "<\u200B");
		}

		// ---- PDA app ------------------------------------------------------------------------------------------

		// Add our app to the PDA's list once the game's data is loaded (the home page and hotbar read it when built).
		public static void EnsurePdaApp()
		{
			if (DataHandler.dictPDAAppIcons == null || DataHandler.dictPDAAppIcons.ContainsKey(AppName))
			{
				return;
			}
			JsonPDAAppIcon app = new JsonPDAAppIcon();
			app.strName = AppName;
			app.strFriendlyName = "CHAT";
			app.strIcon = "pda_apps/IcoPDANotes";
			app.bHidden = false;
			DataHandler.dictPDAAppIcons[AppName] = app;
		}

		private static bool OpenAppPrefix(string appName)
		{
			if (appName != AppName)
			{
				PdaChat.Close();
				return true;
			}
			GUIPDA pda = GUIPDA.instance;
			if (pda != null && !PdaChat.Toggle(pda))
			{
				Plugin.Log.LogWarning("PDA chat could not be opened");
			}
			return false;
		}

		// ---- Drawing (IMGUI: main menu window, bubbles, pings) ---------------------------------------------------

		// Called first in Plugin.OnGUI: the rects collected on the last Repaint become the ones to block.
		public static void BeginGUI()
		{
			if (Event.current.type == EventType.Repaint && _blockingFrame != Time.frameCount)
			{
				_blockingFrame = Time.frameCount;
				Blocking.Clear();
				Blocking.AddRange(BlockingNext);
				BlockingNext.Clear();
			}
		}

		// Called from Plugin.OnGUI.
		public static void OnGUI()
		{
			bool focusedNow = false;
			if (MenuWindowOpen && Plugin.Menu != null)
			{
				if (_menuRect.x == 0f)
				{
					_menuRect.x = Screen.width - _menuRect.width - 20f;
					_menuRect.y = 60f;
				}
				_menuRect = GUILayout.Window(0x0B4F3, _menuRect, DrawMenuWindow, "CHAT", MenuLook.Window);
				AddBlocking(_menuRect);
				focusedNow = GUI.GetNameOfFocusedControl() == InputControl;
			}
			if (WorldSync.InGame)
			{
				EnsureStyles();
				DrawMarks();
				DrawBubbles();
			}
			if (Event.current.type == EventType.Repaint)
			{
				_focused = focusedNow;
			}
		}

		// Collected on the Repaint pass (OnGUI runs several times a frame); read next frame by MouseOverModUI.
		internal static void AddBlocking(Rect r)
		{
			if (Event.current != null && Event.current.type == EventType.Repaint)
			{
				BlockingNext.Add(r);
			}
		}

		private static void DrawMenuWindow(int id)
		{
			GUISkin prev = MenuLook.Use();
			try
			{
				DrawMenuWindowContent(id);
			}
			finally
			{
				MenuLook.Restore(prev);
			}
		}

		private static void DrawMenuWindowContent(int id)
		{
			_scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
			GUILayout.Label(Format(inGame: false, MaxLines));
			GUILayout.EndScrollView();
			if (_scrollToEnd && Event.current.type == EventType.Repaint)
			{
				_scroll.y = float.MaxValue;
				_scrollToEnd = false;
			}
			GUILayout.BeginHorizontal();
			// Enter sends; IMGUI reports it as a KeyDown on the focused field.
			bool enter = Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter) && GUI.GetNameOfFocusedControl() == InputControl;
			GUI.SetNextControlName(InputControl);
			_input = GUILayout.TextField(_input, 300);
			if (GUILayout.Button("Send", GUILayout.Width(60f)) || enter)
			{
				SendText(_input);
				_input = "";
				if (enter)
				{
					Event.current.Use();
				}
			}
			GUILayout.EndHorizontal();
			if (GUILayout.Button("Close"))
			{
				MenuWindowOpen = false;
			}
			GUI.DragWindow();
		}

		private static void EnsureStyles()
		{
			if (_bubbleStyle == null)
			{
				_bubbleStyle = new GUIStyle(GUI.skin.box);
				_bubbleStyle.wordWrap = true;
				_bubbleStyle.alignment = TextAnchor.MiddleCenter;
				_bubbleStyle.fontSize = 14;
				_bubbleStyle.normal.textColor = Color.white;
				_bubbleStyle.richText = true;
			}
			if (_speechStyle == null)
			{
				_speechStyle = new GUIStyle(GUI.skin.box);
				_speechStyle.wordWrap = true;
				_speechStyle.richText = true;
				_speechStyle.alignment = TextAnchor.UpperLeft;
				_speechStyle.fontSize = 14;
				_speechStyle.normal.textColor = Color.white;
				_speechStyle.padding = new RectOffset(8, 8, 5, 5);
			}
			if (_markStyle == null)
			{
				_markStyle = new GUIStyle(GUI.skin.label);
				_markStyle.alignment = TextAnchor.MiddleCenter;
				_markStyle.fontSize = 13;
				_markStyle.fontStyle = FontStyle.Bold;
				_markStyle.normal.textColor = Color.white;
			}
			if (_white == null)
			{
				_white = new Texture2D(1, 1);
				_white.SetPixel(0, 0, Color.white);
				_white.Apply();
			}
		}

		private static Camera GameCamera => (CrewSim.objInstance != null) ? CrewSim.objInstance.camMain : null;

		// World point to IMGUI point, and whether it's on screen.
		private static bool ToScreen(Camera cam, Vector3 world, out Vector2 p)
		{
			Vector3 sp = cam.WorldToScreenPoint(world);
			p = new Vector2(sp.x, Screen.height - sp.y);
			return p.x >= 0f && p.x <= Screen.width && p.y >= 0f && p.y <= Screen.height;
		}

		// A box of size w x h pinned to the screen edge on the side of target, with a tail pointing at it.
		private static Rect EdgeRect(Vector2 target, float w, float h, Color tail)
		{
			const float margin = 12f;
			float cx = Mathf.Clamp(target.x, margin + w / 2f, Screen.width - margin - w / 2f);
			float cy = Mathf.Clamp(target.y, margin + h / 2f, Screen.height - margin - h / 2f);
			Rect r = new Rect(cx - w / 2f, cy - h / 2f, w, h);
			Vector2 dir = (target - r.center).normalized;
			Vector2 start = r.center + new Vector2(dir.x * w / 2f, dir.y * h / 2f);
			DrawLine(start, start + dir * 28f, 3f, tail);
			return r;
		}

		private static void DrawBubbles()
		{
			Bubbles.RemoveAll((Bubble b) => Time.unscaledTime > b.Until);
			Camera cam = GameCamera;
			if (Bubbles.Count == 0 || cam == null)
			{
				return;
			}
			foreach (Bubble b in Bubbles)
			{
				CondOwner co;
				if (!DataHandler.mapCOs.TryGetValue(b.CharacterId, out co) || co == null || co.bDestroyed || co.tf == null)
				{
					continue;
				}
				Vector2 target;
				bool onScreen = ToScreen(cam, co.tf.position + new Vector3(0f, 0.9f, 0f), out target);
				// Sized for the whole message, so the bubble doesn't grow while it's being typed.
				string name = NoTags(b.Name);
				string text = NoTags(b.Text);
				GUIContent full = new GUIContent("<b>" + name + "</b>: " + text);
				float w = Mathf.Min(280f, _speechStyle.CalcSize(full).x + 4f);
				float h = _speechStyle.CalcHeight(full, w);
				Rect r = onScreen ? new Rect(target.x - w / 2f, target.y - h - 6f, w, h) : EdgeRect(target, w, h, Color.white);
				// Typewriter: the first n characters of "Name: text".
				int n = Mathf.FloorToInt((Time.unscaledTime - b.Start) * TypeCharsPerSecond);
				string shown;
				string color = "<color=#" + StateColorHex(b.CharacterId) + "><b>";
				if (n <= b.Name.Length)
				{
					shown = color + NoTags(b.Name.Substring(0, Mathf.Max(0, n))) + "</b></color>";
				}
				else
				{
					int t = Mathf.Min(b.Text.Length, n - b.Name.Length - 2);
					shown = color + name + "</b></color>:" + ((t >= 0) ? (" " + NoTags(b.Text.Substring(0, t))) : "");
				}
				GUI.Box(r, shown, _speechStyle);
			}
		}

		private static void DrawMarks()
		{
			Marks.RemoveAll((Mark m) => Time.unscaledTime > m.Until);
			Camera cam = GameCamera;
			if (Marks.Count == 0 || cam == null)
			{
				return;
			}
			Color gold = new Color(1f, 0.85f, 0.2f);
			foreach (Mark m in Marks)
			{
				Vector2 p;
				bool onScreen = ToScreen(cam, m.Pos, out p);
				if (onScreen)
				{
					// A pulsing diamond with the player's name under it.
					float s = 10f + 3f * Mathf.Sin(Time.unscaledTime * 6f);
					DrawLine(new Vector2(p.x - s, p.y), new Vector2(p.x, p.y - s), 3f, gold);
					DrawLine(new Vector2(p.x, p.y - s), new Vector2(p.x + s, p.y), 3f, gold);
					DrawLine(new Vector2(p.x + s, p.y), new Vector2(p.x, p.y + s), 3f, gold);
					DrawLine(new Vector2(p.x, p.y + s), new Vector2(p.x - s, p.y), 3f, gold);
					Color savedColor = GUI.color;
					GUI.color = HexColor(StateColorHex(m.CharacterId));
					GUI.Label(new Rect(p.x - 120f, p.y + 14f, 240f, 20f), (m.Label.Length > 0) ? (m.Name + ": " + m.Label) : m.Name, _markStyle);
					GUI.color = savedColor;
				}
				else
				{
					Rect r = EdgeRect(p, (m.Label.Length > 0) ? 220f : 120f, 24f, gold);
					GUI.Box(r, "◆ <color=#" + StateColorHex(m.CharacterId) + ">" + NoTags(m.Name) + "</color>" + ((m.Label.Length > 0) ? (": " + NoTags(m.Label)) : ""), _bubbleStyle);
				}
			}
		}

		private static Color HexColor(string hex)
		{
			Color c;
			return ColorUtility.TryParseHtmlString("#" + hex, out c) ? c : Color.white;
		}

		// The topmost person or item under the mouse (not floors, walls or rooms), if any; people first.
		private static CondOwner PointedAt(CondOwner me)
		{
			List<CondOwner> under;
			try
			{
				under = CrewSim.objInstance.GetMouseOverCOsExternal();
			}
			catch (Exception)
			{
				return null;
			}
			if (under == null)
			{
				return null;
			}
			CondOwner item = null;
			foreach (CondOwner co in under)
			{
				if (co == null || co.bDestroyed || co == me || co.tf == null || co.HasCond("IsRoom") || co.GetComponent<Tile>() != null)
				{
					continue;
				}
				if (co.Crew != null)
				{
					return co;
				}
				if (item == null && co.Item != null)
				{
					item = co;
				}
			}
			return item;
		}

		private static void DrawLine(Vector2 a, Vector2 b, float width, Color color)
		{
			Matrix4x4 saved = GUI.matrix;
			Color savedColor = GUI.color;
			GUI.color = color;
			float angle = Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg;
			GUIUtility.RotateAroundPivot(angle, a);
			GUI.DrawTexture(new Rect(a.x, a.y - width / 2f, (b - a).magnitude, width), _white);
			GUI.matrix = saved;
			GUI.color = savedColor;
		}

		// ---- Input --------------------------------------------------------------------------------------------

		internal static bool MouseOverModUI
		{
			get
			{
				Vector2 m = Ostranauts.InputControl.InputManager.MousePosition;
				Vector2 p = new Vector2(m.x, Screen.height - m.y);
				foreach (Rect r in Blocking)
				{
					if (r.Contains(p))
					{
						return true;
					}
				}
				return false;
			}
		}

		// No clicking into the world through our IMGUI windows.
		private static bool MouseHandlerPrefix()
		{
			return !MouseOverModUI;
		}

		// Called from Plugin.Update.
		public static void Update()
		{
			EnsurePdaApp();
			PdaChat.Update();
			if (CrewSim.objInstance == null)
			{
				_weSetTyping = false;
				return;
			}
			// While our IMGUI text field has focus the game's hotkeys (WASD, pause...) stay quiet.
			if (_focused && !CrewSim.Typing)
			{
				CrewSim.Typing = true;
				_weSetTyping = true;
			}
			else if (!_focused && _weSetTyping)
			{
				CrewSim.Typing = false;
				_weSetTyping = false;
			}
			// Ping: ~ (Ё) drops a mark where the mouse points.
			Keyboard kb = Keyboard.current;
			if (WorldSync.InGame && kb != null && kb.backquoteKey.wasPressedThisFrame && !CrewSim.Typing && !MouseOverModUI)
			{
				Camera cam = GameCamera;
				if (cam != null)
				{
					Vector2 m = Ostranauts.InputControl.InputManager.MousePosition;
					Vector3 w = cam.ScreenToWorldPoint(new Vector3(m.x, m.y, 0f - cam.transform.position.z));
					CondOwner me = MyCharacter();
					string id = (me != null) ? me.strID : "";
					// Pointing at a person or an item: the ping sits on it and names it.
					Vector3 at = new Vector3(w.x, w.y, 0f);
					string label = "";
					CondOwner thing = PointedAt(me);
					if (thing != null && thing.tf != null)
					{
						at = new Vector3(thing.tf.position.x, thing.tf.position.y, 0f);
						label = thing.FriendlyName ?? "";
					}
					AddMark(at, Plugin.PlayerName, id, label);
					if (Plugin.Net.Connected)
					{
						Plugin.Net.Send("MARK " + at.x.ToString("R", Inv) + "|" + at.y.ToString("R", Inv) + "|" + id + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(label)));
					}
				}
			}
		}
	}
}
