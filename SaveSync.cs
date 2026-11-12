using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using Ostranauts.Core;
using Ostranauts.Core.Models;

namespace OstranautsMP
{
	// Host: when a save is loaded, send its folder to the client.
	// Client: write the folder to Saves/MP_Host and load it through the game's own loader.
	//
	// Protocol:
	//   SAVE_BEGIN <totalBytes>
	//   FILE <base64 relative path> <size>
	//   BIN CHUNK <raw bytes>          (binary frame, repeated until <size> bytes are received)
	//   SAVE_END
	public static class SaveSync
	{
		public const string FolderName = "MP_Host";

		private const int ChunkSize = 256 * 1024;

		// Queued ahead of the wire at most; the game's own messages wait behind no more than this.
		private const long MaxQueued = 1024 * 1024;

		// Shown in the UI; null when nothing is happening.
		public static volatile string Progress;

		// Host: how much of the save is sent, 0..1 (ClientLoad's bar).
		public static volatile float SendFraction;

		private static readonly MethodInfo LoadSelectedSave = AccessTools.Method(typeof(LoadManager), "OnLoadSelectedSave");

		private static readonly MethodInfo SaveGame = AccessTools.Method(typeof(LoadManager), "SaveGame");

		private static readonly FieldInfo LoadedSave = AccessTools.Field(typeof(LoadManager), "_loadedSave");

		private static string _folder;

		private static FileStream _file;

		private static long _fileRemaining;

		private static long _received;

		private static long _total;

		// Host side. Called from the LoadManager.OnLoadSelectedSave prefix, before the game starts loading.
		public static void HostSend(SaveInfo saveInfo)
		{
			NetSession net = Plugin.Net;
			if (net.CurrentMode != NetSession.Mode.Host || !net.Connected || saveInfo == null)
			{
				return;
			}
			// Read everything now, on the main thread, so a later autosave can't change files mid-transfer.
			List<KeyValuePair<string, byte[]>> files = new List<KeyValuePair<string, byte[]>>();
			long total = 0L;
			try
			{
				string root = Path.GetFullPath(saveInfo.Path);
				foreach (string f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
				{
					string rel = Path.GetFullPath(f).Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
					byte[] bytes = File.ReadAllBytes(f);
					files.Add(new KeyValuePair<string, byte[]>(rel, bytes));
					total += bytes.Length;
				}
			}
			catch (Exception e)
			{
				Progress = "Save send failed: " + e.Message;
				Plugin.Log.LogError(Progress);
				return;
			}
			Plugin.Log.LogInfo("Sending save '" + saveInfo.SaveName + "': " + files.Count + " files, " + total + " bytes");
			// From now on the client will have every item that exists here.
			ItemSync.ResetKnown();
			SendFraction = 0f;
			ClientLoad.Begin();
			// The connection it's for: if that client goes and another comes, the rest isn't sent to the new one.
			int conn = net.ConnId;
			NetSession.StartThread("save sender", () =>
			{
				long sent = 0L;
				net.Send("SAVE_BEGIN " + total);
				foreach (KeyValuePair<string, byte[]> file in files)
				{
					net.Send("FILE " + Convert.ToBase64String(Encoding.UTF8.GetBytes(file.Key)) + " " + file.Value.Length);
					for (int off = 0; off < file.Value.Length; off += ChunkSize)
					{
						if (!net.WaitQueue(MaxQueued, conn))
						{
							Progress = null;
							return;
						}
						int n = Math.Min(ChunkSize, file.Value.Length - off);
						net.SendBinary("CHUNK", file.Value, off, n);
						sent += n;
						SendFraction = (total > 0L) ? ((float)sent / (float)total) : 1f;
						Progress = "Sending save: " + Mb(sent) + " / " + Mb(total) + " MB";
					}
				}
				if (!net.WaitQueue(MaxQueued, conn))
				{
					Progress = null;
					return;
				}
				net.Send("SAVE_END");
				Progress = null;
				Plugin.Log.LogInfo("Save sent");
			});
		}

		// Host side: the ship we're on plus everything docked to it, as of the client's last READY. Null until then.
		private static string _locBaseline;

		private static string _locPending;

		private static float _locSince;

		private const float LocationSettleSeconds = 3f;

		// Host, on the client's READY: the client now has the world as it is.
		internal static void ResetLocation()
		{
			_locBaseline = LocationSignature();
			_locPending = null;
		}

		// Host: a dock/undock was relayed; stop watching until the client answers (DockSync).
		internal static void SuspendLocation()
		{
			_locBaseline = null;
			_locPending = null;
		}

		private static string LocationSignature()
		{
			Ship cur = CrewSim.shipCurrentLoaded;
			if (cur == null)
			{
				return null;
			}
			List<string> ids = new List<string>();
			ids.Add(cur.strRegID);
			foreach (Ship s in cur.GetAllDockedShips())
			{
				if (s != null)
				{
					ids.Add(s.strRegID);
				}
			}
			ids.Sort(StringComparer.Ordinal);
			return string.Join(",", ids.ToArray());
		}

		// Host, every frame. Docking, undocking or moving to another ship isn't replicated; when the set of ships
		// we're on changes and stays changed for a few seconds, pause and resend the save (same as a late join).
		public static void HostWatchLocation(float now)
		{
			NetSession net = Plugin.Net;
			if (net.CurrentMode != NetSession.Mode.Host || !net.Connected)
			{
				_locBaseline = null;
				return;
			}
			if (_locBaseline == null || !WorldSync.InGame || Progress != null)
			{
				return;
			}
			string sig = LocationSignature();
			if (sig == null || sig == _locBaseline)
			{
				_locPending = null;
				return;
			}
			// Apart (the client's place keeps running on its side), or back where the client is: nothing to resend.
			if (Apart.HostActive || Apart.ClientIsAt(sig))
			{
				_locBaseline = sig;
				_locPending = null;
				return;
			}
			if (sig != _locPending)
			{
				_locPending = sig;
				_locSince = now;
				return;
			}
			if (now - _locSince < LocationSettleSeconds)
			{
				return;
			}
			Plugin.Log.LogInfo("Location changed (" + _locBaseline + " -> " + sig + "), resending the save");
			// Wait for the client's next READY before watching again.
			_locBaseline = null;
			_locPending = null;
			HostLateJoin();
		}

		// Host side: a client connected while the host is already in the game.
		// Pause, save the current state the way an autosave does, and send that save.
		public static void HostLateJoin()
		{
			CrewSim.Paused = true;
			Plugin.Log.LogInfo("Sending the whole save: paused, saving");
			LoadManager lm = MonoSingleton<LoadManager>.Instance;
			SaveInfo before = (SaveInfo)LoadedSave.GetValue(null);
			try
			{
				lm.AutoSave(useThreading: false);
				SaveInfo saved = (SaveInfo)LoadedSave.GetValue(null);
				if (saved == null || saved == before)
				{
					// AutoSave can return without saving (no autosave group for this world yet).
					string name = CrewSim.coPlayer.strName + "_mp_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
					SaveGame.Invoke(lm, new object[] { name, 0, false });
					saved = (SaveInfo)LoadedSave.GetValue(null);
				}
				if (saved == null || saved == before)
				{
					Progress = "Could not save the game for the joining client";
					Plugin.Log.LogError(Progress);
					return;
				}
				HostSend(saved);
			}
			catch (Exception e)
			{
				Progress = "Save for joining client failed: " + e.Message;
				Plugin.Log.LogError(Progress);
			}
		}

		// Client side, main thread.
		public static void OnMessage(string cmd, string arg)
		{
			if (Plugin.Net.CurrentMode != NetSession.Mode.Client)
			{
				return;
			}
			try
			{
				switch (cmd)
				{
					case "SAVE_BEGIN":
						Begin(long.Parse(arg));
						break;
					case "FILE":
						OpenFile(arg);
						break;
					case "SAVE_END":
						// A file still open, or fewer bytes than announced: the save is cut short, don't load it.
						if (_file != null || _received != _total)
						{
							throw new Exception("save incomplete, " + Mb(_received) + " / " + Mb(_total) + " MB");
						}
						Progress = null;
						LoadReceived();
						break;
					default:
						Plugin.Log.LogWarning("Unknown message: " + cmd);
						break;
				}
			}
			catch (Exception e)
			{
				Fail(e.Message);
			}
		}

		// Client: the save can't be used. Say so and leave, so the host stops waiting for READY; joining again
		// sends a new save.
		private static void Fail(string why)
		{
			ResetReceive();
			string text = "Save transfer failed: " + why;
			Plugin.Log.LogError(text);
			Alert.Show(text);
			Plugin.Net.Stop();
		}

		public static void ResetReceive()
		{
			CloseFile();
			Progress = null;
		}

		private static void Begin(long total)
		{
			CloseFile();
			_folder = MonoSingleton<LoadManager>.Instance.SavesPath + FolderName;
			if (Directory.Exists(_folder))
			{
				Directory.Delete(_folder, recursive: true);
			}
			Directory.CreateDirectory(_folder);
			_total = total;
			_received = 0L;
			Progress = "Receiving save: 0 / " + Mb(_total) + " MB";
			Plugin.Log.LogInfo("Receiving save into Saves/" + FolderName + ", " + total + " bytes");
		}

		private static void OpenFile(string arg)
		{
			CloseFile();
			int space = arg.IndexOf(' ');
			string rel = Encoding.UTF8.GetString(Convert.FromBase64String(arg.Substring(0, space)));
			long size = long.Parse(arg.Substring(space + 1));
			if (rel.Contains("..") || Path.IsPathRooted(rel))
			{
				throw new Exception("bad path " + rel);
			}
			string path = Path.Combine(_folder, rel);
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			_file = File.Create(path);
			_fileRemaining = size;
			if (_fileRemaining <= 0L)
			{
				CloseFile();
			}
		}

		// Client side, main thread: a binary CHUNK frame.
		public static void OnBinary(string cmd, byte[] data)
		{
			if (cmd != "CHUNK" || Plugin.Net.CurrentMode != NetSession.Mode.Client)
			{
				return;
			}
			try
			{
				WriteChunk(data);
			}
			catch (Exception e)
			{
				Fail(e.Message);
			}
		}

		private static void WriteChunk(byte[] data)
		{
			if (_file == null)
			{
				throw new Exception("chunk without file");
			}
			_file.Write(data, 0, data.Length);
			_fileRemaining -= data.Length;
			_received += data.Length;
			Progress = "Receiving save: " + Mb(_received) + " / " + Mb(_total) + " MB";
			if (_fileRemaining <= 0L)
			{
				CloseFile();
			}
		}

		private static void CloseFile()
		{
			if (_file != null)
			{
				_file.Close();
				_file = null;
			}
		}

		private static void LoadReceived()
		{
			Dictionary<string, JsonSaveInfo> dict = new Dictionary<string, JsonSaveInfo>();
			DataHandler.JsonToData(_folder + "/saveInfo.json", dict);
			JsonSaveInfo json = null;
			foreach (JsonSaveInfo value in dict.Values)
			{
				json = value;
				break;
			}
			if (json == null)
			{
				throw new Exception("saveInfo.json missing or empty");
			}
			// Name it after our folder so the Steam Cloud check never pulls a same-named save over it.
			json.strName = FolderName;
			SaveInfo saveInfo = new SaveInfo(json, _folder);
			Plugin.Log.LogInfo("Loading received save");
			LoadSelectedSave.Invoke(MonoSingleton<LoadManager>.Instance, new object[] { saveInfo });
		}

		private static string Mb(long bytes)
		{
			return (bytes / 1048576.0).ToString("0.0");
		}
	}
}
