using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Tests;

// Petre: "shrink by twenty percent", and he chose an adjustable value over a baked-in one.
//
// The clamp is the whole of the logic worth testing here: state.json is hand-editable, and a
// bad number in it must never leave him with a bar he cannot see or cannot click.
public class BarScalingTests
{
    // Null is both "older state.json with no such key" and "fresh install". Neither should
    // start at 100% -- 80% is the size he settled on.
    [Fact]
    public void An_unconfigured_scale_is_the_default() =>
        Assert.Equal(BarScaling.Default, BarScaling.Clamp(null));

    [Fact]
    public void A_sensible_value_is_kept_as_written() =>
        Assert.Equal(0.7, BarScaling.Clamp(0.7));

    // The bounds are a safety rail, not a preference: 0 collapses the bar to nothing, which
    // leaves nothing on screen to click and no way back except editing the file again.
    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(0.01)]
    public void A_scale_that_would_make_the_bar_unusable_is_raised_to_the_minimum(double value) =>
        Assert.Equal(BarScaling.Minimum, BarScaling.Clamp(value));

    [Fact]
    public void An_absurdly_large_scale_is_capped() =>
        Assert.Equal(BarScaling.Maximum, BarScaling.Clamp(100.0));

    // NaN and infinity survive a JSON round-trip in some writers, and either would surface as
    // an exception from WPF's layout pass -- far from here, and hard to trace back.
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void A_value_that_is_not_a_real_number_falls_back_to_the_default(double value) =>
        Assert.Equal(BarScaling.Default, BarScaling.Clamp(value));

    // The boundaries themselves are valid, not rejected.
    [Fact]
    public void The_bounds_are_themselves_allowed()
    {
        Assert.Equal(BarScaling.Minimum, BarScaling.Clamp(BarScaling.Minimum));
        Assert.Equal(BarScaling.Maximum, BarScaling.Clamp(BarScaling.Maximum));
    }
}
