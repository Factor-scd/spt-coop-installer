using System.Text;

namespace SPTCoopSetup;

/// <summary>Builds the single command-line string that <see cref="System.Diagnostics.ProcessStartInfo.Arguments"/> needs.</summary>
/// <remarks>
/// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> is ignored when
/// <c>UseShellExecute = true</c>, and the <c>runas</c> verb only works under shell execute - so the
/// elevated relaunch has to quote its own arguments. The quoting below follows the rules every
/// Windows CRT program (and therefore <c>CommandLineToArgvW</c>) uses to split a command line back
/// into arguments: double the backslashes in front of a quote and one for the quote itself, double
/// the backslashes in front of the closing quote, wrap in quotes when the argument contains a space,
/// a tab or a quote, and write the empty argument as <c>""</c>.
/// </remarks>
internal static class CommandLine
{
    /// <summary>Joins arguments into one command line, quoting each of them.</summary>
    public static string Join(IEnumerable<string> arguments)
        => string.Join(' ', arguments.Select(Quote));

    /// <summary>Quotes one argument so a CRT-style parser yields it back unchanged.</summary>
    public static string Quote(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        var needsQuotes = false;
        foreach (var ch in value)
        {
            if (ch is ' ' or '\t' or '"')
            {
                needsQuotes = true;
                break;
            }
        }

        if (!needsQuotes)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');

        var backslashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }

            // Backslashes are only special in front of a quote: double them there (plus one escape
            // slash for the quote), otherwise emit them untouched.
            builder.Append('\\', ch == '"' ? (backslashes * 2) + 1 : backslashes);
            backslashes = 0;
            builder.Append(ch);
        }

        // Trailing backslashes would otherwise escape the closing quote.
        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }
}
