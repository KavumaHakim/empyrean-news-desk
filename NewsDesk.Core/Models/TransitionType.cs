namespace NewsDesk.Core.Models;

/// <summary>
/// The 7 distinct transition effects required by the broadcast specification.
/// </summary>
public enum TransitionType
{
    Cut,
    Fade,
    Dissolve,
    SlideLeft,
    SlideRight,
    Wipe,
    Zoom
}

/// <summary>
/// Centrally defines transition timing rules.
/// Animated transitions must have a fixed duration of exactly 500 milliseconds.
/// Cut must switch immediately (0 milliseconds).
/// </summary>
public static class TransitionConfig
{
    public const int AnimatedDurationMs = 500;
    public const int CutDurationMs = 0;

    public static int GetDurationMs(TransitionType type)
    {
        return type == TransitionType.Cut ? CutDurationMs : AnimatedDurationMs;
    }

    public static string ToCssClass(TransitionType type) => type switch
    {
        TransitionType.Cut => "transition-cut",
        TransitionType.Fade => "transition-fade",
        TransitionType.Dissolve => "transition-dissolve",
        TransitionType.SlideLeft => "transition-slide-left",
        TransitionType.SlideRight => "transition-slide-right",
        TransitionType.Wipe => "transition-wipe",
        TransitionType.Zoom => "transition-zoom",
        _ => "transition-cut"
    };

    /// <summary>
    /// Command name sent to the broadcast canvas (kebab-case matching the JS transition map).
    /// </summary>
    public static string ToCommandName(TransitionType type) => type switch
    {
        TransitionType.Cut => "cut",
        TransitionType.Fade => "fade",
        TransitionType.Dissolve => "dissolve",
        TransitionType.SlideLeft => "slide-left",
        TransitionType.SlideRight => "slide-right",
        TransitionType.Wipe => "wipe",
        TransitionType.Zoom => "zoom",
        _ => "fade"
    };
}
