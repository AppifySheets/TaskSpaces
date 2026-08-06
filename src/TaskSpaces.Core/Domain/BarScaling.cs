namespace TaskSpaces.Core.Domain;

// Petre: "I'm now thinking that my window has gotten quite too large... shrink by twenty
// percent", and then, asked whether to hard-code it or make it adjustable, chose adjustable.
//
// One number rather than a set of them, deliberately: it is applied as a single uniform
// LayoutTransform, so every part of the bar keeps its proportions and there is exactly one
// value to try. Spacing was tightened separately in the same change -- that is a design
// decision about density, which is not the same axis as overall size and should not need a
// knob.
//
// Pure and in Core so the clamp is unit-testable without constructing a window.
public static class BarScaling
{
    // 80%: the size Petre asked for. Also the value a brand-new install gets, since there is
    // no reason for a fresh bar to start bigger than the one he settled on.
    public const double Default = 0.8;

    // Deliberately wide, because this is a preference and not a safety rail. The bounds exist
    // only to stop a hand-edited state.json producing an unusable window: 0 or a negative
    // collapses the bar to nothing with no way to click it, and a huge value puts it off
    // screen. Same tolerance the rest of this file's neighbours show towards hand-edited
    // values -- SwitcherShortcut falls back to a default, LaneTint falls back to no tint,
    // and neither crashes.
    public const double Minimum = 0.5;
    public const double Maximum = 2.0;

    // Null means "never configured": both an older state.json with no such key and a fresh
    // install, exactly like FloatingBar and PersistedRenames. NaN and infinity are rejected
    // too -- they survive JSON round-trips in some writers, and either would make WPF's
    // layout pass throw somewhere far from here.
    public static double Clamp(double? value) =>
        value is not { } scale || double.IsNaN(scale) || double.IsInfinity(scale)
            ? Default
            : Math.Clamp(scale, Minimum, Maximum);
}
