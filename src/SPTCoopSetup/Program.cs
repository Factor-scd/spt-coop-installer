using System.Windows.Forms;

namespace SPTCoopSetup;

internal static class Program
{
    /// <summary>Set when <c>--auto</c> is on the command line: never block on a modal dialog.</summary>
    private static bool _auto;

    [STAThread]
    private static int Main(string[] argv)
    {
        try
        {
            PrepareTempDirectory();
            return Run(argv);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportFatal(ex);

            return 99;
        }
    }

    /// <summary>
    /// WinForms writes a temporary activation-context manifest the first time visual styles are
    /// enabled. A fresh service or RDP profile can have a %TEMP% that does not exist yet, which
    /// throws DirectoryNotFoundException during EnableVisualStyles and kills the process before any
    /// window is created - the user sees "nothing happens". Create it up front, and treat visual
    /// styles as cosmetic (this UI is custom-drawn).
    /// </summary>
    private static void PrepareTempDirectory()
    {
        try
        {
            var temp = Path.GetTempPath();
            if (!string.IsNullOrWhiteSpace(temp))
            {
                Directory.CreateDirectory(temp);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("temp directory unavailable: " + ex.Message);
        }
    }

    /// <summary>Last resort: leave a trace on disk and tell the user, instead of dying silently.</summary>
    private static void ReportFatal(Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SPTCoopSetup");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "crash.log"),
                DateTime.Now.ToString("s") + "  " + ex + Environment.NewLine);
        }
        catch
        {
            // nothing else we can do
        }

        try
        {
            MessageBox.Show(
                ex.Message,
                "SPT Co-op Setup could not start",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // no desktop to show it on
        }
    }

    private static int Run(string[] argv)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        try
        {
            Application.EnableVisualStyles();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("visual styles unavailable: " + ex.Message);
        }
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ShowCrash(e.ExceptionObject as Exception);

        var arguments = Arguments.Parse(argv);
        _auto = arguments.Auto;

        // --preview-ui renders the window and exits: no target validation, no elevation, no engine run.
        if (arguments.PreviewPath is { Length: > 0 } previewPath)
        {
            try
            {
                return PreviewRenderer.Run(previewPath, arguments);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.Error.WriteLine("preview failed: " + ex.Message);
                return 1;
            }
        }

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
