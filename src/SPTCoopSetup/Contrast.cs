using System.Drawing;
using System.Globalization;

namespace SPTCoopSetup;

/// <summary>One text/background pair the window paints, with its measured WCAG 2.1 ratio.</summary>
internal readonly record struct ContrastCheck(string Name, Color Foreground, Color Background, double Ratio);

/// <summary>
/// WCAG 2.1 relative luminance and contrast maths, plus the audit table of every text/background
/// pair the window actually uses. Kept dependency-free and pure so it can be asserted from tests.
/// </summary>
internal static class Contrast
{
    /// <summary>WCAG 2.1 AA minimum for normal-size text.</summary>
    public const double MinimumRatio = 4.5;

    /// <summary>Source-over composite of <paramref name="top"/> (may be translucent) onto <paramref name="bottom"/>.</summary>
    public static Color Composite(Color top, Color bottom)
    {
        var alpha = top.A / 255f;
        var inverse = 1f - alpha;
        return Color.FromArgb(
            CompositeChannel(top.R, bottom.R, alpha, inverse),
            CompositeChannel(top.G, bottom.G, alpha, inverse),
            CompositeChannel(top.B, bottom.B, alpha, inverse));
    }

    /// <summary>WCAG 2.1 contrast ratio of foreground over background; the foreground is alpha-composited onto the background first.</summary>
    public static double Ratio(Color foreground, Color background)
    {
        var ink = Composite(foreground, background);

        var lighter = Luminance(ink);
        var darker = Luminance(background);
        if (lighter < darker)
        {
            (lighter, darker) = (darker, lighter);
        }

        var ratio = (lighter + 0.05) / (darker + 0.05);
        return Math.Round(ratio, 4);
    }

    /// <summary>Every text/background pair the window actually uses.</summary>
    public static IReadOnlyList<ContrastCheck> Audit()
    {
        var scrimWorstCase = Composite(Theme.BgScrim, Color.White);

        return new List<ContrastCheck>
        {
            Check("primary-on-base", Theme.TextPrimary, Theme.BgBase),
            Check("primary-on-surface", Theme.TextPrimary, Theme.Surface),
            Check("primary-on-raised", Theme.TextPrimary, Theme.SurfaceRaised),
            Check("secondary-on-surface", Theme.TextSecondary, Theme.Surface),
            Check("secondary-on-raised", Theme.TextSecondary, Theme.SurfaceRaised),
            Check("accent-text-on-fill", Theme.AccentText, Theme.AccentFill),
            Check("primary-on-scrim", Theme.TextPrimary, scrimWorstCase),
            Check("secondary-on-scrim", Theme.TextSecondary, scrimWorstCase),

            // The game canvas: score, hints and the player glyph are drawn straight onto Track.
            Check("primary-on-track", Theme.TextPrimary, Theme.Track),
            Check("secondary-on-track", Theme.TextSecondary, Theme.Track),
            Check("accent-on-track", Theme.AccentFill, Theme.Track),
        };
    }

    /// <summary>True when every pair meets <see cref="MinimumRatio"/>.</summary>
    public static bool AllPass(IReadOnlyList<ContrastCheck> checks)
    {
        foreach (var check in checks)
        {
            if (check.Ratio < MinimumRatio)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>One machine-readable line: "contrast &lt;name&gt; &lt;fg&gt; on &lt;bg&gt; = &lt;ratio&gt;".</summary>
    public static string Describe(ContrastCheck check)
    {
        var ratio = check.Ratio.ToString("0.00", CultureInfo.InvariantCulture);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"contrast {check.Name} {Hex(check.Foreground)} on {Hex(check.Background)} = {ratio}");
    }

    private static ContrastCheck Check(string name, Color foreground, Color background) =>
        new(name, foreground, background, Ratio(foreground, background));

    private static int CompositeChannel(byte top, byte bottom, float alpha, float inverse) =>
        (int)Math.Round(top * alpha + bottom * inverse);

    private static double Luminance(Color color)
    {
        var r = Linearize(color.R);
        var g = Linearize(color.G);
        var b = Linearize(color.B);
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    private static double Linearize(byte channel)
    {
        var s = channel / 255d;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    private static string Hex(Color color) =>
        string.Create(CultureInfo.InvariantCulture, $"#{color.R:X2}{color.G:X2}{color.B:X2}");
}
