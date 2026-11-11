using System.Text;

namespace SPTCoopSetup;

/// <summary>
/// Win32 command-line quoting for <see cref="System.Diagnostics.ProcessStartInfo"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>ProcessStartInfo.ArgumentList</c> is only honoured when <c>UseShellExecute</c> is
/// <c>false</c>. The elevated relaunch needs <c>UseShellExecute = true</c> for the
/// <c>runas</c> verb, so every argument has to be folded into the single
/// <c>ProcessStartInfo.Arguments</c> string that the shell does pass through.
/// </para>
/// <para>
/// The rules are the documented <c>CommandLineToArgvW</c> ones: surround an argument with
/// double quotes when it is empty or contains a space or a quote, double every backslash
/// that immediately precedes a quote or the closing quote, and escape an embedded quote
/// with a backslash.
/// </para>
/// </remarks>
internal static class ArgumentQuoting
{
    /// <summary>Folds discrete arguments into one command line the Windows shell will pass through intact.</summary>
    public static string Join(IEnumerable<string> arguments)
    {
        var builder = new StringBuilder();
        foreach (var argument in arguments)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            Append(builder, argument ?? string.Empty);
        }

        return builder.ToString();
    }

    /// <summary>Splits a command line the way <c>CommandLineToArgvW</c> does. Used by the round-trip test.</summary>
    public static IReadOnlyList<string> Split(string commandLine)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(commandLine))
        {
            return result;
        }

        var current = new StringBuilder();
        var inQuotes = false;
        var haveArgument = false;
        var index = 0;

        while (index < commandLine.Length)
        {
            var character = commandLine[index];

            if (character == '\\')
            {
                var backslashes = 0;
                while (index < commandLine.Length && commandLine[index] == '\\')
                {
                    backslashes++;
                    index++;
                }

                if (index < commandLine.Length && commandLine[index] == '"')
                {
                    current.Append('\\', backslashes / 2);
                    if (backslashes % 2 == 1)
                    {
                        current.Append('"');
                        index++;
                    }

                    haveArgument = true;
                    continue;
                }

                current.Append('\\', backslashes);
                haveArgument = true;
                continue;
            }

            if (character == '"')
            {
                if (inQuotes && index + 1 < commandLine.Length && commandLine[index + 1] == '"')
                {
                    current.Append('"');
                    index += 2;
                    haveArgument = true;
                    continue;
                }

                inQuotes = !inQuotes;
                haveArgument = true;
                index++;
                continue;
            }

            if (!inQuotes && (character == ' ' || character == '\t'))
            {
                if (haveArgument)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    haveArgument = false;
                }

                index++;
                continue;
            }

            current.Append(character);
            haveArgument = true;
            index++;
        }

        if (haveArgument)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    private static void Append(StringBuilder builder, string argument)
    {
        var needsQuotes = argument.Length == 0 || argument.AsSpan().IndexOfAny(' ', '\t', '"') >= 0;
        if (!needsQuotes)
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');
        for (var index = 0; index < argument.Length; index++)
        {
            var backslashes = 0;
            while (index < argument.Length && argument[index] == '\\')
            {
                backslashes++;
                index++;
            }

            if (index == argument.Length)
            {
                // Trailing backslashes must be doubled so they cannot escape the closing quote.
                builder.Append('\\', backslashes * 2);
                break;
            }

            if (argument[index] == '"')
            {
                builder.Append('\\', (backslashes * 2) + 1);
                builder.Append('"');
            }
            else
            {
                builder.Append('\\', backslashes);
                builder.Append(argument[index]);
            }
        }

        builder.Append('"');
    }
}
