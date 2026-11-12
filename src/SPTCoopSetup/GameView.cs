using System.Drawing.Drawing2D;

namespace SPTCoopSetup;

/// <summary>The playable Doodle Jump surface shown in the right-hand column.</summary>
internal sealed class GameView : Control
{
    private const int CornerRadius = 10;
    private const int PlatformRadius = 3;
    private const int TimerIntervalMs = 16;
    private const int PreviewTicks = 240;
    private const int AttractBandMaxWidth = 300;

    private const string AttractPrimaryText = "PRESS ANY ARROW TO PLAY";
    private const string AttractSecondaryText = "or A / D";
    private const string PausedText = "PAUSED";
    private const string PlayAgainText = "press any key to play again";

    private readonly GameModel _model;
    private readonly System.Windows.Forms.Timer _timer;

    private Font _scoreFont;
    private Font _scoreSmallFont;
    private Font _hintFont;
    private Font _hintSmallFont;
    private Font _deadScoreFont;

    private Size _attractPrimarySize;
    private Size _attractSecondarySize;
    private Size _pausedChipSize;
    private Size _deadScoreSize;
    private Size _deadBestSize;
    private Size _deadAgainSize;

    private string _scoreText = string.Empty;
    private string _bestText = string.Empty;
    private int _scoreMetres = -1;
    private int _bestMetres = -1;

    private bool _windowActive = true;
    private bool _pausedByWindow;
    private bool _pausedBeforeWindow;
    private bool _ready;

    public GameView(GameModel model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));

        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
        // Clickable + focusable so clicking the panel takes focus away from the folder box;
        // otherwise arrows keep typing into the text field instead of moving the player.
        SetStyle(ControlStyles.Selectable, true);
        TabStop = false;

        BackColor = Theme.Track;
        ForeColor = Theme.TextPrimary;

        _scoreFont = CreateFont(11f, FontStyle.Bold);
        _scoreSmallFont = CreateFont(8.5f, FontStyle.Regular);
        _hintFont = CreateFont(10f, FontStyle.Bold);
        _hintSmallFont = CreateFont(8.5f, FontStyle.Regular);
        _deadScoreFont = CreateFont(22f, FontStyle.Bold);

        RecalculateTextMetrics();

        _timer = new System.Windows.Forms.Timer { Interval = TimerIntervalMs };
        _timer.Tick += OnSimulationTick;

        _model.Resize(Math.Max(1, Width - 2), Math.Max(1, Height - 2));
        _ready = true;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (CanFocus)
        {
            Focus();
        }
    }

    public GameModel Model => _model;

    /// <summary>True when the game is paused because the window is inactive or minimised.</summary>
    public bool AutoPaused => _pausedByWindow;

    /// <summary>True while the 16 ms simulation timer is running; false whenever the game is idle.</summary>
    public bool IsLooping => _timer.Enabled;

    /// <summary>Called by the window on Activated/Deactivate: pauses while the window is inactive.</summary>
    public void SetWindowActive(bool active)
    {
        if (active)
        {
            if (!_pausedByWindow)
            {
                return;
            }

            _pausedByWindow = false;
            _windowActive = true;
            _model.Paused = _pausedBeforeWindow;
            StartLoop();
            Invalidate();
            return;
        }

        if (_pausedByWindow)
        {
            return;
        }

        _windowActive = false;
        _pausedByWindow = true;
        _pausedBeforeWindow = _model.Paused;
        _model.Paused = true;
        StopLoop();
        Invalidate();
    }

    /// <summary>Routes one key state change; returns true when the game consumed it.</summary>
    public bool HandleKey(Keys key, bool down)
    {
        // The dead overlay accepts any key, even while auto-paused.
        if (down && _model.State == GameState.Dead)
        {
            _model.Restart();
            StartLoop();
            Invalidate();
            return true;
        }

        switch (key)
        {
            case Keys.Left:
            case Keys.A:
                ApplyDirection(-1, down);
                return true;

            case Keys.Right:
            case Keys.D:
                ApplyDirection(1, down);
                return true;

            case Keys.Space:
                if (!down)
                {
                    return true;
                }

                if (_model.State == GameState.Attract)
                {
                    _model.Start();
                    StartLoop();
                }
                else
                {
                    _model.Paused = !_model.Paused;

                    if (_model.Paused)
                    {
                        StopLoop();
                    }
                    else
                    {
                        StartLoop();
                    }
                }

                Invalidate();
                return true;

            default:
                return false;
        }
    }

    /// <summary>Deterministic mid-play frame for --preview-ui (fixed seed, no timer).</summary>
    public void PrimeForPreview()
    {
        _model.Start();
        _model.Direction = 1;

        for (var i = 0; i < PreviewTicks; i++)
        {
            _model.Tick();
        }

        _model.Direction = 0;
        Invalidate();
    }

    /// <summary>Starts/stops the 16 ms simulation timer.</summary>
    public void StartLoop()
    {
        if (_timer.Enabled)
        {
            return;
        }

        if (!IsHandleCreated || !Visible || !_windowActive || _pausedByWindow)
        {
            return;
        }

        if (_model.Paused)
        {
            return;
        }

        _timer.Start();
    }

    public void StopLoop()
    {
        if (_timer.Enabled)
        {
            _timer.Stop();
        }
    }

    private void ApplyDirection(int direction, bool down)
    {
        if (_pausedByWindow)
        {
            return;
        }

        if (down)
        {
            if (_model.State == GameState.Attract)
            {
                _model.Start();
                StartLoop();
            }

            _model.Direction = direction;
            Invalidate();
            return;
        }

        // Only release the direction this key actually set, so A+D then releasing A keeps D moving.
        if (_model.Direction == direction)
        {
            _model.Direction = 0;
        }
    }

    private void OnSimulationTick(object? sender, EventArgs e)
    {
        // One tick per timer event: a starved timer drops frames instead of catching up.
        _model.Tick();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        var bounds = ClientRectangle;

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        DarkPaint.Fill(graphics, bounds, Theme.Track);

        var previousSmoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var canvas = new Rectangle(0, 0, bounds.Width - 1, bounds.Height - 1);

        using (var clip = RoundedRect(canvas, CornerRadius))
        {
            graphics.SetClip(clip, CombineMode.Intersect);

            PaintPlatforms(graphics);
            PaintPlayer(graphics);
            PaintScore(graphics, bounds);
            PaintOverlay(graphics, bounds);

            graphics.ResetClip();
        }

        using (var frame = RoundedRect(canvas, CornerRadius))
        using (var pen = new Pen(Theme.Border, 1f))
        {
            graphics.DrawPath(pen, frame);
        }

        graphics.SmoothingMode = previousSmoothing;
    }

    private void PaintPlatforms(Graphics graphics)
    {
        var platforms = _model.Platforms;

        if (platforms is null || platforms.Count == 0)
        {
            return;
        }

        var brush = new SolidBrush(Theme.TextSecondary);

        try
        {
            for (var i = 0; i < platforms.Count; i++)
            {
                var platform = platforms[i];

                if (platform.Width <= 0f || platform.Height <= 0f)
                {
                    continue;
                }

                using var path = RoundedRect(platform, PlatformRadius);
                graphics.FillPath(brush, path);
            }
        }
        finally
        {
            brush.Dispose();
        }
    }

    private void PaintPlayer(Graphics graphics)
    {
        var player = _model.PlayerBounds;

        if (player.Width <= 0f || player.Height <= 0f)
        {
            return;
        }

        using (var body = new SolidBrush(Theme.AccentFill))
        using (var path = RoundedRect(player, PlatformRadius))
        {
            graphics.FillPath(body, path);
        }

        var eyeSize = Math.Max(1.5f, player.Width * 0.18f);
        var eyeTop = player.Top + player.Height * 0.3f;
        var leftEye = player.Left + player.Width * 0.26f;
        var rightEye = player.Right - player.Width * 0.26f - eyeSize;

        using var eyes = new SolidBrush(Theme.AccentText);
        graphics.FillEllipse(eyes, leftEye, eyeTop, eyeSize, eyeSize);
        graphics.FillEllipse(eyes, rightEye, eyeTop, eyeSize, eyeSize);
    }

    private void PaintScore(Graphics graphics, Rectangle bounds)
    {
        RefreshScoreText();

        const TextFormatFlags flags = TextFormatFlags.SingleLine
            | TextFormatFlags.NoPrefix
            | TextFormatFlags.Right;

        var scoreRect = new Rectangle(4, 8, Math.Max(0, bounds.Width - 8), _scoreFont.Height);
        TextRenderer.DrawText(graphics, _scoreText, _scoreFont, scoreRect, Theme.TextPrimary, flags);

        if (_model.BestMetres <= 0)
        {
            return;
        }

        var bestRect = new Rectangle(
            4,
            scoreRect.Bottom + 1,
            Math.Max(0, bounds.Width - 8),
            _scoreSmallFont.Height);

        TextRenderer.DrawText(graphics, _bestText, _scoreSmallFont, bestRect, Theme.TextSecondary, flags);
    }

    private void PaintOverlay(Graphics graphics, Rectangle bounds)
    {
        switch (_model.State)
        {
            case GameState.Attract:
                PaintAttract(graphics, bounds);
                break;

            case GameState.Dead:
                PaintDead(graphics, bounds);
                break;

            default:
                if (AutoPaused || _model.Paused)
                {
                    PaintPausedChip(graphics);
                }

                break;
        }
    }

    private void PaintAttract(Graphics graphics, Rectangle bounds)
    {
        // Draw the band centred on the canvas so the hint never fights the drifting platforms.
        var bandHeight = _attractPrimarySize.Height + _attractSecondarySize.Height + 18;
        var bandWidth = Math.Min(bounds.Width - 16, AttractBandMaxWidth);

        if (bandWidth <= 0)
        {
            return;
        }

        var band = new Rectangle(
            bounds.Left + (bounds.Width - bandWidth) / 2,
            bounds.Top + (bounds.Height - bandHeight) / 2,
            bandWidth,
            bandHeight);

        using (var bandPath = RoundedRect(band, CornerRadius))
        using (var scrim = new SolidBrush(Theme.BgScrim))
        {
            graphics.FillPath(scrim, bandPath);
        }

        const TextFormatFlags flags = TextFormatFlags.SingleLine
            | TextFormatFlags.NoPrefix
            | TextFormatFlags.HorizontalCenter
            | TextFormatFlags.VerticalCenter;

        var primaryRect = new Rectangle(band.Left + 4, band.Top + 8, band.Width - 8, _attractPrimarySize.Height);
        TextRenderer.DrawText(graphics, AttractPrimaryText, _hintFont, primaryRect, Theme.TextPrimary, flags);

        var secondaryRect = new Rectangle(
            band.Left + 4,
            primaryRect.Bottom + 1,
            band.Width - 8,
            _attractSecondarySize.Height);

        TextRenderer.DrawText(graphics, AttractSecondaryText, _hintSmallFont, secondaryRect, Theme.TextSecondary, flags);
    }

    private void PaintDead(Graphics graphics, Rectangle bounds)
    {
        DarkPaint.Fill(graphics, bounds, Theme.BgScrim);

        RefreshScoreText();

        const TextFormatFlags flags = TextFormatFlags.SingleLine
            | TextFormatFlags.NoPrefix
            | TextFormatFlags.HorizontalCenter
            | TextFormatFlags.VerticalCenter;

        var blockHeight = _deadScoreSize.Height
            + _deadBestSize.Height
            + _deadAgainSize.Height
            + 10;
        var top = bounds.Top + (bounds.Height - blockHeight) / 2;
        var width = Math.Max(0, bounds.Width - 16);

        var scoreRect = new Rectangle(bounds.Left + 8, top, width, _deadScoreSize.Height);
        TextRenderer.DrawText(graphics, _scoreText, _deadScoreFont, scoreRect, Theme.TextPrimary, flags);

        var bestRect = new Rectangle(bounds.Left + 8, scoreRect.Bottom + 2, width, _deadBestSize.Height);
        TextRenderer.DrawText(graphics, _bestText, _hintSmallFont, bestRect, Theme.TextSecondary, flags);

        var againRect = new Rectangle(bounds.Left + 8, bestRect.Bottom + 6, width, _deadAgainSize.Height);
        TextRenderer.DrawText(graphics, PlayAgainText, _hintSmallFont, againRect, Theme.TextSecondary, flags);
    }

    private void PaintPausedChip(Graphics graphics)
    {
        const TextFormatFlags flags = TextFormatFlags.SingleLine
            | TextFormatFlags.NoPrefix
            | TextFormatFlags.HorizontalCenter
            | TextFormatFlags.VerticalCenter;

        var chip = new Rectangle(8, 8, _pausedChipSize.Width, _pausedChipSize.Height);

        if (chip.Width <= 0 || chip.Height <= 0)
        {
            return;
        }

        using (var chipPath = RoundedRect(chip, 5))
        using (var fill = new SolidBrush(Theme.SurfaceRaised))
        {
            graphics.FillPath(fill, chipPath);
        }

        using (var chipPath = RoundedRect(chip, 5))
        using (var pen = new Pen(Theme.Border, 1f))
        {
            graphics.DrawPath(pen, chipPath);
        }

        TextRenderer.DrawText(graphics, PausedText, _hintFont, chip, Theme.TextPrimary, flags);
    }

    private void RefreshScoreText()
    {
        if (_scoreMetres == _model.ScoreMetres && _bestMetres == _model.BestMetres)
        {
            return;
        }

        _scoreMetres = _model.ScoreMetres;
        _bestMetres = _model.BestMetres;
        _scoreText = _scoreMetres.ToString() + " m";
        _bestText = "best " + _bestMetres.ToString() + " m";
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);

        if (!_ready)
        {
            return;
        }

        _model.Resize(Math.Max(1, Width - 2), Math.Max(1, Height - 2));
        Invalidate();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);

        if (Visible)
        {
            StartLoop();
        }
        else
        {
            StopLoop();
        }
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);

        if (!_ready)
        {
            return;
        }

        DisposeFonts();

        _scoreFont = CreateFont(11f, FontStyle.Bold);
        _scoreSmallFont = CreateFont(8.5f, FontStyle.Regular);
        _hintFont = CreateFont(10f, FontStyle.Bold);
        _hintSmallFont = CreateFont(8.5f, FontStyle.Regular);
        _deadScoreFont = CreateFont(22f, FontStyle.Bold);

        RecalculateTextMetrics();
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopLoop();
            _timer.Tick -= OnSimulationTick;
            _timer.Dispose();
            DisposeFonts();
        }

        base.Dispose(disposing);
    }

    private void DisposeFonts()
    {
        _scoreFont?.Dispose();
        _scoreSmallFont?.Dispose();
        _hintFont?.Dispose();
        _hintSmallFont?.Dispose();
        _deadScoreFont?.Dispose();

        _scoreFont = null!;
        _scoreSmallFont = null!;
        _hintFont = null!;
        _hintSmallFont = null!;
        _deadScoreFont = null!;
    }

    /// <summary>
    /// Measures every fixed string once per font or theme change so the 16 ms paint path
    /// does no text measurement work.
    /// </summary>
    private void RecalculateTextMetrics()
    {
        _attractPrimarySize = TextRenderer.MeasureText(AttractPrimaryText, _hintFont);
        _attractSecondarySize = TextRenderer.MeasureText(AttractSecondaryText, _hintSmallFont);
        _deadScoreSize = TextRenderer.MeasureText("888 m", _deadScoreFont);
        _deadBestSize = TextRenderer.MeasureText("best 888 m", _hintSmallFont);
        _deadAgainSize = TextRenderer.MeasureText(PlayAgainText, _hintSmallFont);
        _pausedChipSize = TextRenderer.MeasureText(PausedText, _hintFont);

        _pausedChipSize = new Size(_pausedChipSize.Width + 14, _pausedChipSize.Height + 6);

        _scoreMetres = -1;
        _bestMetres = -1;
    }

    private Font CreateFont(float size, FontStyle style)
    {
        return new Font(Font.FontFamily, size, style, GraphicsUnit.Point);
    }

    private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();

        if (bounds.Width <= 0f || bounds.Height <= 0f)
        {
            return path;
        }

        var arc = Math.Min(radius * 2f, Math.Min(bounds.Width, bounds.Height));

        if (arc <= 0f)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.Left, bounds.Top, arc, arc, 180f, 90f);
        path.AddArc(bounds.Right - arc, bounds.Top, arc, arc, 270f, 90f);
        path.AddArc(bounds.Right - arc, bounds.Bottom - arc, arc, arc, 0f, 90f);
        path.AddArc(bounds.Left, bounds.Bottom - arc, arc, arc, 90f, 90f);
        path.CloseFigure();

        return path;
    }
}
