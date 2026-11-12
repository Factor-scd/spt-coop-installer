using System.Globalization;

namespace SPTCoopSetup;

/// <summary>What the host asked the engine to do.</summary>
internal enum InstallMode
{
    Full,
    Mods,
    Update,
}

/// <summary>
/// Command line for <c>SPTCoopSetup.exe</c>:
/// <c>--mode &lt;full|mods|update&gt; --path &lt;dir&gt; [--auto] [--preview-ui &lt;path.png&gt;] [--test-game &lt;ticks&gt;]</c>.
/// Both <c>--mode=value</c> and <c>--mode value</c> are accepted, unknown options are
/// collected instead of rejected so a stale shortcut can never block a user.
/// </summary>
internal sealed class Arguments
{
    public const string DefaultTargetPath = @"C:\SPT";

    /// <summary>Upper bound for <c>--test-game</c>: a typo such as 20000000 must not hang the build.</summary>
    private const int TestGameTickLimit = 200000;

    private Arguments(string[] argv)
    {
        // The audit trail for the run log's banner: every entry exactly as received, in order,
        // before any parsing normalises a thing. Empty entries carry no information.
        var raw = new List<string>(argv.Length);
        foreach (var entry in argv)
        {
            if (!string.IsNullOrEmpty(entry))
            {
                raw.Add(entry);
            }
        }

        Raw = raw;
    }

    /// <summary>Selected mode, or null when the user has to choose.</summary>
    public InstallMode? Mode { get; private set; }

    /// <summary>Prefilled target folder, or null when the default should be used.</summary>
    public string? TargetPath { get; private set; }

    /// <summary>Set by <c>--auto</c>: never show a modal dialog (for unattended runs).</summary>
    public bool Auto { get; private set; }

    /// <summary>Set by <c>--preview-ui &lt;path&gt;</c>: render the window to that PNG and exit (no install run).</summary>
    public string? PreviewPath { get; private set; }

    /// <summary>Set by <c>--test-game &lt;ticks&gt;</c>: run the game headlessly for that many 16 ms ticks and exit.</summary>
    public int? TestGameTicks { get; private set; }

    /// <summary>Every argv entry as received, verbatim and in order (for the run log's banner).</summary>
    public IReadOnlyList<string> Raw { get; }

    /// <summary>Arguments we did not understand - logged once, then ignored.</summary>
    public List<string> Ignored { get; } = new();

    /// <summary>True when the caller supplied both a mode and a folder, i.e. start without asking.</summary>
    public bool ShouldStartImmediately => Mode.HasValue && TargetPath is not null;

    public static Arguments Parse(string[] argv)
    {
        var result = new Arguments(argv);
        for (var index = 0; index < argv.Length; index++)
        {
            var raw = argv[index] ?? string.Empty;
            if (raw.Length == 0)
            {
                continue;
            }

            var (name, inlineValue) = SplitOption(raw);
            switch (name.ToLowerInvariant())
            {
                case "--mode":
                {
                    var value = inlineValue ?? TakeValue(argv, ref index);
                    if (value is null)
                    {
                        result.Ignored.Add(raw);
                    }
                    else if (TryParseMode(value, out var mode))
                    {
                        result.Mode = mode;
                    }
                    else
                    {
                        result.Ignored.Add(raw + " " + value);
                    }

                    break;
                }

                case "--path":
                {
                    var value = inlineValue ?? TakeValue(argv, ref index);
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        result.Ignored.Add(raw);
                    }
                    else
                    {
                        result.TargetPath = value.Trim().Trim('"');
                    }

                    break;
                }

                case "--auto":
                    result.Auto = true;
                    break;

                case "--preview-ui":
                {
                    var value = inlineValue ?? TakeValue(argv, ref index);
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        result.Ignored.Add(raw);
                    }
                    else
                    {
                        result.PreviewPath = value.Trim().Trim('"');
                    }

                    break;
                }

                case "--test-game":
                {
                    var value = inlineValue ?? TakeValue(argv, ref index);
                    if (value is null)
                    {
                        result.Ignored.Add(raw);
                    }
                    else if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) &&
                             ticks > 0 &&
                             ticks <= TestGameTickLimit)
                    {
                        result.TestGameTicks = ticks;
                    }
                    else
                    {
                        result.Ignored.Add(raw + " " + value);
                    }

                    break;
                }

                default:
                    result.Ignored.Add(raw);
                    break;
            }
        }

        return result;
    }

    public static bool TryParseMode(string value, out InstallMode mode)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "full":
            case "install":
                mode = InstallMode.Full;
                return true;
            case "mods":
            case "mod":
            case "mods-only":
                mode = InstallMode.Mods;
                return true;
            case "update":
                mode = InstallMode.Update;
                return true;
            default:
                mode = InstallMode.Full;
                return false;
        }
    }

    /// <summary>The CLI spelling of a mode, used when the host relaunches itself elevated.</summary>
    public static string ModeArgument(InstallMode mode) => mode switch
    {
        InstallMode.Full => "full",
        InstallMode.Mods => "mods",
        InstallMode.Update => "update",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "unknown install mode"),
    };

    public static string ModeTitle(InstallMode mode) => mode switch
    {
        InstallMode.Full => "Full install",
        InstallMode.Mods => "Mods only",
        InstallMode.Update => "Update",
        _ => mode.ToString(),
    };

    /// <summary>"--mode=value" -&gt; ("--mode", "value"); anything else keeps its name and no value.</summary>
    private static (string Name, string? Value) SplitOption(string raw)
    {
        if (!raw.StartsWith("--", StringComparison.Ordinal))
        {
            return (raw, null);
        }

        var separator = raw.IndexOf('=');
        if (separator <= 2)
        {
            return (raw, null);
        }

        return (raw[..separator], raw[(separator + 1)..]);
    }

    /// <summary>Consumes the next argv entry as this option's value, unless it looks like another option.</summary>
    private static string? TakeValue(string[] argv, ref int index)
    {
        if (index + 1 >= argv.Length)
        {
            return null;
        }

        var candidate = argv[index + 1];
        if (candidate is null || candidate.StartsWith("--", StringComparison.Ordinal))
        {
            return null;
        }

        index++;
        return candidate;
    }
}
