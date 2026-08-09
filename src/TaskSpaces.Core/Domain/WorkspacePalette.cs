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
    // Petre: "the default workspace lane colours feel lame and dark, i want something more
    // cheerful and bright" (#68).
    //
    // These were muted on purpose and the purpose still holds: they sit BEHIND app icons on a
    // translucent bar, so they have to separate the lanes without competing with the icons, the
    // active-window highlight or the amber candidate ring. Brightening them naively fights all
    // three.
    //
    // So the brightness went into the HUES and not into the lanes. The bar dilutes every one of
    // these to ~22% alpha before painting it (FloatingBar.LaneTint), and that dilution is what
    // keeps the icons winning -- it is doing the restraining, so the colour underneath does not
    // have to. A dark base diluted to 22% is a grey smudge; a saturated one at the same 22% is a
    // clear, cheerful wash. Same ink, more colour.
    //
    // Each is the same hue family it was, roughly two stops brighter, so a workspace does not
    // change identity -- teal is still the teal one. The alpha is untouched, which is the whole
    // point: if these ever do compete with the icons, LaneTint's alpha is the dial to turn, not
    // this list.
    //
    // Ordered, as before, so adjacent lanes are far apart in hue -- neighbouring rows are the ones
    // that have to be told apart, and the ordering is the only thing that guarantees it.
    static readonly IReadOnlyList<string> Defaults =
    [
        "#5C6BF5", // indigo, still the app icon's family
        "#1FC9A7", // teal
        "#A95CF2", // violet
        "#FFAA2B", // amber
        "#2BA6F5", // sky
        "#F5568C", // rose
        "#6ECF3D", // green
        "#8B7DFF", // periwinkle
        "#FF7A45", // coral
    ];

    // index is the workspace's position in the user's own ordering, so the colour follows the
    // list rather than a hash of the name: renaming a workspace should not recolour it.
    public static string For(Workspace workspace, int index) =>
        string.IsNullOrWhiteSpace(workspace.Color) ? Defaults[index % Defaults.Count] : workspace.Color!;
}
