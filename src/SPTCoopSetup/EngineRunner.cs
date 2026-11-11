using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace SPTCoopSetup;

/// <summary>
/// Runs the embedded PowerShell engine (setup-spt.ps1 / install-mods.ps1) as a child process and
/// forwards its output line by line.
/// </summary>
/// <remarks>
/// Contract with the engine workstream:
/// <list type="bullet">
/// <item>the three scripts/JSON are embedded and extracted to a throwaway temp folder per run;</item>
/// <item>one argument per <see cref="ProcessStartInfo.ArgumentList"/> entry - never string concat,
/// because target folders contain spaces;</item>
/// <item>stdout and stderr are read concurrently so a full pipe buffer can never deadlock the child;</item>
/// <item>stderr lines are prefixed <c>stderr: </c>;</item>
/// <item>the child sees <c>SPT_SETUP_APP=1</c> and is always called with <c>-NoElevate</c>.</item>
/// </list>
/// </remarks>
internal sealed class EngineRunner : IDisposable
{
    public const string StderrPrefix = "stderr: ";

    private const string SetupScriptResource = "SPTCoopSetup.engine.setup-spt.ps1";
    private const string ModsScriptResource = "SPTCoopSetup.engine.install-mods.ps1";
    private const string ManifestResource = "SPTCoopSetup.engine.mods.json";

    private static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(3);

    private readonly InstallMode _mode;
    private readonly string _targetPath;
    private readonly string _archivesDir;

    private Process? _process;
    private string? _tempDir;
    private volatile bool _exited;

    public EngineRunner(InstallMode mode, string targetPath)
    {
        _mode = mode;
        _targetPath = targetPath;
        _archivesDir = Path.Combine(AppContext.BaseDirectory, "archives");
    }

    /// <summary>One line of engine (or host) output. Raised on a background thread.</summary>
    public event Action<string>? LineReceived;

    /// <summary>Exit code, raised once - including after a cancel. Raised on a background thread.</summary>
    public event Action<int>? Finished;

    public bool IsRunning => _process is not null && !_exited;

    /// <summary>Archive folder handed to the engine via <c>-ArchiveDir</c>.</summary>
    public string ArchivesDirectory => _archivesDir;

    /// <summary>Extracts the embedded engine and launches it. Throws only for host-side failures.</summary>
    public void Start()
    {
        if (_process is not null)
        {
            throw new InvalidOperationException("the engine has already been started");
        }

        EnsureArchivesDirectory();

        var tempDir = Path.Combine(Path.GetTempPath(), "SPTCoopSetup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        _tempDir = tempDir;

        var usesSetupScript = _mode != InstallMode.Mods;
        var scriptPath = usesSetupScript
            ? Extract(tempDir, SetupScriptResource, "setup-spt.ps1")
            : Extract(tempDir, ModsScriptResource, "install-mods.ps1");
        var manifestPath = Extract(tempDir, ManifestResource, "mods.json");

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = tempDir,
        };
        startInfo.Environment["SPT_SETUP_APP"] = "1";

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);

        if (usesSetupScript)
        {
            startInfo.ArgumentList.Add("-InstallDir");
            startInfo.ArgumentList.Add(_targetPath);
            if (_mode == InstallMode.Update)
            {
                startInfo.ArgumentList.Add("-Update");
            }
        }
        else
        {
            startInfo.ArgumentList.Add("-SptDir");
            startInfo.ArgumentList.Add(_targetPath);
        }

        startInfo.ArgumentList.Add("-ArchiveDir");
        startInfo.ArgumentList.Add(_archivesDir);
        startInfo.ArgumentList.Add("-ManifestPath");
        startInfo.ArgumentList.Add(manifestPath);
        startInfo.ArgumentList.Add("-NoElevate");

        LineReceived?.Invoke("[app] engine: " + Describe(startInfo));

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("powershell.exe did not start");
        }

        _process = process;
        _ = PumpAsync(process);
    }

    /// <summary>
    /// Asks the engine to stop: a short grace window, then the whole process tree is killed
    /// (the engine spawns robocopy/PowerShell tickers of its own).
    /// </summary>
    public async Task CancelAsync()
    {
        var process = _process;
        if (process is null || _exited)
        {
            return;
        }

        try
        {
            using var grace = new CancellationTokenSource(CancelGrace);
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            // Still alive after the grace period - fall through and kill it.
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return;
        }

        LineReceived?.Invoke("[app] grace period over - killing the engine process tree");
        Terminate();
    }

    /// <summary>Synchronous kill, used when the window is closing.</summary>
    public void Terminate()
    {
        var process = _process;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone, or not killable - nothing left to do.
        }
    }

    /// <summary>Best-effort removal of the extracted engine (also called on cancel and on exit).</summary>
    public void Cleanup()
    {
        var tempDir = _tempDir;
        _tempDir = null;
        if (tempDir is null)
        {
            return;
        }

        try
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A file may still be held by the child; leave it for the OS to clean up later.
        }
    }

    public void Dispose()
    {
        Terminate();
        Cleanup();
        _process?.Dispose();
        _process = null;
    }

    private async Task PumpAsync(Process process)
    {
        var stdout = ReadAsync(process.StandardOutput, string.Empty);
        var stderr = ReadAsync(process.StandardError, StderrPrefix);

        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Lost the handle - treat it as an exit.
        }

        try
        {
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pipes died with the process; whatever we already printed is all there is.
        }

        var exitCode = -1;
        try
        {
            exitCode = process.ExitCode;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Keep -1: unknown.
        }

        _exited = true;
        Finished?.Invoke(exitCode);
    }

    private async Task ReadAsync(StreamReader reader, string prefix)
    {
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            if (line is null)
            {
                return;
            }

            if (line.Length > 0 && line[0] == '\uFEFF')
            {
                line = line[1..];
            }

            LineReceived?.Invoke(prefix.Length == 0 ? line : prefix + line);
        }
    }

    private void EnsureArchivesDirectory()
    {
        try
        {
            if (Directory.Exists(_archivesDir))
            {
                return;
            }

            Directory.CreateDirectory(_archivesDir);
            LineReceived?.Invoke("[app] created archive folder " + _archivesDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Read-only media (USB key, Program Files without rights): the engine still has the
            // upstream URLs, so a missing archive folder only costs download time.
            LineReceived?.Invoke("[app] WARNING: could not create archive folder " + _archivesDir + " (" + ex.Message + ") - continuing without local archives");
        }
    }

    private static string Extract(string tempDir, string resourceName, string fileName)
    {
        var target = Path.Combine(tempDir, fileName);
        using var resource = typeof(EngineRunner).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("embedded resource '" + resourceName + "' is missing from this build");
        using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        resource.CopyTo(file);
        return target;
    }

    /// <summary>Readable one-liner of the child command for the log (support breadcrumbs).</summary>
    private static string Describe(ProcessStartInfo startInfo)
    {
        var builder = new StringBuilder("powershell.exe");
        foreach (var argument in startInfo.ArgumentList)
        {
            builder.Append(' ');
            builder.Append(argument.Contains(' ') || argument.Contains('"') ? '"' + argument + '"' : argument);
        }

        return builder.ToString();
    }
}
