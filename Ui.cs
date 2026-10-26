using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace SonosCast;

/// <summary>Colours, fonts and drawing helpers shared by the custom-drawn controls.</summary>
static class Theme
{
    public static readonly Color Bg = Color.FromArgb(13, 11, 23);
    public static readonly Color Card = Color.FromArgb(23, 20, 40);
    public static readonly Color CardHover = Color.FromArgb(32, 28, 55);
    public static readonly Color Border = Color.FromArgb(44, 38, 72);
    public static readonly Color Text = Color.FromArgb(242, 240, 255);
    public static readonly Color Muted = Color.FromArgb(150, 144, 182);
    public static readonly Color Violet = Color.FromArgb(139, 92, 246);
    public static readonly Color Blue = Color.FromArgb(59, 130, 246);
    public static readonly Color Cyan = Color.FromArgb(34, 211, 238);
    public static readonly Color Amber = Color.FromArgb(251, 191, 120);
    public static readonly Color Live = Color.FromArgb(52, 211, 153);

    public static readonly Font Title = new("Segoe UI Semibold", 20f);
    public static readonly Font Subtitle = new("Segoe UI", 10f);
    public static readonly Font Body = new("Segoe UI", 10f);
    public static readonly Font BodyBold = new("Segoe UI Semibold", 10.5f);
    public static readonly Font Caption = new("Segoe UI Semibold", 7.5f);
    public static readonly Font Small = new("Segoe UI", 8.5f);
    public static readonly Font Mono = new("Cascadia Mono", 8.25f);
    public static Font Glyph(float size) => new("Segoe MDL2 Assets", size);

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Hq(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }

    public static Image LoadImage(string name)
    {
        using var s = typeof(Theme).Assembly.GetManifestResourceStream($"SonosCast.Assets.{name}")!;
        return Image.FromStream(s);
    }

    public static Icon LoadIcon()
    {
        using var s = typeof(Theme).Assembly.GetManifestResourceStream("SonosCast.Assets.app.ico")!;
        return new Icon(s);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Dark title bar and a matching caption colour on Windows 11.</summary>
    public static void DarkTitleBar(Form f)
    {
        int on = 1;
        DwmSetWindowAttribute(f.Handle, 20, ref on, 4);           // DWMWA_USE_IMMERSIVE_DARK_MODE
        int caption = Bg.R | Bg.G << 8 | Bg.B << 16;
        DwmSetWindowAttribute(f.Handle, 35, ref caption, 4);      // DWMWA_CAPTION_COLOR
    }
}

/// <summary>Base for flicker-free custom controls.</summary>
class Canvas : Control
{
    public Canvas()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
    }

    protected float S => DeviceDpi / 96f;
}

/// <summary>The banner art with the app name over it, fading into the window background.</summary>
sealed class Header : Canvas
{
    readonly Image _banner = Theme.LoadImage("banner.jpg");
    readonly Image _icon = Theme.LoadImage("icon.png");
    public string Tagline { get; set; } = "Your PC's sound, on your Sonos";

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Hq(g);
        g.Clear(Theme.Bg);

        // Cover-fit the banner, biased to the right where the art is brightest.
        float scale = Math.Max((float)Width / _banner.Width, (float)Height / _banner.Height);
        float w = _banner.Width * scale, h = _banner.Height * scale;
        g.DrawImage(_banner, (Width - w) * 0.75f, (Height - h) / 2, w, h);

        using (var fade = new LinearGradientBrush(new Rectangle(0, 0, Width, Height), Color.FromArgb(40, Theme.Bg), Theme.Bg, 90f))
        {
            fade.InterpolationColors = new ColorBlend
            {
                Colors = [Color.FromArgb(30, Theme.Bg), Color.FromArgb(90, Theme.Bg), Theme.Bg],
                Positions = [0f, 0.55f, 1f]
            };
            g.FillRectangle(fade, ClientRectangle);
        }
        using (var side = new LinearGradientBrush(new Rectangle(0, 0, Width, Height), Color.FromArgb(170, Theme.Bg), Color.Transparent, 0f))
            g.FillRectangle(side, 0, 0, Width * 0.6f, Height);

        float pad = 22 * S, iconSize = 52 * S, y = Height - iconSize - 30 * S;
        g.DrawImage(_icon, pad, y, iconSize, iconSize);
        float tx = pad + iconSize + 14 * S;
        TextRenderer.DrawText(g, "SonosCast", Theme.Title, new Point((int)tx, (int)(y - 4 * S)), Theme.Text, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, Tagline, Theme.Subtitle, new Point((int)(tx + 2 * S), (int)(y + 34 * S)), Theme.Muted, TextFormatFlags.NoPadding);
    }
}

/// <summary>A rounded card that hosts other controls.</summary>
sealed class Card : Panel
{
    public string? Caption { get; set; }

    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Hq(g);
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        float s = DeviceDpi / 96f;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.Rounded(r, 14 * s);
        using (var fill = new SolidBrush(Theme.Card)) g.FillPath(fill, path);
        using (var pen = new Pen(Theme.Border, 1f)) g.DrawPath(pen, path);
        if (Caption != null)
            TextRenderer.DrawText(g, Caption.ToUpperInvariant(), Theme.Caption, new Point((int)(16 * s), (int)(12 * s)), Theme.Muted, TextFormatFlags.NoPadding);
    }
}

/// <summary>Dropdown look-alike: a rounded field with a chevron that opens a dark menu.</summary>
sealed class Picker : Canvas
{
    readonly ContextMenuStrip _menu = new() { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, Font = Theme.Body };
    readonly List<object> _items = [];
    bool _hover;
    int _selected = -1;

    public string Glyph { get; set; } = "";
    public string Placeholder { get; set; } = "";
    public event EventHandler? SelectedIndexChanged;

    public Picker()
    {
        Cursor = Cursors.Hand;
        BackColor = Theme.Card;
        _menu.BackColor = Theme.Card;
        _menu.ForeColor = Theme.Text;
    }

    public IReadOnlyList<object> Items => _items;
    public object? SelectedItem => _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;

    public int SelectedIndex
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; Invalidate(); SelectedIndexChanged?.Invoke(this, EventArgs.Empty); }
    }

    public void SetItems(IEnumerable<object> items)
    {
        _items.Clear();
        _items.AddRange(items);
        _selected = -1;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (!Enabled || _items.Count == 0) return;
        _menu.Items.Clear();
        for (int i = 0; i < _items.Count; i++)
        {
            int idx = i;
            var mi = new ToolStripMenuItem(_items[i].ToString()) { Checked = i == _selected, ForeColor = Theme.Text, Padding = new Padding(4, 6, 4, 6) };
            mi.Click += (_, _) => SelectedIndex = idx;
            _menu.Items.Add(mi);
        }
        _menu.MinimumSize = new Size(Width, 0);
        _menu.Show(this, new Point(0, Height + (int)(4 * S)));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Hq(g);
        g.Clear(Parent?.BackColor ?? Theme.Card);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.Rounded(r, 10 * S);
        using (var fill = new SolidBrush(_hover && Enabled ? Theme.CardHover : Color.FromArgb(18, 15, 32))) g.FillPath(fill, path);
        using (var pen = new Pen(_hover && Enabled ? Theme.Violet : Theme.Border, 1f)) g.DrawPath(pen, path);

        var fg = Enabled ? Theme.Text : Theme.Muted;
        using var glyph = Theme.Glyph(12f);
        TextRenderer.DrawText(g, Glyph, glyph, new Rectangle((int)(12 * S), 0, (int)(22 * S), Height), Enabled ? Theme.Violet : Theme.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        var text = SelectedItem?.ToString() ?? Placeholder;
        TextRenderer.DrawText(g, text, Theme.Body, new Rectangle((int)(42 * S), 0, Width - (int)(78 * S), Height),
            SelectedItem == null ? Theme.Muted : fg, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        using var chev = Theme.Glyph(8f);
        TextRenderer.DrawText(g, "", chev, new Rectangle(Width - (int)(34 * S), 0, (int)(22 * S), Height), Theme.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
    }
}

sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = Theme.Text;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        using var f = Theme.Glyph(9f);
        TextRenderer.DrawText(e.Graphics, "", f, e.ImageRectangle, Theme.Cyan, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Card;
        public override Color MenuBorder => Theme.Border;
        public override Color MenuItemBorder => Theme.Violet;
        public override Color MenuItemSelected => Theme.CardHover;
        public override Color MenuItemSelectedGradientBegin => Theme.CardHover;
        public override Color MenuItemSelectedGradientEnd => Theme.CardHover;
        public override Color CheckBackground => Color.Transparent;
        public override Color CheckSelectedBackground => Color.Transparent;
        public override Color CheckPressedBackground => Color.Transparent;
        public override Color ImageMarginGradientBegin => Theme.Card;
        public override Color ImageMarginGradientMiddle => Theme.Card;
        public override Color ImageMarginGradientEnd => Theme.Card;
        public override Color SeparatorDark => Theme.Border;
    }
}

/// <summary>Small round glyph button (refresh, etc.).</summary>
sealed class GlyphButton : Canvas
{
    bool _hover;
    public string Glyph { get; set; } = "";
    public float Spin { get; set; }

    public GlyphButton() { Cursor = Cursors.Hand; BackColor = Theme.Card; }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Hq(g);
        g.Clear(Parent?.BackColor ?? Theme.Card);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.Rounded(r, 10 * S);
        using (var fill = new SolidBrush(_hover && Enabled ? Theme.CardHover : Color.FromArgb(18, 15, 32))) g.FillPath(fill, path);
        using (var pen = new Pen(_hover && Enabled ? Theme.Violet : Theme.Border, 1f)) g.DrawPath(pen, path);

        var state = g.Save();
        g.TranslateTransform(Width / 2f, Height / 2f);
        g.RotateTransform(Spin);
        using var f = Theme.Glyph(12f);
        using var brush = new SolidBrush(Enabled ? Theme.Text : Theme.Muted);
        var size = g.MeasureString(Glyph, f);
        g.DrawString(Glyph, f, brush, -size.Width / 2 + 1, -size.Height / 2 + 1);
        g.Restore(state);
    }
}

/// <summary>The big round start/stop button. Rings pulse outward while streaming.</summary>
sealed class PlayButton : Canvas
{
    bool _hover, _down;
    float _phase;
    readonly System.Windows.Forms.Timer _anim = new() { Interval = 33 };

    public bool Active { get; set { field = value; Invalidate(); } }
    public bool Busy { get; set { field = value; Invalidate(); } }

    public PlayButton()
    {
        Cursor = Cursors.Hand;
        _anim.Tick += (_, _) => { _phase = (_phase + 0.012f) % 1f; if (Active || Busy) Invalidate(); };
        _anim.Start();
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Hq(g);
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        float cx = Width / 2f, cy = Height / 2f;
        float radius = Math.Min(Width, Height) * 0.30f * (_down ? 0.96f : 1f);

        // Outward rings while live; a soft halo otherwise.
        if (Active)
        {
            for (int i = 0; i < 3; i++)
            {
                float t = (_phase + i / 3f) % 1f;
                float rr = radius + t * radius * 0.62f;
                using var pen = new Pen(Color.FromArgb((int)(150 * (1 - t)), Theme.Cyan), 2f * S);
                g.DrawEllipse(pen, cx - rr, cy - rr, rr * 2, rr * 2);
            }
        }
        for (int i = 6; i >= 1; i--)
        {
            float rr = radius + i * 4 * S;
            using var halo = new SolidBrush(Color.FromArgb((Active ? 16 : _hover ? 12 : 7) - i, Active ? Theme.Cyan : Theme.Violet));
            g.FillEllipse(halo, cx - rr, cy - rr, rr * 2, rr * 2);
        }

        var disc = new RectangleF(cx - radius, cy - radius, radius * 2, radius * 2);
        Color c1 = Enabled ? Theme.Violet : Color.FromArgb(70, 64, 96), c2 = Enabled ? (Active ? Theme.Cyan : Theme.Blue) : Color.FromArgb(50, 46, 72);
        if (_hover && Enabled) { c1 = ControlPaint.Light(c1, 0.15f); c2 = ControlPaint.Light(c2, 0.15f); }
        using (var fill = new LinearGradientBrush(disc, c1, c2, 45f)) g.FillEllipse(fill, disc);
        using (var gloss = new LinearGradientBrush(disc, Color.FromArgb(60, Color.White), Color.FromArgb(0, Color.White), 90f))
            g.FillEllipse(gloss, disc.X + radius * 0.2f, disc.Y + radius * 0.06f, radius * 1.6f, radius * 0.9f);

        using var white = new SolidBrush(Color.White);
        if (Busy)
        {
            using var pen = new Pen(Color.White, 3f * S) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            float sr = radius * 0.38f;
            g.DrawArc(pen, cx - sr, cy - sr, sr * 2, sr * 2, _phase * 720f, 260);
        }
        else if (Active)
        {
            float s = radius * 0.62f;
            using var p = Theme.Rounded(new RectangleF(cx - s / 2, cy - s / 2, s, s), 5 * S);
            g.FillPath(white, p);
        }
        else
        {
            float s = radius * 0.78f;
            g.FillPolygon(white, new[] {
                new PointF(cx - s * 0.36f, cy - s * 0.5f),
                new PointF(cx + s * 0.52f, cy),
                new PointF(cx - s * 0.36f, cy + s * 0.5f) });
        }
    }

    protected override void Dispose(bool disposing) { if (disposing) _anim.Dispose(); base.Dispose(disposing); }
}

/// <summary>Volume slider with a gradient fill.</summary>
sealed class Slider : Canvas
{
    int _value;
    bool _drag, _hover;
    public event EventHandler? ValueChanged;

    public Slider() { Cursor = Cursors.Hand; BackColor = Theme.Card; }

    public int Value
    {
        get => _value;
        set { value = Math.Clamp(value, 0, 100); if (value == _value) return; _value = value; Invalidate(); }
    }

    float TrackLeft => 10 * S;
    float TrackWidth => Width - 20 * S;

    void SetFromMouse(int x)
    {
        var v = (int)Math.Round((x - TrackLeft) / TrackWidth * 100);
        if (Math.Clamp(v, 0, 100) == _value) return;
        Value = v;
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseDown(MouseEventArgs e) { if (!Enabled) return; _drag = true; SetFromMouse(e.X); base.OnMouseDown(e); }
    protected override void OnMouseMove(MouseEventArgs e) { if (_drag) SetFromMouse(e.X); base.OnMouseMove(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _drag = false; base.OnMouseUp(e); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (!Enabled) return;
        Value += Math.Sign(e.Delta) * 2;
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Hq(g);
        g.Clear(Parent?.BackColor ?? Theme.Card);
        float h = 6 * S, y = Height / 2f - h / 2;
        using (var track = Theme.Rounded(new RectangleF(TrackLeft, y, TrackWidth, h), h / 2))
        using (var b = new SolidBrush(Color.FromArgb(40, 35, 66))) g.FillPath(b, track);

        float fx = TrackLeft + TrackWidth * _value / 100f;
        if (_value > 0)
        {
            var fr = new RectangleF(TrackLeft, y, Math.Max(h, fx - TrackLeft), h);
            using var fill = Theme.Rounded(fr, h / 2);
            using var grad = new LinearGradientBrush(new RectangleF(TrackLeft, y, TrackWidth, h),
                Enabled ? Theme.Violet : Theme.Muted, Enabled ? Theme.Cyan : Theme.Muted, 0f);
            g.FillPath(grad, fill);
        }
        float tr = (_hover || _drag ? 9 : 8) * S;
        using (var glow = new SolidBrush(Color.FromArgb(_drag ? 70 : 40, Theme.Cyan))) g.FillEllipse(glow, fx - tr - 4 * S, Height / 2f - tr - 4 * S, (tr + 4 * S) * 2, (tr + 4 * S) * 2);
        using (var thumb = new SolidBrush(Enabled ? Color.White : Theme.Muted)) g.FillEllipse(thumb, fx - tr, Height / 2f - tr, tr * 2, tr * 2);
    }
}

/// <summary>Bar-style level meter that animates the captured audio level.</summary>
sealed class LevelMeter : Canvas
{
    readonly float[] _bars = new float[28];
    readonly Random _rng = new();
    float _level;
    public bool Live { get; set; }

    public void Push(float level)
    {
        _level = Live ? Math.Max(level, _level * 0.82f) : _level * 0.8f;
        for (int i = 0; i < _bars.Length; i++)
        {
            // Shape the bars like a soft spectrum around the centre, with a bit of jitter.
            float centre = 1f - Math.Abs(i - _bars.Length / 2f) / (_bars.Length / 2f) * 0.55f;
            float target = _level * centre * (0.65f + (float)_rng.NextDouble() * 0.35f);
            _bars[i] += (target - _bars[i]) * 0.45f;
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Hq(g);
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        float gap = 4 * S, bw = (Width - gap * (_bars.Length - 1)) / _bars.Length;
        using var brush = new LinearGradientBrush(new RectangleF(0, 0, Width, Height), Theme.Violet, Theme.Cyan, 0f);
        brush.InterpolationColors = new ColorBlend { Colors = [Theme.Violet, Theme.Blue, Theme.Cyan, Theme.Amber], Positions = [0f, 0.4f, 0.75f, 1f] };
        for (int i = 0; i < _bars.Length; i++)
        {
            float bh = Math.Max(3 * S, (float)Math.Sqrt(_bars[i]) * Height);
            float x = i * (bw + gap);
            using var p = Theme.Rounded(new RectangleF(x, (Height - bh) / 2, bw, bh), bw / 2);
            g.FillPath(Live ? brush : Brushes.DimGray, p);
        }
    }
}

/// <summary>A small coloured dot + text status line.</summary>
sealed class StatusLine : Canvas
{
    public Color Dot { get; set { field = value; Invalidate(); } } = Theme.Muted;
    public string Detail { get; set { field = value; Invalidate(); } } = "";
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text { get => base.Text; set { base.Text = value; Invalidate(); } }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Hq(g);
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        var main = TextRenderer.MeasureText(Text, Theme.BodyBold);
        float d = 9 * S, total = d + 8 * S + main.Width;
        float x = (Width - total) / 2, y = 4 * S;
        using (var glow = new SolidBrush(Color.FromArgb(60, Dot))) g.FillEllipse(glow, x - 3 * S, y + main.Height / 2f - d / 2 - 3 * S, d + 6 * S, d + 6 * S);
        using (var dot = new SolidBrush(Dot)) g.FillEllipse(dot, x, y + main.Height / 2f - d / 2, d, d);
        TextRenderer.DrawText(g, Text, Theme.BodyBold, new Point((int)(x + d + 8 * S), (int)y), Theme.Text, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, Detail, Theme.Small, new Rectangle(0, (int)(y + main.Height + 4 * S), Width, (int)(20 * S)), Theme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }
}
