using System.Globalization;
using System.Text.RegularExpressions;

namespace SPTCoopSetup;

/// <summary>
/// Unit tests for <see cref="RunLog"/>: flush-per-line, the banner, verbatim engine lines and the
/// keep-N rotation. Plain PASS/FAIL lines and no framework, matching the rest of this harness, so
/// it runs on the Linux build host with nothing but the SDK.
/// </summary>
internal static class RunLogTests
{
    public static int Run()
    {
        var failures = 0;
        var checks = 0;

        void Check(string name, bool condition, string? detail = null)
        {
            checks++;
            if (condition)
            {
                Console.WriteLine("PASS " + name);
                return;
            }

            failures++;
            Console.WriteLine("FAIL " + name + (detail is null ? "" : " -> " + detail));
        }

        // Reads a log the writer still holds open - the flush-per-line guarantee.
        string ReadWhileOpen(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        string[] Lines(string text)
            => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        var root = Path.Combine(Path.GetTempPath(), "spt-runlog-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            // --- 1: Create names the file run-<yyyyMMdd-HHmmss>.log inside the override folder ---
            var dir1 = Path.Combine(root, "create");
            using (var log = RunLog.Create(new[] { "--auto" }, dir1))
            {
                var name = log.Path is null ? "" : Path.GetFileName(log.Path);
                Check("runlog/create makes a run-*.log file", log.Directory == dir1 && log.Path is not null && File.Exists(log.Path), log.Path ?? "null");
                Check(
                    "runlog/create file name is run-<yyyyMMdd-HHmmss>.log",
                    Regex.IsMatch(name, @"^run-\d{8}-\d{6}(-\d+)?\.log$"),
                    name);
                Check("runlog/create is in the override folder", log.Path is not null && Path.GetDirectoryName(log.Path) == dir1, log.Path ?? "null");
            }

            // --- 2: flush-per-line: the line is on disk before Dispose ----------------------------
            var dir2 = Path.Combine(root, "flush");
            var flushLog = RunLog.Create(new[] { "--auto" }, dir2);
            try
            {
                flushLog.Info("first line is already on disk");
                var live = flushLog.Path is null ? "" : ReadWhileOpen(flushLog.Path);
                Check("runlog/flush per line (readable while open)", live.Contains("first line is already on disk", StringComparison.Ordinal) && live.Contains("INFO", StringComparison.Ordinal), live);

                flushLog.Info("second line");
                var live2 = ReadWhileOpen(flushLog.Path!);
                Check("runlog/flush per line covers every write", live2.Contains("second line", StringComparison.Ordinal), live2);
                Check("runlog/line format is timestamp + level", Regex.IsMatch(live2, @"(?m)^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}[+-]\d{2}:\d{2}  INFO      "), live2);
                Check("runlog/lines are LF-terminated", !live2.Contains("\r", StringComparison.Ordinal), "found CR");
            }
            finally
            {
                flushLog.Dispose();
            }

            // --- 3: StartBanner ----------------------------------------------------------------
            var dir3 = Path.Combine(root, "banner");
            using (var log = RunLog.Create(new[] { "--mode", "mods", "--path", @"C:\SPT", "--auto" }, dir3, "install"))
            {
                log.StartBanner("9.9.9-test", "install");
                var text = ReadWhileOpen(log.Path!);
                var lines = Lines(text);
                Check("runlog/banner marker is its own line", Array.IndexOf(lines, "--- run start ---") >= 0, text);
                Check("runlog/banner carries the version", text.Contains("SPT Co-op Setup 9.9.9-test", StringComparison.Ordinal), text);
                Check("runlog/banner carries the kind", text.Contains("kind     install", StringComparison.Ordinal), text);
                Check("runlog/banner carries the argv", text.Contains(@"args     --mode mods --path C:\SPT --auto", StringComparison.Ordinal), text);
            }

            // --- 4: Engine lines are verbatim ---------------------------------------------------
            var dir4 = Path.Combine(root, "engine");
            using (var log = RunLog.Create(new[] { "--auto" }, dir4))
            {
                log.Engine("stderr: boom");
                var text = ReadWhileOpen(log.Path!);
                var line = Array.Find(Lines(text), candidate => candidate.Contains("stderr: boom", StringComparison.Ordinal));
                Check("runlog/engine text is verbatim", line is not null && line.EndsWith("stderr: boom", StringComparison.Ordinal), text);
                Check("runlog/engine lines are levelled ENGINE", line is not null && line.Contains("ENGINE", StringComparison.Ordinal), line ?? "missing");
            }

            // --- 5: rotation keeps the newest 10, ignores foreign files -------------------------
            var dir5 = Path.Combine(root, "rotate");
            Directory.CreateDirectory(dir5);
            var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (var i = 0; i < 13; i++)
            {
                var file = Path.Combine(dir5, "run-" + baseTime.AddHours(i).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log");
                File.WriteAllText(file, "run " + i.ToString(CultureInfo.InvariantCulture));
                File.SetLastWriteTimeUtc(file, baseTime.AddHours(i));
            }

            File.WriteAllText(Path.Combine(dir5, "crash.log"), "crash");
            File.SetLastWriteTimeUtc(Path.Combine(dir5, "crash.log"), baseTime.AddHours(-5));
            File.WriteAllText(Path.Combine(dir5, "notes.txt"), "notes");
            File.SetLastWriteTimeUtc(Path.Combine(dir5, "notes.txt"), baseTime.AddHours(-5));

            var removed = RunLog.Prune(dir5, 10);
            var remaining = Directory.GetFiles(dir5, "run-*.log");
            Check("runlog/prune keeps exactly 10 runs", remaining.Length == 10, "kept " + remaining.Length);
            Check("runlog/prune reports the 3 oldest", removed.Count == 3, "deleted " + removed.Count + ": " + string.Join(",", removed));
            var oldestGone = true;
            for (var i = 0; i < 3; i++)
            {
                var victim = Path.Combine(dir5, "run-" + baseTime.AddHours(i).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log");
                oldestGone &= !File.Exists(victim);
            }

            Check("runlog/prune deletes the 3 oldest", oldestGone, "an old file survived");
            Check("runlog/prune keeps the newest run", File.Exists(Path.Combine(dir5, "run-" + baseTime.AddHours(12).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log")), "newest deleted");
            Check(
                "runlog/prune leaves non-run files alone",
                File.Exists(Path.Combine(dir5, "crash.log")) && File.Exists(Path.Combine(dir5, "notes.txt")),
                "a foreign file was deleted");

            // --- 6: the active run survives even when its name sorts oldest ---------------------
            var dir6 = Path.Combine(root, "active");
            using (var log = RunLog.Create(new[] { "--auto" }, dir6))
            {
                // Twelve files whose names sort after today's, but which were written earlier.
                var now = DateTime.UtcNow;
                for (var i = 0; i < 12; i++)
                {
                    var file = Path.Combine(dir6, "run-20990101-0000" + i.ToString("00", CultureInfo.InvariantCulture) + ".log");
                    File.WriteAllText(file, "future name " + i.ToString(CultureInfo.InvariantCulture));
                    File.SetLastWriteTimeUtc(file, now.AddMinutes(-(i + 1)));
                }

                RunLog.Prune(dir6, 10);
                Check("runlog/prune keeps the open run's file", File.Exists(log.Path), log.Path ?? "null");
                Check("runlog/prune still trims to 10", Directory.GetFiles(dir6, "run-*.log").Length == 10, "kept " + Directory.GetFiles(dir6, "run-*.log").Length);
            }

            // --- 7: an unusable folder yields a silent no-op log --------------------------------
            var blocker = Path.Combine(root, "not-a-directory");
            File.WriteAllText(blocker, "a file where a folder is needed");
            var blockedPath = Path.Combine(blocker, "logs");
            var blockedIsWritable = false;
            try
            {
                Directory.CreateDirectory(blockedPath);
                blockedIsWritable = true;
            }
            catch (Exception)
            {
                // Expected: a directory cannot live under a regular file.
            }

            var blockedLog = RunLog.Create(new[] { "--auto" }, blockedPath);
            var silent = true;
            try
            {
                blockedLog.Info("ignored");
                blockedLog.Warn("ignored");
                blockedLog.Engine("stderr: ignored");
                blockedLog.Raw("ignored");
                blockedLog.StartBanner("0.0.0-test", "install");
                blockedLog.Dispose();
                blockedLog.Dispose();
            }
            catch (Exception ex)
            {
                silent = false;
                Check("runlog/unusable folder writes are silent", false, ex.GetType().Name + ": " + ex.Message);
            }

            if (silent)
            {
                Check("runlog/unusable folder writes are silent", true);
            }

            if (blockedIsWritable)
            {
                Check("runlog/unusable folder yields no path (skipped: folder was writable)", true, null);
            }
            else
            {
                Check("runlog/unusable folder yields no path", blockedLog.Path is null, blockedLog.Path ?? "null");
            }

            // --- 8: two runs in the same second never overwrite ---------------------------------
            var dir8 = Path.Combine(root, "collide");
            var first = RunLog.Create(new[] { "--auto" }, dir8);
            var second = RunLog.Create(new[] { "--auto" }, dir8);
            try
            {
                Check("runlog/same-second runs get different files", first.Path is not null && second.Path is not null && first.Path != second.Path, (first.Path ?? "null") + " vs " + (second.Path ?? "null"));
                Check("runlog/same-second runs both exist", first.Path is not null && File.Exists(first.Path) && second.Path is not null && File.Exists(second.Path), "a run log was overwritten");
                // If both runs landed in the same second the second name must carry the -N suffix;
                // if the clock ticked over, a plain name is correct - so only assert on the former.
                var firstName = first.Path is null ? "" : Path.GetFileName(first.Path);
                var secondName = second.Path is null ? "" : Path.GetFileName(second.Path);
                var sameStamp = Regex.Replace(firstName, @"-\d+\.log$", ".log") == Regex.Replace(secondName, @"-\d+\.log$", ".log");
                Check(
                    "runlog/same-second collision gets a suffix",
                    !sameStamp || Regex.IsMatch(secondName, @"^run-\d{8}-\d{6}-\d+\.log$"),
                    firstName + " / " + secondName);
            }
            finally
            {
                first.Dispose();
                second.Dispose();
            }

            // --- 9: Dispose is idempotent and post-dispose writes are no-ops ---------------------
            var dir9 = Path.Combine(root, "dispose");
            var disposed = RunLog.Create(new[] { "--auto" }, dir9);
            disposed.Info("before dispose");
            disposed.Dispose();
            disposed.Dispose();
            var afterDispose = true;
            try
            {
                disposed.Info("after dispose");
                disposed.Warn("after dispose");
                disposed.Engine("after dispose");
                disposed.Raw("after dispose");
            }
            catch (Exception ex)
            {
                afterDispose = false;
                Check("runlog/write after dispose is a no-op", false, ex.GetType().Name + ": " + ex.Message);
            }

            if (afterDispose)
            {
                Check("runlog/write after dispose is a no-op", true);
                Check("runlog/write after dispose is not logged", disposed.Path is not null && !File.ReadAllText(disposed.Path).Contains("after dispose", StringComparison.Ordinal), disposed.Path ?? "null");
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception)
            {
                // A temp folder left behind is not worth a failure.
            }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "RUNLOG-" + checks + "/" + checks : "RUNLOG-FAILED " + failures + "/" + checks);
        return failures;
    }
}
