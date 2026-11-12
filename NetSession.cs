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
	//
	// For the log (finding out why a connection went):
	// - [Net]: ping, the send queue, traffic each way and its heaviest kinds; every 30 s, every 5 s while the ping is
	//   over SlowPingMs or the queue holds more than BusyQueueBytes.
	// - [Disconnect]: when a connection ends, how (timed out here, closed by the other side, a read or write failing and
	//   with what network error), how long since anything came in and since our last write went out, the pings of the
	//   last 30 s and the queue at that moment.
	// - CLOCK on connecting: each side's own clock, so the two logs can be lined up.
	// - A background thread failing is logged instead of passing silently.
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

			// Why reading or writing stopped (null: not yet; "" for reading: the other side closed it the usual way).
			public string ReadError;

			public string WriteError;

			// Stopwatch ticks of the last write that went out.
			public long LastWriteTicks;
		}

		// ---- Stats for the log (counters under _statLock) ----

		private const int SlowPingMs = 300;

		private const long BusyQueueBytes = 64 * 1024;

		private readonly object _statLock = new object();

		private readonly Dictionary<string, long> _outByKind = new Dictionary<string, long>();

		private readonly Dictionary<string, long> _inByKind = new Dictionary<string, long>();

		private long _outBytes;

		private long _inBytes;

		private float _statsSince;

		private float _nextStats;

		private float _lastStatsOutRate;

		private float _lastStatsInRate;

		// Pings of the last 30 s: (time, ms). Main thread.
		private readonly List<KeyValuePair<float, int>> _pings = new List<KeyValuePair<float, int>>();

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
			StartThread("network listener", () => AcceptLoop(listener, gen));
		}

		public void Join(string address, int port)
		{
			Stop();
			int gen = _generation;
			CurrentMode = Mode.Client;
			Status = "Connecting to " + address + ":" + port + "...";
			Plugin.Log.LogInfo(Status);
			StartThread("network connect", () =>
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
			Count(_outByKind, Kind(o.Head), o.Head.Length + o.Count, isOut: true);
		}

		// The command word of a header line ("BIN <cmd>" for binary).
		private static string Kind(byte[] head)
		{
			int n = Math.Min(head.Length, 40);
			int end = 0;
			while (end < n && head[end] != (byte)' ' && head[end] != (byte)'\n')
			{
				end++;
			}
			string kind = Encoding.ASCII.GetString(head, 0, end);
			if (kind == "BIN" && end < n)
			{
				int start = end + 1;
				int e2 = start;
				while (e2 < n && head[e2] != (byte)' ' && head[e2] != (byte)'\n')
				{
					e2++;
				}
				kind = "BIN " + Encoding.ASCII.GetString(head, start, e2 - start);
			}
			return kind;
		}

		private void Count(Dictionary<string, long> byKind, string kind, long bytes, bool isOut)
		{
			lock (_statLock)
			{
				long had;
				byKind.TryGetValue(kind, out had);
				byKind[kind] = had + bytes;
				if (isOut)
				{
					_outBytes += bytes;
				}
				else
				{
					_inBytes += bytes;
				}
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
						Report(now, null);
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
				Report(now, "timed out here: nothing came in for " + TimeoutSeconds.ToString("0") + " s");
				PeerGone("timed out");
			}
			if (Connected)
			{
				LogStats(now);
			}
		}

		private long QueuedBytes(out int messages)
		{
			messages = 0;
			Conn conn;
			lock (_lock)
			{
				conn = _conn;
			}
			if (conn == null)
			{
				return 0L;
			}
			lock (conn)
			{
				messages = conn.Queue.Count;
				return conn.Bytes;
			}
		}

		// [Net] line, see the top.
		private void LogStats(float now)
		{
			if (now < _nextStats)
			{
				return;
			}
			int messages;
			long queued = QueuedBytes(out messages);
			bool busy = LastRttMs > SlowPingMs || queued > BusyQueueBytes;
			float since = now - _statsSince;
			// Every 5 s while busy, else every 30 s.
			if (since < (busy ? 5f : 30f))
			{
				_nextStats = now + 1f;
				return;
			}
			string outTop;
			string inTop;
			long outBytes;
			long inBytes;
			lock (_statLock)
			{
				outBytes = _outBytes;
				inBytes = _inBytes;
				outTop = Top(_outByKind, since);
				inTop = Top(_inByKind, since);
				_outBytes = 0L;
				_inBytes = 0L;
				_outByKind.Clear();
				_inByKind.Clear();
			}
			_lastStatsOutRate = outBytes / 1024f / Math.Max(since, 0.001f);
			_lastStatsInRate = inBytes / 1024f / Math.Max(since, 0.001f);
			Plugin.Log.LogInfo("[Net] ping " + ((LastRttMs < 0) ? "-" : (LastRttMs + " ms")) + " | queue " + (queued / 1024L) + " KB (" + messages + ") | out " + _lastStatsOutRate.ToString("0.0") + " KB/s: " + outTop + " | in " + _lastStatsInRate.ToString("0.0") + " KB/s: " + inTop + (busy ? " | SLOW" : ""));
			_statsSince = now;
			_nextStats = now + 1f;
		}

		// The three heaviest kinds, in KB/s. Caller holds _statLock.
		private static string Top(Dictionary<string, long> byKind, float seconds)
		{
			List<KeyValuePair<string, long>> list = new List<KeyValuePair<string, long>>(byKind);
			list.Sort((KeyValuePair<string, long> a, KeyValuePair<string, long> b) => b.Value.CompareTo(a.Value));
			StringBuilder sb = new StringBuilder();
			for (int i = 0; i < list.Count && i < 3; i++)
			{
				if (i > 0)
				{
					sb.Append(", ");
				}
				sb.Append(list[i].Key).Append(' ').Append((list[i].Value / 1024f / Math.Max(seconds, 0.001f)).ToString("0.0"));
			}
			return (sb.Length > 0) ? sb.ToString() : "-";
		}

		// [Disconnect] block, see the top. <why>: our own reason, or null to take it from the connection's errors.
		private void Report(float now, string why)
		{
			Conn conn;
			lock (_lock)
			{
				conn = _conn;
			}
			string read = null;
			string write = null;
			long lastWrite = 0L;
			if (conn != null)
			{
				lock (conn)
				{
					read = conn.ReadError;
					write = conn.WriteError;
					lastWrite = conn.LastWriteTicks;
				}
			}
			if (why == null)
			{
				if (write != null)
				{
					why = "sending failed: " + write;
				}
				else if (read == "")
				{
					why = "the other side closed the connection (left, quit the game, or its mod dropped it)";
				}
				else if (read != null)
				{
					why = "receiving failed: " + read;
				}
				else
				{
					why = "the connection closed";
				}
			}
			int messages;
			long queued = QueuedBytes(out messages);
			StringBuilder pings = new StringBuilder();
			foreach (KeyValuePair<float, int> p in _pings)
			{
				if (now - p.Key <= 30f)
				{
					if (pings.Length > 0)
					{
						pings.Append(", ");
					}
					pings.Append(p.Value);
				}
			}
			float sinceWrite = (lastWrite == 0L) ? -1f : (float)(Stopwatch.GetTimestamp() - lastWrite) / Stopwatch.Frequency;
			Plugin.Log.LogWarning("[Disconnect] " + why
				+ "\n  last message in: " + (now - LastReceiveTime).ToString("0.0") + " s ago; last write out: " + ((sinceWrite < 0f) ? "none" : (sinceWrite.ToString("0.0") + " s ago"))
				+ "\n  pings, last 30 s (ms, about one a second): " + ((pings.Length > 0) ? pings.ToString() : "none")
				+ "\n  send queue: " + (queued / 1024L) + " KB in " + messages + " messages"
				+ "\n  traffic at the last [Net] line: out " + _lastStatsOutRate.ToString("0.0") + " KB/s, in " + _lastStatsInRate.ToString("0.0") + " KB/s"
				+ ((write != null && why.IndexOf(write, StringComparison.Ordinal) < 0) ? ("\n  sending: " + write) : "")
				+ ((read != null && read != "" && why.IndexOf(read, StringComparison.Ordinal) < 0) ? ("\n  receiving: " + read) : ""));
			if (InstanceLog.Instance != null)
			{
				InstanceLog.Instance.Flush();
			}
		}

		// A network error in plain words, with Windows' code.
		internal static string Explain(Exception e)
		{
			SocketException se = (e as SocketException) ?? (e.InnerException as SocketException);
			if (se == null)
			{
				return e.GetType().Name + ": " + e.Message;
			}
			string what;
			switch (se.SocketErrorCode)
			{
				case SocketError.ConnectionReset:
					what = "the other side reset the connection (its game closed abruptly, or the network between dropped it)";
					break;
				case SocketError.ConnectionAborted:
					what = "this computer's network gave up on the connection (no answer from the other side for too long)";
					break;
				case SocketError.TimedOut:
					what = "the other side stopped answering (the network between went quiet)";
					break;
				case SocketError.NetworkUnreachable:
				case SocketError.HostUnreachable:
				case SocketError.NetworkDown:
					what = "the network is down or the other side can't be reached";
					break;
				default:
					what = se.SocketErrorCode.ToString();
					break;
			}
			return what + " [" + se.ErrorCode + "]";
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
				Send("CLOCK " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
				_pings.Clear();
				_statsSince = now;
				_nextStats = now + 1f;
				lock (_statLock)
				{
					_outBytes = 0L;
					_inBytes = 0L;
					_outByKind.Clear();
					_inByKind.Clear();
				}
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
				case "CLOCK":
				{
					DateTime theirs;
					if (DateTime.TryParseExact(arg, "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out theirs))
					{
						TimeSpan d = theirs - DateTime.Now;
						string diff = (Math.Abs(d.TotalSeconds) < 2.0) ? "the same as ours" : ((d.TotalSeconds > 0.0 ? "ahead of ours by " : "behind ours by ") + Span(d.Duration()));
						Plugin.Log.LogInfo("The other side's clock: " + theirs.ToString("HH:mm:ss") + " when ours is " + DateTime.Now.ToString("HH:mm:ss") + ", " + diff + " (to line up the two logs)");
					}
					break;
				}
				case "PONG":
					long sent;
					if (long.TryParse(arg, out sent))
					{
						LastRttMs = (int)((Stopwatch.GetTimestamp() - sent) * 1000L / Stopwatch.Frequency);
						_pings.Add(new KeyValuePair<float, int>(now, LastRttMs));
						while (_pings.Count > 0 && now - _pings[0].Key > 30f)
						{
							_pings.RemoveAt(0);
						}
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
			StartThread("network writer", () => WriteLoop(conn));
			StartThread("network reader", () => ReadLoop(c, conn, gen));
			StartThread("network keepalive", () => KeepAlive(c, gen));
			Enqueue(gen, "__connected", null);
		}

		private void ReadLoop(TcpClient c, Conn conn, int gen)
		{
			string error = null;
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
						error = "";
						break;
					}
					if (b != '\n')
					{
						if (lineBytes.Length >= MaxLineBytes)
						{
							Plugin.Log.LogWarning("Dropping the connection: a line over " + MaxLineBytes + " bytes");
							error = "a line over " + MaxLineBytes + " bytes";
							break;
						}
						lineBytes.WriteByte((byte)b);
						continue;
					}
					string line = utf8.GetString(lineBytes.GetBuffer(), 0, (int)lineBytes.Length);
					long lineLength = lineBytes.Length + 1;
					lineBytes.SetLength(0L);
					if (!line.StartsWith("BIN "))
					{
						int sp = line.IndexOf(' ');
						Count(_inByKind, (sp < 0) ? line : line.Substring(0, sp), lineLength, isOut: false);
						Enqueue(gen, line, null);
						continue;
					}
					string[] parts = line.Split(' ');
					int length;
					if (parts.Length != 3 || !int.TryParse(parts[2], out length) || length < 0)
					{
						Plugin.Log.LogWarning("Dropping the connection: a bad binary header");
						error = "a bad binary header";
						break;
					}
					if (length > MaxBinaryBytes)
					{
						Plugin.Log.LogWarning("Dropping the connection: binary " + parts[1] + " of " + length + " bytes, over " + MaxBinaryBytes);
						error = "a binary message over " + MaxBinaryBytes + " bytes";
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
					Count(_inByKind, "BIN " + parts[1], lineLength + length, isOut: false);
					Enqueue(gen, parts[1], data);
				}
			}
			catch (Exception e)
			{
				error = Explain(e);
			}
			lock (conn)
			{
				if (conn.ReadError == null)
				{
					conn.ReadError = error ?? "";
				}
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
						conn.LastWriteTicks = Stopwatch.GetTimestamp();
					}
				}
			}
			catch (Exception e)
			{
				bool closed;
				lock (conn)
				{
					closed = conn.Closed;
					if (!closed && conn.WriteError == null)
					{
						conn.WriteError = Explain(e);
					}
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

		// A background thread whose failure is logged (and flushed) instead of passing silently.
		internal static void StartThread(string name, ThreadStart fn)
		{
			Thread t = new Thread(() =>
			{
				try
				{
					fn();
				}
				catch (ThreadAbortException)
				{
				}
				catch (Exception e)
				{
					Plugin.Log.LogError("[Thread] " + name + " failed: " + e);
					if (InstanceLog.Instance != null)
					{
						InstanceLog.Instance.Flush();
					}
				}
			});
			t.IsBackground = true;
			t.Name = "OstranautsMP " + name;
			t.Start();
		}

		private static string Span(TimeSpan t)
		{
			if (t.TotalHours >= 1.0)
			{
				return (int)t.TotalHours + " h " + t.Minutes + " min" + ((t.Seconds > 0) ? (" " + t.Seconds + " s") : "");
			}
			if (t.TotalMinutes >= 1.0)
			{
				return t.Minutes + " min " + t.Seconds + " s";
			}
			return t.Seconds + " s";
		}
	}
}
