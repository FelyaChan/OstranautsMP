using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using HarmonyLib;
using LitJson;
using UnityEngine;

namespace OstranautsMP
{
	// Your character profile, made in the main menu (Multiplayer window -> Character): face, skin, body type,
	// pronouns, attraction, name and skills, saved on this computer. The skills go to a new body made from the profile
	// (CrewClaim); a crew member picked with the profile keeps their own. When you join and pick a crew member, they become this
	// character (Mirror.Apply: the same rename and looks as the mirror); their skills, traits and items stay theirs.
	//
	// The crew member then belongs to your profile (GUI property map MPProfile on the person, saved with the game):
	// nobody else can pick them, you can again next time. Someone a client played before profiles existed goes to the
	// first profile that picks them. Changes made at a mirror in the game (Mirror) go back into the profile.
	//
	// Files (BepInEx/config): OstranautsMP_profile.json, OstranautsMP_avatar.png (the portrait, as the game draws it
	// flat for lists: FaceAnim2.GetPNG, the face part pictures laid over each other). The PDA messages and the other
	// players' avatars are kept with the profile too (PdaLog).
	public static class Profile
	{
		public class Data
		{
			public string Id;

			public string Name;

			public string[] Face;

			public string Body;

			public string Skin;

			public string Gender;

			public string[] Attract;

			// Skill conditions (Skill*) picked in the editor, up to MaxSkills. A new body gets these (CrewClaim).
			public string[] Skills;
		}

		internal const string Gpm = "MPProfile";

		internal const int MaxSkills = 5;

		private const string Skins = "ABC";

		// The face parts in the game's order (GUIChargenBody: FacePartNext(n)); skin has its own row.
		private static readonly string[] PartLabels = { "Eyes", "Glasses", "Head", "Lips", "Neck", "Nose", "Pupils", "Scar", "Teeth", "Hair", "Beard" };

		private static readonly string[] Genders = { "IsMale", "IsFemale", "IsNB" };

		private static readonly string[] GenderLabels = { "He", "She", "They" };

		private static readonly string[] Attractions = { "IsAttractedMen", "IsAttractedWomen", "IsAttractedNB" };

		private static readonly string[] AttractionLabels = { "Men", "Women", "Nonbinary" };

		private const int WindowId = 0x4D5050;

		internal static Data Current;

		private static Texture2D _avatar;

		// The editor.
		private static bool _open;

		private static Data _edit;

		private static Texture2D _preview;

		private static Dictionary<int, List<string>> _parts;

		private static List<string> _bodies;

		private static string _error;

		private static Rect _rect = new Rect(0f, 0f, MenuLook.Big(560f), 10f);

		private static readonly string[] Tabs = { "Looks", "Skills" };

		private static int _tab;

		// The game's skills (conditions Skill* shown to the player), by friendly name.
		private static List<KeyValuePair<string, string>> _skillList;

		private static Vector2 _skillScroll;

		private static string FilePath => Path.Combine(Paths.ConfigPath, "OstranautsMP_profile.json");

		private static string AvatarPath => Path.Combine(Paths.ConfigPath, "OstranautsMP_avatar.png");

		internal static bool HasLooks => Current != null && Current.Face != null && Current.Face.Length > 0;

		internal static Texture2D Avatar => _avatar;

		internal static bool EditorOpen => _open;

		// ---- Files ---------------------------------------------------------------------------------------------

		public static void Load()
		{
			try
			{
				if (File.Exists(FilePath))
				{
					Current = JsonMapper.ToObject<Data>(File.ReadAllText(FilePath, Encoding.UTF8));
				}
			}
			catch (Exception e)
			{
				Plugin.Log.LogWarning("Profile: could not read " + FilePath + ": " + e.Message);
			}
			if (Current == null)
			{
				Current = new Data();
			}
			if (string.IsNullOrEmpty(Current.Id))
			{
				// Which crew member is yours is remembered by this, not by your name.
				Current.Id = Guid.NewGuid().ToString("N");
				Write();
			}
			try
			{
				if (File.Exists(AvatarPath))
				{
					_avatar = new Texture2D(2, 2, TextureFormat.RGBA32, false);
					_avatar.LoadImage(File.ReadAllBytes(AvatarPath));
					_avatar.filterMode = FilterMode.Point;
					_avatar.hideFlags = HideFlags.HideAndDontSave;
				}
			}
			catch (Exception e)
			{
				Plugin.Log.LogWarning("Profile: could not read the avatar: " + e.Message);
				_avatar = null;
			}
		}

		private static void Write()
		{
			try
			{
				File.WriteAllText(FilePath, JsonMapper.ToJson(Current), new UTF8Encoding(false));
			}
			catch (Exception e)
			{
				Plugin.Log.LogError("Profile: could not save " + FilePath + ": " + e.Message);
			}
		}

		// The character is gone: looks, name, skills and the avatar. The profile gets a new ID, so crew members the old
		// character had in saves are no longer this profile's.
		internal static void Delete()
		{
			if (_open)
			{
				CloseEditor();
			}
			string old = (Current != null) ? Current.Name : null;
			Current = new Data();
			Current.Id = Guid.NewGuid().ToString("N");
			Write();
			try
			{
				if (File.Exists(AvatarPath))
				{
					File.Delete(AvatarPath);
				}
			}
			catch (Exception e)
			{
				Plugin.Log.LogWarning("Profile: could not delete the avatar: " + e.Message);
			}
			if (_avatar != null)
			{
				UnityEngine.Object.Destroy(_avatar);
				_avatar = null;
			}
			Plugin.Log.LogInfo("Profile: deleted " + old);
		}

		// ---- In the game ---------------------------------------------------------------------------------------

		// The profile as the mirror's change set; null if no looks were made yet.
		internal static Mirror.Look ToLook()
		{
			if (!HasLooks)
			{
				return null;
			}
			Mirror.Look look = new Mirror.Look();
			look.NewId = Current.Name;
			look.Face = Current.Face;
			look.Body = Current.Body;
			look.Skin = Current.Skin;
			look.Gender = Current.Gender;
			look.Attract = Current.Attract;
			return look;
		}

		// The character was changed at a mirror in the game: keep the profile (and the avatar) the same.
		internal static void UpdateFrom(Mirror.Look look)
		{
			if (look == null || Current == null)
			{
				return;
			}
			if (!string.IsNullOrEmpty(look.NewId))
			{
				Current.Name = look.NewId;
			}
			if (look.Face != null && look.Face.Length > 0)
			{
				Current.Face = (string[])look.Face.Clone();
			}
			Current.Body = look.Body ?? Current.Body;
			Current.Skin = look.Skin ?? Current.Skin;
			Current.Gender = look.Gender ?? Current.Gender;
			Current.Attract = look.Attract ?? Current.Attract;
			Write();
			Texture2D portrait = Portrait(Current.Face);
			if (portrait != null)
			{
				try
				{
					File.WriteAllBytes(AvatarPath, portrait.EncodeToPNG());
				}
				catch (Exception e)
				{
					Plugin.Log.LogWarning("Profile: could not save the avatar: " + e.Message);
				}
				if (_avatar != null)
				{
					UnityEngine.Object.Destroy(_avatar);
				}
				_avatar = portrait;
				PdaLog.SendAvatar();
			}
			Plugin.Log.LogInfo("Profile updated from the mirror: " + Current.Name);
		}

		// Which profile a crew member belongs to; null if none.
		internal static string OwnerOf(CondOwner co)
		{
			Dictionary<string, string> d;
			string id;
			if (co == null || co.mapGUIPropMaps == null || !co.mapGUIPropMaps.TryGetValue(Gpm, out d) || d == null || !d.TryGetValue("id", out id))
			{
				return null;
			}
			return id;
		}

		internal static void SetOwner(CondOwner co, string profileId)
		{
			if (co == null || co.mapGUIPropMaps == null || string.IsNullOrEmpty(profileId))
			{
				return;
			}
			Dictionary<string, string> d = new Dictionary<string, string>();
			d["id"] = profileId;
			co.mapGUIPropMaps[Gpm] = d;
		}

		// ---- Portrait ------------------------------------------------------------------------------------------

		private static readonly PropertyInfo PartOrder = AccessTools.Property(typeof(FaceAnim2), "FacePartOrder");

		// FaceAnim2.GetPNG without a character: the part pictures in the game's order, each over the last.
		private static Texture2D Portrait(string[] face)
		{
			string[] order = PartOrder.GetValue(null, null) as string[];
			if (order == null || face == null)
			{
				return null;
			}
			List<Color[]> layers = new List<Color[]>();
			int w = 0;
			int h = 0;
			for (int i = 0; i < order.Length; i++)
			{
				int n;
				if (!int.TryParse(order[i], out n) || n < 0 || n >= face.Length)
				{
					continue;
				}
				Texture2D t = DataHandler.LoadPNG("portraits/" + face[n] + ".png", bNorm: false);
				if (t == null)
				{
					continue;
				}
				if (w == 0)
				{
					w = t.width;
					h = t.height;
				}
				if (t.width != w || t.height != h)
				{
					continue;
				}
				layers.Add(t.GetPixels());
			}
			if (layers.Count == 0)
			{
				return null;
			}
			Color[] result = new Color[w * h];
			for (int p = 0; p < result.Length; p++)
			{
				Color c = Color.Lerp(Color.black, layers[0][p], layers[0][p].a);
				for (int l = 1; l < layers.Count; l++)
				{
					c = Color.Lerp(c, layers[l][p], layers[l][p].a);
				}
				c.a = 1f;
				result[p] = c;
			}
			Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
			tex.filterMode = FilterMode.Point;
			tex.hideFlags = HideFlags.HideAndDontSave;
			tex.SetPixels(result);
			tex.Apply();
			return tex;
		}

		// ---- Editor --------------------------------------------------------------------------------------------

		internal static void OpenEditor()
		{
			if (DataHandler.GetLoot("TXTPortraitType") == null)
			{
				_error = "The game's data isn't loaded yet";
				return;
			}
			_edit = new Data();
			_edit.Id = Current.Id;
			_edit.Name = Current.Name;
			_edit.Gender = Current.Gender ?? "IsMale";
			_edit.Attract = (Current.Attract != null) ? (string[])Current.Attract.Clone() : new string[0];
			_edit.Skills = (Current.Skills != null) ? (string[])Current.Skills.Clone() : new string[0];
			_edit.Body = Current.Body;
			_edit.Face = (Current.Face != null && Current.Face.Length > 0) ? (string[])Current.Face.Clone() : FaceAnim2.GetRandomFace(_edit.Gender != "IsFemale", _edit.Gender != "IsMale");
			_edit.Skin = Current.Skin ?? FaceAnim2.GetFaceGroups(_edit.Face)[0];
			_bodies = DataHandler.GetLoot("TXTBodyTypesAll").GetLootNames();
			if (string.IsNullOrEmpty(_edit.Body) || !_bodies.Contains(_edit.Body))
			{
				_edit.Body = (_bodies.Count > 0) ? _bodies[0] : null;
			}
			if (string.IsNullOrEmpty(_edit.Name))
			{
				_edit.Name = RandomName(_edit.Gender);
			}
			BuildParts();
			BuildSkills();
			Redraw();
			_error = null;
			_tab = 0;
			_skillScroll = Vector2.zero;
			_rect.height = 10f;
			_open = true;
			_rect.x = (Screen.width - _rect.width) / 2f;
			_rect.y = Screen.height / 6f;
		}

		internal static void CloseEditor()
		{
			_open = false;
			if (_preview != null)
			{
				UnityEngine.Object.Destroy(_preview);
				_preview = null;
			}
		}

		// The game's lists of each part for this skin (GUIChargenBody.UpdatePartList): one list per part, separated
		// by the "Missing" entries.
		private static void BuildParts()
		{
			_parts = new Dictionary<int, List<string>>();
			Loot loot = DataHandler.GetLoot("TXTPortrait" + _edit.Skin + "Nonbinary");
			if (loot == null)
			{
				return;
			}
			int n = 0;
			foreach (string item in loot.GetAllLootNames())
			{
				if (!_parts.ContainsKey(n))
				{
					_parts[n] = new List<string>();
				}
				if (item.IndexOf("Missing", StringComparison.Ordinal) >= 0)
				{
					n++;
				}
				else
				{
					_parts[n].Add(item);
				}
			}
		}

		private static void BuildSkills()
		{
			_skillList = new List<KeyValuePair<string, string>>();
			foreach (KeyValuePair<string, JsonCond> kv in DataHandler.dictConds)
			{
				JsonCond jc = kv.Value;
				if (jc != null && kv.Key.StartsWith("Skill", StringComparison.Ordinal) && jc.nDisplaySelf == 2)
				{
					_skillList.Add(new KeyValuePair<string, string>(kv.Key, string.IsNullOrEmpty(jc.strNameFriendly) ? kv.Key : jc.strNameFriendly));
				}
			}
			_skillList.Sort((a, b) => string.Compare(a.Value, b.Value, StringComparison.OrdinalIgnoreCase));
		}

		private static void Redraw()
		{
			if (_preview != null)
			{
				UnityEngine.Object.Destroy(_preview);
			}
			_preview = Portrait(_edit.Face);
		}

		private static void NextPart(int part, int dir)
		{
			List<string> list;
			if (_edit.Face == null || part >= _edit.Face.Length || !_parts.TryGetValue(part, out list) || list.Count == 0)
			{
				return;
			}
			int i = list.IndexOf(_edit.Face[part]) + dir;
			if (i >= list.Count)
			{
				i = 0;
			}
			else if (i < 0)
			{
				i = list.Count - 1;
			}
			_edit.Face[part] = list[i];
			Redraw();
		}

		// GUIChargenBody.ChangeSkin: the head comes from the new skin's set, other parts that don't exist for it are
		// picked anew.
		private static void NextSkin(int dir)
		{
			int i = Skins.IndexOf(_edit.Skin ?? "A") + dir;
			if (i >= Skins.Length)
			{
				i = 0;
			}
			else if (i < 0)
			{
				i = Skins.Length - 1;
			}
			_edit.Skin = Skins[i].ToString();
			List<string> names = DataHandler.GetLoot("TXTPortrait" + _edit.Skin + "Nonbinary").GetLootNames("TXTPortrait");
			if (names.Count > 2)
			{
				_edit.Face[2] = names[2];
			}
			BuildParts();
			foreach (KeyValuePair<int, List<string>> kv in _parts)
			{
				if (kv.Key < _edit.Face.Length && kv.Value.Count > 0 && kv.Value.IndexOf(_edit.Face[kv.Key]) < 0)
				{
					_edit.Face[kv.Key] = kv.Value[UnityEngine.Random.Range(0, kv.Value.Count)];
				}
			}
			Redraw();
		}

		private static void RandomFace()
		{
			_edit.Face = FaceAnim2.GetRandomFace(_edit.Gender != "IsFemale", _edit.Gender != "IsMale");
			_edit.Skin = FaceAnim2.GetFaceGroups(_edit.Face)[0];
			BuildParts();
			Redraw();
		}

		private static string RandomName(string gender)
		{
			string first;
			string last;
			DataHandler.GetFullName(gender ?? "IsNB", out first, out last);
			return (first + " " + last).Trim();
		}

		private static void Save()
		{
			string name = (_edit.Name ?? "").Trim();
			string why = Mirror.NameProblem(name, null);
			if (why != null)
			{
				_error = "Name: " + why;
				return;
			}
			_edit.Name = name;
			Current = _edit;
			Write();
			try
			{
				if (_preview != null)
				{
					File.WriteAllBytes(AvatarPath, _preview.EncodeToPNG());
					if (_avatar != null)
					{
						UnityEngine.Object.Destroy(_avatar);
					}
					_avatar = UnityEngine.Object.Instantiate(_preview);
					_avatar.hideFlags = HideFlags.HideAndDontSave;
					PdaLog.SendAvatar();
				}
			}
			catch (Exception e)
			{
				Plugin.Log.LogWarning("Profile: could not save the avatar: " + e.Message);
			}
			Plugin.Log.LogInfo("Profile saved: " + Current.Name);
			CloseEditor();
		}

		public static void OnGUI()
		{
			if (!_open)
			{
				return;
			}
			_rect = GUILayout.Window(WindowId, _rect, DrawEditor, "CHARACTER", MenuLook.BigWindow);
			_rect.y = Mathf.Max(0f, Mathf.Min(_rect.y, Screen.height - _rect.height));
			Chat.AddBlocking(_rect);
		}

		private static void DrawEditor(int id)
		{
			GUISkin prev = MenuLook.Use(big: true);
			try
			{
				DrawEditorContent();
			}
			catch (Exception e)
			{
				_error = e.Message;
				Plugin.Log.LogError("Profile editor: " + e);
			}
			finally
			{
				MenuLook.Restore(prev);
			}
			GUI.DragWindow();
		}

		private static void DrawEditorContent()
		{
			int tab = GUILayout.Toolbar(_tab, Tabs);
			if (tab != _tab)
			{
				_tab = tab;
				// Let the window fit the other tab.
				_rect.height = 10f;
			}
			GUILayout.Space(8f);
			if (_tab == 1)
			{
				DrawSkills();
			}
			else
			{
				DrawLooks();
			}
			if (_error != null)
			{
				GUILayout.Label(_error);
			}
			GUILayout.BeginHorizontal();
			if (GUILayout.Button("Save"))
			{
				Save();
			}
			if (GUILayout.Button("Cancel"))
			{
				CloseEditor();
			}
			GUILayout.EndHorizontal();
		}

		private static void DrawSkills()
		{
			List<string> chosen = new List<string>(_edit.Skills ?? new string[0]);
			GUILayout.Label("Chosen: " + chosen.Count + " / " + MaxSkills);
			_skillScroll = GUILayout.BeginScrollView(_skillScroll, GUILayout.Height(MenuLook.Big(420f)));
			if (_skillList != null)
			{
				foreach (KeyValuePair<string, string> skill in _skillList)
				{
					bool on = chosen.Contains(skill.Key);
					GUI.enabled = on || chosen.Count < MaxSkills;
					bool now = GUILayout.Toggle(on, " " + skill.Value);
					GUI.enabled = true;
					if (now && !on)
					{
						chosen.Add(skill.Key);
					}
					else if (!now && on)
					{
						chosen.Remove(skill.Key);
					}
				}
			}
			GUILayout.EndScrollView();
			_edit.Skills = chosen.ToArray();
		}

		private static void DrawLooks()
		{
			GUILayout.BeginHorizontal();
			GUILayout.BeginVertical(GUILayout.Width(MenuLook.Big(200f)));
			float side = MenuLook.Big(192f);
			Rect r = GUILayoutUtility.GetRect(side, side, GUILayout.Width(side), GUILayout.Height(side));
			if (_preview != null)
			{
				GUI.DrawTexture(r, _preview, ScaleMode.ScaleToFit);
			}
			if (GUILayout.Button("Random face"))
			{
				RandomFace();
			}
			GUILayout.Space(8f);
			GUILayout.Label("Name");
			GUILayout.BeginHorizontal();
			_edit.Name = GUILayout.TextField(_edit.Name ?? "", 50);
			if (GUILayout.Button("?", GUILayout.Width(MenuLook.Big(28f))))
			{
				_edit.Name = RandomName(_edit.Gender);
			}
			GUILayout.EndHorizontal();
			GUILayout.Space(8f);
			GUILayout.Label("Pronouns");
			int g = Mathf.Max(0, Array.IndexOf(Genders, _edit.Gender));
			int ng = GUILayout.Toolbar(g, GenderLabels);
			if (ng != g)
			{
				_edit.Gender = Genders[ng];
			}
			GUILayout.Space(8f);
			GUILayout.Label("Attracted to");
			List<string> attract = new List<string>(_edit.Attract ?? new string[0]);
			for (int i = 0; i < Attractions.Length; i++)
			{
				bool on = attract.Contains(Attractions[i]);
				bool now = GUILayout.Toggle(on, " " + AttractionLabels[i]);
				if (now && !on)
				{
					attract.Add(Attractions[i]);
				}
				else if (!now && on)
				{
					attract.Remove(Attractions[i]);
				}
			}
			attract.Remove("IsAttractedNone");
			if (attract.Count == 0)
			{
				attract.Add("IsAttractedNone");
			}
			_edit.Attract = attract.ToArray();
			GUILayout.EndVertical();
			GUILayout.Space(12f);
			GUILayout.BeginVertical();
			Row("Skin", _edit.Skin ?? "?", () => NextSkin(-1), () => NextSkin(1));
			for (int i = 0; i < PartLabels.Length; i++)
			{
				int part = i;
				List<string> list;
				string count = "-";
				if (_parts != null && _parts.TryGetValue(part, out list) && part < _edit.Face.Length)
				{
					count = (list.IndexOf(_edit.Face[part]) + 1) + "/" + list.Count;
				}
				Row(PartLabels[i], count, () => NextPart(part, -1), () => NextPart(part, 1));
			}
			int b = (_bodies != null) ? _bodies.IndexOf(_edit.Body) : -1;
			Row("Body", (_bodies != null && _bodies.Count > 0) ? ((b + 1) + "/" + _bodies.Count) : "-", () => NextBody(-1), () => NextBody(1));
			GUILayout.Label("(the body is seen in the game)");
			GUILayout.EndVertical();
			GUILayout.EndHorizontal();
		}

		private static void NextBody(int dir)
		{
			if (_bodies == null || _bodies.Count == 0)
			{
				return;
			}
			int i = _bodies.IndexOf(_edit.Body) + dir;
			if (i >= _bodies.Count)
			{
				i = 0;
			}
			else if (i < 0)
			{
				i = _bodies.Count - 1;
			}
			_edit.Body = _bodies[i];
		}

		private static void Row(string label, string value, Action back, Action next)
		{
			GUILayout.BeginHorizontal();
			GUILayout.Label(label, GUILayout.Width(MenuLook.Big(80f)));
			if (GUILayout.Button("<", GUILayout.Width(MenuLook.Big(32f))))
			{
				back();
			}
			GUILayout.Label(value, GUILayout.Width(MenuLook.Big(60f)));
			if (GUILayout.Button(">", GUILayout.Width(MenuLook.Big(32f))))
			{
				next();
			}
			GUILayout.EndHorizontal();
		}
	}
}
