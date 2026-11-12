using System.Globalization;

namespace SPTCoopSetup;

/// <summary>Which of the two window shapes the form is currently showing.</summary>
internal enum LayoutKind
{
    /// <summary>Configuration only: the left column fills the content width and the game is hidden.</summary>
    Idle,

    /// <summary>Install running: the left column gives the game the rest of the content width.</summary>
    Running,
}

/// <summary>
/// Geometry of the two-column window: how wide the configuration column is while idle, how wide it is
/// once the game is visible, and how the switch between those two widths is eased.
/// </summary>
/// <remarks>
/// <para>
/// Pure BCL on purpose. The form, the headless preview path and the host-side tests all have to agree
/// on these numbers, and the tests run on Linux where there is no window to measure.
/// </para>
/// <para>
/// Every width is a logical pixel value; the caller passes a DPI <c>scale</c> so the 520-640 band grows
/// on a high-DPI display instead of leaving the column cramped next to a stretched game.
/// </para>
/// </remarks>
internal static class LayoutState
{
    /// <summary>Duration of the idle/running crossfade. Short enough to feel like a state change, not an animation.</summary>
    public const int AnimationMilliseconds = 350;

    /// <summary>Narrowest the left column may get at 96 DPI: below this the folder row and the buttons wrap.</summary>
    public const float MinLeftWidth = 520f;

    /// <summary>Widest the left column may get at 96 DPI: past this the game column starts to look letterboxed.</summary>
    public const float MaxLeftWidth = 640f;

    /// <summary>
    /// Hard ceiling on the left column as a fraction of the content width. The 640 cap alone is not
    /// enough: on a narrow window it would still leave the game column with nothing, so both columns
    /// stay above a usable size only if the wider column also gives way.
    /// </summary>
    public const float MaxLeftFraction = 0.55f;

    /// <summary>Width of the splitter rule between the two columns; it is drawn, not laid out, but it does take room.</summary>
    public const int DividerWidth = 1;

    /// <summary>
    /// Fraction of the content width the left column asks for once the game is visible. Below
    /// <see cref="MaxLeftFraction"/> so the game, which is the thing being watched, gets the larger share.
    /// </summary>
    private const float RunningLeftFraction = 0.46f;

    /// <summary>Content width inside the window padding, never 0 so every later division has a sane base.</summary>
    public static float ContentWidth(int clientWidth, int paddingHorizontal)
    {
        return Math.Max(1f, clientWidth - paddingHorizontal);
    }

    /// <summary>
    /// Left column width while the game is visible: 46% of the content width, clamped into the DPI-scaled
    /// 520-640 band, then held under the 55% ceiling so neither column can starve the other.
    /// </summary>
    public static float TargetLeftWidth(float contentWidth, float scale)
    {
        var minimum = MinLeftWidth * scale;
        var maximum = MaxLeftWidth * scale;

        // A degenerate (zero or negative) scale would invert the band and make Math.Clamp throw,
        // so collapse it to the floor rather than crash a resize.
        if (maximum < minimum)
        {
            maximum = minimum;
        }

        var ideal = MathF.Round(contentWidth * RunningLeftFraction, MidpointRounding.AwayFromZero);
        var width = Math.Clamp(ideal, minimum, maximum);
        var cap = MathF.Max(minimum, contentWidth * MaxLeftFraction);

        return MathF.Max(1f, MathF.Min(width, cap));
    }

    /// <summary>Left column width while idle: the configuration fills the whole content width.</summary>
    public static float IdleLeftWidth(float contentWidth)
    {
        return MathF.Max(1f, contentWidth);
    }

    /// <summary>Left column width for <paramref name="kind"/>, the single source of truth for the split.</summary>
    public static float LeftWidthFor(LayoutKind kind, float contentWidth, float scale)
    {
        return kind == LayoutKind.Running
            ? TargetLeftWidth(contentWidth, scale)
            : IdleLeftWidth(contentWidth);
    }

    /// <summary>
    /// Game column width: whatever the content has left after the left column and the divider.
    /// Never negative, because a negative width would make the child control throw.
    /// </summary>
    public static float RightWidth(float contentWidth, float leftWidth)
    {
        return MathF.Max(0f, contentWidth - leftWidth - DividerWidth);
    }

    /// <summary>
    /// Game column width for <paramref name="kind"/>: 0 while idle (the column is not shown at all),
    /// otherwise the remainder of the content width.
    /// </summary>
    public static float GameWidthFor(LayoutKind kind, float contentWidth, float scale)
    {
        if (kind != LayoutKind.Running)
        {
            return 0f;
        }

        return RightWidth(contentWidth, LeftWidthFor(LayoutKind.Running, contentWidth, scale));
    }

    /// <summary>
    /// Ease-out cubic over a normalised time. The split starts moving immediately and settles gently,
    /// which reads as the game being revealed rather than as two panes sliding past each other.
    /// Values outside [0, 1] are clamped so a late or early frame cannot overshoot the target width.
    /// </summary>
    public static double EaseOutCubic(double t)
    {
        var clamped = Math.Clamp(t, 0d, 1d);
        var remaining = 1d - clamped;

        return 1d - (remaining * remaining * remaining);
    }

    /// <summary>
    /// Left column width <paramref name="elapsedMs"/> into the animation from <paramref name="from"/> to
    /// <paramref name="to"/>. Landing exactly on <paramref name="to"/> matters: the caller's "am I done"
    /// check compares against the target, and a float lerp that stops at 0.999 would leave the divider
    /// one pixel off forever.
    /// </summary>
    public static float LeftWidthAt(float from, float to, int elapsedMs)
    {
        if (AnimationComplete(elapsedMs))
        {
            return to;
        }

        var progress = Math.Clamp((double)elapsedMs / AnimationMilliseconds, 0d, 1d);
        var eased = EaseOutCubic(progress);

        return (float)(from + ((to - from) * eased));
    }

    /// <summary>True once the animation has run its full <see cref="AnimationMilliseconds"/>.</summary>
    public static bool AnimationComplete(int elapsedMs)
    {
        return elapsedMs >= AnimationMilliseconds;
    }

    /// <summary>
    /// The one-line layout assertion printed by the headless preview path. Same text as
    /// <see cref="LogLine"/> so a log and an assertion can be diffed against each other.
    /// </summary>
    public static string AssertionLine(LayoutKind kind, int configWidth, int gameWidth)
    {
        return Line(kind, configWidth, gameWidth);
    }

    /// <summary>The one-line layout record written to the run log; identical to <see cref="AssertionLine"/>.</summary>
    public static string LogLine(LayoutKind kind, int configWidth, int gameWidth)
    {
        return Line(kind, configWidth, gameWidth);
    }

    private static string Line(LayoutKind kind, int configWidth, int gameWidth)
    {
        return kind == LayoutKind.Running
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"layout state=running game=visible leftWidth={configWidth} rightWidth={gameWidth}")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"layout state=idle game=hidden width={configWidth}");
    }
}
