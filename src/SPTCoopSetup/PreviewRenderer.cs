using System.Drawing.Imaging;

namespace SPTCoopSetup;

/// <summary>
/// Renders the real window to a PNG at a fixed size and prints the contrast audit, so the
/// theme can be inspected from a build script instead of by a human with a screenshot tool.
/// </summary>
internal static class PreviewRenderer
{
    public const int Width = 1100;
    public const int Height = 720;

    /// <summary>
    /// Lays the real window out at <see cref="Width"/>x<see cref="Height"/>, renders it to
    /// <paramref name="outputPath"/> and prints one contrast line per text colour pair.
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

        using var form = new MainForm(arguments);

        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-32000, -32000);   // off-screen: no flash on the desktop
        form.ShowInTaskbar = false;
        form.Show();                                 // realises handles + child layout (no engine run: PreviewPath is set)
        form.ClientSize = new Size(Width, Height);   // set AFTER Show so DPI autoscaling cannot override it
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

        var lines = new List<string> { $"preview {fullPath} {Width}x{Height}" };
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
