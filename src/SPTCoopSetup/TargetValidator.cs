namespace SPTCoopSetup;

/// <summary>The one guard that decides whether a mode can run against a folder at all.</summary>
/// <remarks>
/// Shared by <see cref="Program"/> and <see cref="MainForm"/> so the message exists exactly once.
/// <see cref="Program"/> calls it before any elevation decision: asking for UAC and only then
/// reporting "that folder is not an SPT install" wastes the user's click, and in a non-interactive
/// session the prompt is invisible, so the run hangs instead of failing.
/// </remarks>
internal static class TargetValidator
{
    /// <summary>Null when the mode can run against <paramref name="targetPath"/>, otherwise the message to show.</summary>
    public static string? Validate(InstallMode mode, string targetPath)
    {
        if (mode is not (InstallMode.Mods or InstallMode.Update))
        {
            return null;
        }

        var serverExe = Path.Combine(targetPath, "SPT_Runtime", "SPT.Server.exe");
        if (File.Exists(serverExe))
        {
            return null;
        }

        return Arguments.ModeTitle(mode) + " needs an existing SPT install, and this file was not found:\r\n\r\n"
            + serverExe
            + "\r\n\r\nPick the folder that contains SPT_Runtime, or use \"Full install\" instead.";
    }
}
