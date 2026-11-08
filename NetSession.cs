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
	// Sending only queues the message: one writer thread per connection puts the queue on the wire, so a slow or
	// stuck peer never holds up the caller (the game's main thread).
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

		// A write the peer doesn't take in for this long drops the connection.
		private const int WriteTimeoutMs = 20000;

		// Bigger than anything the mod sends (the largest text is a full CONDS, the largest binary a ship snapshot);
		// a peer sending more is broken or hostile, and is dropped before the memory is taken.
		private const int MaxLineBytes = 16 * 1024 * 1024;

		private const int MaxBinaryBytes = 256 * 1024 * 1024;

		// One message waiting to be written: a header line, and for a binary message its bytes.
		private struct Out
		{
			public byte[] Head;

			public byte[] Body;

			public int Offset;

			public int Count;
		}

		// A connection's send queue. Lock the object itself for its fields.
		private class Conn
		{
			public int Id;

			public TcpClient Client;

			public Stream Stream;

			public readonly Queue<Out> Queue = new Queue<Out>();

			// Bytes queued and not yet written.
			public long Bytes;

			public bool Closed;
		}

		private TcpListener _listener;

		private TcpClient _client;

		private Conn _conn;

		private int _connCount;

		private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

		private readonly object _lock = new object();

		private readonly Queue<Msg> _inbox = new Queue<Msg>();

		private readonly List<Msg> _drain = new List<Msg>();

		// Bumped on every Stop(); messages from threads of an older session are ignored.
		private int _generation;

		public int Generation => _generation;

		// Changes with every new connection (a host gets a new one each time a client connects).
		public int ConnId
		{
			get
			{
				lock (_lock)
				{
					return (_conn != null) ? _conn.Id : 0;
				}
			}
		}

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

		// Thread-safe. Queues the line and returns.
		public void Send(string line)
		{
			Out o;
			o.Head = Utf8.GetBytes(line + "\n");
			o.Body = null;
			o.Offset = 0;
			o.Count = 0;
			Push(o);
		}

		// Thread-safe. Queues the message and returns; <data> must not change afterwards. <cmd> must not contain spaces.
		public void SendBinary(string cmd, byte[] data, int offset, int count)
		{
			Out o;
			o.Head = Utf8.GetBytes("BIN " + cmd + " " + count + "\n");
			o.Body = data;
			o.Offset = offset;
			o.Count = count;
			Push(o);
		}

		private void Push(Out o)
		{
			Conn conn;
			lock (_lock)
			{
				conn = _conn;
			}
			if (conn == null)
			{
				return;
			}
			lock (conn)
			{
				if (conn.Closed)
				{
					return;
				}
				conn.Queue.Enqueue(o);
				conn.Bytes += o.Head.Length + o.Count;
				Monitor.Pulse(conn);
			}
		}

		// Any thread. Waits while more than <maxBytes> are queued on connection <connId>. False if that connection is
		// gone (then nothing more should be sent for it).
		public bool WaitQueue(long maxBytes, int connId)
		{
			while (true)
			{
				long bytes;
				lock (_lock)
				{
					if (_conn == null || _conn.Id != connId)
					{
						return false;
					}
					lock (_conn)
					{
						bytes = _conn.Bytes;
					}
				}
				if (bytes <= maxBytes)
				{
					return true;
				}
				Thread.Sleep(5);
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
					if (m.Line == "__takeover")
					{
						m.Source.Close();
					}
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
				else if (m.Line == "__takeover")
				{
					Plugin.Log.LogInfo("The client connected again from the same address; dropping the old connection");
					if (Connected)
					{
						PeerGone("left");
					}
					else
					{
						lock (_lock)
						{
							CloseClient();
						}
					}
					Attach(m.Source, _generation);
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
					// Another version speaks another protocol: no playing together (each side finds out and drops).
					if (Alert.VersionMismatch(arg))
					{
						PeerGone("has mod version " + arg + ", you have " + Plugin.Version + ": both need the same");
					}
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
			Conn conn;
			lock (_lock)
			{
				if (gen != _generation)
				{
					c.Close();
					return;
				}
				// One client only. A new connection from the client's own address is the client coming back before
				// the old connection timed out: it takes the place, after the old one leaves the usual way (Poll).
				if (_client != null)
				{
					IPAddress from = Address(c);
					if (CurrentMode == Mode.Host && from != null && from.Equals(Address(_client)))
					{
						Msg t;
						t.Gen = gen;
						t.Line = "__takeover";
						t.Data = null;
						t.Source = c;
						_inbox.Enqueue(t);
						return;
					}
					Plugin.Log.LogInfo("Refused a connection from " + from + ": a client is already connected");
					c.Close();
					return;
				}
				c.NoDelay = true;
				c.SendTimeout = WriteTimeoutMs;
				_client = c;
				_conn = new Conn();
				_conn.Id = ++_connCount;
				_conn.Client = c;
				_conn.Stream = c.GetStream();
				conn = _conn;
			}
			StartThread(() => WriteLoop(conn));
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
						if (lineBytes.Length >= MaxLineBytes)
						{
							Plugin.Log.LogWarning("Dropping the connection: a line over " + MaxLineBytes + " bytes");
							break;
						}
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
						Plugin.Log.LogWarning("Dropping the connection: a bad binary header");
						break;
					}
					if (length > MaxBinaryBytes)
					{
						Plugin.Log.LogWarning("Dropping the connection: binary " + parts[1] + " of " + length + " bytes, over " + MaxBinaryBytes);
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

		// One per connection: writes its queue in order until the connection closes.
		private void WriteLoop(Conn conn)
		{
			try
			{
				while (true)
				{
					Out o;
					lock (conn)
					{
						while (conn.Queue.Count == 0)
						{
							if (conn.Closed)
							{
								return;
							}
							Monitor.Wait(conn);
						}
						if (conn.Closed)
						{
							return;
						}
						o = conn.Queue.Peek();
					}
					conn.Stream.Write(o.Head, 0, o.Head.Length);
					if (o.Body != null)
					{
						conn.Stream.Write(o.Body, o.Offset, o.Count);
					}
					lock (conn)
					{
						conn.Queue.Dequeue();
						conn.Bytes -= o.Head.Length + o.Count;
					}
				}
			}
			catch (Exception e)
			{
				bool closed;
				lock (conn)
				{
					closed = conn.Closed;
				}
				if (!closed)
				{
					// Closing makes the read thread report the disconnect.
					Plugin.Log.LogWarning("Dropping the connection: sending failed (" + e.Message + ")");
				}
				try
				{
					conn.Client.Close();
				}
				catch
				{
				}
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
			if (_conn != null)
			{
				lock (_conn)
				{
					_conn.Closed = true;
					_conn.Queue.Clear();
					_conn.Bytes = 0L;
					Monitor.PulseAll(_conn);
				}
				_conn = null;
			}
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

		private static IPAddress Address(TcpClient c)
		{
			try
			{
				return ((IPEndPoint)c.Client.RemoteEndPoint).Address;
			}
			catch
			{
				return null;
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
