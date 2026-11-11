using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows.Forms;

namespace SPTCoopSetup;

/// <summary>
/// Elevation decided in code rather than by the application manifest.
/// </summary>
/// <remarks>
/// Deliberate deviation from "one manifest, one privilege level": the deliverable is a single
/// self-contained exe, so <c>requireAdministrator</c> would raise a UAC prompt for every launch -
/// including "mods only", which has to work from a plain user session on an already-modded tree.
/// The manifest therefore says <c>asInvoker</c> and this class relaunches the exe with
/// <c>runas</c> only when the selected mode (or the target tree's ACLs) actually needs admin.
/// </remarks>
internal static class Elevation
{
    /// <summary>ERROR_CANCELLED - the user answered "No" to the UAC prompt.</summary>
    private const int ErrorCancelled = 1223;

    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or SystemException)
        {
            // No identity (or no Windows security stack) means we cannot prove we are admin.
            return false;
        }
    }

    /// <summary>
    /// Full and update always write outside the user's profile and into the game tree, so they always
    /// elevate. Mods only elevates solely when the existing tree refuses a probe write.
    /// </summary>
    public static bool NeedsAdmin(InstallMode mode, string targetPath)
        => mode is InstallMode.Full or InstallMode.Update || !IsTreeWritable(targetPath);

    /// <summary>Creates and deletes a throwaway file to find out whether the tree is writable by us.</summary>
    public static bool IsTreeWritable(string targetPath)
    {
        foreach (var directory in ProbeDirectories(targetPath))
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                continue;
            }

            var probe = Path.Combine(directory, ".sptsetup-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
                {
                }

                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
            {
                return false;
            }
        }

        // Nothing we could probe (folder does not exist yet) - treat as "needs admin".
        return false;
    }

    /// <summary>
    /// Restarts this exe through the shell's runas verb. Returns false when the user declined the
    /// UAC prompt (Win32 1223) or the shell could not start the process at all.
    /// </summary>
    public static bool RelaunchElevated(IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(Application.ExecutablePath)
        {
            UseShellExecute = true,
            Verb = "runas",
        };

        // ArgumentList is IGNORED when UseShellExecute is true (and runas needs it true), which
        // silently dropped --mode/--path/--auto on every elevated relaunch. Arguments IS passed
        // through by the shell, so quote the list into that single string instead.
        startInfo.Arguments = ArgumentQuoting.Join(arguments);

        try
        {
            using var elevated = Process.Start(startInfo);
            return elevated is not null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            // The user clicked "No". Not an error worth a stack trace.
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static IEnumerable<string> ProbeDirectories(string targetPath)
    {
        // The engine writes mods into <target>\SPT_Runtime, so that folder is the real question.
        yield return Path.Combine(targetPath, "SPT_Runtime");
        yield return targetPath;
    }
}
