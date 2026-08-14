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

    // --- the two timings (#151) -------------------------------------------------------------
    //
    // Petre: "make things configurable, like dimming opacity, timeout, etc." Both of these were
    // consts, and their own comment admitted what they were: "a starting point chosen to be lived
    // with, not a measurement... consts rather than settings until Petre has an opinion about the
    // numbers." He now has one, which is that they should be his to choose.

    // How long the bar holds full strength after the pointer leaves. Petre: "delay 10 seconds,
    // then dim gradually" (#46), which is where the default comes from.
    public const double GraceDefault = 10;

    // Zero is allowed and means "start dimming at once", which is the original #46 behaviour and a
    // reasonable taste. The cap is a rail rather than a preference: past a couple of minutes the
    // grace is indistinguishable from never fading, and "never fade" already has an honest spelling
    // in an idle opacity of 1.0.
    public const double GraceMinimumSeconds = 0;
    public const double GraceMaximumSeconds = 120;

    public static double ClampGraceSeconds(double? value) =>
        value is not { } seconds || double.IsNaN(seconds) || double.IsInfinity(seconds)
            ? GraceDefault
            : Math.Clamp(seconds, GraceMinimumSeconds, GraceMaximumSeconds);

    // How long the dimming itself takes. The asymmetry against brightening (60ms, still a const) is
    // deliberate and not a knob: reaching for the bar should feel like it was already there, and
    // nobody wants to tune that.
    public const double DurationDefaultMs = 4000;

    // The floor is what makes this a fade rather than a switch -- below ~100ms the change is a jump,
    // which is the shape #46 was filed to get rid of. The ceiling stops a fade so slow that the bar
    // is still on its way down when the pointer comes back, which reads as a bar that never dims.
    public const double DurationMinimumMs = 100;
    public const double DurationMaximumMs = 20000;

    public static double ClampDurationMs(double? value) =>
        value is not { } milliseconds || double.IsNaN(milliseconds) || double.IsInfinity(milliseconds)
            ? DurationDefaultMs
            : Math.Clamp(milliseconds, DurationMinimumMs, DurationMaximumMs);
}
