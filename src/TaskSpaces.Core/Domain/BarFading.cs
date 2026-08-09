namespace TaskSpaces.Core.Domain;

// Petre: "when i leave the floating window i want it to fade away, still be visible, but much
// dimmer, so i can see what's behind it better."
//
// The bar is permanently on top and permanently on -- both decided deliberately (see CLAUDE.md:
// it is the only surface that lists windows, so hiding it leaves no way to reach them) -- and the
// price of both is that it permanently covers whatever is under it. Dimming when the pointer is
// elsewhere pays that price back without giving up either decision.
//
// Adjustable rather than baked in, for the same reason BarScaling is: how dim is "much dimmer"
// depends on the wallpaper behind it, and there is no value that is correct for everyone. Pure
// and in Core so the clamp is testable without constructing a window.
public static class BarFading
{
    // Dim enough that you can read what is behind it, strong enough that the row layout is still
    // legible at a glance -- which is the whole point of a bar you do not have to summon.
    public const double Default = 0.35;

    // A safety rail rather than a preference, exactly like BarScaling's. Zero is the dangerous
    // end and it is dangerous in a specific way: an invisible bar still takes its mouse input, so
    // the pointer would meet an unseeable wall that swallows clicks, with no visible thing to
    // hover in order to bring it back. The minimum keeps a ghost of it on screen to aim at.
    public const double Minimum = 0.05;

    // Fully opaque is a legitimate choice: it means "never fade", which is what someone who
    // dislikes the whole idea will want, and it needs no separate on/off flag to express.
    public const double Maximum = 1.0;

    // Null means "never configured": both an older state.json with no such key and a fresh
    // install. NaN and infinity are rejected too -- they survive JSON round-trips in some
    // writers, and either would surface as an exception from WPF's render pass, far from here.
    public static double Clamp(double? value) =>
        value is not { } opacity || double.IsNaN(opacity) || double.IsInfinity(opacity)
            ? Default
            : Math.Clamp(opacity, Minimum, Maximum);
}
