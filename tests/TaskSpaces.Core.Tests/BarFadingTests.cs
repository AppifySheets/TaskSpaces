using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Tests;

// Petre: "when i leave the floating window i want it to fade away, still be visible, but much
// dimmer, so i can see what's behind it better."
//
// As with BarScaling, the clamp is the whole of the logic worth testing: state.json is
// hand-editable, and a bad number in it must never leave a bar that cannot be found again.
public class BarFadingTests
{
    // Null is both "older state.json with no such key" and "fresh install".
    [Fact]
    public void An_unconfigured_opacity_is_the_default() =>
        Assert.Equal(BarFading.Default, BarFading.Clamp(null));

    [Fact]
    public void A_sensible_value_is_kept_as_written() =>
        Assert.Equal(0.5, BarFading.Clamp(0.5));

    // Zero is the dangerous end, and specifically so: an invisible bar still takes its mouse
    // input, so the pointer meets a wall that swallows clicks with nothing visible to hover in
    // order to bring it back.
    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void An_opacity_that_would_make_the_bar_unfindable_is_raised_to_the_minimum(double value) =>
        Assert.Equal(BarFading.Minimum, BarFading.Clamp(value));

    // Fully opaque means "never fade", which is a legitimate preference and needs no separate
    // on/off flag of its own. Above that is meaningless rather than harmful, so it caps.
    [Fact]
    public void An_opacity_past_opaque_is_capped() =>
        Assert.Equal(BarFading.Maximum, BarFading.Clamp(2.0));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void A_value_that_is_not_a_real_number_falls_back_to_the_default(double value) =>
        Assert.Equal(BarFading.Default, BarFading.Clamp(value));

    [Fact]
    public void The_bounds_are_themselves_allowed()
    {
        Assert.Equal(BarFading.Minimum, BarFading.Clamp(BarFading.Minimum));
        Assert.Equal(BarFading.Maximum, BarFading.Clamp(BarFading.Maximum));
    }

    // --- the two timings, settings since #151 --------------------------------------------------

    [Fact]
    public void An_unconfigured_grace_is_the_ten_seconds_the_issue_asked_for() =>
        Assert.Equal(10, BarFading.ClampGraceSeconds(null));

    [Fact]
    public void A_chosen_grace_is_kept_as_written() =>
        Assert.Equal(30, BarFading.ClampGraceSeconds(30));

    // Zero is a real choice, not an error: it is the original #46 behaviour, dimming the moment the
    // pointer leaves.
    [Fact]
    public void A_grace_of_zero_is_allowed() =>
        Assert.Equal(0, BarFading.ClampGraceSeconds(0));

    [Fact]
    public void A_negative_grace_is_raised_to_zero() =>
        Assert.Equal(BarFading.GraceMinimumSeconds, BarFading.ClampGraceSeconds(-5));

    [Fact]
    public void An_absurd_grace_is_capped() =>
        Assert.Equal(BarFading.GraceMaximumSeconds, BarFading.ClampGraceSeconds(100000));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_grace_that_is_not_a_real_number_falls_back_to_the_default(double value) =>
        Assert.Equal(BarFading.GraceDefault, BarFading.ClampGraceSeconds(value));

    [Fact]
    public void An_unconfigured_duration_is_the_four_seconds_it_shipped_with() =>
        Assert.Equal(4000, BarFading.ClampDurationMs(null));

    [Fact]
    public void A_chosen_duration_is_kept_as_written() =>
        Assert.Equal(1500, BarFading.ClampDurationMs(1500));

    // Below about a tenth of a second the fade is a jump, which is the shape #46 exists to remove --
    // so zero is NOT allowed here, unlike the grace, and the difference is deliberate.
    [Fact]
    public void A_duration_of_zero_is_raised_to_the_floor() =>
        Assert.Equal(BarFading.DurationMinimumMs, BarFading.ClampDurationMs(0));

    [Fact]
    public void An_absurd_duration_is_capped() =>
        Assert.Equal(BarFading.DurationMaximumMs, BarFading.ClampDurationMs(600000));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    public void A_duration_that_is_not_a_real_number_falls_back_to_the_default(double value) =>
        Assert.Equal(BarFading.DurationDefaultMs, BarFading.ClampDurationMs(value));
}
