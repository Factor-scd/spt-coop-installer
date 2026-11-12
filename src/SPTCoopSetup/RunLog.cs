using System.Globalization;
using System.Security;
using System.Text;

namespace SPTCoopSetup;

/// <summary>
/// Writes the persistent per-run log under %LOCALAPPDATA%\SPTCoopSetup so a failed install can be
/// diagnosed after the window is gone. Flushes every line; never throws into the UI.
/// </summary>
/// <remarks>
/// <see cref="Directory"/> and <see cref="Path"/> intentionally shadow the BCL types of the same
/// name, so every filesystem call in this file is written out as <c>System.IO.Directory</c> or
/// <c>System.IO.Path</c>.
/// </remarks>
internal sealed class RunLog : IDisposable
{
    /// <summary>How many run logs to keep on disk.</summary>
    public const int KeepRuns = 10;

    private const string FilePrefix = "run-";
    private const string FileSuffix = ".log";
    private const string FolderName = "SPTCoopSetup";

    /// <summary>Guards write+flush; engine lines arrive on a background pump thread.</summary>
    private readonly object _gate = new();
    private readonly string[] _argv;
    private readonly string? _kind;

    private StreamWriter? _writer;
    private bool _disposed;

    private RunLog(string? directory, string? path, StreamWriter? writer, string[] argv, string? kind)
    {
        Directory = directory;
        Path = path;
        _writer = writer;
        _argv = argv;
        _kind = kind;
    }

    /// <summary>Folder the run logs live in; null when it could not be resolved.</summary>
    public string? Directory { get; }

    /// <summary>Full path of this run's log file; null when logging is unavailable.</summary>
    public string? Path { get; }

    /// <summary>
    /// Opens the run log (never throws). <paramref name="directoryOverride"/> exists so tests can
    /// use a temp folder; production passes null and gets %LOCALAPPDATA%\SPTCoopSetup.
    /// <paramref name="kind"/> is the default run kind used by <see cref="StartBanner"/>.
    /// </summary>
    public static RunLog Create(string[] argv, string? directoryOverride = null, string? kind = null)
    {
        var args = argv ?? Array.Empty<string>();

        try
        {
            var directory = string.IsNullOrWhiteSpace(directoryOverride)
                ? DefaultDirectory()
                : directoryOverride;

            if (string.IsNullOrWhiteSpace(directory))
            {
                return new RunLog(null, null, null, args, kind);
            }

            System.IO.Directory.CreateDirectory(directory);

            var (path, writer) = OpenNew(directory);
            if (path is null || writer is null)
            {
                return new RunLog(directory, null, null, args, kind);
            }

            // The new file is the newest by write time, so the default keep-10 rule protects it.
            PruneCore(directory, KeepRuns, path);

            return new RunLog(directory, path, writer, args, kind);
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or SecurityException
                                       or NotSupportedException
                                       or ArgumentException)
        {
            // A broken log must never break an install.
            return new RunLog(null, null, null, args, kind);
        }
    }

    /// <summary>One timestamped, levelled line; written and flushed immediately.</summary>
    public void Line(string level, string message)
    {
        lock (_gate)
        {
            if (_disposed || _writer is null)
            {
                return;
            }

            var stamp = Timestamp();
            var padded = (level ?? "INFO").PadRight(8);

            foreach (var part in SplitLines(message ?? string.Empty))
            {
                _writer.Write(stamp);
                _writer.Write("  ");
                _writer.Write(padded);
                _writer.Write("  ");
                _writer.Write(part);
                _writer.Write('\n');
            }

            FlushLocked();
        }
    }

    public void Info(string message) => Line("INFO", message);

    public void Warn(string message) => Line("WARN", message);

    /// <summary>Engine output, level "ENGINE" - stored verbatim (the 'stderr: ' prefix stays).</summary>
    public void Engine(string line) => Line("ENGINE", line ?? string.Empty);

    /// <summary>No timestamp: for banners like "--- run start ---" that must not be attributed.</summary>
    public void Raw(string text)
    {
        lock (_gate)
        {
            if (_disposed || _writer is null)
            {
                return;
            }

            foreach (var part in SplitLines(text ?? string.Empty))
            {
                _writer.Write(part);
                _writer.Write('\n');
            }

            FlushLocked();
        }
    }

    /// <summary>Writes the "--- run start ---" banner with the app version, kind and argv.</summary>
    public void StartBanner(string appVersion, string kind)
    {
        var effectiveKind = string.IsNullOrWhiteSpace(kind)
            ? (string.IsNullOrWhiteSpace(_kind) ? "install" : _kind!)
            : kind;

        var text = string.Join(
            "\n",
            "--- run start ---",
            "app      SPT Co-op Setup " + (string.IsNullOrWhiteSpace(appVersion) ? "unknown" : appVersion),
            "kind     " + effectiveKind,
            "args     " + string.Join(" ", _argv),
            "started  " + Timestamp(),
            "---");

        Raw(text);
    }

    /// <summary>
    /// Deletes all but the newest <paramref name="keep"/> `run-*.log` files in
    /// <paramref name="directory"/> (never throws). Returns the files it deleted, oldest first.
    /// Pure filesystem logic, unit-testable.
    /// </summary>
    public static IReadOnlyList<string> Prune(string directory, int keep = KeepRuns)
        => PruneCore(directory, keep, null);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            var writer = _writer;
            _writer = null;
            if (writer is null)
            {
                return;
            }

            try
            {
                writer.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Whatever already reached disk is the log we keep.
            }

            try
            {
                writer.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Nothing else we can do, and a log must never break an install.
            }
        }
    }

    /// <summary>Creates the first free <c>run-&lt;yyyyMMdd-HHmmss&gt;.log</c>; never overwrites.</summary>
    private static (string? Path, StreamWriter? Writer) OpenNew(string directory)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        for (var attempt = 1; attempt <= 1000; attempt++)
        {
            var name = FilePrefix + stamp + (attempt == 1 ? string.Empty : "-" + attempt.ToString(CultureInfo.InvariantCulture)) + FileSuffix;
            var path = System.IO.Path.Combine(directory, name);

            try
            {
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
                var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" };
                return (path, writer);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Two runs in the same second: try -2, -3, ...
                continue;
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Rotation ordering: by last-write time, not by name. The active run's file is always the
    /// most recently written one even when two runs started inside the same second, so ordering by
    /// write time is what keeps a live log alive; names break ties.
    /// </summary>
    private static IReadOnlyList<string> PruneCore(string directory, int keep, string? exclude)
    {
        var deleted = new List<string>();

        try
        {
            if (keep < 0 || string.IsNullOrWhiteSpace(directory) || !System.IO.Directory.Exists(directory))
            {
                return deleted;
            }

            var excludeFull = exclude is null ? null : System.IO.Path.GetFullPath(exclude);
            var entries = new List<(string Path, DateTime Written, string Name)>();

            foreach (var file in System.IO.Directory.EnumerateFiles(directory, FilePrefix + "*" + FileSuffix))
            {
                var name = System.IO.Path.GetFileName(file);
                if (!name.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase) ||
                    !name.EndsWith(FileSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (excludeFull is not null &&
                    string.Equals(System.IO.Path.GetFullPath(file), excludeFull, StringComparison.Ordinal))
                {
                    continue;
                }

                DateTime written;
                try
                {
                    written = File.GetLastWriteTimeUtc(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    written = DateTime.MinValue;
                }

                entries.Add((file, written, name));
            }

            entries.Sort((left, right) =>
            {
                var byTime = left.Written.CompareTo(right.Written);
                return byTime != 0 ? byTime : string.CompareOrdinal(left.Name, right.Name);
            });

            var excess = entries.Count - keep;
            for (var i = 0; i < excess; i++)
            {
                var victim = entries[i].Path;
                try
                {
                    File.Delete(victim);
                    deleted.Add(victim);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    // Held open, or already gone: rotation is best effort.
                }
            }
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or SecurityException
                                       or NotSupportedException
                                       or ArgumentException)
        {
            // A read-only or vanished folder simply means nothing gets pruned.
        }

        return deleted;
    }

    private static string? DefaultDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
        {
            return null;
        }

        return System.IO.Path.Combine(local, FolderName);
    }

    private static string Timestamp()
        => DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

    /// <summary>Splits embedded newlines so every file line keeps its own timestamp.</summary>
    private static IEnumerable<string> SplitLines(string text)
    {
        if (text.IndexOf('\n') < 0 && text.IndexOf('\r') < 0)
        {
            yield return text;
            yield break;
        }

        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\r' or '\n'))
            {
                continue;
            }

            yield return text[start..i];
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }

            start = i + 1;
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }

    /// <summary>Caller holds <see cref="_gate"/>; a failing flush only loses a diagnostic line.</summary>
    private void FlushLocked()
    {
        try
        {
            _writer?.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Disk full or the file was yanked away - keep the app running.
        }
    }
}
