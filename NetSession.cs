using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace OstranautsMP
{
	// Minimal TCP session: one host, one client.
	// Wire format: text messages are UTF-8 lines ending in '\n'. A binary message is the line "BIN <cmd> <length>"
	// followed by <length> raw bytes (save file chunks: no base64, a third less traffic).
	// Socket work runs on background threads; everything else happens in Poll() on the main thread.
	public class NetSession
	{
		public enum Mode
		{
			None,
			Host,
			Client
		}

		private struct Msg
		{
			public int Gen;

			public string Line;

			public byte[] Data;

			// The connection a "__disconnected" came from; a stale reader must not drop a newer peer.
			public TcpClient Source;
		}

		public Mode CurrentMode { get; private set; }

		public bool Connected { get; private set; }

		public string Status = "Idle";

		public string RemoteVersion;

		public int LastRttMs = -1;

		public float LastReceiveTime;

		// Called on the main thread for every text message that is not handled here.
		public Action<string, string> OnMessage;

		// Called on the main thread for every binary message.
		public Action<string, byte[]> OnBinary;

		// Called on the main thread when a peer connects.
		public Action OnConnected;

		// Called on the main thread when the peer goes away: "left" or "timed out".
		public Action<string> OnPeerLost;

		// No message at all for this long (keepalives come every KeepAliveMs from a background thread, so a peer
		// frozen while saving or loading still counts as alive) means the peer is gone.
		private const float TimeoutSeconds = 20f;

		private const int KeepAliveMs = 2000;

		private TcpListener _listener;

		private TcpClient _client;

		private Stream _stream;

		private StreamWriter _writer;

		private readonly object _lock = new object();

		// Separate from _lock so a slow socket write never blocks Poll() on the main thread.
		private readonly object _writeLock = new object();

		private readonly Queue<Msg> _inbox = new Queue<Msg>();

		private readonly List<Msg> _drain = new List<Msg>();

		// Bumped on every Stop(); messages from threads of an older session are ignored.
		private int _generation;

		public int Generation => _generation;

		public void Host(int port)
		{
			Stop();
			int gen = _generation;
			try
			{
				_listener = new TcpListener(IPAddress.Any, port);
				_listener.Start();
			}
			catch (Exception e)
			{
				Stop();
				Status = "Host failed: " + e.Message;
				Plugin.Log.LogError(Status);
				return;
			}
			CurrentMode = Mode.Host;
			Status = "Hosting on port " + port + ", waiting for client...";
			Plugin.Log.LogInfo(Status);
			TcpListener listener = _listener;
			StartThread(() => AcceptLoop(listener, gen));
		}

		public void Join(string address, int port)
		{
			Stop();
			int gen = _generation;
			CurrentMode = Mode.Client;
			Status = "Connecting to " + address + ":" + port + "...";
			Plugin.Log.LogInfo(Status);
			StartThread(() =>
			{
				try
				{
					TcpClient c = new TcpClient();
					c.Connect(address, port);
					Attach(c, gen);
				}
				catch (Exception e)
				{
					Enqueue(gen, "__error " + e.Message, null);
				}
			});
		}

		public void Stop()
		{
			lock (_lock)
			{
				_generation++;
				CloseClient();
				if (_listener != null)
				{
					try
					{
						_listener.Stop();
					}
					catch
					{
					}
					_listener = null;
				}
				_inbox.Clear();
			}
			CurrentMode = Mode.None;
			Connected = false;
			Status = "Idle";
			RemoteVersion = null;
			LastRttMs = -1;
		}

		// Thread-safe.
		public void Send(string line)
		{
			StreamWriter writer;
			lock (_lock)
			{
				writer = _writer;
			}
			if (writer == null)
			{
				return;
			}
			lock (_writeLock)
			{
				try
				{
					writer.Write(line + "\n");
				}
				catch
				{
					// The read thread notices the broken socket and reports the disconnect.
				}
			}
		}

		// Thread-safe. <cmd> must not contain spaces.
		public void SendBinary(string cmd, byte[] data, int offset, int count)
		{
			StreamWriter writer;
			Stream stream;
			lock (_lock)
			{
				writer = _writer;
				stream = _stream;
			}
			if (writer == null || stream == null)
			{
				return;
			}
			lock (_writeLock)
			{
				try
				{
					// AutoFlush puts the header on the wire before the raw bytes.
					writer.Write("BIN " + cmd + " " + count + "\n");
					stream.Write(data, offset, count);
				}
				catch
				{
				}
			}
		}

		// Main thread. Blocks until a binary message <cmd> of this session arrives (taking it out of the queue, the rest
		// stays in order) or the time runs out; null then. Messages already taken by a running Poll aren't seen.
		public byte[] WaitBinary(string cmd, int timeoutMs)
		{
			int gen = _generation;
			Stopwatch sw = Stopwatch.StartNew();
			while (sw.ElapsedMilliseconds < timeoutMs && Connected && gen == _generation)
			{
				lock (_lock)
				{
					int n = _inbox.Count;
					byte[] found = null;
					for (int i = 0; i < n; i++)
					{
						Msg m = _inbox.Dequeue();
						if (found == null && m.Gen == gen && m.Data != null && m.Line == cmd)
						{
							found = m.Data;
							continue;
						}
						_inbox.Enqueue(m);
					}
					if (found != null)
					{
						return found;
					}
				}
				Thread.Sleep(5);
			}
			return null;
		}

		public void Poll(float now)
		{
			lock (_lock)
			{
				_drain.AddRange(_inbox);
				_inbox.Clear();
			}
			foreach (Msg m in _drain)
			{
				if (m.Gen != _generation)
				{
					continue;
				}
				if (m.Data != null)
				{
					LastReceiveTime = now;
					if (OnBinary != null)
					{
						OnBinary(m.Line, m.Data);
					}
				}
				else if (m.Line == "__disconnected")
				{
					bool current;
					lock (_lock)
					{
						current = m.Source == _client;
					}
					Plugin.Log.LogInfo("Peer disconnected" + (current ? "" : " (old connection)"));
					if (current && Connected)
					{
						PeerGone("left");
					}
				}
				else
				{
					Handle(m.Line, now);
				}
			}
			_drain.Clear();
			// After the queue: a frame that was frozen for a while first takes in what arrived meanwhile.
			if (Connected && now - LastReceiveTime > TimeoutSeconds)
			{
				Plugin.Log.LogWarning("Peer timed out");
				PeerGone("timed out");
			}
		}

		private void PeerGone(string reason)
		{
			if (CurrentMode == Mode.Host)
			{
				lock (_lock)
				{
					CloseClient();
				}
				Connected = false;
				RemoteVersion = null;
				LastRttMs = -1;
				Status = "Client " + reason + ", waiting for client...";
			}
			else
			{
				Stop();
				Status = (reason == "left") ? "Disconnected" : "Host " + reason;
			}
			if (OnPeerLost != null)
			{
				OnPeerLost(reason);
			}
		}

		private void Handle(string line, float now)
		{
			if (line == "__connected")
			{
				Connected = true;
				LastReceiveTime = now;
				Status = (CurrentMode == Mode.Host) ? "Client connected" : "Connected";
				Plugin.Log.LogInfo(Status);
				Send("HELLO " + Plugin.Version);
				if (OnConnected != null)
				{
					OnConnected();
				}
				return;
			}
			if (line.StartsWith("__error "))
			{
				string msg = line.Substring(8);
				Stop();
				Status = "Error: " + msg;
				Plugin.Log.LogError(Status);
				return;
			}
			LastReceiveTime = now;
			int space = line.IndexOf(' ');
			string cmd = (space < 0) ? line : line.Substring(0, space);
			string arg = (space < 0) ? "" : line.Substring(space + 1);
			switch (cmd)
			{
				case "HELLO":
					RemoteVersion = arg;
					Plugin.Log.LogInfo("Remote plugin version: " + arg);
					break;
				case "PING":
					Send("PONG " + arg);
					break;
				case "KA":
					break;
				case "PONG":
					long sent;
					if (long.TryParse(arg, out sent))
					{
						LastRttMs = (int)((Stopwatch.GetTimestamp() - sent) * 1000L / Stopwatch.Frequency);
					}
					break;
				default:
					if (OnMessage != null)
					{
						OnMessage(cmd, arg);
					}
					else
					{
						Plugin.Log.LogWarning("Unknown message: " + cmd);
					}
					break;
			}
		}

		private void AcceptLoop(TcpListener listener, int gen)
		{
			try
			{
				while (true)
				{
					TcpClient c = listener.AcceptTcpClient();
					Attach(c, gen);
				}
			}
			catch
			{
				// listener.Stop() ends the loop.
			}
		}

		private void Attach(TcpClient c, int gen)
		{
			lock (_lock)
			{
				// Wrong session, or already have a peer (one client only for now).
				if (gen != _generation || _client != null)
				{
					c.Close();
					return;
				}
				c.NoDelay = true;
				_client = c;
				_stream = c.GetStream();
				_writer = new StreamWriter(_stream, new UTF8Encoding(false));
				_writer.AutoFlush = true;
			}
			StartThread(() => ReadLoop(c, gen));
			StartThread(() => KeepAlive(c, gen));
			Enqueue(gen, "__connected", null);
		}

		private void ReadLoop(TcpClient c, int gen)
		{
			try
			{
				BufferedStream input = new BufferedStream(c.GetStream(), 65536);
				MemoryStream lineBytes = new MemoryStream();
				UTF8Encoding utf8 = new UTF8Encoding(false);
				while (true)
				{
					int b = input.ReadByte();
					if (b < 0)
					{
						break;
					}
					if (b != '\n')
					{
						lineBytes.WriteByte((byte)b);
						continue;
					}
					string line = utf8.GetString(lineBytes.GetBuffer(), 0, (int)lineBytes.Length);
					lineBytes.SetLength(0L);
					if (!line.StartsWith("BIN "))
					{
						Enqueue(gen, line, null);
						continue;
					}
					string[] parts = line.Split(' ');
					int length;
					if (parts.Length != 3 || !int.TryParse(parts[2], out length) || length < 0)
					{
						break;
					}
					byte[] data = new byte[length];
					int got = 0;
					while (got < length)
					{
						int n = input.Read(data, got, length - got);
						if (n <= 0)
						{
							throw new EndOfStreamException();
						}
						got += n;
					}
					Enqueue(gen, parts[1], data);
				}
			}
			catch
			{
			}
			Msg m;
			m.Gen = gen;
			m.Line = "__disconnected";
			m.Data = null;
			m.Source = c;
			lock (_lock)
			{
				_inbox.Enqueue(m);
			}
		}

		private void KeepAlive(TcpClient c, int gen)
		{
			while (true)
			{
				Thread.Sleep(KeepAliveMs);
				lock (_lock)
				{
					if (gen != _generation || _client != c)
					{
						return;
					}
				}
				Send("KA");
			}
		}

		// Caller holds _lock.
		private void CloseClient()
		{
			if (_client != null)
			{
				try
				{
					_client.Close();
				}
				catch
				{
				}
				_client = null;
			}
			_writer = null;
			_stream = null;
		}

		private void Enqueue(int gen, string line, byte[] data)
		{
			Msg m;
			m.Gen = gen;
			m.Line = line;
			m.Data = data;
			m.Source = null;
			lock (_lock)
			{
				_inbox.Enqueue(m);
			}
		}

		private static void StartThread(ThreadStart fn)
		{
			Thread t = new Thread(fn);
			t.IsBackground = true;
			t.Start();
		}
	}
}
