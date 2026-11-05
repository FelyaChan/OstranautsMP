using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using LitJson;
using Ostranauts.Core;
using TMPro;
using UnityEngine.UI;

namespace OstranautsMP
{
	// Changing your character's looks and name at a sink's mirror ("View Mirror", the game's GUIChargenBody).
	//
	// Outside character creation the game's mirror only offers hair, beard, glasses and scars. In multiplayer the
	// rest of the face, skin, body and the name are open too (only for one's own character: the host can't open it
	// for the client's character, nor the client for the host's). Character creation itself (IsInChargen) is left as
	// the game has it.
	//
	// The name: a person's ID is their name. While the window is open, typing only edits the text; the rename
	// happens once, when the window closes, the way the game's own rename does it (ID, names, identity condition,
	// person spec, relationships, ship ownership) plus what the game leaves out after character creation: the
	// company roster, ledger entries (salary), and the mod's own references (the client's character, lockers).
	// Random looks don't repaint relatives outside character creation.
	//
	// When the window closes, the new looks and name go to the other player:
	//   LOOK <old ID>|<base64 JSON Look>      either way, only for the sender's own character
	public static class Mirror
	{
		public class Look
		{
			public string NewId;

			public string[] Face;

			public string Body;

			public string Skin;

			public string Gender;

			public string[] Attract;
		}

		private const string Prefab = "GUIChargenBody";

		private static readonly string[] Genders = { "IsMale", "IsFemale", "IsNB" };

		private static readonly string[] Attractions = { "IsAttractedMen", "IsAttractedWomen", "IsAttractedNB", "IsAttractedNone" };

		private static readonly string[] Unlock = { "btnRandFace", "btnRandName", "btnEyes", "btnSkin", "btnLips", "btnNeck", "btnNose", "btnPupils", "btnTeeth", "btnHead", "btnBody", "btnEyesBack", "btnSkinBack", "btnLipsBack", "btnNeckBack", "btnNoseBack", "btnPupilsBack", "btnTeethBack", "btnHeadBack", "btnBodyBack" };

		private static readonly AccessTools.FieldRef<GUIChargenBody, CondOwner> User = AccessTools.FieldRefAccess<GUIChargenBody, CondOwner>("coUser");

		private static readonly AccessTools.FieldRef<GUIChargenBody, TMP_InputField> NameBox = AccessTools.FieldRefAccess<GUIChargenBody, TMP_InputField>("tboxName");

		private static readonly char[] Forbidden = (char[])AccessTools.Field(typeof(GUIChargenBody), "FORBIDDEN_WINDOWS_CHARACTERS").GetValue(null);

		private static readonly string[] LedgerLists = { "aHourly", "aShiftly", "aDaily", "aMonthly", "aYearly", "aMortgage" };

		// The open mirror and whose it is, with the ID it had when opened.
		private static GUIChargenBody _open;

		private static string _openId;

		public static void Patch(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(CrewSim), "RaiseUI"), prefix: new HarmonyMethod(typeof(Mirror), nameof(RaiseUIPrefix)));
			harmony.Patch(AccessTools.Method(typeof(GUIChargenBody), "Init"), postfix: new HarmonyMethod(typeof(Mirror), nameof(InitPostfix)));
			harmony.Patch(AccessTools.Method(typeof(GUIChargenBody), "ChangeName"), prefix: new HarmonyMethod(typeof(Mirror), nameof(ChangeNamePrefix)));
			harmony.Patch(AccessTools.Method(typeof(GUIChargenBody), "ChangeSkinRelatives"), prefix: new HarmonyMethod(typeof(Mirror), nameof(NotOutsideChargen)));
			harmony.Patch(AccessTools.Method(typeof(GUIChargenBody), "SaveAndClose"), prefix: new HarmonyMethod(typeof(Mirror), nameof(SaveAndClosePrefix)));
		}

		private static bool Multiplayer => Plugin.Net.Connected && WorldSync.InGame;

		// The person a mirror is about to be opened for may be edited from this machine.
		private static bool MayEdit(CondOwner person)
		{
			if (person == null)
			{
				return true;
			}
			if (Plugin.Net.CurrentMode == NetSession.Mode.Host)
			{
				return person != CrewClaim.HostClaimed;
			}
			return person == CrewClaim.ClientMine;
		}

		private static bool RaiseUIPrefix(string strCOGUIKey, CondOwner coSelf)
		{
			if (!Multiplayer || coSelf == null || coSelf.mapGUIPropMaps == null || strCOGUIKey == null)
			{
				return true;
			}
			Dictionary<string, string> map;
			string prefab;
			if (!coSelf.mapGUIPropMaps.TryGetValue(strCOGUIKey, out map) || map == null || !map.TryGetValue("strGUIPrefab", out prefab) || prefab != Prefab)
			{
				return true;
			}
			Interaction ia = coSelf.GetInteractionCurrent();
			CondOwner person = (ia != null) ? ia.objThem : null;
			if (MayEdit(person))
			{
				return true;
			}
			Plugin.Log.LogInfo("Mirror for " + person.strName + " not opened here: it's the other player's character");
			return false;
		}

		private static bool InChargen(CondOwner co)
		{
			return co == null || co.HasCond("IsInChargen");
		}

		private static void InitPostfix(GUIChargenBody __instance)
		{
			CondOwner co = User(__instance);
			if (!Multiplayer || InChargen(co))
			{
				_open = null;
				return;
			}
			_open = __instance;
			_openId = co.strID;
			Traverse t = Traverse.Create(__instance);
			foreach (string name in Unlock)
			{
				Button b = t.Field(name).GetValue<Button>();
				if (b != null)
				{
					b.gameObject.SetActive(true);
				}
			}
			TMP_InputField box = NameBox(__instance);
			if (box != null)
			{
				box.interactable = true;
			}
		}

		// Typing only edits the text; the rename happens on closing.
		private static bool ChangeNamePrefix(GUIChargenBody __instance)
		{
			return __instance != _open;
		}

		private static bool NotOutsideChargen(GUIChargenBody __instance)
		{
			return __instance != _open;
		}

		private static void SaveAndClosePrefix(GUIChargenBody __instance)
		{
			if (__instance != _open)
			{
				return;
			}
			_open = null;
			CondOwner co = User(__instance);
			if (co == null || co.bDestroyed)
			{
				return;
			}
			try
			{
				string oldId = _openId ?? co.strID;
				TMP_InputField box = NameBox(__instance);
				string typed = (box != null && box.text != null) ? box.text.Trim() : null;
				if (!string.IsNullOrEmpty(typed) && typed != co.strName)
				{
					string why = NameProblem(typed, co);
					if (why == null)
					{
						Rename(co, typed);
					}
					else
					{
						co.LogMessage("Name not changed: " + why, "Bad", co.strID);
					}
				}
				Look look = new Look();
				look.NewId = co.strID;
				Crew crew = co.GetComponent<Crew>();
				if (crew != null)
				{
					look.Face = crew.FaceParts;
					look.Body = crew.BodyType;
				}
				look.Skin = (co.pspec != null) ? co.pspec.strSkin : null;
				foreach (string g in Genders)
				{
					if (co.HasCond(g))
					{
						look.Gender = g;
					}
				}
				List<string> attract = new List<string>();
				foreach (string a in Attractions)
				{
					if (co.HasCond(a))
					{
						attract.Add(a);
					}
				}
				look.Attract = attract.ToArray();
				if (Plugin.Net.Connected)
				{
					Plugin.Net.Send("LOOK " + oldId + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonMapper.ToJson(look))));
				}
				Plugin.Log.LogInfo("Mirror: " + oldId + ((oldId != co.strID) ? (" is now " + co.strID) : "") + ", looks sent");
			}
			catch (Exception e)
			{
				Plugin.Log.LogError("Mirror: applying the changes failed: " + e);
			}
		}

		// Null if the name can be used (the game's own rules: not empty, up to 50 characters, no characters a file
		// name can't have, nobody else called that).
		private static string NameProblem(string name, CondOwner co)
		{
			if (string.IsNullOrEmpty(name))
			{
				return "empty";
			}
			if (name.Length > 50)
			{
				return "longer than 50 characters";
			}
			if (Forbidden != null && name.IndexOfAny(Forbidden) >= 0)
			{
				return "it has characters that can't be used";
			}
			CondOwner other;
			if (DataHandler.mapCOs.TryGetValue(name, out other) && other != null && other != co)
			{
				return "someone is already called that";
			}
			return null;
		}

		// The game's rename (GUIChargenBody.ChangeName) plus what it leaves out outside character creation.
		private static void Rename(CondOwner co, string newName)
		{
			string old = co.strID;
			co.strID = newName;
			co.strName = newName;
			co.strNameFriendly = newName;
			if (co.objCondID != null)
			{
				co.mapConds.Remove(co.objCondID.strName);
				co.objCondID.strName = co.strName;
				co.mapConds[co.objCondID.strName] = co.objCondID;
			}
			int space = newName.LastIndexOf(' ');
			string first = (space >= 0) ? newName.Substring(0, space) : "";
			string last = (space >= 0) ? newName.Substring(space + 1) : newName;
			if (co.pspec != null)
			{
				co.pspec.strFirstName = first;
				co.pspec.strLastName = last;
				co.pspec.strCO = co.strName;
			}
			GUIChargenStack stack = co.GetComponent<GUIChargenStack>();
			if (stack != null)
			{
				stack.strFirstName = first;
				stack.strLastName = last;
			}
			if (co.socUs != null)
			{
				foreach (Relationship r in co.socUs.GetAllPeople())
				{
					CondOwner other = (r != null && r.pspec != null) ? r.pspec.GetCO() : null;
					if (other != null && other.socUs != null)
					{
						other.socUs.RenamePerson(old, newName);
					}
				}
			}
			foreach (string reg in new List<string>(co.GetShipsOwned()))
			{
				CrewSim.system.RegisterShipOwner(reg, newName);
			}
			// Left out by the game: the crew roster and the books are keyed by name.
			JsonCompany company = co.Company;
			JsonCompanyRules rules;
			if (company != null && company.mapRoster != null && company.mapRoster.TryGetValue(old, out rules))
			{
				company.mapRoster.Remove(old);
				company.mapRoster[newName] = rules;
			}
			RenameInLedger(old, newName);
			CrewClaim.Renamed(old, newName);
			Ownership.Renamed(old, newName);
			Apart.Renamed(old, newName);
			MonoSingleton<GUIRenderTargets>.Instance.ResetLoadedPortraits();
			Plugin.Log.LogInfo("Renamed " + old + " to " + newName);
		}

		private static void RenameInLedger(string old, string newName)
		{
			HashSet<LedgerLI> seen = new HashSet<LedgerLI>();
			foreach (string field in LedgerLists)
			{
				List<LedgerLI> list = AccessTools.Field(typeof(Ledger), field).GetValue(null) as List<LedgerLI>;
				if (list != null)
				{
					foreach (LedgerLI li in list)
					{
						RenameLI(li, old, newName, seen);
					}
				}
			}
			foreach (string field in new string[] { "dictPayees", "dictPayors" })
			{
				Dictionary<string, List<LedgerLI>> dict = AccessTools.Field(typeof(Ledger), field).GetValue(null) as Dictionary<string, List<LedgerLI>>;
				if (dict == null)
				{
					continue;
				}
				foreach (List<LedgerLI> list in dict.Values)
				{
					foreach (LedgerLI li in list)
					{
						RenameLI(li, old, newName, seen);
					}
				}
				List<LedgerLI> moved;
				if (dict.TryGetValue(old, out moved))
				{
					dict.Remove(old);
					List<LedgerLI> existing;
					if (dict.TryGetValue(newName, out existing))
					{
						existing.AddRange(moved);
					}
					else
					{
						dict[newName] = moved;
					}
				}
			}
		}

		private static void RenameLI(LedgerLI li, string old, string newName, HashSet<LedgerLI> seen)
		{
			if (li == null || !seen.Add(li))
			{
				return;
			}
			if (li.strPayee == old)
			{
				li.strPayee = newName;
			}
			if (li.strPayor == old)
			{
				li.strPayor = newName;
			}
		}

		// ---- The other side ------------------------------------------------------------------------------------

		// Main thread. Returns false if the message isn't ours.
		public static bool OnMessage(string cmd, string arg)
		{
			if (cmd != "LOOK")
			{
				return false;
			}
			if (!Multiplayer)
			{
				return true;
			}
			int bar = arg.IndexOf('|');
			if (bar <= 0)
			{
				return true;
			}
			try
			{
				Look look = JsonMapper.ToObject<Look>(Encoding.UTF8.GetString(Convert.FromBase64String(arg.Substring(bar + 1))));
				string oldId = arg.Substring(0, bar);
				CondOwner co;
				if (!DataHandler.mapCOs.TryGetValue(oldId, out co) || co == null || co.bDestroyed)
				{
					if (look.NewId == null || !DataHandler.mapCOs.TryGetValue(look.NewId, out co) || co == null || co.bDestroyed)
					{
						Plugin.Log.LogWarning("Mirror: no " + oldId + " here");
						return true;
					}
				}
				// Only the sender's own character: the client's on the host, the host's on the client.
				CondOwner allowed = (Plugin.Net.CurrentMode == NetSession.Mode.Host) ? CrewClaim.HostClaimed : CrewSim.coPlayer;
				if (co != allowed)
				{
					Plugin.Log.LogWarning("Mirror: " + co.strName + " isn't the other player's character");
					return true;
				}
				Apply(co, look);
			}
			catch (Exception e)
			{
				Plugin.Log.LogError("Mirror: bad LOOK: " + e);
			}
			return true;
		}

		private static void Apply(CondOwner co, Look look)
		{
			if (!string.IsNullOrEmpty(look.NewId) && look.NewId != co.strID)
			{
				string why = NameProblem(look.NewId, co);
				if (why == null)
				{
					Rename(co, look.NewId);
				}
				else
				{
					Plugin.Log.LogWarning("Mirror: not renaming " + co.strID + " to " + look.NewId + ": " + why);
				}
			}
			if (look.Skin != null && co.pspec != null)
			{
				co.pspec.strSkin = look.Skin;
			}
			Crew crew = co.GetComponent<Crew>();
			if (crew != null && look.Face != null && look.Face.Length > 0)
			{
				crew.SetBodyFaceSkin(look.Body, look.Face, bForce: true);
			}
			if (look.Gender != null && Array.IndexOf(Genders, look.Gender) >= 0)
			{
				foreach (string g in Genders)
				{
					if (g == look.Gender)
					{
						co.SetCondAmount(g, 1.0);
					}
					else
					{
						co.ZeroCondAmount(g);
					}
				}
				if (co.pspec != null)
				{
					co.pspec.strGender = look.Gender;
				}
			}
			if (look.Attract != null)
			{
				foreach (string a in Attractions)
				{
					if (Array.IndexOf(look.Attract, a) >= 0)
					{
						co.SetCondAmount(a, 1.0);
					}
					else
					{
						co.ZeroCondAmount(a);
					}
				}
			}
			MonoSingleton<GUIRenderTargets>.Instance.SetFace(co, bForce: true);
			MonoSingleton<GUIRenderTargets>.Instance.ResetLoadedPortraits();
			Plugin.Log.LogInfo("Mirror: " + co.strName + " got new looks from the other player");
		}
	}
}
