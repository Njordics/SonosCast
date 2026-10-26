using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SonosCast;

/// <summary>
/// Captures everything the PC plays (WASAPI loopback), converts it to 16-bit stereo PCM and serves it
/// as an endless WAV stream over HTTP at http://&lt;pc&gt;:&lt;port&gt;/pc-audio.wav.
/// </summary>
public sealed class AudioStreamer : IDisposable
{
    public int Port { get; }
    public int SampleRate { get; private set; }
    public int ClientCount => _clients.Count;
    public bool EverConnected { get; private set; }
    /// <summary>Most recent peak level of the captured audio, 0..1.</summary>
    public float Level { get; private set; }
    public long BytesSent => Interlocked.Read(ref _bytesSent);
    public event Action<string>? Log;

    WasapiLoopbackCapture? _capture;
    WasapiOut? _silence;
    TcpListener? _listener;
    readonly ConcurrentDictionary<int, BlockingCollection<byte[]>> _clients = new();
    int _nextClientId;
    long _bytesSent;
    volatile bool _running;

    public AudioStreamer(int port) => Port = port;

    public void Start(MMDevice? device = null)
    {
        device ??= new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        // Loopback capture delivers nothing while the PC is silent, which would starve the Sonos and make
        // it drop the stream. Playing inaudible silence on the same device keeps samples flowing.
        _silence = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
        _silence.Init(new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(device.AudioClient.MixFormat.SampleRate, 2)));
        _silence.Play();

        _capture = new WasapiLoopbackCapture(device);
        SampleRate = _capture.WaveFormat.SampleRate;
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, e) => { if (e.Exception != null) Log?.Invoke("Capture stopped: " + e.Exception.Message); };

        _listener = new TcpListener(IPAddress.Any, Port);
        _listener.Start();
        _running = true;
        _ = AcceptLoopAsync();

        _capture.StartRecording();
        Log?.Invoke($"Capturing \"{device.FriendlyName}\" ({_capture.WaveFormat.SampleRate} Hz, {_capture.WaveFormat.Channels} ch)");
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;
        var pcm = ToPcm16Stereo(e.Buffer, e.BytesRecorded, _capture!.WaveFormat);
        Level = Peak(pcm);
        if (_clients.IsEmpty) return;
        foreach (var (id, queue) in _clients)
        {
            try
            {
                // If a client falls ~1 s behind, drop audio rather than growing latency forever.
                if (!queue.TryAdd(pcm))
                {
                    while (queue.Count > 5 && queue.TryTake(out _)) { }
                    queue.TryAdd(pcm);
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { /* client just left */ }
        }
    }

    static float Peak(byte[] pcm)
    {
        int peak = 0;
        for (int i = 0; i + 1 < pcm.Length; i += 2)
            peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(pcm, i)));
        return peak / 32768f;
    }

    static byte[] ToPcm16Stereo(byte[] buf, int count, WaveFormat fmt)
    {
        int ch = fmt.Channels, bps = fmt.BitsPerSample / 8;
        int frames = count / (ch * bps);
        var outBuf = new byte[frames * 4];
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (fmt is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);

        for (int f = 0; f < frames; f++)
        {
            for (int c = 0; c < 2; c++)
            {
                int srcCh = Math.Min(c, ch - 1);
                int o = (f * ch + srcCh) * bps;
                float s = isFloat ? BitConverter.ToSingle(buf, o)
                        : bps == 2 ? BitConverter.ToInt16(buf, o) / 32768f
                        : bps == 3 ? ((buf[o] << 8 | buf[o + 1] << 16 | buf[o + 2] << 24) >> 8) / 8388608f
                        : BitConverter.ToInt32(buf, o) / 2147483648f;
                short v = (short)Math.Clamp((int)MathF.Round(s * 32767f), short.MinValue, short.MaxValue);
                outBuf[f * 4 + c * 2] = (byte)v;
                outBuf[f * 4 + c * 2 + 1] = (byte)(v >> 8);
            }
        }
        return outBuf;
    }

    async Task AcceptLoopAsync()
    {
        while (_running)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(); }
            catch { break; }
            _ = Task.Run(() => ServeClient(client));
        }
    }

    void ServeClient(TcpClient client)
    {
        using var owned = client;
        client.NoDelay = true;
        var remote = client.Client.RemoteEndPoint?.ToString();
        try
        {
            var stream = client.GetStream();
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
            using var queue = new BlockingCollection<byte[]>(100);
            _clients[id] = queue;
            EverConnected = true;
            Log?.Invoke($"Speaker connected ({remote})");
            try
            {
                stream.Write(WavHeader(SampleRate));
                while (_running)
                {
                    if (!queue.TryTake(out var chunk, 1000)) continue;
                    stream.Write(chunk);
                    Interlocked.Add(ref _bytesSent, chunk.Length);
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

    public void Dispose()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
        try { _capture?.StopRecording(); } catch { }
        _capture?.Dispose();
        _silence?.Stop();
        _silence?.Dispose();
        foreach (var q in _clients.Values) q.CompleteAdding();
        _clients.Clear();
    }
}
