namespace TaskSpaces.Core.Domain;

// Petre: "i also want different colors for different workspaces in the lanes", "configurable".
//
// Configurable, but never blank: a workspace with no colour set gets one from this palette by
// its position, so lanes are distinguishable the moment they exist rather than only after
// someone goes and picks colours. Workspace.Color overrides it.
//
// Pure and in Core (not in the WPF layer) so the choice is unit-testable and so any future
// surface tints a workspace the same way the bar does.
public static class WorkspacePalette
{
    // Muted rather than saturated: these sit behind app icons on a translucent bar, so they have to
    // separate the lanes without competing with the icons, the active-window highlight or the amber
    // candidate ring. Ordered so adjacent lanes are far apart in hue.
    //
    // #68 tried four replacements for this list -- brighter hues, lighter still, an evenly
    // constructed wheel, deep jewel tones at high alpha -- and Petre rejected every one, at the end
    // in as many words: "old colors were better, they were darker and better. let's just go back
    // and avoid that green." So this IS the original set, restored, with exactly one change.
    //
    // Worth recording why the constructed ones lost, because the arithmetic was sound and the
    // result still looked wrong. Even hue spacing is a property of the WHEEL, not of the colours
    // you get from it: nine hues 40 degrees apart must put one or two in the band between yellow
    // and green, and nothing in that band is attractive as a dark lane -- it is olive, mustard and
    // moss, which is where "terrible colors" and "still bad colors" came from. A hand-picked set
    // can simply decline to visit it. Measuring hue separation was the right check and the wrong
    // objective.
    //
    // THE ONE CHANGE: moss green is gone, replaced by orchid. Petre, twice: "i still don't like the
    // green for sparrow", then "avoid that green". It sat at slot 6, and slot 6 is Sparrow.
    //
    // Orchid rather than another green, and its hue is not a taste call: with moss removed, the
    // widest gap left between the remaining eight is 271-340 (violet to plum), so ~305 is the one
    // place a ninth colour can go without crowding a neighbour. It is 34 degrees from violet and 35
    // from plum -- the most room available, in a palette this muted.
    //
    // Names are what the picker shows, so they have to be the word someone would reach for rather
    // than the hue number.
    public sealed record Swatch(string Name, string Hex);

    public static readonly IReadOnlyList<Swatch> Swatches =
    [
        new("Indigo", "#3C48BE"), // matching the app icon
        new("Teal", "#2E7D6B"),
        new("Violet", "#8A4CD6"),
        new("Amber", "#B06A2C"),
        new("Steel", "#2F6FA8"),
        new("Plum", "#9A3B5A"),
        new("Orchid", "#8A3C7E"), // was moss green -- see above
        new("Slate", "#6A5ACD"),
        new("Rust", "#A8562C"),
    ];

    // Declared AFTER Swatches on purpose: static fields initialise in declaration order, so
    // reading Swatches from above it would read a null.
    static readonly IReadOnlyList<string> Defaults = Swatches.Select(s => s.Hex).ToList();

    // index is the workspace's position in the user's own ordering, so the colour follows the
    // list rather than a hash of the name: renaming a workspace should not recolour it.
    public static string For(Workspace workspace, int index) =>
        string.IsNullOrWhiteSpace(workspace.Color) ? Defaults[index % Defaults.Count] : workspace.Color!;
}
