using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using UnityEngine;

namespace OstranautsMP
{
	// The PDA messages and avatars, kept with the player's profile on this computer, not in the save: the same history
	// in every game and session, in the menus too.
	//
	// Files (BepInEx/config):
	// - OstranautsMP_pda.txt: one message per line, "<id>|<real time, UTC ticks>|<in-game epoch, empty in menus>|
	//   <colour hex>|<sender profile ID>|<action 0/1>|<sender name>|<text>"; the last 100 are kept.
	// - OstranautsMP_avatars/<profile ID>.png: the other players' avatars as they last sent them (own avatar:
	//   Profile.Avatar).
	//
	// Each player keeps their own copy: what they send and what they get. Deleting is only in this copy.
	//
	// Protocol: BIN AVATAR <32 bytes profile ID><png>, sent on connect and when the profile's looks change.
	public static class PdaLog
	{
		public class Entry
		{
			public string Id;

			public long Ticks;

			public double Epoch;

			public string Hex;

			public string ProfileId;

			public bool Action;

			public string Name;

			public string Text;
		}

		private const int MaxEntries = 100;

		private const int IdLength = 32;

		private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

		internal static int Version;

		// The other player's profile ID, from their avatar.
		internal static string PeerProfileId;

		private static List<Entry> _entries;

		private static readonly Dictionary<string, Texture2D> Avatars = new Dictionary<string, Texture2D>();

		private static string FilePath => Path.Combine(Paths.ConfigPath, "OstranautsMP_pda.txt");

		private static string AvatarDir => Path.Combine(Paths.ConfigPath, "OstranautsMP_avatars");

		// ---- Messages ------------------------------------------------------------------------------------------

		private static string Encode(Entry e)
		{
			return e.Id + "|" + e.Ticks.ToString(Inv) + "|" + (double.IsNaN(e.Epoch) ? "" : e.Epoch.ToString("R", Inv)) + "|" + (e.Hex ?? "FFFFFF") + "|" + (e.ProfileId ?? "") + "|" + (e.Action ? "1" : "0") + "|" + (e.Name ?? "").Replace("|", "/") + "|" + (e.Text ?? "");
		}

		private static Entry Decode(string line)
		{
			string[] f = (line ?? "").Split(new char[] { '|' }, 8);
			if (f.Length != 8 || f[0].Length == 0)
			{
				return null;
			}
			Entry e = new Entry();
			e.Id = f[0];
			long.TryParse(f[1], NumberStyles.Integer, Inv, out e.Ticks);
			if (!double.TryParse(f[2], NumberStyles.Float, Inv, out e.Epoch))
			{
				e.Epoch = double.NaN;
			}
			e.Hex = f[3];
			e.ProfileId = f[4];
			e.Action = f[5] == "1";
			e.Name = f[6];
			e.Text = f[7];
			return e;
		}

		private static List<Entry> All()
		{
			if (_entries != null)
			{
				return _entries;
			}
			_entries = new List<Entry>();
			try
			{
				if (File.Exists(FilePath))
				{
					foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
					{
						Entry e = Decode(line);
						if (e != null)
						{
							_entries.Add(e);
						}
					}
				}
			}
			catch (Exception ex)
			{
				Plugin.Log.LogWarning("PDA log: could not read " + FilePath + ": " + ex.Message);
			}
			_entries.Sort((Entry a, Entry b) => a.Ticks.CompareTo(b.Ticks));
			// A file from a version that kept more.
			if (_entries.Count > MaxEntries)
			{
				_entries.RemoveRange(0, _entries.Count - MaxEntries);
			}
			return _entries;
		}

		private static void Write()
		{
			try
			{
				StringBuilder sb = new StringBuilder();
				foreach (Entry e in All())
				{
					sb.Append(Encode(e)).Append('\n');
				}
				File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
			}
			catch (Exception ex)
			{
				Plugin.Log.LogError("PDA log: could not save " + FilePath + ": " + ex.Message);
			}
			Version++;
		}

		// Oldest first.
		internal static List<Entry> Read()
		{
			return All();
		}

		internal static void Add(Entry e)
		{
			List<Entry> all = All();
			if (e == null || e.Id == null || all.Exists((Entry x) => x.Id == e.Id))
			{
				return;
			}
			all.Add(e);
			all.Sort((Entry a, Entry b) => a.Ticks.CompareTo(b.Ticks));
			if (all.Count > MaxEntries)
			{
				all.RemoveRange(0, all.Count - MaxEntries);
			}
			Write();
		}

		internal static void Remove(string id)
		{
			if (All().RemoveAll((Entry x) => x.Id == id) > 0)
			{
				Write();
			}
		}

		internal static void Clear()
		{
			All().Clear();
			Write();
		}

		internal static string NewId()
		{
			return Guid.NewGuid().ToString("N").Substring(0, 12);
		}

		// ---- Avatars -------------------------------------------------------------------------------------------

		// Null if there's none.
		internal static Texture2D AvatarOf(string profileId)
		{
			if (string.IsNullOrEmpty(profileId))
			{
				return null;
			}
			if (Profile.Current != null && profileId == Profile.Current.Id)
			{
				return Profile.Avatar;
			}
			Texture2D tex;
			if (Avatars.TryGetValue(profileId, out tex))
			{
				return tex;
			}
			tex = null;
			string path = Path.Combine(AvatarDir, profileId + ".png");
			try
			{
				if (File.Exists(path))
				{
					tex = Load(File.ReadAllBytes(path));
				}
			}
			catch (Exception e)
			{
				Plugin.Log.LogWarning("PDA log: could not read " + path + ": " + e.Message);
			}
			// Also remembered when missing, so the disk isn't asked every frame.
			Avatars[profileId] = tex;
			return tex;
		}

		private static Texture2D Load(byte[] png)
		{
			Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
			if (!tex.LoadImage(png))
			{
				UnityEngine.Object.Destroy(tex);
				return null;
			}
			tex.filterMode = FilterMode.Point;
			tex.hideFlags = HideFlags.HideAndDontSave;
			return tex;
		}

		// To the other player: on connect and when the looks change.
		internal static void SendAvatar()
		{
			if (!Plugin.Net.Connected || Profile.Current == null || Profile.Avatar == null || Profile.Current.Id == null || Profile.Current.Id.Length != IdLength)
			{
				return;
			}
			byte[] png;
			try
			{
				png = Profile.Avatar.EncodeToPNG();
			}
			catch (Exception e)
			{
				Plugin.Log.LogWarning("PDA log: could not encode the avatar: " + e.Message);
				return;
			}
			byte[] data = new byte[IdLength + png.Length];
			Encoding.ASCII.GetBytes(Profile.Current.Id, 0, IdLength, data, 0);
			Buffer.BlockCopy(png, 0, data, IdLength, png.Length);
			Plugin.Net.SendBinary("AVATAR", data, 0, data.Length);
		}

		// Main thread.
		public static void OnAvatar(byte[] data)
		{
			if (data == null || data.Length <= IdLength)
			{
				return;
			}
			string id = Encoding.ASCII.GetString(data, 0, IdLength);
			PeerProfileId = id;
			foreach (char ch in id)
			{
				// A file name: only the hex of a Guid.
				if (!Uri.IsHexDigit(ch))
				{
					return;
				}
			}
			byte[] png = new byte[data.Length - IdLength];
			Buffer.BlockCopy(data, IdLength, png, 0, png.Length);
			Texture2D tex = Load(png);
			if (tex == null)
			{
				return;
			}
			Texture2D old;
			if (Avatars.TryGetValue(id, out old) && old != null)
			{
				UnityEngine.Object.Destroy(old);
			}
			Avatars[id] = tex;
			try
			{
				Directory.CreateDirectory(AvatarDir);
				File.WriteAllBytes(Path.Combine(AvatarDir, id + ".png"), png);
			}
			catch (Exception e)
			{
				Plugin.Log.LogWarning("PDA log: could not save the avatar of " + id + ": " + e.Message);
			}
			Version++;
		}
	}
}
