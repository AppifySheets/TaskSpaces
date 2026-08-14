using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Geometry;

// A name for a monitor ARRANGEMENT, so the bar can remember where it sat on each one (#150).
// Petre: "when changing screen resolution, when connecting from a laptop, floating window may not
// show... it needs to redraw, readjust, maybe have its own place for each layout, as windows already
// does i think."
//
// This is the "as windows already does" half. Windows 11's own "remember window locations based on
// monitor connection" keys a window's position by the set of displays attached, and that is exactly
// the shape wanted here: docked at the desk the bar sits where it was at the desk, and the
// RDP-from-laptop layout gets its own remembered spot rather than a clamped guess derived from the
// desk position.
//
// Pure, and in Core rather than in the bar, for the usual reason: the awkward cases are about the
// STRING (does enumeration order change it? does a monitor moving in the arrangement change it?) and
// every one of them is testable without a second monitor to plug in.
public static class MonitorLayoutKey
{
    // The empty string means "no layout known", and it is a real answer rather than an error. It
    // happens in compatibility mode and whenever the monitor query fails, and it must never be used
    // as a dictionary key, because two different unknown layouts are not the same layout. Callers
    // read it as "do not remember, and do not look up".
    public const string Unknown = "";

    // Physical rectangles, one per display, sorted so the key describes the ARRANGEMENT and nothing
    // else. Sorting is the whole substance of this method: EnumDisplayMonitors makes no promise
    // about order, and an unsorted join would give the same three screens two different names
    // depending on which one Windows happened to hand back first -- turning "I am back at my desk"
    // into "I have never seen this layout".
    //
    // Rectangles rather than display NUMBERS or device names. A number is an index into whatever is
    // attached at the time (unplug the middle screen and everything after it renumbers), and a
    // device name is stable for a port rather than for a picture. What actually decides whether a
    // remembered position is still meaningful is the geometry: same rectangles, same desk.
    //
    // Resolution is part of the key by the same argument, and that is deliberate rather than
    // accidental: dropping a 4K screen to 1080p moves every edge the bar could be parked against,
    // so the position it had at 4K is not the position it wants at 1080p.
    public static string Of(IEnumerable<MonitorBounds>? monitors)
    {
        if (monitors is null) return Unknown;

        var parts = monitors
            .Where(m => m is not null)
            .Select(m => $"{m.Left},{m.Top},{m.Right},{m.Bottom}")
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();

        return parts.Count == 0 ? Unknown : string.Join("|", parts);
    }

    public static bool IsKnown(string? key) => !string.IsNullOrEmpty(key);
}
