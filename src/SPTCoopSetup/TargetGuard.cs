namespace SPTCoopSetup;

/// <summary>
/// The one place that decides whether a target folder can host the selected mode.
/// </summary>
/// <remarks>
/// Both entry points call this: <see cref="Program"/> before it decides anything about
/// elevation (so a wrong folder never raises a UAC prompt), and <see cref="MainForm"/> when
/// the user presses Start. Keeping one implementation means one message, not two drifting
/// copies of it.
/// </remarks>
internal static class TargetGuard
{
    /// <summary>True when the mode needs an SPT install that already exists.</summary>
    public static bool NeedsExistingInstall(InstallMode mode) => mode is InstallMode.Mods or InstallMode.Update;

    /// <summary>
    /// Checks <paramref name="path"/> for the mode. Returns false with a user-facing
    /// <paramref name="message"/> when the run must not start.
    /// </summary>
    public static bool TryValidate(InstallMode mode, string path, out string message)
    {
        message = string.Empty;
        if (!NeedsExistingInstall(mode))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            message = Arguments.ModeTitle(mode) + " needs an existing SPT install, and no folder was given.";
            return false;
        }

        // The engine writes into <target>\SPT_Runtime, so the server exe is the marker that matters.
        var serverExe = Path.Combine(path, "SPT_Runtime", "SPT.Server.exe");
        if (File.Exists(serverExe))
        {
            return true;
        }

        message = Arguments.ModeTitle(mode) + " needs an existing SPT install, and this file was not found:\r\n\r\n" + serverExe +
                  "\r\n\r\nPick the folder that contains SPT_Runtime, or use \"Full install\" instead.";
        return false;
    }
}
