using SPTCoopSetup;

// Regression tests for D1 (elevated relaunch lost every argument) and D2 (UAC before validation).
// No test framework on purpose: the repo's other harness is a shell script, and this must run on
// the Linux build host with nothing but the SDK.

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

void CheckEqual(string name, string expected, string actual)
    => Check(name, expected == actual, "expected <" + expected + "> actual <" + actual + ">");

// --- D1: quoting survives the round trip the elevated relaunch depends on -------------------

var spaced = new[] { "--mode", "mods", "--path", @"C:\Program Files\SPT", "--auto" };
var spacedLine = ArgumentQuoting.Join(spaced);
var spacedBack = ArgumentQuoting.Split(spacedLine);
Check("D1/join quotes the spaced path", spacedLine.Contains("\"C:\\Program Files\\SPT\""), spacedLine);
Check("D1/split round-trips every argument", spacedBack.Count == 5 && spacedBack[3] == @"C:\Program Files\SPT" && spacedBack[0] == "--mode" && spacedBack[1] == "mods" && spacedBack[4] == "--auto", string.Join("|", spacedBack));

var parsed = Arguments.Parse(spacedBack.ToArray());
Check("D1/parsed mode survives", parsed.Mode == InstallMode.Mods, parsed.Mode?.ToString() ?? "null");
Check("D1/parsed path survives", parsed.TargetPath == @"C:\Program Files\SPT", parsed.TargetPath ?? "null");
Check("D1/parsed auto survives", parsed.Auto, "false");

// A path with no spaces needs no quotes, and then a trailing backslash needs no doubling either.
var plainTrailing = new[] { "--path", @"E:\SPT\" };
CheckEqual("D1/unspaced trailing backslash stays verbatim", @"--path E:\SPT\", ArgumentQuoting.Join(plainTrailing));
var plainTrailingBack = ArgumentQuoting.Split(ArgumentQuoting.Join(plainTrailing));
Check("D1/unspaced trailing backslash round-trips", plainTrailingBack.Count == 2 && plainTrailingBack[1] == @"E:\SPT\", string.Join("|", plainTrailingBack));

// Quoted + trailing backslash is the dangerous case: the backslash MUST be doubled.
var quotedTrailing = new[] { "--path", @"C:\Program Files\SPT\" };
CheckEqual("D1/quoted trailing backslash is doubled", "--path \"C:\\Program Files\\SPT\\\\\"", ArgumentQuoting.Join(quotedTrailing));
var quotedTrailingBack = ArgumentQuoting.Split(ArgumentQuoting.Join(quotedTrailing));
Check("D1/quoted trailing backslash round-trips", quotedTrailingBack.Count == 2 && quotedTrailingBack[1] == @"C:\Program Files\SPT\", string.Join("|", quotedTrailingBack));

// An embedded quote must not split the argument.
var quoted = new[] { "--path", "C:\\weird \"name\"\\SPT" };
var quotedBack = ArgumentQuoting.Split(ArgumentQuoting.Join(quoted));
Check("D1/embedded quote round-trips", quotedBack.Count == 2 && quotedBack[1] == "C:\\weird \"name\"\\SPT", string.Join("|", quotedBack));

// Empty list and empty argument are both legal inputs from the call sites.
CheckEqual("D1/empty list joins to empty string", "", ArgumentQuoting.Join(Array.Empty<string>()));
Check("D1/empty list splits to nothing", ArgumentQuoting.Split("").Count == 0, "non-zero");
Check("D1/empty argument survives", ArgumentQuoting.Split(ArgumentQuoting.Join(new[] { "" })).Count == 1, "lost");

// --- D2: validation short-circuits before elevation ----------------------------------------

var missing = Path.Combine(Path.GetTempPath(), "spt-argv-tests-missing-" + Guid.NewGuid().ToString("N"));
Check("D2/mods needs a real tree", !TargetGuard.TryValidate(InstallMode.Mods, missing, out var modsMessage), "validated a folder that does not exist");
Check("D2/mods message names the marker file", modsMessage.Contains(Path.Combine("SPT_Runtime", "SPT.Server.exe")), modsMessage);
Check("D2/update needs a real tree", !TargetGuard.TryValidate(InstallMode.Update, missing, out _), "validated a folder that does not exist");
Check("D2/full install does not need one", TargetGuard.TryValidate(InstallMode.Full, missing, out _), "refused a full install");

// A folder that does have the marker passes.
var tree = Path.Combine(Path.GetTempPath(), "spt-argv-tests-tree-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(tree, "SPT_Runtime"));
File.WriteAllText(Path.Combine(tree, "SPT_Runtime", "SPT.Server.exe"), "stub");
Check("D2/mods accepts an existing tree", TargetGuard.TryValidate(InstallMode.Mods, tree, out var okMessage), okMessage);
Directory.Delete(tree, recursive: true);

// --- Game physics: pure model, no WinForms/System.Drawing.Color involved -------------------

var gameFailures = GameModelTests.Run();
failures += gameFailures;
checks += GameModelTests.Checks;

// --- Layout geometry: the same widths the form and the headless preview path compute -------

var layoutFailures = LayoutStateTests.Run();
failures += layoutFailures;
checks += LayoutStateTests.Checks;

// --- D3: the --test-game switch and the verbatim Raw audit trail ----------------------------

var testGameSpaced = Arguments.Parse(new[] { "--test-game", "600" });
Check("D3/--test-game <ticks> parses", testGameSpaced.TestGameTicks == 600, testGameSpaced.TestGameTicks?.ToString() ?? "null");

var testGameInline = Arguments.Parse(new[] { "--test-game=600" });
Check("D3/--test-game=<ticks> parses", testGameInline.TestGameTicks == 600, testGameInline.TestGameTicks?.ToString() ?? "null");

var testGameJunk = Arguments.Parse(new[] { "--test-game", "abc" });
Check(
    "D3/--test-game with junk is ignored",
    testGameJunk.TestGameTicks is null && testGameJunk.Ignored.Contains("--test-game abc"),
    "ticks " + (testGameJunk.TestGameTicks?.ToString() ?? "null") + " ignored [" + string.Join("|", testGameJunk.Ignored) + "]");

var testGameBare = Arguments.Parse(new[] { "--test-game" });
Check(
    "D3/bare --test-game is ignored",
    testGameBare.TestGameTicks is null && testGameBare.Ignored.Contains("--test-game"),
    "ticks " + (testGameBare.TestGameTicks?.ToString() ?? "null") + " ignored [" + string.Join("|", testGameBare.Ignored) + "]");

var testGameZero = Arguments.Parse(new[] { "--test-game", "0" });
Check(
    "D3/--test-game 0 is ignored",
    testGameZero.TestGameTicks is null && testGameZero.Ignored.Contains("--test-game 0"),
    "ticks " + (testGameZero.TestGameTicks?.ToString() ?? "null") + " ignored [" + string.Join("|", testGameZero.Ignored) + "]");

// --- --preview-state: which window shape the headless preview renders ----------------------

var previewIdle = Arguments.Parse(new[] { "--preview-state", "idle" });
Check(
    "layout/--preview-state idle sets the idle shape",
    previewIdle.PreviewState == LayoutKind.Idle && previewIdle.Ignored.Count == 0,
    "state " + (previewIdle.PreviewState?.ToString() ?? "null") + " ignored [" + string.Join("|", previewIdle.Ignored) + "]");

var previewInline = Arguments.Parse(new[] { "--preview-state=running" });
Check(
    "layout/--preview-state=running sets the running shape",
    previewInline.PreviewState == LayoutKind.Running,
    "state " + (previewInline.PreviewState?.ToString() ?? "null"));

// The switch is typed by hand in bug reports, so neither spelling nor case should matter.
var previewMixedCase = Arguments.Parse(new[] { "--preview-state", "IDLE" });
Check(
    "layout/--preview-state is case-insensitive",
    previewMixedCase.PreviewState == LayoutKind.Idle,
    "state " + (previewMixedCase.PreviewState?.ToString() ?? "null"));

var previewBare = Arguments.Parse(new[] { "--preview-state" });
Check(
    "layout/bare --preview-state is ignored",
    previewBare.PreviewState is null && previewBare.Ignored.Contains("--preview-state"),
    "state " + (previewBare.PreviewState?.ToString() ?? "null") + " ignored [" + string.Join("|", previewBare.Ignored) + "]");

// A junk value must not silently pick a shape: the renderer keeps its own default instead.
var previewBogus = Arguments.Parse(new[] { "--preview-state=bogus" });
Check(
    "layout/--preview-state with junk is ignored",
    previewBogus.PreviewState is null && previewBogus.Ignored.Any(entry => entry.Contains("bogus", StringComparison.Ordinal)),
    "state " + (previewBogus.PreviewState?.ToString() ?? "null") + " ignored [" + string.Join("|", previewBogus.Ignored) + "]");

// Back-compat: every existing --preview-ui invocation has no --preview-state at all.
var previewAbsent = Arguments.Parse(new[] { "--mode", "mods", "--preview-ui", "out.png" });
Check(
    "layout/absent --preview-state leaves the state null",
    previewAbsent.PreviewState is null,
    "state " + (previewAbsent.PreviewState?.ToString() ?? "null"));

var previewBoth = Arguments.Parse(new[] { "--preview-state", "idle", "--preview-ui", "out.png" });
Check(
    "layout/--preview-state and --preview-ui parse together",
    previewBoth.PreviewState == LayoutKind.Idle && previewBoth.PreviewPath == "out.png",
    "state " + (previewBoth.PreviewState?.ToString() ?? "null") + " path " + (previewBoth.PreviewPath ?? "null"));

// Raw is the audit trail: every non-empty entry, verbatim and in order, unknown flags included.
var rawArgs = new[] { "--mode", "mods", "--weird-flag", "--path", @"C:\Program Files\SPT", "stray", "", "--auto" };
var rawParsed = Arguments.Parse(rawArgs);
CheckEqual(
    "D3/Raw keeps every entry verbatim in order",
    @"--mode|mods|--weird-flag|--path|C:\Program Files\SPT|stray|--auto",
    string.Join("|", rawParsed.Raw));

// The headless runner itself: exit 0 proves all four assertions held (its two output lines
// are printed above and are not PASS lines, so they cannot mask a suite that did not run).
Check("game/--test-game 600 exits 0", GameTest.Run(600) == 0, "run returned non-zero");

// --- Run log: flush-per-line, banner and rotation ------------------------------------------

// RunLogTests prints its own PASS/FAIL lines and its own RUNLOG-<n>/<n> verdict but only returns
// the failure count, so its output is captured here to fold the check count into the totals and
// then replayed verbatim.
var runLogBuffer = new StringWriter();
var captureOut = Console.Out;
Console.SetOut(runLogBuffer);
int runLogFailures;
try
{
    runLogFailures = RunLogTests.Run();
}
finally
{
    Console.SetOut(captureOut);
}

var runLogOut = runLogBuffer.ToString();
Console.Out.Write(runLogOut);
foreach (var line in runLogOut.Split('\n'))
{
    if (line.StartsWith("PASS ", StringComparison.Ordinal) || line.StartsWith("FAIL ", StringComparison.Ordinal))
    {
        checks++;
    }
}

failures += runLogFailures;

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ARGV-" + checks + "/" + checks : "ARGV-FAILED " + failures + "/" + checks);
return failures == 0 ? 0 : 1;
