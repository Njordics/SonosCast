using NAudio.CoreAudioApi;

namespace SonosCast;

public sealed class MainForm : Form
{
    const int StreamPort = 8930;
    public const string ShowEventName = "SonosCast.ShowWindow";

    readonly Settings _settings = Settings.Load();
    readonly Header _header = new();
    readonly GlyphButton _gear = new() { Glyph = "", Translucent = true };
    readonly Card _deviceCard = new() { Caption = "Speaker" };
    readonly Picker _speakers = new() { Glyph = "", Placeholder = "Searching for Sonos speakers…" };
    readonly GlyphButton _refresh = new() { Glyph = "" };
    readonly Label _outputCaption = new() { Text = "CAPTURE FROM", Font = Theme.Caption, ForeColor = Theme.Muted, BackColor = Theme.Card, AutoSize = true };
    readonly Picker _outputs = new() { Glyph = "", Placeholder = "No audio outputs" };
    readonly PlayButton _toggle = new() { Enabled = false };
    readonly StatusLine _status = new() { Text = "Idle" };
    readonly LevelMeter _meter = new();
    readonly Card _volumeCard = new();
    readonly Label _volGlyph = new() { Text = "", Font = Theme.Glyph(14f), ForeColor = Theme.Violet, BackColor = Theme.Card, TextAlign = ContentAlignment.MiddleCenter };
    readonly Slider _volume = new() { Enabled = false };
    readonly Label _volValue = new() { Text = "–", Font = Theme.BodyBold, ForeColor = Theme.Text, BackColor = Theme.Card, TextAlign = ContentAlignment.MiddleRight };
    readonly Card _logCard = new() { Caption = "Activity" };
    readonly TextBox _log = new() { Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Theme.Card, ForeColor = Theme.Muted, Font = Theme.Mono, ScrollBars = ScrollBars.None };
    readonly NotifyIcon _tray = new() { Text = "SonosCast", Visible = true };
    readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 1000 };
    readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 40 };
    readonly System.Windows.Forms.Timer _volumeDebounce = new() { Interval = 150 };

    AudioStreamer? _streamer;
    string? _playingOn;
    int _stalledTicks;
    long _lastBytes;
    bool _searching, _startHidden, _startedUp;
    readonly EventWaitHandle _showSignal = new(false, EventResetMode.AutoReset, ShowEventName);

    public MainForm(bool startInTray = false)
    {
        _startHidden = startInTray;
        Text = "SonosCast";
        Icon = Theme.LoadIcon();
        _tray.Icon = Icon;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;

        _deviceCard.Controls.AddRange([_speakers, _refresh, _outputCaption, _outputs]);
        _volumeCard.Controls.AddRange([_volGlyph, _volume, _volValue]);
        _logCard.Controls.Add(_log);
        _header.Controls.Add(_gear);
        Controls.AddRange([_logCard, _volumeCard, _meter, _status, _toggle, _deviceCard, _header]);

        float s = DeviceDpi / 96f;
        var work = Screen.FromPoint(Cursor.Position).WorkingArea;
        ClientSize = new Size((int)(460 * s), Math.Min((int)(760 * s), work.Height - (int)(60 * s)));
        MinimumSize = SizeFromClientSize(new Size((int)(400 * s), (int)(600 * s)));

        _refresh.Click += async (_, _) => await FindSpeakersAsync();
        _toggle.Click += async (_, _) => { if (_toggle.Enabled && !_toggle.Busy) await ToggleAsync(); };
        _speakers.SelectedIndexChanged += async (_, _) => await OnSpeakerChangedAsync();
        _volume.ValueChanged += (_, _) => { _volValue.Text = _volume.Value.ToString(); _volumeDebounce.Stop(); _volumeDebounce.Start(); };
        _volumeDebounce.Tick += async (_, _) => { _volumeDebounce.Stop(); await SetVolumeAsync(); };
        _statusTimer.Tick += async (_, _) => await UpdateStatusAsync();
        _uiTimer.Tick += (_, _) =>
        {
            _meter.Live = _streamer != null;
            _meter.Push(_streamer?.Level ?? 0f);
            if (_searching) { _refresh.Spin = (_refresh.Spin + 12) % 360; _refresh.Invalidate(); }
        };
        _uiTimer.Start();

        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false };
        menu.Items.Add("Show SonosCast", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Start / Stop", null, async (_, _) => await ToggleAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Close());
        foreach (ToolStripItem i in menu.Items) i.ForeColor = Theme.Text;
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };

        LoadOutputs();
        _log.TabStop = false;
        Shown += async (_, _) => { ActiveControl = _toggle; await StartupAsync(); };
        _gear.Click += (_, _) => ShowSettingsMenu();

        // Another launch of SonosCast (e.g. from the Start menu while we sit in the tray) asks us to show up.
        new Thread(() =>
        {
            while (_showSignal.WaitOne())
                if (IsHandleCreated) BeginInvoke(RestoreFromTray);
        }) { IsBackground = true }.Start();
        SetStatus("Idle", Theme.Muted, "Pick a speaker and press play");
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(this);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        float s = DeviceDpi / 96f;
        int W = ClientSize.Width, pad = (int)(18 * s), inner = (int)(16 * s);
        int cardW = W - pad * 2;

        _header.SetBounds(0, 0, W, (int)(150 * s));
        _gear.SetBounds(W - pad - (int)(36 * s), (int)(14 * s), (int)(36 * s), (int)(36 * s));

        // Speaker + capture pickers
        int y = (int)(140 * s);
        int field = (int)(40 * s);
        _deviceCard.SetBounds(pad, y, cardW, (int)(146 * s));
        _refresh.SetBounds(cardW - inner - field, (int)(32 * s), field, field);
        _speakers.SetBounds(inner, (int)(32 * s), cardW - inner * 2 - field - (int)(8 * s), field);
        _outputCaption.Location = new Point(inner, (int)(82 * s));
        _outputs.SetBounds(inner, (int)(98 * s), cardW - inner * 2, field);
        y = _deviceCard.Bottom + (int)(4 * s);

        // Play button, status and meter
        int play = (int)(150 * s);
        _toggle.SetBounds((W - play) / 2, y, play, play);
        y = _toggle.Bottom - (int)(10 * s);
        _status.SetBounds(pad, y, cardW, (int)(46 * s));
        y = _status.Bottom + (int)(4 * s);
        _meter.SetBounds(pad + (int)(50 * s), y, cardW - (int)(100 * s), (int)(30 * s));
        y = _meter.Bottom + (int)(14 * s);

        // Volume
        _volumeCard.SetBounds(pad, y, cardW, (int)(58 * s));
        int vInset = (int)(10 * s);
        _volGlyph.SetBounds((int)(12 * s), vInset, (int)(34 * s), _volumeCard.Height - vInset * 2);
        _volValue.SetBounds(cardW - (int)(56 * s), vInset, (int)(40 * s), _volumeCard.Height - vInset * 2);
        _volume.SetBounds((int)(46 * s), (int)(12 * s), cardW - (int)(108 * s), (int)(34 * s));
        y = _volumeCard.Bottom + (int)(12 * s);

        // Activity log fills the rest
        _logCard.SetBounds(pad, y, cardW, Math.Max((int)(60 * s), ClientSize.Height - y - pad));
        _log.SetBounds(inner, (int)(32 * s), cardW - inner * 2, _logCard.Height - (int)(42 * s));
    }

    void RestoreFromTray() { Show(); WindowState = FormWindowState.Normal; Activate(); }

    protected override void SetVisibleCore(bool value)
    {
        // Launched with --tray (start with Windows): stay in the tray, but still find speakers and auto-play.
        if (_startHidden)
        {
            _startHidden = false;
            CreateHandle();
            BeginInvoke(async () => await StartupAsync());
            value = false;
        }
        base.SetVisibleCore(value);
    }

    async Task StartupAsync()
    {
        if (_startedUp) return;
        _startedUp = true;
        await FindSpeakersAsync();

        if (!_settings.AutoPlay) return;
        // Right after boot the network may not be up yet, so keep looking for a couple of minutes.
        for (int attempt = 0; Selected == null && attempt < 12; attempt++)
        {
            await Task.Delay(10_000);
            await FindSpeakersAsync();
        }
        if (Selected != null && _streamer == null) await StartAsync();
    }

    void ShowSettingsMenu()
    {
        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, Font = Theme.Body };
        var startup = new ToolStripMenuItem("Start with Windows") { Checked = Settings.StartWithWindows };
        startup.Click += (_, _) =>
        {
            try
            {
                Settings.StartWithWindows = !startup.Checked;
                Log(Settings.StartWithWindows ? "SonosCast will start with Windows (in the tray)." : "SonosCast won't start with Windows.");
            }
            catch (Exception ex) { Log("Couldn't change startup setting: " + ex.Message); }
        };
        var autoplay = new ToolStripMenuItem("Start playing when SonosCast opens") { Checked = _settings.AutoPlay };
        autoplay.Click += (_, _) =>
        {
            _settings.AutoPlay = !_settings.AutoPlay;
            _settings.Save();
            Log(_settings.AutoPlay ? "Will start playing automatically on the last speaker." : "Auto-play turned off.");
        };
        var github = new ToolStripMenuItem("SonosCast on GitHub");
        github.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/Njordics/SonosCast") { UseShellExecute = true });
        menu.Items.AddRange([startup, autoplay, new ToolStripSeparator(), github]);
        foreach (ToolStripItem i in menu.Items) { i.ForeColor = Theme.Text; i.Padding = new Padding(4, 6, 4, 6); }
        menu.Show(_gear, new Point(_gear.Width - menu.PreferredSize.Width, _gear.Height + 4));
    }

    void SetStatus(string text, Color dot, string detail)
    {
        _status.Text = text;
        _status.Dot = dot;
        _status.Detail = detail;
    }

    void LoadOutputs()
    {
        var en = new MMDeviceEnumerator();
        var def = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
        var items = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).Select(d => new OutputItem(d)).ToList();
        _outputs.SetItems(items);
        _outputs.SelectedIndex = items.FindIndex(i => i.Device.ID == def);
    }

    sealed record OutputItem(MMDevice Device)
    {
        public override string ToString() => Device.FriendlyName;
    }

    void Log(string msg)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(msg)); return; }
        _log.AppendText($"{DateTime.Now:HH:mm:ss}  {msg}{Environment.NewLine}");
    }

    SonosDevice? Selected => _speakers.SelectedItem as SonosDevice;

    async Task FindSpeakersAsync()
    {
        if (_searching) return;
        _searching = true;
        _refresh.Enabled = false;
        SetStatus("Searching…", Theme.Amber, "Looking for Sonos speakers on your network");
        try
        {
            var found = await Sonos.DiscoverAsync();
            _speakers.SetItems(found);
            _speakers.Placeholder = "No speakers found — press refresh";
            Log(found.Count == 0 ? "No Sonos speakers found. Is this PC on the same network?" : $"Found {found.Count} speaker(s).");

            // Prefer the speaker used last time, otherwise a Play:1, otherwise the first one.
            var pick = found.FindIndex(d => d.Uuid == _settings.LastSpeakerUuid);
            if (pick < 0) pick = found.FindIndex(d => d.ModelName.Contains("Play:1", StringComparison.OrdinalIgnoreCase));
            if (pick < 0 && found.Count > 0) pick = 0;
            if (pick >= 0) _speakers.SelectedIndex = pick;
            if (found.Count == 0) SetStatus("No speakers found", Color.IndianRed, "Make sure this PC and your Sonos share a network");
            else SetStatus("Ready", Theme.Violet, "Press play to send your PC audio");
        }
        finally
        {
            _searching = false;
            _refresh.Spin = 0;
            _refresh.Enabled = true;
        }
    }

    async Task OnSpeakerChangedAsync()
    {
        var dev = Selected;
        _toggle.Enabled = dev != null;
        _volume.Enabled = dev != null;
        if (dev == null) return;
        try
        {
            _settings.LastSpeakerUuid = dev.Uuid;
            _settings.Save();
            _volume.Value = await Sonos.GetVolumeAsync(dev.Ip);
            _volValue.Text = _volume.Value.ToString();
        }
        catch (Exception ex) { Log(ex.Message); }
    }

    async Task SetVolumeAsync()
    {
        if (Selected is not { } dev) return;
        try { await Sonos.SetVolumeAsync(dev.Ip, _volume.Value); }
        catch (Exception ex) { Log(ex.Message); }
    }

    async Task ToggleAsync()
    {
        if (_streamer == null) await StartAsync();
        else await StopAsync();
    }

    async Task StartAsync()
    {
        if (Selected is not { } dev) return;
        _toggle.Busy = true;
        SetStatus("Connecting…", Theme.Amber, $"Asking {dev.RoomName} to tune in");
        try
        {
            _streamer = new AudioStreamer(StreamPort);
            _streamer.Log += Log;
            _streamer.Start((_outputs.SelectedItem as OutputItem)?.Device);

            var url = $"http://{Sonos.LocalIpFor(dev.Ip)}:{StreamPort}/pc-audio.wav";
            _playingOn = await Sonos.GetCoordinatorIpAsync(dev);
            if (_playingOn != dev.Ip) Log("Speaker is grouped — playing on its whole group.");
            Log($"Asking {dev.RoomName} to play {url}");
            await Sonos.PlayStreamAsync(_playingOn, url, $"PC Audio ({Environment.MachineName})");

            _toggle.Active = true;
            _speakers.Enabled = _outputs.Enabled = _refresh.Enabled = false;
            _stalledTicks = 0; _lastBytes = 0;
            _statusTimer.Start();
            _tray.Text = $"SonosCast — playing on {dev.RoomName}";
        }
        catch (Exception ex)
        {
            // The speaker never fetched the stream before giving up on Play: almost always the firewall.
            bool blocked = ex is TimeoutException && _streamer is { EverConnected: false };
            var reason = ex is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse }
                ? $"Port {StreamPort} is in use. Is another copy of SonosCast (or an older version) still running?"
                : ex.Message;
            Log("Could not start: " + reason);
            SetStatus("Couldn't start", Color.IndianRed, reason);
            _streamer?.Dispose(); _streamer = null;
            _toggle.Busy = false;
            if (blocked) await OfferFirewallFixAsync();
        }
        finally { _toggle.Busy = false; }
    }

    async Task StopAsync()
    {
        _statusTimer.Stop();
        _toggle.Busy = true;
        if (_playingOn != null) await Sonos.StopAsync(_playingOn);
        _streamer?.Dispose(); _streamer = null; _playingOn = null;
        _toggle.Busy = false;
        _toggle.Active = false;
        _speakers.Enabled = _outputs.Enabled = _refresh.Enabled = true;
        SetStatus("Stopped", Theme.Muted, "Press play to send your PC audio");
        _tray.Text = "SonosCast";
        Log("Stopped.");
    }

    async Task UpdateStatusAsync()
    {
        if (_streamer == null || _playingOn == null) return;
        var bytes = _streamer.BytesSent;
        var kbps = (bytes - _lastBytes) * 8 / 1000;
        _lastBytes = bytes;
        string state;
        try { state = await Sonos.GetTransportStateAsync(_playingOn); }
        catch { state = "UNREACHABLE"; }

        var room = Selected?.RoomName ?? "Sonos";
        if (state == "PLAYING" && _streamer.ClientCount > 0)
            SetStatus($"Live on {room}", Theme.Live,
                $"{_streamer.SampleRate / 1000.0:0.#} kHz · 16-bit stereo · {kbps:N0} kbps · PC buffer {_streamer.TakePeakBacklogMs()} ms");
        else if (state == "TRANSITIONING" || !_streamer.EverConnected)
            SetStatus("Connecting…", Theme.Amber, $"Waiting for {room} to pick up the stream");
        else
            SetStatus($"{room}: {state.Replace('_', ' ').ToLowerInvariant()}", Theme.Amber, "The speaker isn't playing the stream");

        // The speaker never reached our stream: almost always Windows Firewall blocking inbound connections.
        if (!_streamer.EverConnected)
        {
            if (++_stalledTicks == 8) await OfferFirewallFixAsync();
            return;
        }

        // Someone may have changed the source in the Sonos app; only flag it, don't fight them.
        if (state is "STOPPED" or "PAUSED_PLAYBACK" && _streamer.ClientCount == 0 && ++_stalledTicks == 8)
            Log("The Sonos is no longer playing the stream (changed in the Sonos app?). Press stop, then play to resume.");
        if (_streamer.ClientCount > 0) _stalledTicks = 0;
    }

    async Task OfferFirewallFixAsync()
    {
        Log("The Sonos could not connect to this PC — Windows Firewall is probably blocking SonosCast.");
        var answer = MessageBox.Show(this,
            "The Sonos speaker could not connect to this PC to fetch the audio.\n\n" +
            "This is almost always Windows Firewall blocking SonosCast. Allow SonosCast through the firewall " +
            "on private networks now? (Windows will ask for administrator permission.)",
            "SonosCast — firewall", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes) return;

        var exe = Environment.ProcessPath!;
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("netsh",
                $"advfirewall firewall add rule name=\"SonosCast\" dir=in action=allow program=\"{exe}\" enable=yes profile=private,domain")
            { UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden });
            await p!.WaitForExitAsync();
            if (p.ExitCode != 0) { Log($"Adding the firewall rule failed (netsh exit {p.ExitCode})."); return; }
        }
        catch (System.ComponentModel.Win32Exception) { Log("Firewall change cancelled."); return; }

        Log("Firewall rule added. Restarting the stream…");
        await StopAsync();
        await StartAsync();
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (_streamer != null)
        {
            e.Cancel = true;
            await StopAsync();
            _tray.Visible = false;
            Close();
            return;
        }
        _tray.Visible = false;
        base.OnFormClosing(e);
    }
}
