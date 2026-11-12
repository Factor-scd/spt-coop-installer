using System.Drawing.Imaging;

namespace SPTCoopSetup;

/// <summary>
/// Renders the real window to a PNG at a fixed size and prints the contrast audit, so the
/// theme can be inspected from a build script instead of by a human with a screenshot tool.
/// </summary>
internal static class PreviewRenderer
{
    public const int Width = 1180;
    public const int Height = 760;

    /// <summary>
    /// Lays the real window out at <see cref="Width"/>x<see cref="Height"/> in the shape
    /// <see cref="Arguments.PreviewState"/> names (running when it is absent), renders it to
    /// <paramref name="outputPath"/> and prints one contrast line per text colour pair plus the
    /// layout assertion for the captured shape.
    /// Returns 0 on success, 3 when any ratio is below <see cref="Contrast.MinimumRatio"/>.
    /// </summary>
    public static int Run(string outputPath, Arguments arguments)
    {
        var fullPath = Path.GetFullPath(outputPath.Trim().Trim('"'));

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // A missing switch keeps the pre-existing running two-column capture.
        var state = arguments.PreviewState ?? LayoutKind.Running;

        using var form = new MainForm(arguments);

        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-32000, -32000);   // off-screen: no flash on the desktop
        form.ShowInTaskbar = false;
        form.Show();                                 // realises handles + child layout (no engine run: PreviewPath is set)

        // Settle the shape before the capture: animate:false skips the 350 ms reveal, so the PNG is
        // the final geometry rather than a mid-animation frame. OnShown has already applied it for the
        // preview path; this only steps in if it has not, because re-applying the running shape calls
        // SnapToRunning -> StartGameLoop, which would undo the StopLoop that keeps the capture still.
        if (form.LayoutState != state)
        {
            form.SetLayoutState(state, animate: false);
        }

        // MinimumSize is DPI-scaled; on a 125%/150% host it would clamp the capture above the
        // proof size, so the constraint is dropped for the render only.
        form.MinimumSize = Size.Empty;
        form.MaximumSize = Size.Empty;
        form.ClientSize = new Size(Width, Height);   // set AFTER Show so DPI autoscaling cannot override it
        form.PerformLayout();
        Application.DoEvents();

        // The column widths follow the content width and are recomputed by the root panel's resize
        // handler; one more layout pass with the forced size in place makes sure the assertion below
        // reports the captured geometry, not the sizes the window had before the resize.
        form.PerformLayout();
        Application.DoEvents();

        using (var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
        {
            form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            bitmap.Save(fullPath, ImageFormat.Png);
        }

        form.Hide();

        var checks = Contrast.Audit();
        var allPass = Contrast.AllPass(checks);

        var lines = new List<string>
        {
            $"preview {fullPath} {Width}x{Height}",
            LayoutState.AssertionLine(form.LayoutState, form.ConfigColumnWidth, form.GameColumnWidth),
        };
        lines.AddRange(checks.Select(Contrast.Describe));
        lines.Add($"contrast minimum {Contrast.MinimumRatio:0.00} {(allPass ? "ok" : "FAILED")}");

        foreach (var line in lines)
        {
            Console.Out.WriteLine(line);
        }

        Console.Out.Flush();

        // A WinExe started without an inherited stdout would lose the audit, so mirror it to disk.
        try
        {
            File.WriteAllLines(fullPath + ".contrast.txt", lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The sidecar is a convenience; the PNG and the exit code already carry the result.
        }

        return allPass ? 0 : 3;
    }
}
