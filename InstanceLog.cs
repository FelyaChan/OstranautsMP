using System;
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
	public class InstanceLog : ILogListener
	{
		private const int MaxSlots = 8;

		public static InstanceLog Instance;

		public string FilePath;

		private readonly object _lock = new object();

		private FileStream _stream;

		private StreamWriter _writer;

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
			log.Write("Persistent data: " + Application.persistentDataPath);
			log.Write("System memory: " + SystemInfo.systemMemorySize + " MB, GPU: " + SystemInfo.graphicsDeviceName + " (" + SystemInfo.graphicsMemorySize + " MB)");
			log.LogMemory();
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
						File.WriteAllBytes(Path.Combine(Paths.BepInExRootPath, "OstranautsMP_" + i + ".prev.log"), old);
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
				_writer.AutoFlush = true;
				FilePath = path;
				return true;
			}
			return false;
		}

		public void Write(string line)
		{
			lock (_lock)
			{
				if (_writer == null)
				{
					return;
				}
				try
				{
					_writer.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + line);
				}
				catch
				{
				}
			}
		}

		public void LogMemory()
		{
			try
			{
				Process p = Process.GetCurrentProcess();
				Write("[Memory] private " + Mb(p.PrivateMemorySize64) + " MB, working set " + Mb(p.WorkingSet64) + " MB, managed " + Mb(GC.GetTotalMemory(false)) + " MB");
			}
			catch (Exception e)
			{
				Write("[Memory] unavailable: " + e.Message);
			}
		}

		// BepInEx sources. Unity messages arrive here too (UnityLogListening), but OnUnityLog writes them with stack traces.
		public void LogEvent(object sender, LogEventArgs eventArgs)
		{
			if (eventArgs.Source.SourceName == "Unity Log")
			{
				return;
			}
			Write("[" + eventArgs.Level + " " + eventArgs.Source.SourceName + "] " + eventArgs.Data);
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
		}

		private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
		{
			Write("[Scene] loaded " + scene.name + " (" + mode + ")");
			LogMemory();
		}

		private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
		{
			Write("[Unhandled] " + e.ExceptionObject);
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
