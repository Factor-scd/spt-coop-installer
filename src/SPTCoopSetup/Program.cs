using System.Windows.Forms;

namespace SPTCoopSetup;

internal static class Program
{
    /// <summary>Set when <c>--auto</c> is on the command line: never block on a modal dialog.</summary>
    private static bool _auto;

    [STAThread]
    private static int Main(string[] argv)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ShowCrash(e.ExceptionObject as Exception);

        var arguments = Arguments.Parse(argv);
        _auto = arguments.Auto;

        // Validate the target BEFORE any elevation decision. A missing SPT install must produce
        // the friendly message, not a UAC prompt followed by the same message.
        if (arguments.Mode.HasValue &&
            !TargetGuard.TryValidate(arguments.Mode.Value, arguments.TargetPath ?? Arguments.DefaultTargetPath, out var guardMessage))
        {
            return Fail(guardMessage);
        }

        // Elevate before any window exists: the mode decides the privilege level, and a relaunch
        // would throw away whatever the user had already typed.
        if (arguments.Mode.HasValue && !EnsureElevation(arguments))
        {
            return 1;
        }

        using var form = new MainForm(arguments);
        Application.Run(form);
        return form.ExitCode;
    }

    private static bool EnsureElevation(Arguments arguments)
    {
        var mode = arguments.Mode!.Value;
        var target = arguments.TargetPath ?? Arguments.DefaultTargetPath;
        if (Elevation.IsElevated() || !Elevation.NeedsAdmin(mode, target))
        {
            return true;
        }

        var relaunch = new List<string> { "--mode", Arguments.ModeArgument(mode), "--path", target };
        if (arguments.Auto)
        {
            relaunch.Add("--auto");
        }

        if (Elevation.RelaunchElevated(relaunch))
        {
            return false;
        }

        // Declined (or the shell refused). No stack trace, no half-done install.
        Fail("Administrator rights are required for a " + Arguments.ModeTitle(mode).ToLowerInvariant() + " into " + target + ".");
        return false;
    }

    /// <summary>
    /// Reports a refusal without ever blocking an unattended run: <c>--auto</c> writes to
    /// stderr and exits, everything else shows the message box.
    /// </summary>
    private static int Fail(string message)
    {
        if (_auto)
        {
            try
            {
                Console.Error.WriteLine(message);
            }
            catch (IOException)
            {
                // No stderr to write to (WinExe under a detached host) - the exit code carries it.
            }

            return 2;
        }

        MessageBox.Show(message, "SPT Co-op Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return 2;
    }

    private static void ShowCrash(Exception? exception)
    {
        var message = exception?.Message ?? "unknown error";
        var text = "SPT Co-op Setup hit an unexpected error:\r\n\r\n" + message;

        if (_auto)
        {
            try
            {
                Console.Error.WriteLine(text);
            }
            catch (IOException)
            {
                // Nothing to write to - the non-zero exit code is the signal.
            }

            return;
        }

        try
        {
            MessageBox.Show(
                text,
                "SPT Co-op Setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Nothing left to show the message with.
        }
    }
}
