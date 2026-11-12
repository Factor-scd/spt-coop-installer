using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

// The form exposes a LayoutState property (the current shape) which would hide the type of the
// same name inside this class; the alias keeps the geometry helpers reachable.
using LayoutStateModel = SPTCoopSetup.LayoutState;

namespace SPTCoopSetup;

/// <summary>
/// The installer window. It has two shapes: idle, where the configuration column fills the window
/// and the game does not exist at all, and running, where a 350 ms reveal eases the game column
/// open next to it. The left column carries mode, folder, progress and the collapsible log drawer.
/// </summary>
internal sealed class MainForm : Form, IMessageFilter
{
    private const int LogLineCap = 5000;
    private const int LogTrimChunk = 1000;
    private const int QuietWindowSeconds = 45;
    private const int ReportTailLines = 40;

    /// <summary>Left-column rows: 0 header, 1 mode, 2 target, 3 progress, 4 status, 5 actions.</summary>
    private const int SpacerRow = 6;
    private const int DrawerRow = 7;

    private const string QuietMarker = "(-- still working, engine quiet for 45s --)";

    /// <summary>The engine prints its mod loop as "  [3/12] slug 1.2.3" - the only progress it exposes.</summary>
    private static readonly Regex ModProgressPattern = new(@"\[(\d+)/(\d+)\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly char[] NewLineChars = ['\r', '\n'];

    private readonly Arguments _arguments;
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 1000 };

    /// <summary>Drives the idle/running reveal at ~60 fps; only alive while the reveal is in flight.</summary>
    private readonly System.Windows.Forms.Timer _expandTimer = new() { Interval = 16 };

    /// <summary>Elapsed source for the reveal, so a late tick still lands on the right width.</summary>
    private readonly Stopwatch _expandClock = new();

    /// <summary>Everything this window has shown, kept for "Copy log" and the final report.</summary>
    private readonly List<string> _logLines = new();

    /// <summary>The subset still in the RichTextBox once the line cap starts trimming.</summary>
    private readonly List<string> _visibleLines = new();

    /// <summary>Paints BgBase, the cover-scaled still and the scrim behind the whole window.</summary>
    private readonly ShaderBackdrop _backdrop = new();

    /// <summary>The persistent per-run file; every line the pane shows is mirrored into it.</summary>
    private readonly RunLog _runLog;

    /// <summary>
    /// The playable game in the right column. Null while idle: the game does not exist until the
    /// window actually reveals it, so an idle window costs no game CPU and no model memory.
    /// </summary>
    private GameView? _gameView;

    private readonly DarkRadioButton _modeFull = new() { Text = "Full install (game + SPT + mods)", AutoSize = true, Checked = true };
    private readonly DarkRadioButton _modeMods = new() { Text = "Mods only (existing SPT install)", AutoSize = true };
    private readonly DarkRadioButton _modeUpdate = new() { Text = "Update (game patch + mods)", AutoSize = true };

    private readonly TextBox _pathBox = new() { Text = Arguments.DefaultTargetPath };
    private readonly DarkButton _browseButton = new() { Text = "Browse..." };
    private readonly Label _validationLabel = new() { AutoSize = false, Visible = false, Dock = DockStyle.Fill };

    private readonly Label _phaseLabel = new() { Text = "Ready.", AutoSize = true };
    private readonly Label _percentLabel = new() { Text = "0 %", AutoSize = true };
    private readonly Label _elapsedLabel = new() { Text = "00:00", AutoSize = true };
    private readonly DarkProgressBar _progress = new() { Minimum = 0, Maximum = 100, Value = 0, Style = DarkProgressStyle.Blocks };
    private readonly Label _statusLabel = new() { AutoSize = false };

    private readonly Label _titleLabel = new() { AutoSize = true };
    private readonly Label _subtitleLabel = new() { AutoSize = true };
    private readonly Label _gameCaptionLabel = new() { AutoSize = false };

    private readonly RichTextBox _logBox = new();
    private readonly DarkButton _logToggleButton = new();
    private readonly DarkPanel _logDrawer = new();

    private readonly DarkPanel _reportGroup = new("Result");
    private readonly TextBox _reportBox = new();
    private readonly DarkButton _openFolderButton = new();
    private readonly DarkButton _copyLogButton = new();

    private readonly DarkButton _startButton = new() { Primary = true };
    private readonly DarkButton _cancelButton = new();

    private TableLayoutPanel _root = null!;
    private TableLayoutPanel _left = null!;
    private TableLayoutPanel _drawerLayout = null!;
    private Control _divider = null!;
    private Control _gameColumn = null!;
    private DarkPanel _gameFrame = null!;
    private Font? _titleFont;

    private bool _expanding;
    private float _expandFrom;
    private float _expandTo;

    private bool _drawerExpanded;
    private bool _drawerApplied;
    private bool _logBoxStale;

    private EngineRunner? _runner;
    private InstallMode _runningMode = InstallMode.Full;
    private string _runningPath = string.Empty;
    private bool _running;
    private bool _cancelRequested;
    private bool _quietNoted;
    private bool _progressDeterminate;
    private DateTime _startedUtc;
    private DateTime _lastOutputUtc;

    public MainForm(Arguments arguments)
    {
        _arguments = arguments;

        var kind = _arguments.PreviewPath is not null ? "preview" : "install";
        _runLog = RunLog.Create(_arguments.Raw.ToArray(), kind: kind);
        _runLog.StartBanner(Application.ProductVersion, kind);

        BuildLayout();
        ApplyArguments();

        _clock.Tick += OnClockTick;
        _expandTimer.Tick += OnExpandTick;
        FormClosing += OnFormClosing;
        FormClosed += OnFormClosed;
        DpiChanged += OnDpiChanged;

        AppendAppLine("SPT Co-op Setup " + Application.ProductVersion);
        foreach (var ignored in _arguments.Ignored)
        {
            AppendAppLine("ignoring unknown argument: " + ignored);
        }

        // Deliberate breadcrumb: this is how a user finds the file after a failed install.
        AppendAppLine("run log: " + (_runLog.Path ?? "unavailable"));

        // The window starts in the single-column shape; record it so a log says which shape ran.
        AppendLayoutLine(LayoutKind.Idle);
    }

    /// <summary>Process exit code: the engine's code once a run finished, 2 for a refused run.</summary>
    public int ExitCode { get; private set; }

    // ---------------------------------------------------------------- layout surface

    /// <summary>Which shape the window is showing: one config column while idle, two while running.</summary>
    public LayoutKind LayoutState { get; private set; } = LayoutKind.Idle;

    /// <summary>Rounded width of the configuration column, in pixels, as currently laid out.</summary>
    public int ConfigColumnWidth => _root is null
        ? 0
        : (int)MathF.Round(_root.ColumnStyles[0].Width, MidpointRounding.AwayFromZero);

    /// <summary>Width the game column gets once the reveal is done; 0 while the window is idle.</summary>
    public int GameColumnWidth
    {
        get
        {
            if (LayoutState != LayoutKind.Running || _root is null)
            {
                return 0;
            }

            return (int)MathF.Round(
                LayoutStateModel.RightWidth(ContentWidth(), ConfigColumnWidth),
                MidpointRounding.AwayFromZero);
        }
    }

    /// <summary>True only while the game column is on screen with a live game inside it.</summary>
    public bool GameVisible => LayoutState == LayoutKind.Running && _gameView is { Visible: true };

    /// <summary>
    /// Switches the window between the single-column and the two-column shape. <paramref name="animate"/>
    /// false applies the final geometry at once (the headless preview path needs a settled frame);
    /// true runs the 350 ms reveal. Calling it with the shape already applied is a no-op.
    /// </summary>
    public void SetLayoutState(LayoutKind kind, bool animate)
    {
        if (IsDisposed)
        {
            return;
        }

        var changed = LayoutState != kind;
        LayoutState = kind;

        if (kind == LayoutKind.Idle)
        {
            CancelExpansion();
            ApplyIdleLayout();
            return;
        }

        if (!animate)
        {
            SnapToRunning(announce: changed);
            return;
        }

        if (_expanding)
        {
            // A resize during the reveal moves the target; it never restarts the animation.
            _expandTo = LayoutStateModel.TargetLeftWidth(ContentWidth(), DpiScale);
            return;
        }

        if (!changed)
        {
            // Already two columns: a second run must not re-animate.
            SnapToRunning(announce: false);
            return;
        }

        BeginExpansion();
    }

    // ---------------------------------------------------------------- window surface

    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModePre20H1 = 19;

    /// <summary>Paints the still plus scrim; never call the base, it would cover the image with BackColor.</summary>
    protected override void OnPaintBackground(PaintEventArgs e) => _backdrop.Paint(e.Graphics, ClientSize);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        var on = 1;
        try
        {
            if (DwmSetWindowAttribute(Handle, DwmUseImmersiveDarkMode, ref on, sizeof(int)) != 0)
            {
                // Windows 10 1809-1903 answered to attribute 19; anything older just ignores both.
                DwmSetWindowAttribute(Handle, DwmUseImmersiveDarkModePre20H1, ref on, sizeof(int));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // No DWM (or none of the attribute) here: keep the OS title bar rather than fail.
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void OnDpiChanged(object? sender, DpiChangedEventArgs e)
    {
        // The cached cover-scaled still was sized for the old DPI: drop it and repaint.
        _backdrop.Invalidate();
        UpdateLeftColumnWidth();
        Invalidate();
    }

    // ---------------------------------------------------------------- layout

    private void BuildLayout()
    {
        SuspendLayout();

        Text = "SPT Co-op Setup";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1180, 760);
        MinimumSize = new Size(1100, 720);
        AutoScaleMode = AutoScaleMode.Font;
        KeyPreview = true;

        // ProcessKeyMessage only fires while the FORM holds the message; with any child control
        // focused the game starved. A message filter sees every key before dispatch, whatever has focus.
        Application.AddMessageFilter(this);

        SetStyle(
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw,
            true);
        DoubleBuffered = true;
        BackColor = Theme.BgBase;
        ForeColor = Theme.TextPrimary;

        _root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(12),
            BackColor = Color.Transparent,
        };

        // [0] left column - Absolute, recomputed on every resize (see UpdateLeftColumnWidth).
        // [1] the divider, [2] the game gets everything that is left. Both right-hand columns are
        // collapsed to nothing while the window is idle, so the config column really fills it.
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 560f));
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0f));
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        _left = BuildLeftColumn();

        _divider = BuildDivider();
        _gameColumn = BuildGameColumn();

        _root.Controls.Add(_left, 0, 0);
        _root.Controls.Add(_divider, 1, 0);
        _root.Controls.Add(_gameColumn, 2, 0);

        Controls.Add(_root);
        _root.SizeChanged += (_, _) => UpdateLeftColumnWidth();

        ResumeLayout(true);
        ApplyIdleLayout();
        UpdateLeftColumnWidth();
        PerformLayout();
    }

    /// <summary>
    /// Single-column shape: no divider, no game column, no left margin, so the configuration really
    /// fills the window instead of leaving a strip of dead space beside it.
    /// </summary>
    private void ApplyIdleLayout()
    {
        _left.Margin = new Padding(0);
        _root.ColumnStyles[1].SizeType = SizeType.Absolute;
        _root.ColumnStyles[1].Width = 0f;
        _divider.Visible = false;
        _gameColumn.Visible = false;
        ApplyLeftWidth(LayoutStateModel.IdleLeftWidth(ContentWidth()));
    }

    /// <summary>Two-column chrome: divider back to one pixel, game column shown, left margin restored.</summary>
    private void ApplyRunningChrome()
    {
        _left.Margin = new Padding(0, 0, 12, 0);
        _root.ColumnStyles[1].SizeType = SizeType.Absolute;
        _root.ColumnStyles[1].Width = LayoutStateModel.DividerWidth;
        _divider.Visible = true;
        _gameColumn.Visible = true;
    }

    /// <summary>DPI factor used to scale the 520-640 band; 1.0 when the handle does not exist yet.</summary>
    private float DpiScale
    {
        get
        {
            var dpi = DeviceDpi > 0 ? DeviceDpi : 96;
            return dpi / 96f;
        }
    }

    /// <summary>Content width inside the window padding, in logical pixels.</summary>
    private float ContentWidth()
    {
        if (_root is null)
        {
            return LayoutStateModel.ContentWidth(ClientSize.Width, 0);
        }

        // Before the handle exists the root panel is still 0 wide: fall back to the form so the
        // launch breadcrumb records a real width instead of "1".
        var width = _root.ClientSize.Width > 0 ? _root.ClientSize.Width : ClientSize.Width;
        return LayoutStateModel.ContentWidth(width, _root.Padding.Horizontal);
    }

    /// <summary>Writes a column width, skipping the layout churn when it is already there.</summary>
    private void ApplyLeftWidth(float width)
    {
        if (_root is null)
        {
            return;
        }

        var column = _root.ColumnStyles[0];
        if (column.SizeType == SizeType.Absolute && Math.Abs(column.Width - width) < 0.5f)
        {
            return;
        }

        column.SizeType = SizeType.Absolute;
        column.Width = width;
    }

    /// <summary>
    /// Recomputes the configuration column for the current shape and size: full width while idle,
    /// the clamped running width otherwise, or just a moved target while the reveal is in flight.
    /// </summary>
    private void UpdateLeftColumnWidth()
    {
        if (_root is null)
        {
            return;
        }

        if (LayoutState == LayoutKind.Idle)
        {
            ApplyLeftWidth(LayoutStateModel.IdleLeftWidth(ContentWidth()));
            return;
        }

        if (_expanding)
        {
            // A resize mid-reveal must not fight the timer: move where it is heading instead.
            _expandTo = LayoutStateModel.TargetLeftWidth(ContentWidth(), DpiScale);
            return;
        }

        ApplyLeftWidth(LayoutStateModel.TargetLeftWidth(ContentWidth(), DpiScale));
    }

    private static Control BuildDivider() => new Panel
    {
        Dock = DockStyle.Fill,
        Margin = Padding.Empty,
        BackColor = Theme.Border,
    };

    // ---------------------------------------------------------------- idle -> running reveal

    /// <summary>Starts the 350 ms reveal from the current (idle) width to the running target.</summary>
    private void BeginExpansion()
    {
        EnsureGameCreated();
        ApplyRunningChrome();

        // Showing the column fires GameView.OnVisibleChanged, which would start the simulation
        // while the window is still moving; the finishing tick owns that.
        _gameView?.StopLoop();

        _expandFrom = _root.ColumnStyles[0].Width;
        _expandTo = LayoutStateModel.TargetLeftWidth(ContentWidth(), DpiScale);

        _expanding = true;
        _expandClock.Restart();
        _expandTimer.Start();
    }

    /// <summary>
    /// One reveal frame: read the stopwatch, ease the column down and let the table lay itself out.
    /// No control is created or destroyed here and nothing blocks the UI thread.
    /// </summary>
    private void OnExpandTick(object? sender, EventArgs e)
    {
        if (!_expanding)
        {
            _expandTimer.Stop();
            return;
        }

        var elapsed = (int)_expandClock.ElapsedMilliseconds;
        if (LayoutStateModel.AnimationComplete(elapsed))
        {
            FinishExpansion(announce: true);
            return;
        }

        ApplyLeftWidth(LayoutStateModel.LeftWidthAt(_expandFrom, _expandTo, elapsed));
    }

    /// <summary>
    /// Settles the reveal: snap to the target, stop the timer and only now start the simulation,
    /// so the game never ticks while it is still off the settled layout.
    /// </summary>
    private void FinishExpansion(bool announce)
    {
        CancelExpansion();
        ApplyLeftWidth(LayoutStateModel.TargetLeftWidth(ContentWidth(), DpiScale));
        StartGameLoop();

        if (announce)
        {
            AppendLayoutLine(LayoutKind.Running);
        }
    }

    /// <summary>Applies the final two-column geometry at once, with no animation.</summary>
    private void SnapToRunning(bool announce)
    {
        CancelExpansion();
        EnsureGameCreated();
        ApplyRunningChrome();
        FinishExpansion(announce);
    }

    /// <summary>Stops the reveal timer in every exit path; safe to call when nothing is running.</summary>
    private void CancelExpansion()
    {
        _expandTimer.Stop();
        _expandClock.Reset();
        _expanding = false;
    }

    private void StartGameLoop() => _gameView?.StartLoop();

    /// <summary>
    /// Builds the game lazily. While idle there is no model and no timer at all, which is what
    /// keeps an idle window at zero game CPU.
    /// </summary>
    private void EnsureGameCreated()
    {
        if (_gameView is not null)
        {
            return;
        }

        var view = new GameView(new GameModel(
            _arguments.PreviewPath is not null ? GameModel.PreviewSeed : Environment.TickCount,
            480,
            600))
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };

        _gameView = view;
        _gameFrame.Controls.Add(view);

        // If the column is already on screen the add above made the control visible, and
        // OnVisibleChanged started the loop; the reveal decides when it may actually run.
        view.StopLoop();
    }

    /// <summary>Records the current shape in the pane and in the run log, so a bug report carries it.</summary>
    private void AppendLayoutLine(LayoutKind kind)
    {
        var line = LayoutStateModel.LogLine(kind, ConfigColumnWidth, GameColumnWidth);
        AppendAppLine(line);
        _runLog.Info(line);
    }

    private TableLayoutPanel BuildLeftColumn()
    {
        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 8,
            Padding = Padding.Empty,
            Margin = new Padding(0, 0, 12, 0),
            BackColor = Color.Transparent,
        };

        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // 0 header block
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // 1 install mode
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // 2 target folder
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // 3 progress
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // 4 status line
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // 5 actions
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));   // 6 spacer (slack, not a gap)
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // 7 log drawer (collapsed)

        left.Controls.Add(BuildHeader(), 0, 0);
        left.Controls.Add(BuildModeGroup(), 0, 1);
        left.Controls.Add(BuildPathGroup(), 0, 2);
        left.Controls.Add(BuildProgressGroup(), 0, 3);
        left.Controls.Add(BuildStatusLine(), 0, 4);
        left.Controls.Add(BuildActionBar(), 0, 5);
        left.Controls.Add(BuildSpacer(), 0, 6);
        left.Controls.Add(BuildLogDrawer(), 0, 7);

        return left;
    }

    private Control BuildHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 0, 0, 10),
            BackColor = Color.Transparent,
        };

        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _titleFont = new Font(Font.FontFamily, 15f, FontStyle.Bold);

        _titleLabel.Text = "SPT CO-OP SETUP";
        _titleLabel.Font = _titleFont;
        _titleLabel.ForeColor = Theme.TextPrimary;
        _titleLabel.BackColor = Color.Transparent;
        _titleLabel.Anchor = AnchorStyles.Left;
        _titleLabel.Margin = new Padding(0, 0, 0, 2);

        _subtitleLabel.Text = "Installer for SPT + Fika co-op";
        _subtitleLabel.ForeColor = Theme.TextSecondary;
        _subtitleLabel.BackColor = Color.Transparent;
        _subtitleLabel.Anchor = AnchorStyles.Left;
        _subtitleLabel.Margin = new Padding(0);

        // Wrap the subtitle to the column width instead of clipping it.
        header.Layout += (_, _) => _subtitleLabel.MaximumSize = new Size(Math.Max(120, header.ClientSize.Width - 2), 0);

        header.Controls.Add(_titleLabel, 0, 0);
        header.Controls.Add(_subtitleLabel, 0, 1);
        return header;
    }

    private Control BuildSpacer() => new Panel
    {
        Dock = DockStyle.Fill,
        Margin = Padding.Empty,
        BackColor = Color.Transparent,
    };

    private Control BuildStatusLine()
    {
        _statusLabel.Text = "Pick a mode and a folder - Start install validates the target first.";
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.ForeColor = Theme.TextSecondary;
        _statusLabel.BackColor = Color.Transparent;
        _statusLabel.Margin = new Padding(0, 0, 0, 10);
        _statusLabel.AutoEllipsis = true;

        return _statusLabel;
    }

    private DarkPanel BuildModeGroup()
    {
        var group = NewDarkPanel("Install mode");

        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.Surface,
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        for (var row = 0; row < 3; row++)
        {
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        stack.Controls.Add(_modeFull, 0, 0);
        stack.Controls.Add(_modeMods, 0, 1);
        stack.Controls.Add(_modeUpdate, 0, 2);

        group.Controls.Add(stack);
        return group;
    }

    private DarkPanel BuildPathGroup()
    {
        var group = NewDarkPanel("Target folder");

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.Surface,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // folder row
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // validation slot (collapses when hidden)

        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            BackColor = Theme.Surface,
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, 29f));

        // DarkField owns the cell and docks _pathBox inside itself over the raised fill.
        var pathField = new DarkField(_pathBox)
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 8, 0),
        };

        _browseButton.AutoSize = true;
        _browseButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _browseButton.MinimumSize = new Size(100, 27);
        _browseButton.Margin = new Padding(0);
        _browseButton.Anchor = AnchorStyles.Right;
        _browseButton.Click += OnBrowseClicked;

        row.Controls.Add(pathField, 0, 0);
        row.Controls.Add(_browseButton, 1, 0);

        // The pre-flight refusal slot: replaces the modal for empty/bad/TargetGuard paths.
        _validationLabel.ForeColor = Theme.TextSecondary;
        _validationLabel.BackColor = Theme.Surface;
        _validationLabel.Visible = false;
        _validationLabel.AutoEllipsis = true;
        _validationLabel.Margin = new Padding(0, 4, 0, 0);

        _pathBox.TextChanged += OnPathTextChanged;

        layout.Controls.Add(row, 0, 0);
        layout.Controls.Add(_validationLabel, 0, 1);

        group.Controls.Add(layout);
        return group;
    }

    private DarkPanel BuildProgressGroup()
    {
        var group = NewDarkPanel("Progress");

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 3,
            BackColor = Theme.Surface,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // phase + percentage
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22f)); // bar
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // elapsed

        _phaseLabel.Text = "Ready - pick a mode and a folder, then press Start install.";
        _phaseLabel.ForeColor = Theme.TextPrimary;
        _phaseLabel.BackColor = Theme.Surface;
        _phaseLabel.Anchor = AnchorStyles.Left;
        _phaseLabel.Margin = new Padding(0, 0, 8, 6);

        _percentLabel.ForeColor = Theme.TextPrimary;
        _percentLabel.BackColor = Theme.Surface;
        _percentLabel.Anchor = AnchorStyles.Right;
        _percentLabel.Margin = new Padding(0, 0, 0, 6);
        _percentLabel.Font = new Font(Font, FontStyle.Bold);

        _progress.Dock = DockStyle.Fill;
        _progress.Margin = new Padding(0);

        _elapsedLabel.ForeColor = Theme.TextPrimary;
        _elapsedLabel.BackColor = Theme.Surface;
        _elapsedLabel.Anchor = AnchorStyles.Right;
        _elapsedLabel.Margin = new Padding(0, 4, 0, 0);
        _elapsedLabel.Font = new Font(Font, FontStyle.Bold);

        layout.Controls.Add(_phaseLabel, 0, 0);
        layout.Controls.Add(_percentLabel, 1, 0);
        layout.SetColumnSpan(_progress, 2);
        layout.Controls.Add(_progress, 0, 1);
        layout.SetColumnSpan(_elapsedLabel, 2);
        layout.Controls.Add(_elapsedLabel, 0, 2);

        group.Controls.Add(layout);

        UpdatePercentLabel();
        return group;
    }

    private Control BuildLogDrawer()
    {
        _logDrawer.AutoSize = true;
        _logDrawer.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _logDrawer.Dock = DockStyle.Fill;
        _logDrawer.Margin = new Padding(0);

        _drawerLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            BackColor = Theme.Surface,
        };
        _drawerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _drawerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f)); // header
        _drawerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // log box
        _drawerLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // result pane

        _logBox.Dock = DockStyle.Fill;
        _logBox.ReadOnly = true;
        _logBox.WordWrap = false;
        _logBox.DetectUrls = false;
        _logBox.HideSelection = false;
        _logBox.ScrollBars = RichTextBoxScrollBars.Both;
        _logBox.BackColor = Theme.Surface;
        _logBox.ForeColor = Theme.TextPrimary;
        _logBox.BorderStyle = BorderStyle.None;
        _logBox.Font = Theme.MonoFont;
        _logBox.Margin = new Padding(0);
        _logBox.Visible = false; // collapsed by default

        _drawerLayout.Controls.Add(BuildDrawerHeader(), 0, 0);
        _drawerLayout.Controls.Add(_logBox, 0, 1);
        _drawerLayout.Controls.Add(BuildReportGroup(), 0, 2);

        _logDrawer.Controls.Add(_drawerLayout);
        return _logDrawer;
    }

    private Control BuildDrawerHeader()
    {
        _logToggleButton.Text = "▸ Show log";
        _logToggleButton.AutoSize = false;
        _logToggleButton.Size = new Size(150, 26);
        _logToggleButton.Margin = new Padding(0);
        _logToggleButton.Anchor = AnchorStyles.Left;
        _logToggleButton.Click += OnLogToggleClicked;

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 1,
            Margin = Padding.Empty,
            BackColor = Theme.Surface,
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        header.Controls.Add(_logToggleButton, 0, 0);

        return header;
    }

    private DarkPanel BuildReportGroup()
    {
        _reportGroup.AutoSize = true;
        _reportGroup.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _reportGroup.Dock = DockStyle.Fill;
        _reportGroup.Visible = false;
        _reportGroup.Margin = new Padding(0, 8, 0, 0);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.Surface,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 130f));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _reportBox.Multiline = true;
        _reportBox.ReadOnly = true;
        _reportBox.WordWrap = false;
        _reportBox.ScrollBars = ScrollBars.Both;
        _reportBox.Dock = DockStyle.Fill;
        _reportBox.BackColor = Theme.Surface;
        _reportBox.ForeColor = Theme.TextPrimary;
        _reportBox.BorderStyle = BorderStyle.None;
        _reportBox.Font = Theme.MonoFont;
        _reportBox.Margin = new Padding(0);

        _openFolderButton.Text = "Open install folder";
        _openFolderButton.AutoSize = true;
        _openFolderButton.Enabled = false;
        _openFolderButton.Click += OnOpenFolderClicked;

        _copyLogButton.Text = "Copy log";
        _copyLogButton.AutoSize = true;
        _copyLogButton.Click += OnCopyLogClicked;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 6, 0, 0),
            BackColor = Theme.Surface,
        };
        buttons.Controls.Add(_openFolderButton);
        buttons.Controls.Add(_copyLogButton);

        layout.Controls.Add(_reportBox, 0, 0);
        layout.Controls.Add(buttons, 0, 1);

        _reportGroup.Controls.Add(layout);
        return _reportGroup;
    }

    private Control BuildActionBar()
    {
        _startButton.Text = "Start install";
        _startButton.AutoSize = false;
        _startButton.Size = new Size(150, 34);
        _startButton.Margin = new Padding(0);
        _startButton.Click += OnStartClicked;

        _cancelButton.Text = "Cancel";
        _cancelButton.AutoSize = false;
        _cancelButton.Size = new Size(110, 34);
        _cancelButton.Enabled = false;
        _cancelButton.Margin = new Padding(0, 0, 8, 0);
        _cancelButton.Click += OnCancelClicked;

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0, 8, 0, 8),
            BackColor = Color.Transparent,
        };
        bar.Controls.Add(_startButton);
        bar.Controls.Add(_cancelButton);

        AcceptButton = _startButton;
        return bar;
    }

    private Control BuildGameColumn()
    {
        var column = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = Padding.Empty,
            Margin = new Padding(12, 0, 0, 0),
            BackColor = Color.Transparent,
        };
        column.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        column.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f)); // caption
        column.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // game

        _gameCaptionLabel.Text = "TAKE A BREAK — arrows/A-D to move, SPACE to pause";
        _gameCaptionLabel.Dock = DockStyle.Fill;
        _gameCaptionLabel.ForeColor = Theme.TextSecondary;
        _gameCaptionLabel.BackColor = Color.Transparent;
        _gameCaptionLabel.TextAlign = ContentAlignment.MiddleLeft;
        _gameCaptionLabel.Margin = new Padding(0, 0, 0, 8);

        // One rounded panel: the game canvas draws its own rounded frame inside this hairline.
        // The frame exists from launch; the game is added to it only when the window reveals it.
        _gameFrame = new DarkPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Padding = new Padding(1),
            Margin = Padding.Empty,
        };

        column.Controls.Add(_gameCaptionLabel, 0, 0);
        column.Controls.Add(_gameFrame, 0, 1);
        return column;
    }

    private static DarkPanel NewDarkPanel(string title) => new(title)
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Fill,
        Margin = new Padding(0, 0, 0, 10),
    };

    // ---------------------------------------------------------------- log drawer

    private void OnLogToggleClicked(object? sender, EventArgs e)
    {
        SetLogDrawerExpanded(!_drawerExpanded);
    }

    /// <summary>
    /// Collapsed: the drawer is only its header and the spacer row above the buttons takes the
    /// slack. Expanded: the drawer owns 35% of the column and the spacer drops to zero.
    /// </summary>
    private void SetLogDrawerExpanded(bool expanded)
    {
        if (_drawerApplied && _drawerExpanded == expanded)
        {
            return;
        }

        _drawerExpanded = expanded;
        _drawerApplied = true;

        SuspendLayout();
        _logDrawer.SuspendLayout();
        _drawerLayout.SuspendLayout();

        try
        {
            if (expanded)
            {
                _logBox.Visible = true;
                RefreshLogBox();

                _drawerLayout.AutoSize = false;
                _logDrawer.AutoSize = false;
                _logDrawer.Dock = DockStyle.Fill;

                SetLeftRow(SpacerRow, SizeType.Absolute, 0f);
                SetLeftRow(DrawerRow, SizeType.Percent, 35f);
            }
            else
            {
                _logBox.Visible = false;

                _drawerLayout.AutoSize = true;
                _logDrawer.AutoSize = true;
                _logDrawer.AutoSizeMode = AutoSizeMode.GrowAndShrink;

                SetLeftRow(DrawerRow, SizeType.AutoSize, 0f);
                SetLeftRow(SpacerRow, SizeType.Percent, 100f);
            }

            _logToggleButton.Text = expanded ? "▾ Hide log" : "▸ Show log";
        }
        finally
        {
            _drawerLayout.ResumeLayout(true);
            _logDrawer.ResumeLayout(true);
            ResumeLayout(true);
        }

        PerformLayout();
    }

    private void SetLeftRow(int index, SizeType type, float height)
    {
        // The style collection has no mutable SizeType, so the row is replaced wholesale.
        _left.RowStyles[index] = new RowStyle(type, height);
    }

    /// <summary>Replays everything the collapsed box missed; cheap because the pane is capped.</summary>
    private void RefreshLogBox()
    {
        if (!_logBoxStale && _logBox.TextLength > 0)
        {
            return;
        }

        _logBox.Text = string.Join("\n", _visibleLines);
        _logBoxStale = false;

        if (_logBox.Focused)
        {
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.SelectionLength = 0;
            _logBox.ScrollToCaret();
        }
    }

    // ---------------------------------------------------------------- arguments / state

    private void ApplyArguments()
    {
        if (_arguments.TargetPath is not null)
        {
            _pathBox.Text = _arguments.TargetPath;
        }

        if (_arguments.Mode is { } mode)
        {
            _modeFull.Checked = mode == InstallMode.Full;
            _modeMods.Checked = mode == InstallMode.Mods;
            _modeUpdate.Checked = mode == InstallMode.Update;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // The preview host sets the size and the shape after Show(), so the shape asked for on the
        // command line has to be applied here, with no animation: a render wants a settled frame.
        if (_arguments.PreviewPath is not null)
        {
            SetLayoutState(_arguments.PreviewState ?? LayoutKind.Running, animate: false);

            if (LayoutState == LayoutKind.Running)
            {
                // Deterministic mid-play frame; the preview must not animate or tick.
                EnsureGameCreated();
                _gameView!.PrimeForPreview();
                _gameView!.StopLoop();
            }
        }

        if (_arguments.PreviewPath is null && _arguments.ShouldStartImmediately)
        {
            // Let the window paint first - the engine's first lines arrive fast.
            BeginInvoke(new Action(() => OnStartClicked(this, EventArgs.Empty)));
        }
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _gameView?.SetWindowActive(true);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        _gameView?.SetWindowActive(false);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);

        UpdateLeftColumnWidth();

        // Minimising must pause the simulation, not keep it running off-screen.
        _gameView?.SetWindowActive(WindowState != FormWindowState.Minimized);
    }

    // ---------------------------------------------------------------- key routing

    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    /// <summary>
    /// Forwards arrow/A/D/SPACE presses and releases to the game, but only while no text-entry
    /// control has focus. Runs before the dialog-key machinery, so the game wins over focus
    /// navigation - except for Tab/Escape/Enter, which always keep their window meaning.
    /// </summary>
    public bool PreFilterMessage(ref Message m)
    {
        var isKeyMessage = m.Msg is WmKeyDown or WmKeyUp or WmSysKeyDown or WmSysKeyUp;

        if (!isKeyMessage || _gameView is not { Visible: true } || IsTextEntryFocused())
        {
            return false;
        }

        var key = (Keys)((int)m.WParam & (int)Keys.KeyCode);
        var down = m.Msg is WmKeyDown or WmSysKeyDown;

        return key is not (Keys.Tab or Keys.Escape or Keys.Enter) && _gameView!.HandleKey(key, down);
    }

    /// <summary>True while the folder box, the log pane or the report pane owns the keyboard.</summary>
    private bool IsTextEntryFocused()
    {
        var active = ActiveControl;
        for (var depth = 0; depth < 8 && active is ContainerControl container && container.ActiveControl is { } inner; depth++)
        {
            active = inner;
        }

        for (var control = active; control is not null; control = control.Parent)
        {
            if (control is TextBoxBase
                || ReferenceEquals(control, _pathBox)
                || ReferenceEquals(control, _logBox)
                || ReferenceEquals(control, _reportBox))
            {
                // Only a live input eats the game's keys: a disabled one cannot hold focus, and a
                // stale ActiveControl must not make the game unplayable while the engine runs.
                return control.Enabled && control.Focused;
            }
        }

        return false;
    }

    // ---------------------------------------------------------------- mode / folder

    private InstallMode SelectedMode()
    {
        if (_modeMods.Checked)
        {
            return InstallMode.Mods;
        }

        return _modeUpdate.Checked ? InstallMode.Update : InstallMode.Full;
    }

    private void OnBrowseClicked(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Pick the SPT install folder (the one that contains SPT_Runtime)",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };

        var current = _pathBox.Text.Trim();
        if (current.Length > 0 && Directory.Exists(current))
        {
            dialog.SelectedPath = current;
        }

        if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            _pathBox.Text = dialog.SelectedPath;
        }
    }

    private void OnPathTextChanged(object? sender, EventArgs e) => ShowValidationMessage(string.Empty);

    private void ShowValidationMessage(string message)
    {
        var text = FirstLine(message);
        _validationLabel.Text = text;
        _validationLabel.Visible = text.Length > 0;
    }

    // ---------------------------------------------------------------- run

    private void OnStartClicked(object? sender, EventArgs e)
    {
        if (_running)
        {
            return;
        }

        try
        {
            StartInstall();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetRunningState(false);
            _phaseLabel.Text = "Failed.";
            ReportFailure("could not start the engine: " + ex.Message, blocking: true);
        }
    }

    private void StartInstall()
    {
        var mode = SelectedMode();
        var typed = _pathBox.Text.Trim().Trim('"');
        if (typed.Length == 0)
        {
            RefusePreFlight("Pick a target folder first.");
            return;
        }

        string path;
        try
        {
            path = Path.GetFullPath(typed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            RefusePreFlight("'" + typed + "' is not a valid folder path.");
            return;
        }

        if (!TargetGuard.TryValidate(mode, path, out var guardMessage))
        {
            RefusePreFlight(guardMessage);
            return;
        }

        path = _pathBox.Text = path;
        ShowValidationMessage(string.Empty);

        if (Elevation.NeedsAdmin(mode, path) && !Elevation.IsElevated())
        {
            AppendAppLine("administrator rights needed - relaunching elevated (answer Yes on the UAC prompt)");
            var relaunch = new List<string> { "--mode", Arguments.ModeArgument(mode), "--path", path, "--auto" };
            if (!Elevation.RelaunchElevated(relaunch))
            {
                ReportFailure(
                    "Administrator rights are required for this run and the elevation prompt was declined.\r\n\r\nNothing was changed.",
                    blocking: true);
                return;
            }

            Close();
            return;
        }

        StartRun(mode, path);
    }

    /// <summary>
    /// A refusal before anything ran: the message lands in the slot under the folder box instead
    /// of a modal, and still produces the FAILED log line plus exit code 2.
    /// </summary>
    private void RefusePreFlight(string message)
    {
        ShowValidationMessage(message);
        ReportFailure(message, blocking: false);
    }

    private void StartRun(InstallMode mode, string path)
    {
        _running = true;
        _runningMode = mode;
        _runningPath = path;
        _cancelRequested = false;
        _quietNoted = false;
        _progressDeterminate = false;
        _startedUtc = DateTime.UtcNow;
        _lastOutputUtc = _startedUtc;

        _reportGroup.Visible = false;
        _reportBox.Clear();
        _elapsedLabel.Text = "00:00";
        _progress.Style = DarkProgressStyle.Marquee;
        _progress.MarqueeAnimationSpeed = 30;
        _progress.Value = 0;
        _phaseLabel.Text = Arguments.ModeTitle(mode) + " running...";
        ShowValidationMessage(string.Empty);
        UpdatePercentLabel();

        SetRunningState(true);
        AppendAppLine("starting " + Arguments.ModeTitle(mode).ToLowerInvariant() + " in " + path);
        _runLog.Info("mode=" + Arguments.ModeTitle(mode) + " target=" + path);

        var runner = new EngineRunner(mode, path);
        runner.LineReceived += OnEngineLine;
        runner.Finished += OnEngineFinished;
        _runner = runner;

        try
        {
            runner.Start();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            runner.LineReceived -= OnEngineLine;
            runner.Finished -= OnEngineFinished;
            _runner = null;
            runner.Dispose();
            SetRunningState(false);
            _progress.Style = DarkProgressStyle.Blocks;
            _phaseLabel.Text = "Failed.";
            UpdatePercentLabel();
            ReportFailure("could not start the engine: " + ex.Message, blocking: true);
            return;
        }

        _clock.Start();

        // The engine is actually running now (not merely requested): make room for the game.
        SetLayoutState(LayoutKind.Running, animate: true);
    }

    private void OnCancelClicked(object? sender, EventArgs e)
    {
        if (!_running || _runner is null)
        {
            return;
        }

        _cancelRequested = true;
        _cancelButton.Enabled = false;
        _phaseLabel.Text = "Cancelling - stopping the engine...";
        AppendAppLine("cancel requested - waiting up to 3s for the engine, then killing it");
        _ = _runner.CancelAsync();
    }

    private void SetRunningState(bool running)
    {
        _running = running;
        _startButton.Enabled = !running;
        _cancelButton.Enabled = running;
        _modeFull.Enabled = !running;
        _modeMods.Enabled = !running;
        _modeUpdate.Enabled = !running;
        _pathBox.Enabled = !running;
        _browseButton.Enabled = !running;
    }

    // ---------------------------------------------------------------- progress

    /// <summary>"42 %" while determinate, an em dash while the bar is still marquee.</summary>
    private void UpdatePercentLabel()
    {
        if (_progress.Style == DarkProgressStyle.Marquee)
        {
            _percentLabel.Text = "—";
            return;
        }

        var span = _progress.Maximum - _progress.Minimum;
        var percent = span <= 0
            ? 0
            : (int)Math.Round(100d * (_progress.Value - _progress.Minimum) / span, MidpointRounding.AwayFromZero);

        percent = Math.Clamp(percent, 0, 100);
        _percentLabel.Text = percent.ToString(CultureInfo.InvariantCulture) + " %";
    }

    // ---------------------------------------------------------------- engine callbacks

    private void OnEngineLine(string line)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action<string>(OnEngineLine), line);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // The window is closing; drop the line.
            }

            return;
        }

        _lastOutputUtc = DateTime.UtcNow;
        _quietNoted = false;
        AppendLine(line, "ENGINE");

        var match = ModProgressPattern.Match(line);
        if (!match.Success
            || !int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var done)
            || !int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var total)
            || total <= 0)
        {
            return;
        }

        done = Math.Clamp(done, 0, total);
        if (!_progressDeterminate)
        {
            _progressDeterminate = true;
            _progress.Style = DarkProgressStyle.Continuous;
            _progress.Maximum = total;
        }
        else if (_progress.Maximum != total)
        {
            if (_progress.Value > total)
            {
                _progress.Value = total;
            }

            _progress.Maximum = total;
        }

        _progress.Value = Math.Clamp(done, _progress.Minimum, _progress.Maximum);
        _phaseLabel.Text = "Installing mods: " + done.ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture);
        UpdatePercentLabel();
    }

    private void OnEngineFinished(int exitCode)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action<int>(OnEngineFinished), exitCode);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // The window is closing; nothing to report into.
            }

            return;
        }

        // A run can end before the reveal has finished: land on the final two-column layout rather
        // than let the timer keep easing a window that already reports a result.
        if (_expanding)
        {
            SnapToRunning(announce: true);
        }

        var elapsed = DateTime.UtcNow - _startedUtc;
        _clock.Stop();
        _elapsedLabel.Text = FormatElapsed(elapsed);

        var runner = _runner;
        _runner = null;
        if (runner is not null)
        {
            runner.LineReceived -= OnEngineLine;
            runner.Finished -= OnEngineFinished;
            runner.Cleanup();
            runner.Dispose();
        }

        SetRunningState(false);
        _progress.Style = DarkProgressStyle.Blocks;
        if (_progressDeterminate)
        {
            _progress.Value = _cancelRequested || exitCode != 0 ? _progress.Minimum : _progress.Maximum;
        }

        UpdatePercentLabel();

        _phaseLabel.Text = _cancelRequested ? "Cancelled." : exitCode == 0 ? "Finished." : "Failed.";

        _runLog.Info(
            "engine exit code " + exitCode.ToString(CultureInfo.InvariantCulture)
            + " elapsed " + FormatElapsed(elapsed)
            + " cancelled " + (_cancelRequested ? "true" : "false"));

        ShowReport(_runningMode, _runningPath, exitCode, elapsed);
        ExitCode = exitCode;

        if (_cancelRequested)
        {
            AppendAppLine("run cancelled after " + FormatElapsed(elapsed) + " - the report pane has the details");
            return;
        }

        if (exitCode != 0)
        {
            ReportFailure(BuildEngineFailure(_runningMode, _runningPath, exitCode), blocking: true);
        }
    }

    private void OnClockTick(object? sender, EventArgs e)
    {
        if (!_running)
        {
            return;
        }

        _elapsedLabel.Text = FormatElapsed(DateTime.UtcNow - _startedUtc);

        if (_quietNoted || DateTime.UtcNow - _lastOutputUtc < TimeSpan.FromSeconds(QuietWindowSeconds))
        {
            return;
        }

        // A 20 minute robocopy prints nothing: say something so it does not look hung.
        _quietNoted = true;
        AppendLine(QuietMarker, "APP");
    }

    // ---------------------------------------------------------------- report

    private void ShowReport(InstallMode mode, string path, int exitCode, TimeSpan elapsed)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Mode:      " + Arguments.ModeTitle(mode));
        builder.AppendLine("Target:    " + path);
        builder.AppendLine("Result:    " + (_cancelRequested ? "cancelled" : exitCode == 0 ? "success" : "failed"));
        builder.AppendLine("Exit code: " + exitCode.ToString(CultureInfo.InvariantCulture));
        builder.Append("Elapsed:   ").AppendLine(FormatElapsed(elapsed));
        builder.AppendLine();
        builder.AppendLine("Engine's last " + ReportTailLines.ToString(CultureInfo.InvariantCulture) + " lines:");

        var skip = Math.Max(0, _logLines.Count - ReportTailLines);
        for (var index = skip; index < _logLines.Count; index++)
        {
            builder.Append("  ").AppendLine(_logLines[index]);
        }

        _reportBox.Text = builder.ToString();
        _reportBox.SelectionStart = 0;
        _reportBox.SelectionLength = 0;

        bool folderExists;
        try
        {
            folderExists = Directory.Exists(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            folderExists = false;
        }

        _openFolderButton.Enabled = folderExists;
        _reportGroup.Visible = true;

        // The result lives in the drawer, so a finished run opens it.
        SetLogDrawerExpanded(true);
    }

    private void ReportFailure(string message, bool blocking)
    {
        AppendAppLine("FAILED: " + FirstLine(message));
        _phaseLabel.Text = "Failed.";
        if (ExitCode == 0)
        {
            ExitCode = 2;
        }

        // --auto means unattended: log it and stay open, never sit on a modal dialog.
        if (!blocking || _arguments.Auto || IsDisposed)
        {
            return;
        }

        MessageBox.Show(this, message, "SPT Co-op Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private string BuildEngineFailure(InstallMode mode, string path, int exitCode)
    {
        var detail = FindEngineDiagnostic()
            ?? ("engine exited with code " + exitCode.ToString(CultureInfo.InvariantCulture));

        var builder = new StringBuilder();
        builder.AppendLine("The engine reported a failure.");
        builder.AppendLine();
        builder.AppendLine(detail);
        builder.AppendLine();
        builder.AppendLine("Mode:      " + Arguments.ModeTitle(mode));
        builder.AppendLine("Target:    " + path);
        builder.AppendLine("Exit code: " + exitCode.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine();
        builder.Append("Full log in the report pane - use \"Copy log\" to share it.");
        return builder.ToString();
    }

    /// <summary>First engine line that names the failure ("SETUP FAILED: ..." / "DIE: ...").</summary>
    private string? FindEngineDiagnostic()
    {
        foreach (var line in _logLines)
        {
            var text = line.StartsWith(EngineRunner.StderrPrefix, StringComparison.Ordinal)
                ? line[EngineRunner.StderrPrefix.Length..]
                : line;
            text = text.TrimStart();
            if (text.StartsWith("SETUP FAILED:", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("DIE:", StringComparison.OrdinalIgnoreCase))
            {
                return text;
            }
        }

        return null;
    }

    private void OnOpenFolderClicked(object? sender, EventArgs e)
    {
        try
        {
            if (!Directory.Exists(_runningPath))
            {
                return;
            }

            Process.Start("explorer.exe", "\"" + _runningPath + "\"");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            ReportFailure("could not open " + _runningPath + ": " + ex.Message, blocking: false);
        }
    }

    private void OnCopyLogClicked(object? sender, EventArgs e)
    {
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, _logLines));
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or ArgumentException)
        {
            ReportFailure("could not copy the log to the clipboard: " + ex.Message, blocking: false);
        }
    }

    // ---------------------------------------------------------------- log plumbing

    private void AppendAppLine(string text) => AppendLine("[app] " + text, "APP");

    /// <summary>
    /// Adds one line to the pane and to the run log with the same text. Never touches focus: the
    /// caret only moves while the pane is already visible and focused by the user.
    /// </summary>
    private void AppendLine(string text, string level)
    {
        // EngineRunner labels its own bookkeeping lines "[app] ...": those stay APP in the file.
        var effective = text.StartsWith("[app] ", StringComparison.Ordinal) ? "APP" : level;

        if (string.Equals(effective, "ENGINE", StringComparison.Ordinal))
        {
            _runLog.Engine(text);
        }
        else
        {
            _runLog.Line(effective, text);
        }

        _logLines.Add(text);
        _visibleLines.Add(text);

        var trimmed = false;
        while (_visibleLines.Count > LogLineCap)
        {
            _visibleLines.RemoveRange(0, Math.Min(LogTrimChunk, _visibleLines.Count));
            trimmed = true;
        }

        // Collapsed drawer: the pane is stale until the user opens it (keeps the callback cheap).
        if (!_logBox.Visible)
        {
            _logBoxStale = true;
            return;
        }

        if (trimmed)
        {
            _logBox.Text = string.Join("\n", _visibleLines);
        }
        else
        {
            _logBox.AppendText(text + "\n");
        }

        if (_logBox.Focused)
        {
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.SelectionLength = 0;
            _logBox.ScrollToCaret();
        }
    }

    private static string FormatElapsed(TimeSpan elapsed)
        => ((int)elapsed.TotalMinutes).ToString("00", CultureInfo.InvariantCulture)
            + ":"
            + elapsed.Seconds.ToString("00", CultureInfo.InvariantCulture);

    private static string FirstLine(string text)
    {
        var newline = text.IndexOfAny(NewLineChars);
        return (newline < 0 ? text : text[..newline]).Trim();
    }

    // ---------------------------------------------------------------- shutdown

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        var runner = _runner;
        if (!_running || runner is null)
        {
            return;
        }

        if (!_arguments.Auto && e.CloseReason is CloseReason.UserClosing)
        {
            var answer = MessageBox.Show(
                this,
                "The installer is still running.\r\n\r\nCancel it and quit?",
                "SPT Co-op Setup",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        _cancelRequested = true;
        runner.Terminate();
        runner.Cleanup();
    }

    private void OnFormClosed(object? sender, FormClosedEventArgs e)
    {
        // Stop the reveal before the window goes: a queued tick must never touch a dead form.
        // The timer object itself is disposed by Dispose(bool).
        CancelExpansion();
        _clock.Stop();
        _clock.Dispose();
        _runner?.Dispose();
        _runner = null;
        _runLog.Info("window closed exit code " + ExitCode.ToString(CultureInfo.InvariantCulture));
        _runLog.Dispose();
        _backdrop.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Application.RemoveMessageFilter(this);
            CancelExpansion();
            _expandTimer.Tick -= OnExpandTick;
            _expandTimer.Dispose();
            _clock.Dispose();
            _runner?.Dispose();
            _runner = null;
            _backdrop.Dispose();

            _titleFont?.Dispose();
            _titleFont = null;

            // Idempotent: whichever of OnFormClosed/Dispose runs second is a no-op.
            _runLog.Dispose();
        }

        base.Dispose(disposing);
    }
}
