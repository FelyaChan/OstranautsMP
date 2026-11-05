using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace OstranautsMP
{
	// The mod's look in the main menu.
	//
	// Button: the menu's buttons are textured quads on the console (MainMenu raycasts them). A copy of the OPTIONS
	// quad with our own picture (a NEW button recoloured, "MULTI PLAYER", res/GUIBtnMultiplayer*.png, built into the
	// DLL) sits in the empty slot of the lower console, below OPTIONS. It's placed relative to OPTIONS in OPTIONS'
	// own size and axes, so it stays there at any resolution. Pressed, it shows the "In" picture like the others.
	//
	// Windows: a GUISkin in the console's colours (dark panel, the teal of CREDITS / WIKI, green text fields) with
	// the menu's own font, for the mod's IMGUI windows (Multiplayer, menu chat, crew picker).
	public static class MenuLook
	{
		// Where the slot is, measured on the menu at 1920x1080: from OPTIONS' centre, in OPTIONS' widths / heights.
		private const float SlotRight = 0.06f;

		private const float SlotDown = 1.42f;

		private const float SlotScale = 0.62f;

		internal static Action OnClick;

		private static MainMenu _for;

		private static GameObject _button;

		private static Material _material;

		private static Texture2D _tex;

		private static Texture2D _texIn;

		private static bool _pressed;

		private static bool _failed;

		private static readonly FieldInfo FOptions = AccessTools.Field(typeof(MainMenu), "tfOptions");

		private static readonly FieldInfo FCam = AccessTools.Field(typeof(MainMenu), "camMain");

		private static readonly FieldInfo FShipEditor = AccessTools.Field(typeof(MainMenu), "txtShipEditor");

		private static readonly string[] Overlays = { "cgWarning", "cgBBG", "cgBlack", "cgManual", "cgOptions", "cgCredits" };

		// True while the 3D button is in the menu (otherwise Plugin draws a plain one).
		internal static bool HasButton => _button != null && _for != null;

		private static Texture2D LoadTexture(string name)
		{
			using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("OstranautsMP." + name))
			{
				if (s == null)
				{
					return null;
				}
				byte[] bytes = new byte[s.Length];
				s.Read(bytes, 0, bytes.Length);
				Texture2D t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
				t.LoadImage(bytes);
				t.filterMode = FilterMode.Bilinear;
				t.wrapMode = TextureWrapMode.Clamp;
				return t;
			}
		}

		private static void Build(MainMenu menu)
		{
			_for = menu;
			_button = null;
			Transform options = FOptions.GetValue(menu) as Transform;
			if (options == null)
			{
				Plugin.Log.LogWarning("Menu: no OPTIONS button to copy");
				_failed = true;
				return;
			}
			if (_tex == null)
			{
				_tex = LoadTexture("GUIBtnMultiplayer.png");
				_texIn = LoadTexture("GUIBtnMultiplayerIn.png");
			}
			if (_tex == null)
			{
				Plugin.Log.LogWarning("Menu: button picture missing from the DLL");
				_failed = true;
				return;
			}
			GameObject go = UnityEngine.Object.Instantiate(options.gameObject, options.parent);
			go.name = "MPMultiplayerButton";
			MeshRenderer r = go.GetComponent<MeshRenderer>();
			if (r == null)
			{
				UnityEngine.Object.Destroy(go);
				_failed = true;
				return;
			}
			_material = new Material(r.sharedMaterial);
			_material.SetTexture("_MainTex", _tex);
			r.material = _material;
			// OPTIONS' size in the world along its own axes.
			Vector3 size = Vector3.one;
			MeshFilter mf = options.GetComponent<MeshFilter>();
			if (mf != null && mf.sharedMesh != null)
			{
				size = mf.sharedMesh.bounds.size;
			}
			Vector3 s = options.lossyScale;
			float width = Mathf.Abs(size.x * s.x);
			float height = Mathf.Abs(size.y * s.y);
			go.transform.position = options.position + options.right * (SlotRight * width) - options.up * (SlotDown * height);
			go.transform.rotation = options.rotation;
			go.transform.localScale = options.localScale * SlotScale;
			_button = go;
			Plugin.Log.LogInfo("Menu: Multiplayer button placed");
		}

		// Called from Plugin.Update while the main menu is up.
		public static void Update(MainMenu menu, bool blocked)
		{
			if (menu == null)
			{
				_for = null;
				_button = null;
				return;
			}
			if (_for != menu)
			{
				_failed = false;
				Build(menu);
			}
			if (_button == null || _failed)
			{
				return;
			}
			Mouse mouse = Mouse.current;
			Camera cam = FCam.GetValue(menu) as Camera;
			if (mouse == null || cam == null)
			{
				return;
			}
			bool over = false;
			if (!blocked && menu.bClickable && !Covered(menu))
			{
				RaycastHit hit;
				over = Physics.Raycast(cam.ScreenPointToRay(Ostranauts.InputControl.InputManager.MousePosition), out hit) && hit.transform == _button.transform;
			}
			if (mouse.leftButton.wasPressedThisFrame && over)
			{
				_pressed = true;
			}
			if (_pressed && mouse.leftButton.wasReleasedThisFrame)
			{
				_pressed = false;
				if (over && OnClick != null)
				{
					OnClick();
				}
			}
			bool shown = _pressed && over;
			_material.SetTexture("_MainTex", (shown && _texIn != null) ? _texIn : _tex);
		}

		// One of the menu's own panels (options, credits, manual...) is open.
		private static bool Covered(MainMenu menu)
		{
			foreach (string f in Overlays)
			{
				CanvasGroup cg = Traverse.Create(menu).Field(f).GetValue<CanvasGroup>();
				if (cg != null && cg.alpha > 0f)
				{
					return true;
				}
			}
			Traverse t = Traverse.Create(menu);
			return t.Field("_loadingScreen").GetValue<GameObject>() != null || t.Field("goGUIMods").GetValue<GameObject>() != null;
		}

		// ---- Skin ----------------------------------------------------------------------------------------------

		private static GUISkin _skin;

		private static readonly Color Teal = new Color32(43, 181, 211, 255);

		private static readonly Color TealLight = new Color32(110, 216, 236, 255);

		private static readonly Color TealDark = new Color32(26, 132, 158, 255);

		private static readonly Color Panel = new Color32(11, 22, 26, 245);

		private static readonly Color Ink = new Color32(6, 28, 36, 255);

		private static readonly Color Text = new Color32(200, 238, 245, 255);

		private static readonly Color Green = new Color32(140, 255, 150, 255);

		private static Texture2D Box(Color fill, Color edge, int border)
		{
			const int n = 16;
			Texture2D t = new Texture2D(n, n, TextureFormat.RGBA32, false);
			for (int y = 0; y < n; y++)
			{
				for (int x = 0; x < n; x++)
				{
					bool e = x < border || y < border || x >= n - border || y >= n - border;
					t.SetPixel(x, y, e ? edge : fill);
				}
			}
			t.Apply();
			t.hideFlags = HideFlags.HideAndDontSave;
			return t;
		}

		private static void Set(GUIStyle st, Texture2D normal, Texture2D hover, Texture2D active, Color text)
		{
			st.normal.background = normal;
			st.hover.background = hover ?? normal;
			st.active.background = active ?? normal;
			st.focused.background = hover ?? normal;
			st.onNormal.background = normal;
			st.onHover.background = hover ?? normal;
			st.onActive.background = active ?? normal;
			st.onFocused.background = hover ?? normal;
			st.normal.textColor = st.hover.textColor = st.active.textColor = st.focused.textColor = text;
			st.onNormal.textColor = st.onHover.textColor = st.onActive.textColor = st.onFocused.textColor = text;
			st.border = new RectOffset(4, 4, 4, 4);
		}

		private static Font MenuFont()
		{
			MainMenu menu = Plugin.Menu;
			UnityEngine.UI.Text t = (menu != null) ? FShipEditor.GetValue(menu) as UnityEngine.UI.Text : null;
			return (t != null) ? t.font : null;
		}

		private static void BuildSkin()
		{
			_skin = UnityEngine.Object.Instantiate(GUI.skin);
			_skin.hideFlags = HideFlags.HideAndDontSave;
			Font font = MenuFont();
			if (font != null)
			{
				_skin.font = font;
			}
			Texture2D panel = Box(Panel, Teal, 2);
			Set(_skin.window, panel, panel, panel, TealLight);
			_skin.window.fontSize = 18;
			_skin.window.fontStyle = FontStyle.Bold;
			_skin.window.alignment = TextAnchor.UpperCenter;
			_skin.window.padding = new RectOffset(14, 14, 32, 14);
			// The title is drawn inside the padding; this lifts it to the top edge.
			_skin.window.contentOffset = new Vector2(0f, -24f);
			_skin.box.normal.background = panel;
			_skin.box.normal.textColor = Text;
			_skin.label.normal.textColor = Text;
			_skin.label.fontSize = 15;
			_skin.label.wordWrap = true;
			Set(_skin.button, Box(Teal, TealDark, 2), Box(TealLight, TealDark, 2), Box(TealDark, Ink, 2), Ink);
			_skin.button.fontSize = 16;
			_skin.button.fontStyle = FontStyle.Bold;
			_skin.button.padding = new RectOffset(10, 10, 6, 6);
			_skin.button.margin = new RectOffset(4, 4, 4, 4);
			Texture2D field = Box(new Color32(4, 12, 14, 255), TealDark, 1);
			Texture2D fieldOn = Box(new Color32(4, 12, 14, 255), Teal, 1);
			Set(_skin.textField, field, fieldOn, fieldOn, Green);
			_skin.textField.fontSize = 16;
			_skin.textField.padding = new RectOffset(8, 8, 5, 5);
			Set(_skin.textArea, field, fieldOn, fieldOn, Green);
			_skin.settings.cursorColor = Green;
			_skin.settings.selectionColor = new Color(0.17f, 0.71f, 0.83f, 0.45f);
			Texture2D track = Box(new Color32(4, 12, 14, 255), new Color32(4, 12, 14, 255), 0);
			_skin.verticalScrollbar.normal.background = track;
			Set(_skin.verticalScrollbarThumb, Box(TealDark, TealDark, 0), Box(Teal, Teal, 0), Box(Teal, Teal, 0), Ink);
			_skin.scrollView.normal.background = null;
		}

		// For the mod's IMGUI windows: GUILayout.Window(..., MenuLook.Window) and Use() at the top of the window function.
		internal static GUIStyle Window
		{
			get
			{
				if (_skin == null)
				{
					BuildSkin();
				}
				return _skin.window;
			}
		}

		// Switches to the skin and returns the one in use before, to give back to Restore.
		internal static GUISkin Use()
		{
			if (_skin == null)
			{
				BuildSkin();
			}
			else if (_skin.font == null || _skin.font == GUI.skin.font)
			{
				Font f = MenuFont();
				if (f != null)
				{
					_skin.font = f;
				}
			}
			GUISkin prev = GUI.skin;
			GUI.skin = _skin;
			return prev;
		}

		internal static void Restore(GUISkin prev)
		{
			if (prev != null)
			{
				GUI.skin = prev;
			}
		}
	}
}
