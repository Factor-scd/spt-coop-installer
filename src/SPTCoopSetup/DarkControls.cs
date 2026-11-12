using System.ComponentModel;

namespace SPTCoopSetup;

/// <summary>Pixel helpers shared by the custom-drawn controls.</summary>
internal static class DarkPaint
{
    /// <summary>Draws a 1px frame on the outer edge of <paramref name="bounds"/>.</summary>
    public static void Frame(Graphics graphics, Rectangle bounds, Color color)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        using var brush = new SolidBrush(color);

        graphics.FillRectangle(brush, bounds.X, bounds.Y, bounds.Width, 1);
        graphics.FillRectangle(brush, bounds.X, bounds.Bottom - 1, bounds.Width, 1);
        graphics.FillRectangle(brush, bounds.X, bounds.Y, 1, bounds.Height);
        graphics.FillRectangle(brush, bounds.Right - 1, bounds.Y, 1, bounds.Height);
    }

    /// <summary>Fills <paramref name="bounds"/> with a solid colour.</summary>
    public static void Fill(Graphics graphics, Rectangle bounds, Color color)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        using var brush = new SolidBrush(color);
        graphics.FillRectangle(brush, bounds);
    }
}

/// <summary>A flat surface with a 1px border and an optional header title; the GroupBox replacement.</summary>
internal sealed class DarkPanel : Panel
{
    private const int HorizontalPadding = 12;
    private const int BottomPadding = 10;
    private const int TitleTop = 6;

    private string? _title;
    private Font? _titleFont;

    public DarkPanel(string? title = null)
    {
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer,
            true);

        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Theme.Surface;
        ForeColor = Theme.TextPrimary;

        _titleFont = CreateTitleFont(Font);
        _title = title;
        ApplyTitlePadding();
    }

    [DefaultValue(null)]
    public string? Title
    {
        get => _title;
        set
        {
            if (string.Equals(_title, value, StringComparison.Ordinal))
            {
                return;
            }

            _title = value;
            ApplyTitlePadding();
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var bounds = ClientRectangle;

        DarkPaint.Fill(e.Graphics, bounds, Theme.Surface);
        DarkPaint.Frame(e.Graphics, bounds, Theme.Border);

        if (!string.IsNullOrEmpty(_title) && _titleFont is not null)
        {
            TextRenderer.DrawText(
                e.Graphics,
                _title,
                _titleFont,
                new Point(Padding.Left, TitleTop),
                Theme.TextPrimary,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        base.OnPaint(e);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);

        var replacement = CreateTitleFont(Font);
        _titleFont?.Dispose();
        _titleFont = replacement;

        ApplyTitlePadding();
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _titleFont?.Dispose();
            _titleFont = null;
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Reserves the header band inside the top padding so AutoSize accounts for the title.
    /// </summary>
    private void ApplyTitlePadding()
    {
        var top = BottomPadding;

        if (!string.IsNullOrEmpty(_title) && _titleFont is not null)
        {
            top = TextRenderer.MeasureText(_title, _titleFont).Height + 10;
        }

        Padding = new Padding(HorizontalPadding, top, HorizontalPadding, BottomPadding);
    }

    private static Font CreateTitleFont(Font baseFont) => new(baseFont, FontStyle.Bold);
}

/// <summary>A bordered raised surface that hosts an input control and highlights while it has focus.</summary>
internal sealed class DarkField : Panel
{
    private readonly Control _child;

    public DarkField(Control child)
    {
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer,
            true);

        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Theme.SurfaceRaised;
        ForeColor = Theme.TextPrimary;
        Padding = new Padding(8, 5, 8, 5);

        _child = child;
        child.Dock = DockStyle.Fill;
        child.BackColor = Theme.SurfaceRaised;
        child.ForeColor = Theme.TextPrimary;

        if (child is TextBoxBase box)
        {
            box.BorderStyle = BorderStyle.None;
        }

        child.GotFocus += OnChildFocusChanged;
        child.LostFocus += OnChildFocusChanged;
        child.Enter += OnChildFocusChanged;
        child.Leave += OnChildFocusChanged;

        Controls.Add(child);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var bounds = ClientRectangle;
        var borderColour = _child.Focused ? Theme.AccentFill : Theme.Border;

        DarkPaint.Fill(e.Graphics, bounds, Theme.SurfaceRaised);
        DarkPaint.Frame(e.Graphics, bounds, borderColour);

        base.OnPaint(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _child is not null)
        {
            _child.GotFocus -= OnChildFocusChanged;
            _child.LostFocus -= OnChildFocusChanged;
            _child.Enter -= OnChildFocusChanged;
            _child.Leave -= OnChildFocusChanged;
        }

        base.Dispose(disposing);
    }

    private void OnChildFocusChanged(object? sender, EventArgs e) => Invalidate();
}

/// <summary>A flat button with a primary and a secondary emphasis, drawn from the theme palette.</summary>
internal sealed class DarkButton : Button
{
    private static readonly Color HoverFill = Color.FromArgb(30, 32, 39);
    private static readonly Color PressedFill = Color.FromArgb(12, 13, 16);
    private static readonly Color PrimaryPressedFill = Color.FromArgb(214, 217, 226);

    private bool _hovered;
    private bool _pressed;

    public DarkButton()
    {
        SetStyle(
            ControlStyles.UserPaint
                | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw,
            true);

        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        BackColor = Theme.SurfaceRaised;
        ForeColor = Theme.TextPrimary;
    }

    /// <summary>True for the single high-emphasis action in a view.</summary>
    [DefaultValue(false)]
    public bool Primary { get; set; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var bounds = ClientRectangle;
        var enabled = Enabled;
        var interactive = enabled && _hovered;

        Color fill;
        Color text;
        Color? border = null;

        if (!enabled)
        {
            fill = Theme.Surface;
            text = Theme.TextSecondary;
            border = Theme.Border;
        }
        else if (Primary)
        {
            fill = _pressed ? PrimaryPressedFill : Theme.AccentFill;
            text = Theme.AccentText;
        }
        else
        {
            fill = _pressed ? PressedFill : interactive ? HoverFill : Theme.SurfaceRaised;
            text = Theme.TextPrimary;
            border = Theme.Border;
        }

        DarkPaint.Fill(e.Graphics, bounds, fill);

        if (border is not null)
        {
            DarkPaint.Frame(e.Graphics, bounds, border.Value);
        }

        TextRenderer.DrawText(
            e.Graphics,
            Text,
            Font,
            bounds,
            text,
            TextFormatFlags.HorizontalCenter
                | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine
                | TextFormatFlags.EndEllipsis
                | TextFormatFlags.NoPrefix);

        if (Focused && ShowFocusCues)
        {
            var focusBounds = Rectangle.Inflate(bounds, -3, -3);
            DarkPaint.Frame(e.Graphics, focusBounds, text);
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        if (mevent.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }

        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        if (_pressed)
        {
            _pressed = false;
            Invalidate();
        }

        base.OnMouseUp(mevent);
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        if (_pressed)
        {
            _pressed = false;
            Invalidate();
        }

        base.OnMouseCaptureChanged(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        _hovered = false;
        _pressed = false;
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        Invalidate();
        base.OnLostFocus(e);
    }
}

internal enum DarkProgressStyle
{
    Blocks,
    Continuous,
    Marquee
}

/// <summary>A flat progress indicator with block, continuous and marquee modes.</summary>
internal sealed class DarkProgressBar : Control
{
    private readonly System.Windows.Forms.Timer _marquee;

    private int _minimum;
    private int _maximum = 100;
    private int _value;
    private DarkProgressStyle _style = DarkProgressStyle.Blocks;
    private int _marqueeSpeed = 30;
    private int _marqueeOffset;

    public DarkProgressBar()
    {
        SetStyle(
            ControlStyles.UserPaint
                | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw,
            true);

        BackColor = Theme.Track;
        Size = new Size(220, 12);

        _marquee = new System.Windows.Forms.Timer { Interval = Math.Max(1, _marqueeSpeed) };
        _marquee.Tick += OnMarqueeTick;

        SyncMarquee();
    }

    [DefaultValue(0)]
    public int Minimum
    {
        get => _minimum;
        set
        {
            if (_minimum == value)
            {
                return;
            }

            _minimum = value;
            _value = ClampValue(_value);
            Invalidate();
        }
    }

    [DefaultValue(100)]
    public int Maximum
    {
        get => _maximum;
        set
        {
            if (_maximum == value)
            {
                return;
            }

            _maximum = value;
            _value = ClampValue(_value);
            Invalidate();
        }
    }

    [DefaultValue(0)]
    public int Value
    {
        get => _value;
        set
        {
            var clamped = ClampValue(value);

            if (_value == clamped)
            {
                return;
            }

            _value = clamped;
            Invalidate();
        }
    }

    [DefaultValue(DarkProgressStyle.Blocks)]
    public DarkProgressStyle Style
    {
        get => _style;
        set
        {
            if (_style == value)
            {
                return;
            }

            _style = value;

            if (_style != DarkProgressStyle.Marquee)
            {
                _marqueeOffset = 0;
            }

            SyncMarquee();
            Invalidate();
        }
    }

    /// <summary>Milliseconds per marquee frame; zero or lower disables the animation.</summary>
    [DefaultValue(30)]
    public int MarqueeAnimationSpeed
    {
        get => _marqueeSpeed;
        set
        {
            if (_marqueeSpeed == value)
            {
                return;
            }

            _marqueeSpeed = value;
            SyncMarquee();
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var bounds = ClientRectangle;

        DarkPaint.Fill(e.Graphics, bounds, Theme.Track);
        DarkPaint.Frame(e.Graphics, bounds, Theme.Border);

        var inner = Rectangle.Inflate(bounds, -1, -1);

        if (inner.Width > 0 && inner.Height > 0)
        {
            if (_style == DarkProgressStyle.Marquee)
            {
                PaintMarqueeBand(e.Graphics, inner);
            }
            else
            {
                PaintProgress(e.Graphics, inner);
            }
        }

        base.OnPaint(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SyncMarquee();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _marquee.Stop();
        base.OnHandleDestroyed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _marquee.Stop();
            _marquee.Tick -= OnMarqueeTick;
            _marquee.Dispose();
        }

        base.Dispose(disposing);
    }

    private void PaintProgress(Graphics graphics, Rectangle inner)
    {
        var width = (int)Math.Round(GetFraction() * inner.Width);
        width = Math.Clamp(width, 0, inner.Width);

        if (width <= 0)
        {
            return;
        }

        DarkPaint.Fill(graphics, new Rectangle(inner.X, inner.Y, width, inner.Height), Theme.AccentFill);
    }

    private void PaintMarqueeBand(Graphics graphics, Rectangle inner)
    {
        var band = MarqueeBandWidth(inner.Width);
        var left = Math.Max(inner.Left, inner.Left + _marqueeOffset);
        var right = Math.Min(inner.Right, inner.Left + _marqueeOffset + band);

        if (right <= left)
        {
            return;
        }

        DarkPaint.Fill(graphics, new Rectangle(left, inner.Y, right - left, inner.Height), Theme.AccentFill);
    }

    private double GetFraction()
    {
        if (_maximum <= _minimum)
        {
            return 0d;
        }

        var fraction = (_value - _minimum) / (double)(_maximum - _minimum);
        return Math.Clamp(fraction, 0d, 1d);
    }

    private static int MarqueeBandWidth(int innerWidth) =>
        Math.Min(Math.Max(24, innerWidth / 4), innerWidth);

    private int ClampValue(int value)
    {
        var low = Math.Min(_minimum, _maximum);
        var high = Math.Max(_minimum, _maximum);
        return Math.Clamp(value, low, high);
    }

    private void OnMarqueeTick(object? sender, EventArgs e)
    {
        var innerWidth = Math.Max(0, ClientSize.Width - 2);

        if (innerWidth <= 0)
        {
            _marqueeOffset = 0;
            return;
        }

        _marqueeOffset += Math.Max(2, innerWidth / 24);

        if (_marqueeOffset > innerWidth)
        {
            _marqueeOffset = -MarqueeBandWidth(innerWidth);
        }

        Invalidate();
    }

    private void SyncMarquee()
    {
        var shouldAnimate = !DesignMode
            && _style == DarkProgressStyle.Marquee
            && _marqueeSpeed > 0;

        if (shouldAnimate)
        {
            _marquee.Interval = Math.Max(1, _marqueeSpeed);

            if (!_marquee.Enabled)
            {
                _marquee.Start();
            }
        }
        else
        {
            _marquee.Stop();

            if (_style != DarkProgressStyle.Marquee)
            {
                _marqueeOffset = 0;
            }
        }
    }
}

/// <summary>
/// A flat radio button with a hand-drawn ring glyph. The label starts at <see cref="TextOffset"/>
/// instead of the base control's glyph column, so AutoSize is measured here rather than inherited -
/// the base measurement is what used to clip the last characters of a long label.
/// </summary>
internal sealed class DarkRadioButton : RadioButton
{
    private const int GlyphSize = 16;
    private const int DotSize = 8;
    private const int TextOffset = 19;
    private const int LabelRightPad = 4;

    public DarkRadioButton()
    {
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer,
            true);

        DoubleBuffered = true;
        BackColor = Theme.Surface;
        ForeColor = Theme.TextPrimary;
    }

    /// <summary>
    /// Measures the label with the font actually used for painting, offset by the glyph column and
    /// rounded up so an ellipsis never replaces the tail of the text.
    /// </summary>
    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(
            Text ?? string.Empty,
            Font,
            Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

        var width = TextOffset + text.Width + Padding.Right + LabelRightPad;
        var height = Math.Max(GlyphSize + 6, text.Height);

        return new Size(width, height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var bounds = ClientRectangle;

        e.Graphics.Clear(Theme.Surface);

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        PaintGlyph(e.Graphics, bounds);
        PaintLabel(e.Graphics, bounds);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Invalidate();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        Invalidate();
        base.OnCheckedChanged(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        Invalidate();
        base.OnLostFocus(e);
    }

    private void PaintGlyph(Graphics graphics, Rectangle bounds)
    {
        var ringColour = !Enabled
            ? Theme.Border
            : Checked
                ? Theme.AccentFill
                : Theme.TextSecondary;

        var top = (bounds.Height - GlyphSize) / 2;
        var ring = new Rectangle(0, top, GlyphSize - 1, GlyphSize - 1);

        using (var pen = new Pen(ringColour, 1f))
        {
            graphics.DrawEllipse(pen, ring);
        }

        if (!Checked)
        {
            return;
        }

        var dotOffset = (GlyphSize - DotSize) / 2;
        DarkPaint.Fill(
            graphics,
            new Rectangle(dotOffset, top + dotOffset, DotSize, DotSize),
            Theme.AccentFill);
    }

    private void PaintLabel(Graphics graphics, Rectangle bounds)
    {
        var textBounds = new Rectangle(
            TextOffset,
            0,
            Math.Max(0, bounds.Width - TextOffset),
            bounds.Height);

        if (textBounds.Width <= 0)
        {
            return;
        }

        var textColour = Enabled ? Theme.TextPrimary : Theme.TextSecondary;

        TextRenderer.DrawText(
            graphics,
            Text,
            Font,
            textBounds,
            textColour,
            TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine
                | TextFormatFlags.EndEllipsis
                | TextFormatFlags.NoPrefix);

        if (Focused && ShowFocusCues)
        {
            var focus = new Rectangle(
                textBounds.X - 1,
                (bounds.Height - Font.Height) / 2 - 1,
                textBounds.Width,
                Font.Height + 2);

            DarkPaint.Frame(graphics, focus, Theme.TextPrimary);
        }
    }
}
