namespace TaskSpaces.Core.Domain;

// How long the pointer has to rest on an icon before the hover card appears (#139, #151).
//
// The card carries a picture a quarter the size of the window, and a surface that large has to be
// ASKED for rather than triggered by a pointer passing through on its way somewhere else. Petre: "i
// want to go over the taskspaces window with my mouse, if i'm not meaning to do anything with it, and
// it should not show me those cards", then "is 300ms enough?"
//
// That question is why this is a setting rather than a number in the bar. It has no answer anyone can
// derive: 300ms is defensible, and so is the 400 his machine reports.
public static class HoverDwelling
{
    // No default of its own, and that is the design. When nothing is configured the answer comes from
    // WINDOWS -- SystemParameters.MouseHoverTime, which is the OS's own definition of "resting rather
    // than passing", what the taskbar's thumbnails wait for, and already a setting the user can tune.
    // Inheriting it means the bar behaves like the rest of the desktop until somebody says otherwise.
    //
    // The system value is passed in rather than read here, because Core cannot see WPF and because a
    // test needs to be able to say "suppose Windows reports 400".
    public static double ClampMs(double? configured, double systemMs) =>
        configured is { } chosen && !double.IsNaN(chosen) && !double.IsInfinity(chosen)
            ? Math.Clamp(chosen, MinimumMs, MaximumMs)
            // The system value is clamped too, and not out of tidiness: MouseHoverTime comes from the
            // registry, where it can be zero (every sweep of the row shows a card) or ten seconds (the
            // feature looks broken). A hostile value there must not become the bar's behaviour.
            : Math.Clamp(double.IsNaN(systemMs) || double.IsInfinity(systemMs) ? SystemFallbackMs : systemMs,
                SystemFloorMs, SystemCeilingMs);

    // Zero is allowed for a CONFIGURED value and not for an inherited one, which looks inconsistent
    // and is the point: "show it the instant I touch an icon" is a choice somebody can make on
    // purpose, and it is not something to be given a registry value nobody set for this.
    public const double MinimumMs = 0;

    // Past about a second and a half the card feels broken rather than deliberate: the pointer has
    // stopped, nothing happened, and the natural conclusion is that hovering does nothing.
    public const double MaximumMs = 1500;

    // The rails on what Windows is allowed to tell us.
    public const double SystemFloorMs = 250;
    public const double SystemCeilingMs = 1000;

    // Used only when the system value is unreadable, which should not happen and costs one constant
    // to survive. Windows' own default is 400ms.
    public const double SystemFallbackMs = 400;
}
