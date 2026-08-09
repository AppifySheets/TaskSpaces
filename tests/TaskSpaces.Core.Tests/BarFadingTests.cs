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
}
