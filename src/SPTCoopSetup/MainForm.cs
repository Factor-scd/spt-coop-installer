using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace SPTCoopSetup;

/// <summary>The installer window: mode + folder, live progress, live log, and a final report.</summary>
internal sealed class MainForm : Form
{
    private const int LogLineCap = 5000;
    private const int LogTrimChunk = 1000;
    private const int QuietWindowSeconds = 45;
    private const int ReportTailLines = 40;

    private const string QuietMarker = "(-- still working, engine quiet for 45s --)";

    /// <summary>The engine prints its mod loop as "  [3/12] slug 1.2.3" - the only progress it exposes.</summary>
    private static readonly Regex ModProgressPattern = new(@"\[(\d+)/(\d+)\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly char[] NewLineChars = ['\r', '\n'];

    private readonly Arguments _arguments;
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 1000 };

    /// <summary>Everything this window has shown, kept for "Copy log" and the final report.</summary>
    private readonly List<string> _logLines = new();

    /// <summary>The subset still in the RichTextBox once the line cap starts trimming.</summary>
    private readonly List<string> _visibleLines = new();

    private readonly RadioButton _modeFull = new() { Text = "Full install (game + SPT + mods)", AutoSize = true, Checked = true };
    private readonly RadioButton _modeMods = new() { Text = "Mods only (existing SPT install)", AutoSize = true };
    private readonly RadioButton _modeUpdate = new() { Text = "Update (game patch + mods)", AutoSize = true };

    private readonly TextBox _pathBox = new() { Text = Arguments.DefaultTargetPath };
    private readonly Button _browseButton = new() { Text = "Browse..." };

    private readonly Label _phaseLabel = new() { Text = "Ready.", AutoSize = true };
    private readonly Label _elapsedLabel = new() { Text = "00:00", AutoSize = true };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Value = 0, Style = ProgressBarStyle.Blocks };
    private readonly RichTextBox _logBox = new();

    private readonly GroupBox _reportGroup = new();
    private readonly TextBox _reportBox = new();
    private readonly Button _openFolderButton = new();
    private readonly Button _copyLogButton = new();

    private readonly Button _startButton = new();
    private readonly Button _cancelButton = new();

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

        BuildLayout();
        ApplyArguments();

        _clock.Tick += OnClockTick;
        FormClosing += OnFormClosing;
        FormClosed += OnFormClosed;

        AppendAppLine("SPT Co-op Setup " + Application.ProductVersion);
        foreach (var ignored in _arguments.Ignored)
        {
            AppendAppLine("ignoring unknown argument: " + ignored);
        }
    }

    /// <summary>Process exit code: the engine's code once a run finished, 2 for a refused run.</summary>
    public int ExitCode { get; private set; }

    // ---------------------------------------------------------------- layout

    private void BuildLayout()
    {
        SuspendLayout();

        Text = "SPT Co-op Setup";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(920, 780);
        MinimumSize = new Size(780, 640);
        AutoScaleMode = AutoScaleMode.Font;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(12),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // mode
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // folder
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // progress
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // log - takes the slack
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // result
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // actions

        root.Controls.Add(BuildModeGroup(), 0, 0);
        root.Controls.Add(BuildPathGroup(), 0, 1);
        root.Controls.Add(BuildProgressGroup(), 0, 2);
        root.Controls.Add(BuildLogGroup(), 0, 3);
        root.Controls.Add(BuildReportGroup(), 0, 4);
        root.Controls.Add(BuildActionBar(), 0, 5);

        Controls.Add(root);
        ResumeLayout(true);
        PerformLayout();
    }

    private GroupBox BuildModeGroup()
    {
        var group = NewGroupBox("Install mode");

        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 3,
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

    private GroupBox BuildPathGroup()
    {
        var group = NewGroupBox("Target folder");

        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, 29f));

        _pathBox.Dock = DockStyle.Fill;
        _pathBox.Margin = new Padding(0, 0, 8, 0);

        _browseButton.AutoSize = true;
        _browseButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _browseButton.MinimumSize = new Size(100, 27);
        _browseButton.Margin = new Padding(0);
        _browseButton.Anchor = AnchorStyles.Right;
        _browseButton.Click += OnBrowseClicked;

        row.Controls.Add(_pathBox, 0, 0);
        row.Controls.Add(_browseButton, 1, 0);

        group.Controls.Add(row);
        return group;
    }

    private GroupBox BuildProgressGroup()
    {
        var group = NewGroupBox("Progress");

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 2,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));

        _phaseLabel.Text = "Ready - pick a mode and a folder, then press Start install.";
        _phaseLabel.Anchor = AnchorStyles.Left;
        _phaseLabel.Margin = new Padding(0, 0, 8, 6);

        _elapsedLabel.Anchor = AnchorStyles.Right;
        _elapsedLabel.Margin = new Padding(0, 0, 0, 6);
        _elapsedLabel.Font = new Font(Font, FontStyle.Bold);

        _progress.Dock = DockStyle.Fill;
        _progress.Margin = new Padding(0);

        layout.Controls.Add(_phaseLabel, 0, 0);
        layout.Controls.Add(_elapsedLabel, 1, 0);
        layout.SetColumnSpan(_progress, 2);
        layout.Controls.Add(_progress, 0, 1);

        group.Controls.Add(layout);
        return group;
    }

    private GroupBox BuildLogGroup()
    {
        var group = NewGroupBox("Log");
        group.AutoSize = false;
        group.Dock = DockStyle.Fill;

        _logBox.Dock = DockStyle.Fill;
        _logBox.ReadOnly = true;
        _logBox.WordWrap = false;
        _logBox.DetectUrls = false;
        _logBox.HideSelection = false;
        _logBox.ScrollBars = RichTextBoxScrollBars.Both;
        _logBox.BackColor = SystemColors.Window;
        _logBox.Font = new Font("Consolas", 9f);

        group.Controls.Add(_logBox);
        return group;
    }

    private GroupBox BuildReportGroup()
    {
        _reportGroup.Text = "Result";
        _reportGroup.AutoSize = true;
        _reportGroup.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _reportGroup.Dock = DockStyle.Fill;
        _reportGroup.Padding = new Padding(10, 6, 10, 10);
        _reportGroup.Visible = false;
        _reportGroup.Margin = new Padding(0, 6, 0, 0);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 160f));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _reportBox.Multiline = true;
        _reportBox.ReadOnly = true;
        _reportBox.WordWrap = false;
        _reportBox.ScrollBars = ScrollBars.Both;
        _reportBox.Dock = DockStyle.Fill;
        _reportBox.BackColor = SystemColors.Window;
        _reportBox.Font = new Font("Consolas", 9f);
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
            Margin = new Padding(0, 8, 0, 0),
        };
        bar.Controls.Add(_startButton);
        bar.Controls.Add(_cancelButton);

        AcceptButton = _startButton;
        return bar;
    }

    private static GroupBox NewGroupBox(string title) => new()
    {
        Text = title,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Fill,
        Padding = new Padding(10, 6, 10, 10),
        Margin = new Padding(0, 0, 0, 6),
    };

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
        if (_arguments.ShouldStartImmediately)
        {
            // Let the window paint first - the engine's first lines arrive fast.
            BeginInvoke(new Action(() => OnStartClicked(this, EventArgs.Empty)));
        }
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
            ReportFailure("Pick a target folder first.", blocking: true);
            return;
        }

        string path;
        try
        {
            path = Path.GetFullPath(typed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            ReportFailure("'" + typed + "' is not a valid folder path.", blocking: true);
            return;
        }

        if (!TargetGuard.TryValidate(mode, path, out var guardMessage))
        {
            ReportFailure(guardMessage, blocking: true);
            return;
        }

        path = _pathBox.Text = path;

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
        _progress.Style = ProgressBarStyle.Marquee;
        _progress.MarqueeAnimationSpeed = 30;
        _progress.Value = 0;
        _phaseLabel.Text = Arguments.ModeTitle(mode) + " running...";

        SetRunningState(true);
        AppendAppLine("starting " + Arguments.ModeTitle(mode).ToLowerInvariant() + " in " + path);

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
            _progress.Style = ProgressBarStyle.Blocks;
            _phaseLabel.Text = "Failed.";
            ReportFailure("could not start the engine: " + ex.Message, blocking: true);
            return;
        }

        _clock.Start();
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
        AppendLine(line);

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
            _progress.Style = ProgressBarStyle.Continuous;
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
        _progress.Style = ProgressBarStyle.Blocks;
        if (_progressDeterminate)
        {
            _progress.Value = _cancelRequested || exitCode != 0 ? _progress.Minimum : _progress.Maximum;
        }

        _phaseLabel.Text = _cancelRequested ? "Cancelled." : exitCode == 0 ? "Finished." : "Failed.";

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
        AppendLine(QuietMarker);
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

    private void AppendAppLine(string text) => AppendLine("[app] " + text);

    private void AppendLine(string text)
    {
        _logLines.Add(text);
        _visibleLines.Add(text);

        var trimmed = false;
        while (_visibleLines.Count > LogLineCap)
        {
            _visibleLines.RemoveRange(0, Math.Min(LogTrimChunk, _visibleLines.Count));
            trimmed = true;
        }

        if (trimmed)
        {
            _logBox.Text = string.Join("\n", _visibleLines);
        }
        else
        {
            _logBox.AppendText(text + "\n");
        }

        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.SelectionLength = 0;
        _logBox.ScrollToCaret();
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
        _clock.Stop();
        _clock.Dispose();
        _runner?.Dispose();
        _runner = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _clock.Dispose();
            _runner?.Dispose();
            _runner = null;
        }

        base.Dispose(disposing);
    }
}
