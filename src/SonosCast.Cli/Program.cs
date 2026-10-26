using System.Diagnostics;
using System.Runtime.InteropServices;
using SonosCast;

const string Usage = """
    sonoscast — send your computer's sound to a Sonos speaker

    Usage:
      sonoscast list                         List Sonos speakers on the network
      sonoscast play [options]               Stream this computer's audio (Ctrl+C to stop)
      sonoscast stop   [--speaker NAME]      Stop the speaker
      sonoscast volume N [--speaker NAME]    Set the speaker volume (0-100)

    Options for play:
      --speaker NAME   Room name (or part of it) or IP address. Default: the only/first speaker found.
      --source SRC     Where the audio comes from:
                         auto      (default) Linux: PulseAudio/PipeWire monitor of the default output
                                             macOS: the BlackHole loopback device via ffmpeg
                         stdin     raw 16-bit little-endian stereo PCM on standard input
                         "<cmd>"   any shell command that writes raw 16-bit stereo PCM to stdout
      --device NAME    macOS: the loopback input device for ffmpeg (default "BlackHole 2ch")
      --rate HZ        Sample rate (default 48000)
      --port N         Port to serve the stream on (default 8930)
      --volume N       Set the speaker volume before playing
    """;

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(Usage);
    return 0;
}

string? Opt(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

try
{
    switch (args[0])
    {
        case "list":
            var all = await Sonos.DiscoverAsync();
            if (all.Count == 0) { Console.WriteLine("No Sonos speakers found. Is this computer on the same network?"); return 1; }
            foreach (var d in all) Console.WriteLine($"  {d.RoomName,-24} {d.ModelName,-22} {d.Ip}");
            return 0;

        case "stop":
            await Sonos.StopAsync(await Sonos.GetCoordinatorIpAsync(await PickSpeakerAsync(Opt("--speaker"))));
            Console.WriteLine("Stopped.");
            return 0;

        case "volume":
            if (args.Length < 2 || !int.TryParse(args[1], out var vol)) { Console.Error.WriteLine("Usage: sonoscast volume N"); return 2; }
            var vdev = await PickSpeakerAsync(Opt("--speaker"));
            await Sonos.SetVolumeAsync(vdev.Ip, vol);
            Console.WriteLine($"{vdev.RoomName} volume set to {Math.Clamp(vol, 0, 100)}.");
            return 0;

        case "play":
            return await PlayAsync();

        default:
            Console.Error.WriteLine($"Unknown command '{args[0]}'.\n");
            Console.WriteLine(Usage);
            return 2;
    }
}
catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException or TimeoutException)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 1;
}

async Task<SonosDevice> PickSpeakerAsync(string? wanted)
{
    if (wanted != null && System.Net.IPAddress.TryParse(wanted, out _))
        return await Sonos.DescribeAsync(wanted) ?? throw new InvalidOperationException($"No Sonos speaker answered at {wanted}.");

    Console.WriteLine("Looking for Sonos speakers…");
    var found = await Sonos.DiscoverAsync();
    if (found.Count == 0) throw new InvalidOperationException("No Sonos speakers found. Is this computer on the same network?");
    if (wanted == null) return found[0];
    return found.FirstOrDefault(d => d.RoomName.Equals(wanted, StringComparison.OrdinalIgnoreCase))
        ?? found.FirstOrDefault(d => d.RoomName.Contains(wanted, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"No speaker called '{wanted}'. Found: {string.Join(", ", found.Select(d => d.RoomName))}");
}

async Task<int> PlayAsync()
{
    int rate = int.TryParse(Opt("--rate"), out var r) ? r : 48000;
    int port = int.TryParse(Opt("--port"), out var p) ? p : 8930;
    var dev = await PickSpeakerAsync(Opt("--speaker"));

    var (input, proc, description) = OpenSource(Opt("--source") ?? "auto", rate, Opt("--device") ?? "BlackHole 2ch");
    Console.WriteLine($"Capturing from {description}");

    using var server = new WavStreamServer(port, rate);
    server.Log += m => Console.WriteLine("  " + m);

    var coordinator = await Sonos.GetCoordinatorIpAsync(dev);
    if (int.TryParse(Opt("--volume"), out var v)) await Sonos.SetVolumeAsync(dev.Ip, v);
    var url = $"http://{Sonos.LocalIpFor(dev.Ip)}:{port}/pc-audio.wav";
    Console.WriteLine($"Asking {dev.RoomName} ({dev.ModelName}) to play {url}");
    try { await Sonos.PlayStreamAsync(coordinator, url, $"{Environment.MachineName} audio"); }
    catch (TimeoutException) when (!server.EverConnected)
    {
        try { proc?.Kill(entireProcessTree: true); } catch { }
        throw new InvalidOperationException(
            $"{dev.RoomName} couldn't fetch the stream from this computer. Allow incoming connections on TCP port {port} in your firewall " +
            "(Linux: e.g. sudo ufw allow " + port + "/tcp; macOS: allow 'sonoscast' when asked) and try again.");
    }

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    float level = 0;
    var pump = Task.Run(async () =>
    {
        var buf = new byte[rate * 4 / 50]; // 20 ms
        while (!cts.IsCancellationRequested)
        {
            int n = await ReadFullAsync(input, buf, cts.Token);
            if (n <= 0) break;
            var chunk = buf[..(n & ~3)];
            level = Math.Max(WavStreamServer.Peak(chunk), level * 0.9f);
            server.Push(chunk);
        }
    });

    Console.WriteLine("Streaming. Press Ctrl+C to stop.\n");
    long lastBytes = 0;
    int waited = 0;
    while (!cts.IsCancellationRequested && !pump.IsCompleted)
    {
        try { await Task.Delay(1000, cts.Token); } catch (OperationCanceledException) { break; }
        var bytes = server.BytesSent;
        var kbps = (bytes - lastBytes) * 8 / 1000;
        lastBytes = bytes;
        var bar = new string('█', (int)(Math.Sqrt(level) * 20)).PadRight(20, '·');
        Console.Write($"\r  {bar}  {kbps,5} kbps  buffer {server.TakePeakBacklogMs(),3} ms  speakers {server.ClientCount}   ");
        if (!server.EverConnected && ++waited == 10)
            Console.WriteLine("\n  The speaker hasn't connected yet. Check that your firewall allows incoming connections on port " + port + ".");
    }

    Console.WriteLine("\nStopping…");
    await Sonos.StopAsync(coordinator);
    try { proc?.Kill(entireProcessTree: true); } catch { }
    if (pump.IsCompleted && !cts.IsCancellationRequested)
    {
        Console.Error.WriteLine("The audio source ended." + (proc != null ? $" ({proc.StartInfo.FileName} exited with code {(proc.HasExited ? proc.ExitCode : -1)})" : ""));
        return 1;
    }
    return 0;
}

static async Task<int> ReadFullAsync(Stream s, byte[] buf, CancellationToken ct)
{
    int total = 0;
    while (total < buf.Length)
    {
        int n;
        try { n = await s.ReadAsync(buf.AsMemory(total), ct); }
        catch (OperationCanceledException) { return total; }
        if (n == 0) return total;
        total += n;
    }
    return total;
}

static (Stream input, Process? proc, string description) OpenSource(string source, int rate, string macDevice)
{
    if (source == "stdin")
        return (Console.OpenStandardInput(), null, "standard input (16-bit stereo PCM)");

    if (source != "auto")
        return Launch(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh", OperatingSystem.IsWindows() ? ["/c", source] : ["-c", source], $"command: {source}");

    if (OperatingSystem.IsLinux())
    {
        // parec works with both PulseAudio and PipeWire (via pipewire-pulse); @DEFAULT_MONITOR@ is "whatever is playing".
        if (OnPath("parec"))
            return Launch("parec", ["--device=@DEFAULT_MONITOR@", "--format=s16le", $"--rate={rate}", "--channels=2", "--raw", "--latency-msec=20"],
                "the default output's monitor (PulseAudio/PipeWire, via parec)");
        if (OnPath("pw-record"))
            return Launch("pw-record", ["-P", "{ stream.capture.sink=true }", "--format=s16", $"--rate={rate}", "--channels=2", "-"],
                "the default output's monitor (PipeWire, via pw-record)");
        throw new InvalidOperationException("Couldn't find parec or pw-record. Install pulseaudio-utils (Debian/Ubuntu: sudo apt install pulseaudio-utils) or pipewire.");
    }

    if (OperatingSystem.IsMacOS())
    {
        if (!OnPath("ffmpeg"))
            throw new InvalidOperationException("macOS capture needs ffmpeg and the BlackHole loopback driver: brew install ffmpeg blackhole-2ch  (see the README for setup).");
        return Launch("ffmpeg", ["-hide_banner", "-loglevel", "error", "-f", "avfoundation", "-i", $":{macDevice}",
                "-f", "s16le", "-ac", "2", "-ar", rate.ToString(), "-"],
            $"\"{macDevice}\" via ffmpeg");
    }

    throw new InvalidOperationException("On Windows, use the SonosCast app — or pass --source stdin / --source \"<command>\".");
}

static (Stream, Process, string) Launch(string file, string[] argv, string description)
{
    var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, UseShellExecute = false };
    foreach (var a in argv) psi.ArgumentList.Add(a);
    var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Couldn't start {file}.");
    return (proc.StandardOutput.BaseStream, proc, description);
}

static bool OnPath(string exe) =>
    (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Any(dir => File.Exists(Path.Combine(dir, exe)) || (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && File.Exists(Path.Combine(dir, exe + ".exe"))));
