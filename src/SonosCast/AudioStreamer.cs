using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SonosCast;

/// <summary>
/// Captures everything a Windows output device plays (WASAPI loopback), converts it to 16-bit stereo PCM
/// and feeds it to a <see cref="WavStreamServer"/>.
/// </summary>
public sealed class AudioStreamer : IDisposable
{
    public int Port { get; }
    public int SampleRate => _server?.SampleRate ?? 0;
    public int ClientCount => _server?.ClientCount ?? 0;
    public bool EverConnected => _server?.EverConnected ?? false;
    public long BytesSent => _server?.BytesSent ?? 0;
    public int TakePeakBacklogMs() => _server?.TakePeakBacklogMs() ?? 0;
    /// <summary>Most recent peak level of the captured audio, 0..1.</summary>
    public float Level { get; private set; }
    public event Action<string>? Log;

    WasapiLoopbackCapture? _capture;
    WavStreamServer? _server;

    public AudioStreamer(int port) => Port = port;

    public void Start(MMDevice? device = null)
    {
        device ??= new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        _capture = new WasapiLoopbackCapture(device);
        _server = new WavStreamServer(Port, _capture.WaveFormat.SampleRate);
        _server.Log += m => Log?.Invoke(m);
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, e) => { if (e.Exception != null) Log?.Invoke("Capture stopped: " + e.Exception.Message); };
        _capture.StartRecording();
        Log?.Invoke($"Capturing \"{device.FriendlyName}\" ({_capture.WaveFormat.SampleRate} Hz, {_capture.WaveFormat.Channels} ch)");
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;
        var pcm = ToPcm16Stereo(e.Buffer, e.BytesRecorded, _capture!.WaveFormat);
        Level = WavStreamServer.Peak(pcm);
        _server?.Push(pcm);
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

    public void Dispose()
    {
        try { _capture?.StopRecording(); } catch { }
        _capture?.Dispose();
        _server?.Dispose();
    }
}
