using System;
using System.Text;
using LitJson;
using UnityEngine;

namespace OstranautsMP
{
	// People who appear on the host while the client is in the game: hired, brought by a story or an encounter, a new
	// player body. Nobody sends a person by themselves; their items do: the body parts and clothes of a new person reach
	// the client through ItemSync, with the person as their parent. A client without that person asks with NEED <id>,
	// and the host answers with the person's save.
	//
	// The client builds them the way the game loads a saved crew member (Ship.SpawnItems: CreatePart from the save,
	// Ship.AddCO, PostGameLoad). Their items arrive next through ItemSync; positions, animations and conditions through
	// WorldSync and CondSync like everyone else's.
	//
	// Protocol: host -> client, binary PERSON <gzip JSON Payload>
	public static class PersonSync
	{
		public class Payload
		{
			public string Ship;

			// Where and what they are on the ship (GUI property maps included), as the ship save keeps a crew member.
			public JsonItem Item;

			public JsonCondOwnerSave Save;

			// Their entry in their company's roster, if they have one (shifts, permissions).
			public Roster Rules;
		}

		// JsonCompanyRules' saved values. Not the class itself: its public static fields (a Color among them) would be
		// written into the JSON too.
		public class Roster
		{
			public int[] aHours;

			public int[] aDutyLvls;

			public bool bShoreLeave;

			public bool bAirlockPermission;

			public bool bRestorePermission;

			public bool bReplaceBatteries;

			public bool bReplaceO2Bottles;

			public bool bForbidUnwear;
		}

		// Host: send this person to the client.
		internal static void HostSend(CondOwner co)
		{
			if (Plugin.Net.CurrentMode != NetSession.Mode.Host || !Plugin.Net.Connected || co == null || co.bDestroyed || co.Crew == null || co.ship == null)
			{
				return;
			}
			if (!Apart.HostSends(co))
			{
				return;
			}
			try
			{
				Payload p = new Payload();
				p.Ship = co.ship.strRegID;
				p.Item = co.ship.GetJsonItem(co);
				p.Save = co.GetJSONSave();
				JsonCompanyRules rules;
				if (co.Company != null && co.Company.mapRoster != null && co.Company.mapRoster.TryGetValue(co.strID, out rules))
				{
					p.Rules = new Roster();
					p.Rules.aHours = rules.aHours;
					p.Rules.aDutyLvls = rules.aDutyLvls;
					p.Rules.bShoreLeave = rules.bShoreLeave;
					p.Rules.bAirlockPermission = rules.bAirlockPermission;
					p.Rules.bRestorePermission = rules.bRestorePermission;
					p.Rules.bReplaceBatteries = rules.bReplaceBatteries;
					p.Rules.bReplaceO2Bottles = rules.bReplaceO2Bottles;
					p.Rules.bForbidUnwear = rules.bForbidUnwear;
				}
				byte[] data = DockSync.Gzip(Encoding.UTF8.GetBytes(JsonMapper.ToJson(p)));
				Plugin.Net.SendBinary("PERSON", data, 0, data.Length);
				Plugin.Log.LogInfo("Sent " + co.strName + " (" + co.strID + ") on " + p.Ship + ", " + data.Length + " bytes");
			}
			catch (Exception e)
			{
				Plugin.Log.LogError("PERSON " + co.strID + " not sent: " + e);
			}
		}

		// Client, main thread: the PERSON binary frame.
		public static void OnBinary(byte[] data)
		{
			if (Plugin.Net.CurrentMode != NetSession.Mode.Client || !WorldSync.InGame)
			{
				return;
			}
			try
			{
				Payload p = JsonMapper.ToObject<Payload>(Encoding.UTF8.GetString(DockSync.Gunzip(data)));
				Create(p);
			}
			catch (Exception e)
			{
				Plugin.Log.LogError("Bad PERSON: " + e);
			}
		}

		private static void Create(Payload p)
		{
			if (p == null || p.Save == null || p.Item == null || p.Save.strID == null)
			{
				return;
			}
			string id = p.Save.strID;
			CondOwner co;
			if (DataHandler.mapCOs.TryGetValue(id, out co) && co != null && !co.bDestroyed)
			{
				return;
			}
			Ship ship = (CrewSim.system != null) ? CrewSim.system.GetShipByRegID(p.Ship) : null;
			if (ship == null || ship.bDestroyed || ship.LoadState != Ship.Loaded.Full)
			{
				Plugin.Log.LogInfo("PERSON " + id + ": ship " + p.Ship + " isn't loaded here");
				return;
			}
			// Their own faction, made with them on the host (PersonSpec.MakeCondOwner).
			if (CrewSim.system.GetFaction(id) == null)
			{
				JsonFaction jf = new JsonFaction();
				jf.Init();
				jf.strName = id;
				jf.strNameFriendly = id;
				CrewSim.system.AddFaction(jf);
			}
			DataHandler.dictCOSaves[id] = p.Save;
			GameObject go = ship.CreatePart(p.Item, id, bLoot: false);
			co = (go != null) ? go.GetComponent<CondOwner>() : null;
			if (co == null)
			{
				Plugin.Log.LogWarning("PERSON " + id + " (" + p.Item.strName + ") could not be made");
				return;
			}
			ship.AddCO(co, bTiles: true);
			co.SetCrewZOffset();
			co.PostGameLoad(Ship.Loaded.Full);
			FaceAnim2.GetPNG(co);
			if (p.Rules != null && co.Company != null && co.Company.mapRoster != null)
			{
				JsonCompanyRules rules = new JsonCompanyRules();
				rules.aHours = p.Rules.aHours ?? rules.aHours;
				rules.aDutyLvls = p.Rules.aDutyLvls ?? rules.aDutyLvls;
				rules.bShoreLeave = p.Rules.bShoreLeave;
				rules.bAirlockPermission = p.Rules.bAirlockPermission;
				rules.bRestorePermission = p.Rules.bRestorePermission;
				rules.bReplaceBatteries = p.Rules.bReplaceBatteries;
				rules.bReplaceO2Bottles = p.Rules.bReplaceO2Bottles;
				rules.bForbidUnwear = p.Rules.bForbidUnwear;
				co.Company.mapRoster[id] = rules;
			}
			Plugin.Log.LogInfo("Made " + co.strName + " (" + id + ") on " + p.Ship + " from the host");
		}
	}
}
