using System.Drawing;

namespace SPTCoopSetup;

/// <summary>
/// The dark palette used by the whole window. Every colour the UI paints comes from here so the
/// contrast audit in <see cref="Contrast"/> describes what is actually on screen.
/// </summary>
internal static class Theme
{
    /// <summary>Window background, the darkest layer.</summary>
    public static readonly Color BgBase = Color.FromArgb(8, 9, 12);

    /// <summary>Scrim drawn over imagery so text stays readable; translucent by design.</summary>
    public static readonly Color BgScrim = Color.FromArgb(199, 8, 9, 12);

    /// <summary>Card and panel background sitting on <see cref="BgBase"/>.</summary>
    public static readonly Color Surface = Color.FromArgb(16, 17, 20);

    /// <summary>Elevated surface for hovered rows, focused inputs and footer bars.</summary>
    public static readonly Color SurfaceRaised = Color.FromArgb(22, 24, 29);

    /// <summary>Hairline dividers and panel outlines.</summary>
    public static readonly Color Border = Color.FromArgb(42, 44, 50);

    /// <summary>Primary body and heading text.</summary>
    public static readonly Color TextPrimary = Color.FromArgb(255, 255, 255);

    /// <summary>Secondary text: labels, hints, log lines.</summary>
    public static readonly Color TextSecondary = Color.FromArgb(201, 203, 210);

    /// <summary>Fill for the one high-emphasis action per view.</summary>
    public static readonly Color AccentFill = Color.FromArgb(240, 242, 248);

    /// <summary>Text drawn on top of <see cref="AccentFill"/>.</summary>
    public static readonly Color AccentText = Color.FromArgb(10, 11, 14);

    /// <summary>Progress bar and scrollbar gutter.</summary>
    public static readonly Color Track = Color.FromArgb(20, 21, 26);

    /// <summary>
    /// Monospaced font for the log view. Lives for the whole process lifetime and must not be
    /// disposed by callers. The UI font is intentionally left as the WinForms default so font
    /// autoscaling stays exactly as it was.
    /// </summary>
    public static Font MonoFont { get; } = new("Consolas", 9f, FontStyle.Regular, GraphicsUnit.Point);
}
