using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OstranautsMP
{
	// Per-instance log: BepInEx/OstranautsMP_<n>.log, where n is the first slot not held by another running instance.
	// Two game instances share Player.log and overwrite each other; this file belongs to one process only.
	// The previous run's file is kept as OstranautsMP_<n>.prev.log so a crash log survives a restart.
	// A normal exit ends the file with EndMarker; a previous file without it (the game crashed, froze and was killed,
	// or the computer went off) is reported at the top of the new one, with its last entry's time and where Unity's
	// own log and crash reports are.
	public class InstanceLog : ILogListener
	{
		private const int MaxSlots = 8;

		public static InstanceLog Instance;

		public string FilePath;

		private readonly object _lock = new object();

		private FileStream _stream;

		private StreamWriter _writer;

		// Repeats: past RepeatLimit times in a minute, the same message (with its stack) is only counted, and the count
		// is written once the minute is up. A message repeated every frame then costs a line a minute, not megabytes.
		private const int RepeatLimit = 10;

		// Messages tracked per minute at most; past that, new ones are written without counting.
		private const int RepeatKeysMax = 5000;

		private static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(1.0);

		private readonly Dictionary<string, int> _seen = new Dictionary<string, int>();

		private DateTime _windowStart = DateTime.UtcNow;

		private const string EndMarker = "=== Session ended normally";

		// The previous run's file didn't end normally: its last line (or "" when that couldn't be told).
		private string _previousCut;

		private DateTime _previousTime;

		public static void Start()
		{
			InstanceLog log = new InstanceLog();
			if (!log.Open())
			{
				return;
			}
			Instance = log;
			BepInEx.Logging.Logger.Listeners.Add(log);
			Application.logMessageReceivedThreaded += log.OnUnityLog;
			SceneManager.sceneLoaded += log.OnSceneLoaded;
			AppDomain.CurrentDomain.UnhandledException += log.OnUnhandledException;
			Process p = Process.GetCurrentProcess();
			log.Write("=== " + Plugin.PluginName + " " + Plugin.Version + " | pid " + p.Id + " | Unity " + Application.unityVersion + " | started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
			log.Write("Command line: " + Environment.CommandLine);
			log.Write("System memory: " + SystemInfo.systemMemorySize + " MB, GPU: " + SystemInfo.graphicsDeviceName + " (" + SystemInfo.graphicsMemorySize + " MB)");
			log.LogMemory();
			log.ReportPrevious();
			log.Flush();
		}

		private void ReportPrevious()
		{
			if (_previousCut == null)
			{
				return;
			}
			Write("[Previous run] " + Path.GetFileNameWithoutExtension(FilePath) + ".prev.log did not end normally (the game crashed, hung and was closed, or the computer went off). Its last entry: " + ((_previousCut.Length > 0) ? _previousCut : "(empty)"));
			try
			{
				string unityLog = Application.consoleLogPath;
				if (!string.IsNullOrEmpty(unityLog))
				{
					Write("[Previous run] Unity's log of that run: " + Path.Combine(Path.GetDirectoryName(unityLog), "Player-prev.log"));
				}
				string crashes = Path.Combine(Path.Combine(Path.Combine(Path.GetTempPath(), Application.companyName), Application.productName), "Crashes");
				if (Directory.Exists(crashes))
				{
					string newest = null;
					DateTime newestTime = DateTime.MinValue;
					foreach (string dir in Directory.GetDirectories(crashes))
					{
						DateTime t = Directory.GetLastWriteTime(dir);
						if (t > newestTime)
						{
							newestTime = t;
							newest = dir;
						}
					}
					// A report written after the previous run started is that run's.
					if (newest != null && newestTime >= _previousTime)
					{
						Write("[Previous run] Unity crash report: " + newest + " (" + newestTime.ToString("yyyy-MM-dd HH:mm:ss") + ")");
					}
					else
					{
						Write("[Previous run] no Unity crash report for it in " + crashes);
					}
				}
			}
			catch (Exception e)
			{
				Write("[Previous run] could not look for Unity's reports: " + e.Message);
			}
		}

		// The game is closing the usual way.
		public void End()
		{
			Write(EndMarker + " " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
			Flush(true);
		}

		private bool Open()
		{
			for (int i = 1; i <= MaxSlots; i++)
			{
				string path = Path.Combine(Paths.BepInExRootPath, "OstranautsMP_" + i + ".log");
				FileStream fs;
				try
				{
					// FileShare.Read: others can read it, but a second instance can't take this slot.
					fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
				}
				catch (IOException)
				{
					continue;
				}
				try
				{
					if (fs.Length > 0L)
					{
						byte[] old = new byte[fs.Length];
						fs.Read(old, 0, old.Length);
						string prevPath = Path.Combine(Paths.BepInExRootPath, "OstranautsMP_" + i + ".prev.log");
						File.WriteAllBytes(prevPath, old);
						CheckPrevious(old, File.GetLastWriteTime(path));
					}
				}
				catch
				{
					// Losing the previous log is not worth failing over.
				}
				try
				{
					fs.SetLength(0L);
				}
				catch
				{
					fs.Close();
					continue;
				}
				_stream = fs;
				_writer = new StreamWriter(fs, new UTF8Encoding(false));
				// Not flushed per line (the whole Unity log comes through here): Plugin flushes once a second, errors and
				// crashes go out at once.
				_writer.AutoFlush = false;
				FilePath = path;
				return true;
			}
			return false;
		}

		// The previous file's end: a normal exit leaves EndMarker in its last lines.
		private void CheckPrevious(byte[] old, DateTime written)
		{
			string tail = Encoding.UTF8.GetString(old, Math.Max(0, old.Length - 4096), Math.Min(old.Length, 4096));
			if (tail.Contains(EndMarker))
			{
				return;
			}
			string[] lines = tail.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
			string last = "";
			for (int i = lines.Length - 1; i >= 0; i--)
			{
				string l = lines[i].TrimEnd('\r');
				if (l.StartsWith("[", StringComparison.Ordinal))
				{
					last = (l.Length > 200) ? (l.Substring(0, 200) + "...") : l;
					break;
				}
			}
			_previousCut = last;
			// The run started some time before its last write; a crash report from then on is its.
			_previousTime = written.AddHours(-12.0);
			try
			{
				string head = Encoding.UTF8.GetString(old, 0, Math.Min(old.Length, 512));
				int at = head.IndexOf("started ", StringComparison.Ordinal);
				DateTime started;
				if (at >= 0 && head.Length >= at + 8 + 19 && DateTime.TryParseExact(head.Substring(at + 8, 19), "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out started))
				{
					_previousTime = started;
				}
			}
			catch
			{
			}
		}

		public void Write(string line)
		{
			lock (_lock)
			{
				if (_writer == null)
				{
					return;
				}
				int n;
				if (_seen.TryGetValue(line, out n))
				{
					_seen[line] = n + 1;
					if (n >= RepeatLimit)
					{
						return;
					}
				}
				else if (_seen.Count < RepeatKeysMax)
				{
					_seen[line] = 1;
				}
				WriteLine(line);
			}
		}

		// Caller holds _lock.
		private void WriteLine(string line)
		{
			try
			{
				_writer.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + line);
			}
			catch
			{
			}
		}

		public void LogMemory()
		{
			// Managed heap only: the process figures (private bytes, working set) always read 0 under Unity.
			Write("[Memory] managed " + Mb(GC.GetTotalMemory(false)) + " MB");
		}

		// BepInEx sources. Unity messages arrive here too (UnityLogListening), but OnUnityLog writes them with stack traces.
		public void LogEvent(object sender, LogEventArgs eventArgs)
		{
			if (eventArgs.Source.SourceName == "Unity Log")
			{
				return;
			}
			Write("[" + eventArgs.Level + " " + eventArgs.Source.SourceName + "] " + eventArgs.Data);
			if ((eventArgs.Level & (LogLevel.Error | LogLevel.Fatal)) != 0)
			{
				Flush();
			}
		}

		public void Flush()
		{
			Flush(false);
		}

		// <final>: the game is closing, write the counts now.
		public void Flush(bool final)
		{
			lock (_lock)
			{
				if (_writer == null)
				{
					return;
				}
				if (final || DateTime.UtcNow - _windowStart >= RepeatWindow)
				{
					foreach (KeyValuePair<string, int> kv in _seen)
					{
						if (kv.Value > RepeatLimit)
						{
							string first = kv.Key;
							int nl = first.IndexOf('\n');
							if (nl >= 0)
							{
								first = first.Substring(0, nl);
							}
							if (first.Length > 300)
							{
								first = first.Substring(0, 300) + "...";
							}
							WriteLine("[Repeated] " + (kv.Value - RepeatLimit) + " more times in the last minute: " + first);
						}
					}
					_seen.Clear();
					_windowStart = DateTime.UtcNow;
				}
				try
				{
					_writer.Flush();
				}
				catch
				{
				}
			}
		}

		private void OnUnityLog(string message, string stackTrace, LogType type)
		{
			if ((type == LogType.Error || type == LogType.Exception || type == LogType.Assert) && !string.IsNullOrEmpty(stackTrace))
			{
				Write("[Unity " + type + "] " + message + "\n" + stackTrace.TrimEnd());
			}
			else
			{
				Write("[Unity " + type + "] " + message);
			}
			if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
			{
				Flush();
			}
		}

		private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
		{
			Write("[Scene] loaded " + scene.name + " (" + mode + ")");
			LogMemory();
		}

		private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
		{
			Write("[Unhandled] " + e.ExceptionObject);
			Flush();
		}

		public void Dispose()
		{
			lock (_lock)
			{
				if (_writer != null)
				{
					_writer.Close();
					_writer = null;
					_stream = null;
				}
			}
		}

		private static string Mb(long bytes)
		{
			return (bytes / 1048576L).ToString();
		}
	}
}
