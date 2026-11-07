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
	// Two kinds of messages:
	// - Speech: Enter in the game opens a one-line field (quick chat). What's typed is said by your character: a bubble,
	//   typed out like a typewriter as "Name: text" over them, then gone after a few seconds (if the character is off
	//   screen, the bubble sits at the screen edge with a tail pointing at them). It isn't kept anywhere.
	// - PDA messages: "/pda text" in the quick chat (or PDA mode: "/pda" alone switches the quick chat to it and back),
	//   and everything typed in the main menu chat window. These are records in the "CHAT" app on the PDA (PdaChat):
	//   the sender's avatar and colour, the in-game time, the text; each can be deleted, or the whole chat. They are kept
	//   with the player's profile on this computer, across games and sessions (PdaLog). Notices (joined, left, the
	//   mod's own) are records too, for this session only. The app is an entry added to the PDA's app list (DataHandler.dictPDAAppIcons)
	//   that GUIPDA.OpenApp is patched to handle.
	// The ~ key (Ё on a Russian layout, same physical key) drops a ping where the mouse points; both players see it.
	// Names in bubbles and pings are coloured by how the speaker's character is doing: green (fine) to red (bad),
	// from the worst condition they show (the game's own Good / Badish / Bad / BadVery colour classes).
	//
	// Protocol: NAME <name>; SAY <characterID>|<epoch>|<text> (speech); CHAT <message ID>|<profile ID>|<epoch>|<colour
	// hex>|<name>|<text> (a PDA message; epoch empty when not in game);
	// MARK <x>|<y>|<characterID>|<base64 label>|<colour hex>|<target person ID>. The ping sits where the mouse was; if
	// that was over an item, it names it. Over a person, it names them and rings them, following them while it lasts. Each player picks their ping colour in the Multiplayer window.
	// "/me text" in chat is a role-play action: shown as "* <character name> text", in the bubble in italics.
	public static class Chat
	{
		private const string AppName = "mpchat";

		private const int MaxLines = 200;

		// How long a bubble stays after it's fully typed.
		private const float BubbleSeconds = 5f;

		// Bubbles one speaker can have at once (older ones above the newest).
		private const int MaxBubbles = 3;

		// Where each bubble is drawn above its speaker (pixels up), easing towards its place in the stack.
		private static readonly Dictionary<string, float> BubbleLift = new Dictionary<string, float>();

		private const float TypeCharsPerSecond = 30f;

		private const float MarkSeconds = 8f;

		private const string InputControl = "mp_chat_input";

		private struct Line
		{
			public double Epoch;

			// Real time (UTC), to sort with the saved messages.
			public long Ticks;

			public string Clock;

			public string Name;

			public string Text;

			public bool System;

			// "/me": Name is the character's name, Text the action.
			public bool Action;

			// The sender's colour (system lines: grey).
			public Color Color;

			// For deleting one line.
			public int Id;
		}

		private struct Bubble
		{
			public string CharacterId;

			public string Name;

			public string Text;

			public bool Action;

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

			public Color Color;

			// The person the ping rings, or null.
			public string TargetId;

			public float Start;

			public float Until;
		}

		private static readonly List<Line> Lines = new List<Line>();

		private static int _nextLineId;

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

		// Quick chat: Enter in the game opens a one-line field; Enter says it (bubble), Esc closes it.
		private const string QuickControl = "MPQuickChat";

		private static bool _quickOpen;

		private static string _quickText = "";

		private static int _quickOpenedFrame;

		// Quick chat sends to the PDA instead of speaking ("/pda" alone switches).
		private static bool _pdaMode;

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
			PdaLog.SendAvatar();
			PdaProfile.SendBio();
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
				case "SAY":
				{
					string[] f = arg.Split(new char[] { '|' }, 3);
					if (f.Length == 3)
					{
						AddSpeech(f[0], (PeerName != null) ? PeerName : "Player", f[2]);
					}
					return true;
				}
				case "CHAT":
				{
					string[] f = arg.Split(new char[] { '|' }, 6);
					if (f.Length != 6)
					{
						return true;
					}
					double epoch;
					if (!double.TryParse(f[2], NumberStyles.Float, Inv, out epoch))
					{
						epoch = double.NaN;
					}
					string name = (f[4].Length > 0) ? f[4] : ((PeerName != null) ? PeerName : "Player");
					AddRecord(f[0], f[1], name, f[5], epoch, f[3]);
					string rest;
					bool action = IsAction(f[5], out rest);
					Color color;
					PdaNotify.Show(f[1], name, action ? rest : f[5], ColorUtility.TryParseHtmlString("#" + f[3], out color) ? color : Color.white, action);
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
						Color color = new Color(1f, 0.85f, 0.2f);
						Color parsed;
						if (f.Length >= 5 && ColorUtility.TryParseHtmlString("#" + f[4], out parsed))
						{
							color = parsed;
						}
						AddMark(new Vector3(x, y, 0f), (PeerName != null) ? PeerName : "Player", f[2], label, color, (f.Length >= 6) ? f[5] : null);
					}
					return true;
				}
				default:
					return false;
			}
		}

		// Main menu chat window: always a PDA message. Quick chat: speech, unless "/pda ..." or PDA mode.
		internal static void SendText(string raw)
		{
			Send(raw, WorldSync.InGame ? _pdaMode : true);
		}

		private static void Send(string raw, bool toPda)
		{
			string text = Clean(raw).Trim();
			if (text.Length == 0)
			{
				return;
			}
			if (text.Equals("/sync", StringComparison.OrdinalIgnoreCase))
			{
				Apart.SyncCommand();
				return;
			}
			if (text.Equals("/fixships", StringComparison.OrdinalIgnoreCase))
			{
				Mirror.FixShipsCommand();
				return;
			}
			if (text.Equals("/pda", StringComparison.OrdinalIgnoreCase))
			{
				_pdaMode = !_pdaMode;
				return;
			}
			if (text.StartsWith("/pda ", StringComparison.OrdinalIgnoreCase))
			{
				text = text.Substring(5).Trim();
				toPda = true;
				if (text.Length == 0)
				{
					return;
				}
			}
			CondOwner me = MyCharacter();
			double epoch = WorldSync.InGame ? StarSystem.fEpoch : double.NaN;
			string id = (me != null) ? me.strID : "";
			if (!toPda && me == null)
			{
				// Nobody to say it: it goes to the PDA instead.
				toPda = true;
			}
			string msgId = PdaLog.NewId();
			string profileId = (Profile.Current != null) ? Profile.Current.Id : "";
			string name = SpeakerName(id, Plugin.PlayerName).Replace("|", "/");
			if (Plugin.Net.Connected)
			{
				if (toPda)
				{
					Plugin.Net.Send("CHAT " + msgId + "|" + profileId + "|" + (double.IsNaN(epoch) ? "" : epoch.ToString("R", Inv)) + "|" + Plugin.MarkColorHex + "|" + name + "|" + text);
				}
				else
				{
					Plugin.Net.Send("SAY " + id + "|" + (double.IsNaN(epoch) ? "" : epoch.ToString("R", Inv)) + "|" + text);
				}
			}
			else if (Plugin.Net.CurrentMode == NetSession.Mode.None)
			{
				AddSystem("Not connected");
			}
			if (toPda)
			{
				AddRecord(msgId, profileId, name, text, epoch, Plugin.MarkColorHex);
			}
			else
			{
				AddSpeech(id, Plugin.PlayerName, text);
			}
		}

		// In the game the character's name, in the menus the player's.
		private static string SpeakerName(string characterId, string player)
		{
			CondOwner co;
			return (!string.IsNullOrEmpty(characterId) && DataHandler.mapCOs.TryGetValue(characterId, out co) && co != null && !string.IsNullOrEmpty(co.strName)) ? co.strName : player;
		}

		// Speech: a bubble over the character, kept nowhere. "/me ..." is an action.
		private static void AddSpeech(string characterId, string player, string text)
		{
			string speaker = SpeakerName(characterId, player);
			string rest;
			if (IsAction(text, out rest))
			{
				AddBubble(characterId, speaker, rest, action: true);
				return;
			}
			AddBubble(characterId, speaker, text);
		}

		// A PDA message: into this player's profile's log (PdaLog), in the game and in the menus.
		private static void AddRecord(string msgId, string profileId, string speaker, string text, double epoch, string hex)
		{
			string rest;
			bool action = IsAction(text, out rest);
			if (action)
			{
				text = rest;
			}
			PdaLog.Entry e = new PdaLog.Entry();
			e.Id = msgId;
			e.Ticks = DateTime.UtcNow.Ticks;
			e.Epoch = epoch;
			e.Hex = hex;
			e.ProfileId = profileId;
			e.Action = action;
			e.Name = speaker;
			e.Text = text;
			PdaLog.Add(e);
			Version++;
			_scrollToEnd = true;
			Plugin.Log.LogInfo("[PDA] " + speaker + ": " + text);
		}

		private static bool IsAction(string text, out string rest)
		{
			rest = null;
			if (text.Length > 4 && text.StartsWith("/me ", StringComparison.OrdinalIgnoreCase))
			{
				rest = text.Substring(4).Trim();
				return rest.Length > 0;
			}
			return false;
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

		internal static void AddSystem(string text)
		{
			AddLine(null, text, WorldSync.InGame ? StarSystem.fEpoch : double.NaN, true, false, new Color(0.6f, 0.6f, 0.6f));
		}

		private static void AddLine(string name, string text, double epoch, bool system, bool action, Color color)
		{
			Line l;
			l.Color = color;
			l.Id = ++_nextLineId;
			l.Action = action;
			l.Epoch = epoch;
			l.Ticks = DateTime.UtcNow.Ticks;
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
			Plugin.Log.LogInfo("[Chat] " + (system ? "* " : (action ? ("* " + name + " ") : (name + ": "))) + text);
		}

		// For the PDA page and the menu window: the profile's saved messages (PdaLog) and this session's notices, by real
		// time.
		internal struct Record
		{
			public string Id;

			public double Epoch;

			public long Ticks;

			public string Clock;

			public string Name;

			public string Text;

			public bool System;

			public bool Action;

			public Color Color;

			public string ProfileId;
		}

		internal static int RecordsVersion => Version + PdaLog.Version * 7919;

		internal static List<Record> Records()
		{
			List<Record> list = new List<Record>();
			foreach (Line l in Lines)
			{
				Record r;
				r.Id = "n:" + l.Id;
				r.Epoch = l.Epoch;
				r.Ticks = l.Ticks;
				r.Clock = l.Clock;
				r.Name = l.Name;
				r.Text = l.Text;
				r.System = true;
				r.Action = false;
				r.Color = l.Color;
				r.ProfileId = null;
				list.Add(r);
			}
			foreach (PdaLog.Entry e in PdaLog.Read())
			{
				Record r;
				r.Id = "m:" + e.Id;
				r.Epoch = e.Epoch;
				r.Ticks = e.Ticks;
				r.Clock = new DateTime(e.Ticks, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm");
				r.Name = e.Name;
				r.Text = e.Text;
				r.System = false;
				r.Action = e.Action;
				Color c;
				r.Color = ColorUtility.TryParseHtmlString("#" + e.Hex, out c) ? c : Color.white;
				r.ProfileId = e.ProfileId;
				list.Add(r);
			}
			list.Sort((Record a, Record b) => a.Ticks.CompareTo(b.Ticks));
			return list;
		}

		internal static void DeleteRecord(string id)
		{
			if (id.StartsWith("m:", StringComparison.Ordinal))
			{
				PdaLog.Remove(id.Substring(2));
				return;
			}
			int n;
			if (int.TryParse(id.Substring(2), out n))
			{
				Lines.RemoveAll((Line l) => l.Id == n);
				Version++;
			}
		}

		internal static void ClearRecords()
		{
			PdaLog.Clear();
			Lines.Clear();
			Version++;
		}

		// The last <max> records; in game with the in-game time they were sent at (real time for those sent from menus).
		internal static string Format(bool inGame, int max)
		{
			List<Record> records = Records();
			StringBuilder sb = new StringBuilder();
			for (int i = Math.Max(0, records.Count - max); i < records.Count; i++)
			{
				Record l = records[i];
				string stamp = (inGame && !double.IsNaN(l.Epoch)) ? MathUtils.GetUTCFromS(l.Epoch) : l.Clock;
				if (sb.Length > 0)
				{
					sb.Append('\n');
				}
				sb.Append('[').Append(stamp).Append("] ");
				sb.Append(l.System ? ("* " + l.Text) : (l.Action ? ("* " + l.Name + " " + l.Text) : (l.Name + ": " + l.Text)));
			}
			return sb.ToString();
		}

		private static void AddBubble(string characterId, string name, string text, bool action = false)
		{
			if (string.IsNullOrEmpty(characterId))
			{
				return;
			}
			// Up to MaxBubbles per speaker: the new one at the bottom, the older ones pushed up, each on its own timer.
			int mine = 0;
			for (int i = Bubbles.Count - 1; i >= 0; i--)
			{
				if (Bubbles[i].CharacterId == characterId && ++mine >= MaxBubbles)
				{
					Bubbles.RemoveAt(i);
				}
			}
			Bubble nb;
			nb.CharacterId = characterId;
			nb.Name = name;
			nb.Text = text;
			nb.Action = action;
			nb.Start = Time.unscaledTime;
			nb.Until = nb.Start + (name.Length + 2 + text.Length) / TypeCharsPerSecond + BubbleSeconds;
			Bubbles.Add(nb);
		}

		private static void AddMark(Vector3 pos, string name, string characterId, string label, Color color, string targetId = null)
		{
			Marks.RemoveAll((Mark m) => m.Name == name);
			Mark mk;
			mk.Pos = pos;
			mk.TargetId = string.IsNullOrEmpty(targetId) ? null : targetId;
			mk.Name = name;
			mk.CharacterId = characterId;
			mk.Label = label ?? "";
			mk.Color = color;
			mk.Start = Time.unscaledTime;
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
			PdaProfile.Register();
		}

		private static bool OpenAppPrefix(string appName)
		{
			if (appName == PdaProfile.AppName)
			{
				PdaChat.Close();
				GUIPDA ppda = GUIPDA.instance;
				if (ppda != null && !PdaProfile.Toggle(ppda))
				{
					Plugin.Log.LogWarning("PDA profile could not be opened");
				}
				return false;
			}
			PdaProfile.Close();
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
			if (_quickOpen && !WorldSync.InGame)
			{
				_quickOpen = false;
			}
			if (_quickOpen)
			{
				DrawQuick();
				focusedNow = focusedNow || GUI.GetNameOfFocusedControl() == QuickControl;
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

		// Commands offered over the quick chat: what's typed so far picks them, a click or Tab puts one in.
		private static readonly string[][] QuickCommands =
		{
			new[] { "/pda", "switch between speaking and the PDA chat" },
			new[] { "/pda ", "write one message to the PDA chat" },
			new[] { "/me ", "an action of your character" },
			new[] { "/sync", "host: take the other player's place now" },
			new[] { "/fixships", "host: give ships back to the captain" }
		};

		private static List<string[]> QuickMatches()
		{
			List<string[]> list = new List<string[]>();
			string t = _quickText ?? "";
			if (t.Length > 0 && !t.StartsWith("/", StringComparison.Ordinal))
			{
				return list;
			}
			foreach (string[] c in QuickCommands)
			{
				// Once a command and its text are being typed, nothing more to offer.
				if (t.Length > c[0].Length && t.StartsWith(c[0], StringComparison.OrdinalIgnoreCase) && c[0].EndsWith(" ", StringComparison.Ordinal))
				{
					return new List<string[]>();
				}
				if (c[0].StartsWith(t, StringComparison.OrdinalIgnoreCase))
				{
					list.Add(c);
				}
			}
			return list;
		}

		private static void PickQuick(string cmd)
		{
			if (cmd == "/pda")
			{
				// Switches right away, the field stays open.
				_pdaMode = !_pdaMode;
				_quickText = "";
			}
			else
			{
				_quickText = cmd;
			}
			_quickCaretToEnd = true;
		}

		private static bool _quickCaretToEnd;

		private static GUIStyle _quickHint;

		private static void DrawQuick()
		{
			Event e = Event.current;
			List<string[]> matches = QuickMatches();
			// The Enter that opened it arrives here in the same frame; it isn't the one that sends.
			if (e.type == EventType.KeyDown && Time.frameCount != _quickOpenedFrame)
			{
				if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
				{
					if (_quickText.Trim().Length > 0)
					{
						SendText(_quickText);
					}
					_quickOpen = false;
					_quickText = "";
					e.Use();
					return;
				}
				if (e.keyCode == KeyCode.Escape)
				{
					_quickOpen = false;
					_quickText = "";
					e.Use();
					return;
				}
				if (e.keyCode == KeyCode.Tab && matches.Count > 0 && _quickText.Length > 0)
				{
					PickQuick(matches[0][0]);
					e.Use();
					return;
				}
			}
			float w = Mathf.Min(560f, Screen.width - 40f);
			float x = (Screen.width - w) / 2f;
			Rect r = new Rect(x + 64f, Screen.height - 120f, w - 64f, 34f);
			Rect mode = new Rect(x, r.y, 60f, r.height);
			GUISkin prev = MenuLook.Use();
			try
			{
				if (_quickHint == null)
				{
					_quickHint = new GUIStyle(GUI.skin.label);
					_quickHint.richText = true;
					_quickHint.alignment = TextAnchor.MiddleLeft;
					_quickHint.fontSize = 18;
				}
				// Where it goes: said aloud, or the PDA chat.
				Color c = GUI.color;
				bool pda = _pdaMode || _quickText.StartsWith("/pda ", StringComparison.OrdinalIgnoreCase);
				GUI.color = pda ? new Color(0.6f, 0.85f, 1f) : Color.white;
				if (GUI.Button(mode, pda ? "PDA" : "SAY"))
				{
					_pdaMode = !_pdaMode;
				}
				GUI.color = c;
				// The offered commands, listed upwards from the field.
				float y = r.y;
				foreach (string[] m in matches)
				{
					y -= 32f;
					Rect row = new Rect(r.x, y, r.width, 30f);
					GUI.Box(row, GUIContent.none);
					string label = (m[0] == "/pda") ? (_pdaMode ? "back to speaking" : "switch to the PDA chat") : m[1];
					if (GUI.Button(row, "<b>" + m[0].Trim() + "</b>  " + label, _quickHint))
					{
						PickQuick(m[0]);
					}
					AddBlocking(row);
				}
				GUI.SetNextControlName(QuickControl);
				_quickText = GUI.TextField(r, _quickText, 300);
				if (GUI.GetNameOfFocusedControl() != QuickControl)
				{
					GUI.FocusControl(QuickControl);
					// A field that gets the focus selects all its text, and the next key typed would replace it
					// (the first letter lost): the caret goes to the end instead, as soon as the focus is there.
					_quickCaretToEnd = true;
				}
				else if (_quickCaretToEnd)
				{
					TextEditor te = GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl) as TextEditor;
					if (te != null)
					{
						te.MoveTextEnd();
					}
					_quickCaretToEnd = false;
				}
			}
			finally
			{
				MenuLook.Restore(prev);
			}
			AddBlocking(r);
			AddBlocking(mode);
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
				_bubbleStyle.fontSize = 18;
				_bubbleStyle.normal.textColor = Color.white;
				_bubbleStyle.richText = true;
			}
			if (_speechStyle == null)
			{
				_speechStyle = new GUIStyle(GUI.skin.box);
				_speechStyle.wordWrap = true;
				_speechStyle.richText = true;
				_speechStyle.alignment = TextAnchor.UpperLeft;
				_speechStyle.fontSize = 18;
				_speechStyle.normal.textColor = Color.white;
				_speechStyle.padding = new RectOffset(8, 8, 5, 5);
			}
			if (_markStyle == null)
			{
				_markStyle = new GUIStyle(GUI.skin.label);
				_markStyle.alignment = TextAnchor.MiddleCenter;
				_markStyle.fontSize = 18;
				_markStyle.richText = true;
				_markStyle.wordWrap = true;
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
			// Newest first, so each speaker's stack is built from the bottom up.
			Dictionary<string, float> stackTop = new Dictionary<string, float>();
			HashSet<string> alive = new HashSet<string>();
			for (int bi = Bubbles.Count - 1; bi >= 0; bi--)
			{
				Bubble b = Bubbles[bi];
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
				string sep = b.Action ? " " : ": ";
				GUIContent full = new GUIContent((b.Action ? "<i>" : "") + "<b>" + name + "</b>" + sep + text + (b.Action ? "</i>" : ""));
				float w = Mathf.Min(380f, _speechStyle.CalcSize(full).x + 4f);
				float h = _speechStyle.CalcHeight(full, w);
				Rect r = onScreen ? new Rect(target.x - w / 2f, target.y - h - 6f, w, h) : EdgeRect(target, w, h, Color.white);
				// Its place in the speaker's stack: above the newer ones; it slides up there.
				float below;
				stackTop.TryGetValue(b.CharacterId, out below);
				stackTop[b.CharacterId] = below + h + 4f;
				string key = b.CharacterId + "|" + b.Start.ToString("R", Inv);
				alive.Add(key);
				float lift;
				lift = BubbleLift.TryGetValue(key, out lift) ? Mathf.Lerp(lift, below, Mathf.Clamp01(Time.unscaledDeltaTime * 10f)) : below;
				BubbleLift[key] = lift;
				r.y -= lift;
				// The older ones fade over their last half second.
				Color keepColor = GUI.color;
				GUI.color = new Color(keepColor.r, keepColor.g, keepColor.b, keepColor.a * Mathf.Clamp01((b.Until - Time.unscaledTime) * 2f));
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
					int t = Mathf.Min(b.Text.Length, n - b.Name.Length - sep.Length);
					shown = color + name + "</b></color>" + sep.TrimEnd() + ((t >= 0) ? (" " + NoTags(b.Text.Substring(0, t))) : "");
				}
				if (b.Action)
				{
					shown = "<i>" + shown + "</i>";
				}
				GUI.Box(r, shown, _speechStyle);
				GUI.color = keepColor;
			}
			if (Event.current.type == EventType.Repaint)
			{
				List<string> gone = new List<string>();
				foreach (string key in BubbleLift.Keys)
				{
					if (!alive.Contains(key))
					{
						gone.Add(key);
					}
				}
				foreach (string key in gone)
				{
					BubbleLift.Remove(key);
				}
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
			foreach (Mark m in Marks)
			{
				float age = Time.unscaledTime - m.Start;
				// Fades out over its last second.
				float fade = Mathf.Clamp01(m.Until - Time.unscaledTime);
				Color c = m.Color;
				c.a = fade;
				string text = "<color=#" + StateColorHex(m.CharacterId) + "><b>" + NoTags(m.Name) + "</b></color>" + ((m.Label.Length > 0) ? (": " + NoTags(m.Label)) : "");
				GUIContent content = new GUIContent(text);
				float tw = Mathf.Min(420f, _markStyle.CalcSize(content).x + 12f);
				float th = _markStyle.CalcHeight(content, tw) + 4f;
				Vector2 p;
				CondOwner target = MarkTarget(m);
				Vector3 at = (target != null) ? target.tf.position : m.Pos;
				bool onScreen = ToScreen(cam, at, out p);
				if (onScreen && target != null)
				{
					// A person: a ring around them, rippling out, the label under it.
					Vector2 edge;
					ToScreen(cam, at + Vector3.right * PersonRadius(target), out edge);
					float r = Mathf.Max(14f, (edge - p).magnitude);
					float pop = 1f + 0.6f * Mathf.Clamp01(1f - age / 0.3f);
					for (int k = 0; k < 2; k++)
					{
						float phase = (age * 0.9f + k * 0.5f) % 1f;
						Color rc = c;
						rc.a = fade * (1f - phase) * 0.8f;
						DrawCircle(p, r + 30f * phase, 2f, rc);
					}
					float ring = r * pop + 1.5f * Mathf.Sin(age * 6f);
					Color shadow = new Color(0f, 0f, 0f, 0.6f * fade);
					DrawCircle(p + new Vector2(2f, 2f), ring, 4f, shadow);
					DrawCircle(p, ring, 4f, c);
					Rect label = new Rect(p.x - tw / 2f, p.y + ring + 8f, tw, th);
					DrawBox(label, new Color(0f, 0f, 0f, 0.6f * fade));
					DrawBox(new Rect(label.x, label.y, 3f, label.height), c);
					Color saved = GUI.color;
					GUI.color = new Color(1f, 1f, 1f, fade);
					GUI.Label(label, content, _markStyle);
					GUI.color = saved;
				}
				else if (onScreen)
				{
					// Pops in, then pulses, with two rings rippling out.
					float pop = 1f + 1.2f * Mathf.Clamp01(1f - age / 0.3f);
					for (int k = 0; k < 2; k++)
					{
						float phase = (age * 0.9f + k * 0.5f) % 1f;
						Color rc = c;
						rc.a = fade * (1f - phase) * 0.8f;
						DrawCircle(p, 10f + 46f * phase, 3f, rc);
					}
					float s = (14f + 3f * Mathf.Sin(age * 6f)) * pop;
					Color shadow = new Color(0f, 0f, 0f, 0.6f * fade);
					DrawDiamond(p + new Vector2(2f, 2f), s, 5f, shadow);
					DrawDiamond(p, s, 5f, c);
					DrawBox(new Rect(p.x - 3f, p.y - 3f, 6f, 6f), c);
					Rect label = new Rect(p.x - tw / 2f, p.y + s + 8f, tw, th);
					DrawBox(label, new Color(0f, 0f, 0f, 0.6f * fade));
					DrawBox(new Rect(label.x, label.y, 3f, label.height), c);
					Color saved = GUI.color;
					GUI.color = new Color(1f, 1f, 1f, fade);
					GUI.Label(label, content, _markStyle);
					GUI.color = saved;
				}
				else
				{
					Rect r = EdgeRect(p, tw + 24f, th, c);
					DrawBox(r, new Color(0f, 0f, 0f, 0.6f * fade));
					DrawBox(new Rect(r.x, r.y, 3f, r.height), c);
					Color saved = GUI.color;
					GUI.color = new Color(1f, 1f, 1f, fade);
					GUI.Label(new Rect(r.x + 12f, r.y, r.width - 12f, r.height), content, _markStyle);
					GUI.color = saved;
				}
			}
		}

		// The person a ping follows, while they're here and shown.
		private static CondOwner MarkTarget(Mark m)
		{
			CondOwner co;
			if (m.TargetId != null && DataHandler.mapCOs.TryGetValue(m.TargetId, out co) && co != null && !co.bDestroyed && co.tf != null && co.ship != null && co.Visible)
			{
				return co;
			}
			return null;
		}

		// Half the person's drawn size, in world units.
		private static float PersonRadius(CondOwner co)
		{
			Renderer r = co.GetComponent<Renderer>();
			if (r == null)
			{
				return 0.5f;
			}
			Vector3 e = r.bounds.extents;
			return Mathf.Max(e.x, e.y);
		}

		// The person drawn under this world point, if any (nearest centre wins).
		private static CondOwner PersonAt(Vector3 w, CondOwner me)
		{
			List<Ship> ships = CrewSim.GetAllLoadedShips();
			if (ships == null)
			{
				return null;
			}
			CondOwner best = null;
			float bestD = float.MaxValue;
			foreach (Ship ship in ships)
			{
				if (ship == null || ship.bDestroyed)
				{
					continue;
				}
				foreach (CondOwner co in ship.GetPeople(bAllowDocked: false))
				{
					if (co == null || co.bDestroyed || co == me || co.tf == null || !co.Visible)
					{
						continue;
					}
					Vector2 d = new Vector2(w.x - co.tf.position.x, w.y - co.tf.position.y);
					float radius = PersonRadius(co);
					if (d.magnitude <= radius && d.magnitude < bestD)
					{
						best = co;
						bestD = d.magnitude;
					}
				}
			}
			return best;
		}

		private static void DrawDiamond(Vector2 p, float s, float width, Color color)
		{
			DrawLine(new Vector2(p.x - s, p.y), new Vector2(p.x, p.y - s), width, color);
			DrawLine(new Vector2(p.x, p.y - s), new Vector2(p.x + s, p.y), width, color);
			DrawLine(new Vector2(p.x + s, p.y), new Vector2(p.x, p.y + s), width, color);
			DrawLine(new Vector2(p.x, p.y + s), new Vector2(p.x - s, p.y), width, color);
		}

		private static void DrawCircle(Vector2 p, float radius, float width, Color color)
		{
			const int segments = 24;
			Vector2 prev = p + new Vector2(radius, 0f);
			for (int i = 1; i <= segments; i++)
			{
				float a = i * Mathf.PI * 2f / segments;
				Vector2 next = p + new Vector2(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius);
				DrawLine(prev, next, width, color);
				prev = next;
			}
		}

		private static void DrawBox(Rect r, Color color)
		{
			Color saved = GUI.color;
			GUI.color = color;
			GUI.DrawTexture(r, _white);
			GUI.color = saved;
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
			PdaProfile.Update();
			if (CrewSim.objInstance == null)
			{
				_weSetTyping = false;
				return;
			}
			// While our IMGUI text field has focus the game's hotkeys (WASD, pause...) stay quiet.
			bool typing = _focused || PdaProfile.Editing;
			if (typing && !CrewSim.Typing)
			{
				CrewSim.Typing = true;
				_weSetTyping = true;
			}
			else if (!typing && _weSetTyping)
			{
				CrewSim.Typing = false;
				_weSetTyping = false;
			}
			Keyboard kb = Keyboard.current;
			// Quick chat: Enter opens it when nothing else is being typed into; also while hosting with nobody connected yet.
			if (!_quickOpen && WorldSync.InGame && Plugin.Net.CurrentMode != NetSession.Mode.None && kb != null && (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame) && !CrewSim.Typing)
			{
				_quickOpen = true;
				_quickText = "";
				_quickOpenedFrame = Time.frameCount;
				// Right away, so the first letters typed don't reach the game's hotkeys.
				CrewSim.Typing = true;
				_weSetTyping = true;
				_focused = true;
			}
			// Ping: ~ (Ё) drops a mark where the mouse points.
			if (WorldSync.InGame && kb != null && kb.backquoteKey.wasPressedThisFrame && !CrewSim.Typing && !MouseOverModUI)
			{
				Camera cam = GameCamera;
				if (cam != null)
				{
					Vector2 m = Ostranauts.InputControl.InputManager.MousePosition;
					Vector3 w = cam.ScreenToWorldPoint(new Vector3(m.x, m.y, 0f - cam.transform.position.z));
					CondOwner me = MyCharacter();
					string id = (me != null) ? me.strID : "";
					// Where the mouse is; over a person or an item, the ping names it.
					Vector3 at = new Vector3(w.x, w.y, 0f);
					string label = "";
					string target = "";
					CondOwner person = PersonAt(at, me);
					if (person != null)
					{
						label = person.FriendlyName ?? "";
						target = person.strID;
						at = new Vector3(person.tf.position.x, person.tf.position.y, 0f);
					}
					else
					{
						CondOwner thing = PointedAt(me);
						if (thing != null)
						{
							label = thing.FriendlyName ?? "";
						}
					}
					AddMark(at, Plugin.PlayerName, id, label, Plugin.MarkColor, target);
					if (Plugin.Net.Connected)
					{
						Plugin.Net.Send("MARK " + at.x.ToString("R", Inv) + "|" + at.y.ToString("R", Inv) + "|" + id + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(label)) + "|" + Plugin.MarkColorHex + "|" + target);
					}
				}
			}
		}
	}
}
