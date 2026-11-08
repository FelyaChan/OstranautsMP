using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Ostranauts.Core;
using Ostranauts.Core.Models;
using UnityEngine;

namespace OstranautsMP
{
	[BepInPlugin(Guid, PluginName, Version)]
	public class Plugin : BaseUnityPlugin
	{
		public const string Guid = "fcona.ostranauts.multiplayer";

		public const string PluginName = "Ostranauts Multiplayer";

		public const string Version = "0.40.2";

		internal static ManualLogSource Log;

		internal static MainMenu Menu;

		internal static readonly NetSession Net = new NetSession();

		private const int WindowId = 0x0B4F1;

		private const string DiscordUrl = "https://discord.gg/TnVpW7yBxG";

		private const string GitHubUrl = "https://github.com/FelyaChan/OstranautsMP";

		private static GUIStyle _reportStyle;

		private static ConfigEntry<string> _playerName;

		private static ConfigEntry<string> _lastAddress;

		private static ConfigEntry<string> _lastPort;

		private static ConfigEntry<string> _markColor;

		private static ConfigEntry<bool> _highlights;

		internal static bool HighlightsOn => _highlights == null || _highlights.Value;

		// Ping colours offered in the Multiplayer window.
		private static readonly string[] MarkPalette = { "FFD933", "FF8A1F", "FF4D4D", "FF5FD2", "B06CFF", "4DA6FF", "2BD9C8", "6BFF6B", "F2F2F2" };

		private static Texture2D[] _swatches;

		private static Texture2D _swatchEdge;

		// This player's ping colour (hex, no '#').
		internal static string MarkColorHex
		{
			get
			{
				string v = (_markColor != null) ? _markColor.Value : null;
				Color c;
				return (v != null && ColorUtility.TryParseHtmlString("#" + v, out c)) ? v : MarkPalette[0];
			}
		}

		internal static Color MarkColor
		{
			get
			{
				Color c;
				return ColorUtility.TryParseHtmlString("#" + MarkColorHex, out c) ? c : new Color(1f, 0.85f, 0.2f);
			}
		}

		internal static string PlayerName
		{
			get
			{
				string n = (_playerName != null) ? _playerName.Value : null;
				return string.IsNullOrEmpty(n) ? "Player" : n;
			}
		}

		// Whether we have set Menu.bClickable = false. Reset when a new main menu starts.
		private static bool _menuBlocked;

		private bool _windowOpen;

		private Rect _windowRect = new Rect(0f, 0f, MenuLook.Big(380f), 10f);

		private static readonly Rect ButtonRect = new Rect(20f, 0f, 160f, 40f);

		private string _address = "127.0.0.1";

		private string _port = "7777";

		// The name being typed; saved with the Save button.
		private string _nameEdit = "";

		private float _nextPing;

		private float _nextMemoryLog;

		private float _nextLogFlush;

		private void Awake()
		{
			InstanceLog.Start();
			Log = Logger;
			_playerName = Config.Bind("General", "PlayerName", "Player", "Your name in multiplayer chat.");
			_lastAddress = Config.Bind("General", "LastAddress", "127.0.0.1", "Host address used for the last Join.");
			_lastPort = Config.Bind("General", "LastPort", "7777", "Port used for the last Host or Join.");
			_markColor = Config.Bind("General", "MarkColor", MarkPalette[0], "Colour of your pings (~ key), hex RRGGBB.");
			_highlights = Config.Bind("General", "Highlights", true, "Your lockers light up in your colour under the mouse; the other player's character has a faint aura in their colour while yours can see them.");
			Apart.Init(Config);
			Profile.Load();
			_address = _lastAddress.Value;
			_port = _lastPort.Value;
			_nameEdit = PlayerName;
			Net.OnMessage = RouteMessage;
			Net.OnConnected = OnPeerConnected;
			Net.OnBinary = RouteBinary;
			Net.OnPeerLost = delegate(string reason)
			{
				string who = (Chat.PeerName != null) ? Chat.PeerName : ((Net.CurrentMode == NetSession.Mode.Host) ? "The client" : "The host");
				Chat.OnPeerLost(reason);
				Alert.PeerLost(who, reason);
			};
			MenuLook.OnClick = OpenWindow;
			Harmony harmony = new Harmony(Guid);
			TryPatch("MainMenu.Start", () => harmony.Patch(AccessTools.Method(typeof(MainMenu), "Start"), postfix: new HarmonyMethod(typeof(Plugin), nameof(MainMenuStartPostfix))));
			TryPatch("MainMenu.EnsureSingleInstance", () => harmony.Patch(AccessTools.Method(typeof(MainMenu), "EnsureSingleInstance"), prefix: new HarmonyMethod(typeof(Plugin), nameof(EnsureSingleInstancePrefix))));
			TryPatch("LoadManager.OnLoadSelectedSave", () => harmony.Patch(AccessTools.Method(typeof(LoadManager), "OnLoadSelectedSave"), prefix: new HarmonyMethod(typeof(Plugin), nameof(LoadSelectedSavePrefix))));
			TryPatch("WorldSync", () => WorldSync.Patch(harmony));
			TryPatch("OrderSync", () => OrderSync.Patch(harmony));
			TryPatch("ItemSync", () => ItemSync.Patch(harmony));
			TryPatch("CrewClaim", () => CrewClaim.Patch(harmony));
			TryPatch("Chat", () => Chat.Patch(harmony));
			TryPatch("DockSync", () => DockSync.Patch(harmony));
			TryPatch("ShipSync", () => ShipSync.Patch(harmony));
			TryPatch("ClientQuiet", () => ClientQuiet.Patch(harmony));
			TryPatch("Budget", () => Budget.Patch(harmony));
			TryPatch("PlayerInteractions", () => PlayerInteractions.Patch(harmony));
			TryPatch("Apart", () => Apart.Patch(harmony));
			TryPatch("WorkProgress", () => WorkProgress.Patch(harmony));
			TryPatch("ClientLoad", () => ClientLoad.Patch(harmony));
			TryPatch("Ownership", () => Ownership.Patch(harmony));
			TryPatch("Mirror", () => Mirror.Patch(harmony));
			if (PatchProblem != null)
			{
				Log.LogError(PatchProblem);
			}
			RemoveUnitySingleInstance();
			Log.LogInfo(PluginName + " " + Version + " loaded" + ((InstanceLog.Instance != null) ? (", instance log: " + InstanceLog.Instance.FilePath) : ", instance log unavailable"));
		}

		// Patches that failed (a game update renamed or changed what they patch). Any failure turns Host and Join off:
		// half the sync is worse than none. Patches a module made before its failure stay in place.
		private static readonly List<string> _failedPatches = new List<string>();

		internal static string PatchProblem => (_failedPatches.Count == 0) ? null : ("Mod not fully loaded (" + string.Join(", ", _failedPatches.ToArray()) + "): did the game update? Host and Join are off.");

		private static void TryPatch(string name, Action patch)
		{
			try
			{
				patch();
			}
			catch (Exception e)
			{
				_failedPatches.Add(name);
				Log.LogError("Patch failed: " + name + ": " + e);
			}
		}

		// Unity checks this flag natively before plugins load, so removing it only takes effect on the next launch.
		private static void RemoveUnitySingleInstance()
		{
			string path = Path.Combine(Application.dataPath, "boot.config");
			try
			{
				if (!File.Exists(path))
				{
					return;
				}
				string[] lines = File.ReadAllLines(path);
				List<string> kept = new List<string>();
				foreach (string line in lines)
				{
					if (!line.TrimStart().StartsWith("single-instance"))
					{
						kept.Add(line);
					}
				}
				if (kept.Count == lines.Length)
				{
					return;
				}
				string backup = path + ".bak";
				if (!File.Exists(backup))
				{
					File.Copy(path, backup);
				}
				// Keep the file's LF line endings; WriteAllLines would write CRLF on Windows.
				File.WriteAllText(path, string.Join("\n", kept.ToArray()) + "\n");
				Log.LogWarning("Removed single-instance from boot.config (backup: boot.config.bak). Restart the game once for it to take effect.");
			}
			catch (Exception e)
			{
				Log.LogError("Could not edit boot.config: " + e.Message);
			}
		}

		private static void MainMenuStartPostfix(MainMenu __instance)
		{
			Menu = __instance;
			_menuBlocked = false;
			if (PatchProblem != null)
			{
				Alert.Show(PatchProblem);
			}
		}

		private static bool EnsureSingleInstancePrefix(ref bool __result)
		{
			__result = true;
			return false;
		}

		private static void LoadSelectedSavePrefix(SaveInfo saveInfo)
		{
			SaveSync.HostSend(saveInfo);
		}

		private static void OnPeerConnected()
		{
			Chat.OnConnected();
			if (Net.CurrentMode == NetSession.Mode.Host && WorldSync.InGame)
			{
				SaveSync.HostLateJoin();
			}
		}

		private static void RouteBinary(string cmd, byte[] data)
		{
			if (cmd == "DOCK")
			{
				DockSync.OnBinary(data);
			}
			else if (cmd == "SNAP")
			{
				Apart.OnBinary(data);
			}
			else if (cmd == "SHIPNEW")
			{
				ShipSync.OnBinary(data);
			}
			else if (cmd == "PERSON")
			{
				PersonSync.OnBinary(data);
			}
			else if (cmd == "AVATAR")
			{
				PdaLog.OnAvatar(data);
			}
			else if (cmd == "CHUNK")
			{
				SaveSync.OnBinary(cmd, data);
			}
			else
			{
				WarnUnknown("BIN " + cmd);
			}
		}

		// Commands nobody handled, logged once each: with both sides on one version, that's a bug of ours.
		private static readonly HashSet<string> _unknown = new HashSet<string>();

		private static void WarnUnknown(string cmd)
		{
			if (_unknown.Add(cmd))
			{
				Log.LogWarning("Unknown message: " + cmd + " (further ones not logged)");
			}
		}

		private static void RouteMessage(string cmd, string arg)
		{
			if (!WorldSync.OnMessage(cmd, arg, Time.unscaledTime) && !OrderSync.OnMessage(cmd, arg) && !ItemSync.OnMessage(cmd, arg, Time.unscaledTime) && !CrewClaim.OnMessage(cmd, arg) && !Chat.OnMessage(cmd, arg) && !DockSync.OnMessage(cmd, arg) && !ShipSync.OnMessage(cmd, arg, Time.unscaledTime) && !Budget.OnMessage(cmd, arg) && !PlayerInteractions.OnMessage(cmd, arg) && !Apart.OnMessage(cmd, arg) && !WorkProgress.OnMessage(cmd, arg) && !Ownership.OnMessage(cmd, arg) && !Mirror.OnMessage(cmd, arg) && !Highlights.OnMessage(cmd, arg) && !ClientLoad.OnMessage(cmd, arg) && !PdaProfile.OnMessage(cmd, arg))
			{
				// The save transfer is the client's; the host has nobody left to give it to.
				if (Net.CurrentMode == NetSession.Mode.Client)
				{
					SaveSync.OnMessage(cmd, arg);
				}
				else
				{
					WarnUnknown(cmd);
				}
			}
		}

		private void Update()
		{
			Net.Poll(Time.unscaledTime);
			// First, so everything sent this frame already knows whether the players are apart.
			Apart.Update(Time.unscaledTime);
			if (InstanceLog.Instance != null && Time.unscaledTime >= _nextMemoryLog)
			{
				_nextMemoryLog = Time.unscaledTime + 60f;
				InstanceLog.Instance.LogMemory();
			}
			if (InstanceLog.Instance != null && Time.unscaledTime >= _nextLogFlush)
			{
				_nextLogFlush = Time.unscaledTime + 1f;
				InstanceLog.Instance.Flush();
			}
			if (Net.Connected && Time.unscaledTime >= _nextPing)
			{
				_nextPing = Time.unscaledTime + 1f;
				Net.Send("PING " + Stopwatch.GetTimestamp());
			}
			if (!Net.Connected && Net.CurrentMode != NetSession.Mode.Host)
			{
				SaveSync.ResetReceive();
			}
			WorldSync.Update(Time.unscaledTime);
			ItemSync.Update();
			ItemSync.RetryUpdate(Time.unscaledTime);
			ItemSync.HostCheck(Time.unscaledTime);
			CrewClaim.Update();
			Chat.Update();
			SaveSync.HostWatchLocation(Time.unscaledTime);
			ShipSync.Update(Time.unscaledTime);
			Budget.Update(Time.unscaledTime);
			PlayerInteractions.Update(Time.unscaledTime);
			Highlights.Update(Time.unscaledTime);
			Mirror.Update();
			if (Menu == null)
			{
				if (Profile.EditorOpen)
				{
					Profile.CloseEditor();
				}
				_windowOpen = false;
				Chat.MenuWindowOpen = false;
				MenuLook.Update(null, blocked: true);
				return;
			}
			MenuLook.Update(Menu, _windowOpen || Net.CurrentMode == NetSession.Mode.Client || Chat.MenuWindowOpen);
			// A client in the main menu can only wait for the host.
			bool clientWaiting = Net.CurrentMode == NetSession.Mode.Client;
			if (clientWaiting)
			{
				_windowOpen = true;
			}
			// No clicks through our windows (or the Multiplayer button) into the menu behind them. Set every frame:
			// the menu turns bClickable back on by itself after some of its own popups.
			bool block = _windowOpen || clientWaiting || Chat.MenuWindowOpen || Chat.MouseOverModUI || Profile.EditorOpen;
			if (block)
			{
				Menu.bClickable = false;
				_menuBlocked = true;
			}
			else if (_menuBlocked)
			{
				Menu.bClickable = true;
				_menuBlocked = false;
			}
		}

		private void LateUpdate()
		{
			WorldSync.LateUpdate(Time.unscaledTime, Time.unscaledDeltaTime);
		}

		private void OnGUI()
		{
			Chat.BeginGUI();
			if (Menu != null)
			{
				if (_windowOpen)
				{
					// Fit to what it shows now: after Host / Join it has less in it, and a kept old height leaves a gap.
					_windowRect.width = MenuLook.Big(380f);
					_windowRect.height = 10f;
					_windowRect = GUILayout.Window(WindowId, _windowRect, DrawWindow, "MULTIPLAYER", MenuLook.BigWindow);
					// Bigger than it was: keep it on the screen.
					_windowRect.y = Mathf.Max(0f, Mathf.Min(_windowRect.y, Screen.height - _windowRect.height));
					Chat.AddBlocking(_windowRect);
				}
				else if (!MenuLook.HasButton)
				{
					// Only if the console button couldn't be made.
					Rect button = new Rect(ButtonRect.x, Screen.height - 60f, ButtonRect.width, ButtonRect.height);
					Chat.AddBlocking(button);
					GUISkin prev = MenuLook.Use();
					if (GUI.Button(button, "MULTIPLAYER"))
					{
						OpenWindow();
					}
					MenuLook.Restore(prev);
				}
			}
			if (Net.CurrentMode != NetSession.Mode.None && !_windowOpen)
			{
				GUI.Label(new Rect(10f, 10f, 600f, 24f), StatusLine());
			}
			Highlights.OnGUI();
			PdaChat.OnGUI();
			PdaProfile.OnGUI();
			CrewClaim.OnGUI();
			Apart.OnGUI();
			ClientLoad.OnGUI();
			Alert.OnGUI();
			PdaNotify.OnGUI();
			if (Menu != null)
			{
				Profile.OnGUI();
			}
			Chat.OnGUI();
		}

		private void OpenWindow()
		{
			_windowOpen = true;
			_windowRect.x = (Screen.width - _windowRect.width) / 2f;
			_windowRect.y = Screen.height / 3f;
		}

		private void DrawWindow(int id)
		{
			GUISkin prev = MenuLook.Use(big: true);
			try
			{
				DrawWindowContent(id);
			}
			finally
			{
				MenuLook.Restore(prev);
			}
		}

		private void DrawWindowContent(int id)
		{
			GUILayout.Label("Status: " + Net.Status);
			if (PatchProblem != null)
			{
				GUILayout.Label(PatchProblem);
			}
			if (Net.Connected)
			{
				string ver = Net.RemoteVersion ?? "?";
				if (Net.RemoteVersion != null && Net.RemoteVersion != Version)
				{
					ver += " (mismatch, local " + Version + ")";
				}
				GUILayout.Label("Remote version: " + ver);
				GUILayout.Label("Ping: " + ((Net.LastRttMs < 0) ? "-" : (Net.LastRttMs + " ms")));
				GUILayout.Label("Last message: " + (Time.unscaledTime - Net.LastReceiveTime).ToString("0.0") + " s ago");
				if (Chat.PeerName != null)
				{
					GUILayout.Label("Other player: " + Chat.PeerName);
				}
			}
			string progress = SaveSync.Progress;
			if (progress != null)
			{
				GUILayout.Label(progress);
			}
			else if (Net.CurrentMode == NetSession.Mode.Client && Net.Connected)
			{
				GUILayout.Label("Waiting for the host to load a save...");
			}
			if (Net.CurrentMode == NetSession.Mode.None)
			{
				// The name goes to the other player when connecting, so it can only be changed while not connected.
				GUILayout.Label("Your name");
				GUILayout.BeginHorizontal();
				_nameEdit = GUILayout.TextField(_nameEdit, 32);
				// Written to the config here and by Host / Join, not on every key.
				string typed = _nameEdit.Trim();
				GUI.enabled = typed.Length > 0 && typed != _playerName.Value;
				if (GUILayout.Button("Save", GUILayout.Width(MenuLook.Big(70f))))
				{
					SaveName();
				}
				GUI.enabled = true;
				GUILayout.EndHorizontal();
				GUILayout.Space(8f);
				GUILayout.Label("Port");
				_port = GUILayout.TextField(_port);
				GUI.enabled = PatchProblem == null;
				if (GUILayout.Button("Host"))
				{
					int port;
					if (TryParsePort(out port))
					{
						SaveName();
						_lastPort.Value = _port.Trim();
						Net.Host(port);
					}
				}
				GUI.enabled = true;
				GUILayout.Space(8f);
				GUILayout.Label("Host address");
				_address = GUILayout.TextField(_address);
				GUI.enabled = PatchProblem == null;
				if (GUILayout.Button("Join"))
				{
					int port;
					if (TryParsePort(out port))
					{
						SaveName();
						_lastAddress.Value = _address.Trim();
						_lastPort.Value = _port.Trim();
						Net.Join(_address.Trim(), port);
					}
				}
				GUI.enabled = true;
			}
			else
			{
				GUILayout.Label("Your name: " + PlayerName);
				if (GUILayout.Button("Disconnect"))
				{
					Net.Stop();
				}
			}
			GUILayout.Space(8f);
			DrawProfile();
			GUILayout.Space(8f);
			GUILayout.Label("Ping colour (~)");
			DrawSwatches();
			bool hl = GUILayout.Toggle(_highlights.Value, " Highlights: your lockers, the other player's aura");
			if (hl != _highlights.Value)
			{
				_highlights.Value = hl;
			}
			GUILayout.Space(8f);
			if (GUILayout.Button(Chat.MenuWindowOpen ? "Hide chat" : "Chat"))
			{
				Chat.MenuWindowOpen = !Chat.MenuWindowOpen;
			}
			if (Net.CurrentMode != NetSession.Mode.Client)
			{
				if (GUILayout.Button("Close"))
				{
					_windowOpen = false;
				}
			}
			GUILayout.Space(8f);
			// Fixed sizes: the row looks the same whatever the window shows above it.
			if (_reportStyle == null)
			{
				_reportStyle = new GUIStyle(GUI.skin.label);
				_reportStyle.alignment = TextAnchor.MiddleLeft;
				_reportStyle.wordWrap = false;
			}
			GUIContent report = new GUIContent("Report bug:");
			float rowH = GUI.skin.button.CalcHeight(report, 100f);
			float linkW = MenuLook.Big(90f);
			GUILayout.BeginHorizontal();
			GUILayout.Label(report, _reportStyle, GUILayout.Width(_reportStyle.CalcSize(report).x), GUILayout.Height(rowH));
			if (GUILayout.Button("Discord", GUILayout.Width(linkW), GUILayout.Height(rowH)))
			{
				Application.OpenURL(DiscordUrl);
			}
			if (GUILayout.Button("GitHub", GUILayout.Width(linkW), GUILayout.Height(rowH)))
			{
				Application.OpenURL(GitHubUrl);
			}
			GUILayout.FlexibleSpace();
			GUILayout.EndHorizontal();
			GUI.DragWindow();
		}

		// Deleting the character: the name typed to confirm.
		private static bool _deleting;

		private static string _deleteTyped = "";

		// Your character: the avatar, the name, the editor, and deleting it.
		private static void DrawProfile()
		{
			GUILayout.BeginHorizontal();
			float side = MenuLook.Big(64f);
			Rect r = GUILayoutUtility.GetRect(side, side, GUILayout.Width(side), GUILayout.Height(side));
			if (Profile.Avatar != null)
			{
				GUI.DrawTexture(r, Profile.Avatar, ScaleMode.ScaleToFit);
			}
			else
			{
				GUI.Box(r, "?");
			}
			GUILayout.BeginVertical();
			GUILayout.Label(Profile.HasLooks ? Profile.Current.Name : "No character yet");
			if (GUILayout.Button(Profile.HasLooks ? "Edit character" : "Create character"))
			{
				Profile.OpenEditor();
			}
			// Only while not connected: the profile goes to the host with a claim.
			if (Profile.HasLooks && Net.CurrentMode == NetSession.Mode.None && !_deleting && GUILayout.Button("Delete character"))
			{
				_deleting = true;
				_deleteTyped = "";
			}
			GUILayout.EndVertical();
			GUILayout.EndHorizontal();
			if (!_deleting)
			{
				return;
			}
			if (!Profile.HasLooks || Net.CurrentMode != NetSession.Mode.None)
			{
				_deleting = false;
				return;
			}
			GUILayout.Label("Type the name to delete: " + Profile.Current.Name);
			_deleteTyped = GUILayout.TextField(_deleteTyped, 50);
			GUILayout.BeginHorizontal();
			GUI.enabled = _deleteTyped.Trim() == Profile.Current.Name;
			if (GUILayout.Button("Delete"))
			{
				Profile.Delete();
				_deleting = false;
			}
			GUI.enabled = true;
			if (GUILayout.Button("Cancel"))
			{
				_deleting = false;
			}
			GUILayout.EndHorizontal();
		}

		private static void DrawSwatches()
		{
			if (_swatches == null)
			{
				_swatches = new Texture2D[MarkPalette.Length];
				for (int i = 0; i < MarkPalette.Length; i++)
				{
					Color c;
					ColorUtility.TryParseHtmlString("#" + MarkPalette[i], out c);
					Texture2D t = new Texture2D(1, 1);
					t.SetPixel(0, 0, c);
					t.Apply();
					t.hideFlags = HideFlags.HideAndDontSave;
					_swatches[i] = t;
				}
				_swatchEdge = new Texture2D(1, 1);
				_swatchEdge.SetPixel(0, 0, Color.white);
				_swatchEdge.Apply();
				_swatchEdge.hideFlags = HideFlags.HideAndDontSave;
			}
			string current = MarkColorHex;
			GUILayout.BeginHorizontal();
			for (int i = 0; i < MarkPalette.Length; i++)
			{
				float side = MenuLook.Big(30f);
				Rect r = GUILayoutUtility.GetRect(side, side, GUILayout.Width(side), GUILayout.Height(side));
				bool selected = string.Equals(current, MarkPalette[i], StringComparison.OrdinalIgnoreCase);
				if (selected)
				{
					GUI.DrawTexture(r, _swatchEdge);
				}
				GUI.DrawTexture(selected ? new Rect(r.x + 3f, r.y + 3f, r.width - 6f, r.height - 6f) : new Rect(r.x + 2f, r.y + 2f, r.width - 4f, r.height - 4f), _swatches[i]);
				if (GUI.Button(r, GUIContent.none, GUIStyle.none))
				{
					_markColor.Value = MarkPalette[i];
				}
			}
			GUILayout.EndHorizontal();
		}

		// Trimmed; an empty name isn't saved, and the field goes back to the name that's sent.
		private void SaveName()
		{
			string n = _nameEdit.Trim();
			if (n.Length > 0)
			{
				_playerName.Value = n;
			}
			_nameEdit = PlayerName;
		}

		private bool TryParsePort(out int port)
		{
			if (int.TryParse(_port.Trim(), out port) && port > 0 && port < 65536)
			{
				return true;
			}
			Net.Status = "Invalid port";
			return false;
		}

		private string StatusLine()
		{
			string s = "MP " + Net.CurrentMode + ": " + Net.Status;
			if (Net.Connected && Net.LastRttMs >= 0)
			{
				s += " | ping " + Net.LastRttMs + " ms";
			}
			string progress = SaveSync.Progress;
			if (progress != null)
			{
				s += " | " + progress;
			}
			string notice = ItemSync.Notice;
			if (notice != null)
			{
				s += " | " + notice;
			}
			string apart = Apart.HostNotice;
			if (apart != null)
			{
				s += " | " + apart;
			}
			else if (Apart.ClientActive)
			{
				s += " | apart";
			}
			string mine = CrewClaim.MineName;
			if (mine != null)
			{
				s += " | you: " + mine;
			}
			return s;
		}

		private void OnApplicationQuit()
		{
			Net.Stop();
			if (InstanceLog.Instance != null)
			{
				InstanceLog.Instance.Flush(true);
			}
		}
	}
}
