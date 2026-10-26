using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SonosCast;

/// <summary>
/// Serves 16-bit stereo PCM, pushed in by any capture source, as an endless WAV stream over HTTP at
/// http://&lt;host&gt;:&lt;port&gt;/pc-audio.wav. Platform-neutral: the capture side lives in each front end.
/// </summary>
public sealed class WavStreamServer : IDisposable
{
    public int Port { get; }
    public int SampleRate { get; }
    public int ClientCount => _clients.Count;
    public bool EverConnected { get; private set; }
    public long BytesSent => Interlocked.Read(ref _bytesSent);

    /// <summary>
    /// Most audio a speaker may lag behind live before old audio is dropped. Sonos reads in bursts, so a little
    /// headroom avoids glitches; anything more is pure added delay.
    /// </summary>
    public int MaxBacklogMs { get; set; } = 250;

    /// <summary>Largest backlog seen across connected speakers since the last call, in milliseconds.</summary>
    public int TakePeakBacklogMs() => (int)(Interlocked.Exchange(ref _peakBacklogBytes, 0) * 1000L / BytesPerSecond);

    public event Action<string>? Log;

    int BytesPerSecond => SampleRate * 4;

    sealed class Client
    {
        public readonly ConcurrentQueue<byte[]> Queue = new();
        public readonly SemaphoreSlim Signal = new(0);
        public long QueuedBytes;
    }

    readonly ConcurrentDictionary<int, Client> _clients = new();
    readonly TcpListener _listener;
    readonly Timer _keepAlive;
    int _nextClientId;
    long _bytesSent, _peakBacklogBytes;
    readonly object _silenceLock = new();
    long _lastPushTicks, _silenceFilledBytes;
    volatile bool _running = true;

    public WavStreamServer(int port, int sampleRate)
    {
        Port = port;
        SampleRate = sampleRate;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _ = AcceptLoopAsync();
        // If the source goes quiet (nothing playing, a paused capture tool), feed silence so the speaker
        // never starves and drops the stream.
        _lastPushTicks = Environment.TickCount64;
        _keepAlive = new Timer(_ => FillSilence(), null, 20, 20);
    }

    /// <summary>Queue a block of interleaved 16-bit little-endian stereo PCM for every connected speaker.</summary>
    public void Push(byte[] pcm)
    {
        lock (_silenceLock)
        {
            _lastPushTicks = Environment.TickCount64;
            _silenceFilledBytes = 0;
        }
        Broadcast(pcm);
    }

    void FillSilence()
    {
        byte[] silence;
        lock (_silenceLock)
        {
            long idleMs = Environment.TickCount64 - _lastPushTicks;
            if (idleMs < 150) return;
            // Owe exactly as much silence as real time has passed. Timer ticks arrive late and unevenly on
            // Windows, so adding a fixed amount per tick would fall behind and starve the speaker.
            long owed = idleMs * BytesPerSecond / 1000 - _silenceFilledBytes;
            owed &= ~3L; // whole stereo frames
            if (owed <= 0) return;
            _silenceFilledBytes += owed;
            silence = new byte[owed];
        }
        if (!_clients.IsEmpty) Broadcast(silence);
    }

    void Broadcast(byte[] pcm)
    {
        long max = (long)BytesPerSecond * MaxBacklogMs / 1000;
        foreach (var c in _clients.Values)
        {
            c.Queue.Enqueue(pcm);
            var queued = Interlocked.Add(ref c.QueuedBytes, pcm.Length);
            // Drop the oldest audio when the speaker falls too far behind, so delay can't creep up over time.
            while (queued > max && c.Queue.TryDequeue(out var old))
                queued = Interlocked.Add(ref c.QueuedBytes, -old.Length);
            InterlockedMax(ref _peakBacklogBytes, queued);
            c.Signal.Release();
        }
    }

    static void InterlockedMax(ref long target, long value)
    {
        long cur;
        while (value > (cur = Interlocked.Read(ref target)) && Interlocked.CompareExchange(ref target, value, cur) != cur) { }
    }

    async Task AcceptLoopAsync()
    {
        while (_running)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(); }
            catch { break; }
            _ = Task.Run(() => ServeClientAsync(client));
        }
    }

    async Task ServeClientAsync(TcpClient tcp)
    {
        using var owned = tcp;
        tcp.NoDelay = true;
        tcp.SendBufferSize = 16 * 1024; // keep the OS from hiding extra audio in a big socket buffer
        var remote = tcp.Client.RemoteEndPoint?.ToString();
        try
        {
            var stream = tcp.GetStream();
            var request = ReadRequestHead(stream);
            if (request == null) return;
            var parts = request.Split(' ');
            string method = parts[0], path = parts.Length > 1 ? parts[1] : "/";

            if (!path.StartsWith("/pc-audio"))
            {
                Write(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                return;
            }

            Write(stream, "HTTP/1.1 200 OK\r\nContent-Type: audio/wav\r\nCache-Control: no-cache\r\n" +
                          "Connection: close\r\nServer: SonosCast\r\n\r\n");
            if (method == "HEAD") return;

            int id = Interlocked.Increment(ref _nextClientId);
            var client = new Client();
            _clients[id] = client;
            EverConnected = true;
            Log?.Invoke($"Speaker connected ({remote})");
            try
            {
                stream.Write(WavHeader(SampleRate));
                while (_running)
                {
                    if (!await client.Signal.WaitAsync(1000)) continue;
                    while (client.Queue.TryDequeue(out var chunk))
                    {
                        Interlocked.Add(ref client.QueuedBytes, -chunk.Length);
                        await stream.WriteAsync(chunk);
                        Interlocked.Add(ref _bytesSent, chunk.Length);
                    }
                }
            }
            finally
            {
                _clients.TryRemove(id, out _);
                Log?.Invoke($"Speaker disconnected ({remote})");
            }
        }
        catch (IOException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    static string? ReadRequestHead(NetworkStream s)
    {
        var sb = new StringBuilder();
        var b = new byte[1];
        while (sb.Length < 8192)
        {
            if (s.Read(b, 0, 1) <= 0) return null;
            sb.Append((char)b[0]);
            if (sb.Length >= 4 && sb.ToString(sb.Length - 4, 4) == "\r\n\r\n") break;
        }
        return sb.ToString();
    }

    static void Write(Stream s, string text) => s.Write(Encoding.ASCII.GetBytes(text));

    static byte[] WavHeader(int sampleRate)
    {
        // Sizes set to the maximum: the stream is endless.
        const uint huge = 0xFFFFFFFF - 36 - 8;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8.ToArray()); w.Write(huge + 36);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)2);
        w.Write(sampleRate); w.Write(sampleRate * 4); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(huge);
        return ms.ToArray();
    }

    /// <summary>Peak level (0..1) of a block of 16-bit PCM.</summary>
    public static float Peak(byte[] pcm)
    {
        int peak = 0;
        for (int i = 0; i + 1 < pcm.Length; i += 2)
            peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(pcm, i)));
        return peak / 32768f;
    }

    public void Dispose()
    {
        _running = false;
        _keepAlive.Dispose();
        try { _listener.Stop(); } catch { }
        foreach (var c in _clients.Values) c.Signal.Release();
        _clients.Clear();
    }
}
