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

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ARGV-" + checks + "/" + checks : "ARGV-FAILED " + failures + "/" + checks);
return failures == 0 ? 0 : 1;
