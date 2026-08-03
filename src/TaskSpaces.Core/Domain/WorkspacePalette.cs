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
    // Muted rather than saturated: these sit behind app icons on a translucent bar, so they
    // have to separate the lanes without competing with the icons or with the active-window
    // highlight. Ordered so adjacent lanes are far apart in hue.
    static readonly IReadOnlyList<string> Defaults =
    [
        "#3C48BE", // indigo, matching the app icon
        "#2E7D6B", // teal
        "#8A4CD6", // violet
        "#B06A2C", // amber
        "#2F6FA8", // steel blue
        "#9A3B5A", // plum
        "#4B7A2E", // moss
        "#6A5ACD", // slate blue
        "#A8562C", // rust
    ];

    // index is the workspace's position in the user's own ordering, so the colour follows the
    // list rather than a hash of the name: renaming a workspace should not recolour it.
    public static string For(Workspace workspace, int index) =>
        string.IsNullOrWhiteSpace(workspace.Color) ? Defaults[index % Defaults.Count] : workspace.Color!;
}
