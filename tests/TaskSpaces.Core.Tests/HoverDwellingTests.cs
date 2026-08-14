using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Tests;

// #139 asked for 300ms, #151 made it a setting, and this is where the argument between them is
// settled: nothing is hardcoded at 300 or at 400, and an unconfigured dwell inherits whatever Windows
// says. Petre asked "is 300ms enough?", which has no answer anyone can derive, so the number is his.
public class HoverDwellingTests
{
    // Windows' own default, as it happens, and the value his machine reports.
    const double System400 = 400;

    [Fact]
    public void With_nothing_configured_it_inherits_windows() =>
        Assert.Equal(System400, HoverDwelling.ClampMs(null, System400));

    [Fact]
    public void A_chosen_dwell_overrules_windows() =>
        Assert.Equal(300, HoverDwelling.ClampMs(300, System400));

    // "Show it the instant I touch an icon" is a deliberate choice, and it is allowed for a
    // CONFIGURED value only. That asymmetry is the point of the next test.
    [Fact]
    public void A_dwell_of_zero_is_allowed_when_it_was_chosen() =>
        Assert.Equal(0, HoverDwelling.ClampMs(0, System400));

    // MouseHoverTime comes from the registry, where it can be zero -- and a zero nobody set for this
    // purpose would show a card on every sweep across a row, which is the complaint the dwell exists
    // to answer. So an inherited zero is floored while a chosen one is honoured.
    [Fact]
    public void An_inherited_zero_is_floored() =>
        Assert.Equal(HoverDwelling.SystemFloorMs, HoverDwelling.ClampMs(null, 0));

    // The other end of the same rail: ten seconds from the registry would make the feature look
    // broken rather than deliberate.
    [Fact]
    public void An_inherited_eternity_is_capped() =>
        Assert.Equal(HoverDwelling.SystemCeilingMs, HoverDwelling.ClampMs(null, 10000));

    [Fact]
    public void A_chosen_dwell_past_the_maximum_is_capped() =>
        Assert.Equal(HoverDwelling.MaximumMs, HoverDwelling.ClampMs(9000, System400));

    [Fact]
    public void A_negative_chosen_dwell_is_raised_to_zero() =>
        Assert.Equal(HoverDwelling.MinimumMs, HoverDwelling.ClampMs(-100, System400));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_chosen_dwell_that_is_not_a_real_number_falls_back_to_windows(double value) =>
        Assert.Equal(System400, HoverDwelling.ClampMs(value, System400));

    // Both sides unreadable, which should not happen and must not throw or produce a zero dwell.
    [Fact]
    public void An_unreadable_system_value_falls_back_to_windows_own_default() =>
        Assert.Equal(HoverDwelling.SystemFallbackMs, HoverDwelling.ClampMs(null, double.NaN));
}
