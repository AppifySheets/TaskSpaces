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
    // Petre, in order, and the order is the argument (#68): "lame and dark, i want something more
    // cheerful and bright" -> "make them lighter still" -> "you're repeating gepha's and sparrow's
    // colors, why? we have many colors to choose from, let's not repeat colors" -> "no contrast"
    // -> "dark was better" -> "dark but not gloomy, can you do that?"
    //
    // Two separate faults, and chasing them with the palette alone is what took six rounds.
    //
    // THE REPEATS were real and measurable. Every hand-picked set clustered: 12 degrees between
    // indigo and periwinkle, 19 between coral and amber, when nine colours spread evenly is 40
    // apart. Two hues that close are one colour. So the hues are no longer chosen, they are
    // CONSTRUCTED -- nine of them, 40 degrees apart, walked in steps of 200 so consecutive
    // positions land on opposite sides of the wheel. No future edit can reintroduce a
    // near-duplicate by eye.
    //
    // THE GLOOM was never in the palette at all, which is why brightening it kept failing. A lane
    // is painted OVER the bar's near-black background, so at the old ~22% alpha what reached the
    // eye was two thirds background and one third colour: every hue arrived as the same dark grey
    // with a hint. Making the palette lighter did not fix it -- a pale colour blended two thirds
    // into black is exactly the washed-out "no contrast" middle Petre saw next. The alpha was the
    // gloom, and it is turned up in FloatingBar.LaneTint where it belongs.
    //
    // Which is what lets these be DARK and still not gloomy: deep, properly chromatic jewel tones
    // painted at most of their strength, rather than bright ones painted at a fraction of it. They
    // sit at a common OKLCh lightness -- perceptual, so they read as equally dark, which equal RGB
    // values do not -- with chroma short of the gamut edge, because at the edge these stop being
    // lane tints and start being alarm colours.
    //
    // Names are what the picker shows, so they have to be the word someone would reach for rather
    // than the hue number.
    public sealed record Swatch(string Name, string Hex);

    // Ordered by POSITION, and the order walks the wheel in steps of 200 degrees rather than 40 --
    // so consecutive positions land on opposite sides of it and every neighbouring pair is 200
    // degrees apart, the most nine colours allow. Neighbouring rows are the ones that have to be
    // told apart; stepping round the wheel in sequence would have made each pair of neighbours the
    // most similar ones on the bar.
    //
    // Rotated so slot 0 stays in the app icon's blue family, the one tie worth keeping.
    public static readonly IReadOnlyList<Swatch> Swatches =
    [
        new("Blue", "#3969D8"),   // OKLCh hue 264 -- the app icon's family
        new("Olive", "#7A7436"),  // OKLCh hue 104
        new("Violet", "#8D45D0"), // OKLCh hue 304
        new("Green", "#3B843D"),  // OKLCh hue 144
        new("Rose", "#B13F89"),   // OKLCh hue 344
        new("Teal", "#3D7F76"),   // OKLCh hue 184
        new("Rust", "#BD4141"),   // OKLCh hue  24
        new("Azure", "#3C7B91"),  // OKLCh hue 224
        new("Amber", "#966636"),  // OKLCh hue  64
    ];

    // Declared AFTER Swatches on purpose: static fields initialise in declaration order, so
    // reading Swatches from above it would read a null.
    static readonly IReadOnlyList<string> Defaults = Swatches.Select(s => s.Hex).ToList();

    // index is the workspace's position in the user's own ordering, so the colour follows the
    // list rather than a hash of the name: renaming a workspace should not recolour it.
    public static string For(Workspace workspace, int index) =>
        string.IsNullOrWhiteSpace(workspace.Color) ? Defaults[index % Defaults.Count] : workspace.Color!;
}
