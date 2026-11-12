using System.Globalization;

namespace SPTCoopSetup;

/// <summary>
/// Geometry regression tests for <see cref="LayoutState"/>. Standalone on purpose: the project has no
/// test framework and must run on the Linux build host, where there is no window to measure.
/// </summary>
internal static class LayoutStateTests
{
    /// <summary>Width of the window the installer actually opens with, minus the 12px padding on both sides.</summary>
    private const float DefaultContent = 1156f;

    /// <summary>Float comparison slack: the widths are compared after easing, so exact equality is unfair.</summary>
    private const float Tolerance = 0.001f;

    private static int _checks;
    private static int _failures;

    /// <summary>Number of checks executed by the last <see cref="Run"/>.</summary>
    public static int Checks
    {
        get { return _checks; }
    }

    /// <summary>Number of checks that failed in the last <see cref="Run"/>.</summary>
    public static int Failures
    {
        get { return _failures; }
    }

    /// <summary>Runs every check and returns the failure count.</summary>
    public static int Run()
    {
        _checks = 0;
        _failures = 0;

        ContentWidthFloor();
        IdleShape();
        RunningTarget();
        CapHolds();
        RightWidths();
        Easing();
        Animation();
        Lines();

        Console.WriteLine();
        return _failures;
    }

    private static void ContentWidthFloor()
    {
        Check(
            "layout/content width subtracts the padding",
            Near(LayoutState.ContentWidth(1180, 24), DefaultContent),
            LayoutState.ContentWidth(1180, 24) + " vs " + DefaultContent);

        // A window smaller than its own padding must not hand a zero width to the layout engine.
        Check(
            "layout/content width floors at 1",
            Near(LayoutState.ContentWidth(10, 24), 1f) && Near(LayoutState.ContentWidth(0, 0), 1f),
            LayoutState.ContentWidth(10, 24) + " and " + LayoutState.ContentWidth(0, 0));
    }

    private static void IdleShape()
    {
        Check(
            "layout/idle left width is the content width",
            Near(LayoutState.IdleLeftWidth(DefaultContent), DefaultContent)
                && Near(LayoutState.LeftWidthFor(LayoutKind.Idle, DefaultContent, 1f), DefaultContent),
            LayoutState.LeftWidthFor(LayoutKind.Idle, DefaultContent, 1f) + " vs " + DefaultContent);

        Check(
            "layout/idle game column is hidden",
            Near(LayoutState.GameWidthFor(LayoutKind.Idle, DefaultContent, 1f), 0f),
            LayoutState.GameWidthFor(LayoutKind.Idle, DefaultContent, 1f).ToString("0.###", CultureInfo.InvariantCulture));

        // Nothing is left for the game while idle, and the clamp keeps that at zero rather than -1.
        Check(
            "layout/idle right width is zero",
            Near(LayoutState.RightWidth(DefaultContent, LayoutState.IdleLeftWidth(DefaultContent)), 0f),
            LayoutState.RightWidth(DefaultContent, LayoutState.IdleLeftWidth(DefaultContent)).ToString("0.###", CultureInfo.InvariantCulture));
    }

    private static void RunningTarget()
    {
        // 46% of 1000 is 460: under the floor, so the floor is what the column reports.
        Check(
            "layout/target left width floors at 520 * scale",
            Near(LayoutState.TargetLeftWidth(1000f, 1f), LayoutState.MinLeftWidth)
                && Near(LayoutState.TargetLeftWidth(1000f, 1.5f), LayoutState.MinLeftWidth * 1.5f),
            LayoutState.TargetLeftWidth(1000f, 1f) + " and " + LayoutState.TargetLeftWidth(1000f, 1.5f));

        // 46% of 2600 is 1196: over the ceiling, so the ceiling wins even on a very wide window.
        Check(
            "layout/target left width caps at 640 * scale",
            Near(LayoutState.TargetLeftWidth(2600f, 1f), LayoutState.MaxLeftWidth)
                && Near(LayoutState.TargetLeftWidth(2600f, 1.5f), LayoutState.MaxLeftWidth * 1.5f),
            LayoutState.TargetLeftWidth(2600f, 1f) + " and " + LayoutState.TargetLeftWidth(2600f, 1.5f));

        // In the middle of the band the fraction wins, unclamped.
        Check(
            "layout/target left width follows 46 percent inside the band",
            Near(LayoutState.TargetLeftWidth(1300f, 1f), 598f),
            LayoutState.TargetLeftWidth(1300f, 1f).ToString("0.###", CultureInfo.InvariantCulture));

        // A degenerate scale (no DPI at all) must still leave a usable column, never 0 or a throw.
        Check(
            "layout/target left width never drops below 1",
            Near(LayoutState.TargetLeftWidth(0f, 0f), 1f) && LayoutState.TargetLeftWidth(1f, 1f) >= 1f,
            LayoutState.TargetLeftWidth(0f, 0f) + " and " + LayoutState.TargetLeftWidth(1f, 1f));
    }

    private static void CapHolds()
    {
        // The 55% ceiling exists so the left column cannot eat the game column. On a narrow window it
        // coincides with the 520 floor, which is exactly where a naive 46% would otherwise be overruled.
        var narrow = 900f;
        var narrowCap = MathF.Max(LayoutState.MinLeftWidth, narrow * LayoutState.MaxLeftFraction);
        Check(
            "layout/narrow window stops at the 55 percent cap",
            Near(LayoutState.TargetLeftWidth(narrow, 1f), narrowCap) && narrowCap <= LayoutState.MinLeftWidth + Tolerance,
            LayoutState.TargetLeftWidth(narrow, 1f) + " vs cap " + narrowCap);

        var capHolds = true;
        var floorHolds = true;
        for (var content = 200f; content <= 3000f; content += 50f)
        {
            foreach (var scale in new[] { 0.75f, 1f, 1.25f, 1.5f })
            {
                var width = LayoutState.TargetLeftWidth(content, scale);
                var cap = MathF.Max(LayoutState.MinLeftWidth * scale, content * LayoutState.MaxLeftFraction);
                if (width > cap + Tolerance) capHolds = false;
                if (width < MathF.Max(1f, LayoutState.MinLeftWidth * scale) - Tolerance) floorHolds = false;
            }
        }

        Check("layout/target left width stays under the cap everywhere", capHolds, "a width exceeded the 55% ceiling");
        Check("layout/target left width stays above the floor everywhere", floorHolds, "a width fell under the 520 floor");
    }

    private static void RightWidths()
    {
        var left = LayoutState.TargetLeftWidth(DefaultContent, 1f);
        var right = LayoutState.GameWidthFor(LayoutKind.Running, DefaultContent, 1f);

        Check(
            "layout/running right width is the remainder",
            Near(right, DefaultContent - left - LayoutState.DividerWidth),
            right + " vs " + (DefaultContent - left - LayoutState.DividerWidth));

        Check(
            "layout/running left and right fill the content",
            Near(left + right + LayoutState.DividerWidth, DefaultContent),
            (left + right + LayoutState.DividerWidth) + " vs " + DefaultContent);

        // A left column wider than the window would otherwise produce a negative child width.
        Check(
            "layout/right width never goes negative",
            Near(LayoutState.RightWidth(400f, 800f), 0f) && Near(LayoutState.RightWidth(0f, 0f), 0f),
            LayoutState.RightWidth(400f, 800f) + " and " + LayoutState.RightWidth(0f, 0f));
    }

    private static void Easing()
    {
        Check(
            "layout/ease starts at 0 and ends at 1",
            Near(LayoutState.EaseOutCubic(0d), 0d) && Near(LayoutState.EaseOutCubic(1d), 1d),
            LayoutState.EaseOutCubic(0d) + " and " + LayoutState.EaseOutCubic(1d));

        // 1 - 0.5^3 = 0.875: the curve is most of the way there at the halfway point.
        Check(
            "layout/ease is 0.875 at the halfway point",
            Math.Abs(LayoutState.EaseOutCubic(0.5d) - 0.875d) <= 0.01d,
            LayoutState.EaseOutCubic(0.5d).ToString("0.####", CultureInfo.InvariantCulture));

        var monotonic = true;
        var previous = 0d;
        for (var step = 0; step <= 20; step++)
        {
            var current = LayoutState.EaseOutCubic(step / 20d);
            if (current < previous) monotonic = false;
            previous = current;
        }

        Check("layout/ease is monotonic", monotonic, "the curve went backwards");

        // Elapsed time can overshoot a frame; the curve must not extrapolate past the target.
        Check(
            "layout/ease clamps outside 0..1",
            Near(LayoutState.EaseOutCubic(-1d), 0d) && Near(LayoutState.EaseOutCubic(2d), 1d),
            LayoutState.EaseOutCubic(-1d) + " and " + LayoutState.EaseOutCubic(2d));
    }

    private static void Animation()
    {
        var from = LayoutState.IdleLeftWidth(DefaultContent);
        var to = LayoutState.TargetLeftWidth(DefaultContent, 1f);

        Check(
            "layout/animation starts at the idle width",
            Near(LayoutState.LeftWidthAt(from, to, 0), from),
            LayoutState.LeftWidthAt(from, to, 0) + " vs " + from);

        // Landing exactly on the target matters: the shrink path settles into the game column.
        Check(
            "layout/animation lands exactly on the target",
            LayoutState.LeftWidthAt(from, to, LayoutState.AnimationMilliseconds) == to
                && LayoutState.LeftWidthAt(from, to, 5000) == to,
            LayoutState.LeftWidthAt(from, to, LayoutState.AnimationMilliseconds) + " vs " + to);

        // Growing back out has to land just as exactly, in the other direction.
        Check(
            "layout/animation lands exactly when growing",
            LayoutState.LeftWidthAt(to, from, LayoutState.AnimationMilliseconds) == from
                && LayoutState.LeftWidthAt(to, from, LayoutState.AnimationMilliseconds / 2) >= to,
            LayoutState.LeftWidthAt(to, from, LayoutState.AnimationMilliseconds) + " vs " + from);

        // Shrinking: the ease-out curve is already past the halfway point at 175ms.
        var middle = LayoutState.LeftWidthAt(from, to, LayoutState.AnimationMilliseconds / 2);
        Check(
            "layout/animation stays between the endpoints",
            middle < from && middle > to && middle < (from + to) / 2f,
            middle + " not inside (" + to + ", " + from + ") past the midpoint");

        Check(
            "layout/animation is complete at 350ms",
            !LayoutState.AnimationComplete(LayoutState.AnimationMilliseconds - 1)
                && LayoutState.AnimationComplete(LayoutState.AnimationMilliseconds)
                && !LayoutState.AnimationComplete(0)
                && LayoutState.AnimationComplete(5000),
            "boundary at " + LayoutState.AnimationMilliseconds + "ms is wrong");
    }

    private static void Lines()
    {
        const string idleLine = "layout state=idle game=hidden width=1156";
        const string runningLine = "layout state=running game=visible leftWidth=532 rightWidth=623";

        Check(
            "layout/idle line is byte-for-byte",
            LayoutState.AssertionLine(LayoutKind.Idle, 1156, 0) == idleLine,
            LayoutState.AssertionLine(LayoutKind.Idle, 1156, 0));

        Check(
            "layout/running line is byte-for-byte",
            LayoutState.AssertionLine(LayoutKind.Running, 532, 623) == runningLine,
            LayoutState.AssertionLine(LayoutKind.Running, 532, 623));

        // The assertion and the run log must be the same sentence, or a diff of the two lies.
        Check(
            "layout/log line matches the assertion line",
            LayoutState.LogLine(LayoutKind.Idle, 1156, 0) == idleLine
                && LayoutState.LogLine(LayoutKind.Running, 532, 623) == runningLine,
            LayoutState.LogLine(LayoutKind.Running, 532, 623));

        // End to end: the numbers in the line are the geometry the layout computes.
        var content = LayoutState.ContentWidth(1180, 24);
        var left = (int)LayoutState.LeftWidthFor(LayoutKind.Running, content, 1f);
        var game = (int)LayoutState.GameWidthFor(LayoutKind.Running, content, 1f);
        Check(
            "layout/running line follows the computed geometry",
            LayoutState.LogLine(LayoutKind.Running, left, game) == runningLine,
            LayoutState.LogLine(LayoutKind.Running, left, game));
    }

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) <= Tolerance;

    private static void Check(string name, bool condition, string? detail = null)
    {
        _checks++;
        if (condition)
        {
            Console.WriteLine("PASS " + name);
            return;
        }

        _failures++;
        Console.WriteLine("FAIL " + name + (detail is null ? "" : " -> " + detail));
    }
}
