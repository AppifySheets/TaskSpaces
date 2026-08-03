using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Tests;

// Petre: "assign shortcuts to workspaces, ability to do so from within the workspaces window".
// This parses what he types before anything tries to register it.
public class ChordTests
{
    [Fact]
    public void The_usual_chord_parses_to_its_modifiers_and_key()
    {
        var chord = Chord.Parse("Ctrl+Alt+1").Value;

        Assert.Equal(Chord.Control | Chord.Alt, chord.Modifiers);
        Assert.Equal((uint)'1', chord.VirtualKey); // '1'..'9' VK codes equal their ASCII values
    }

    [Theory]
    [InlineData("ctrl+alt+1")]
    [InlineData("CTRL+ALT+1")]
    [InlineData("Control + Alt + 1")]   // spaces and the long modifier name
    public void Parsing_is_forgiving_about_case_spacing_and_synonyms(string text) =>
        Assert.Equal(Chord.Parse("Ctrl+Alt+1").Value, Chord.Parse(text).Value);

    [Theory]
    [InlineData("Ctrl+Alt+Left", 0x25u)]
    [InlineData("Ctrl+Alt+Right", 0x27u)]
    [InlineData("Ctrl+Shift+F5", 0x74u)]
    [InlineData("Win+Alt+W", (uint)'W')]
    public void Arrows_function_keys_and_letters_are_all_bindable(string text, uint expectedKey) =>
        Assert.Equal(expectedKey, Chord.Parse(text).Value.VirtualKey);

    [Fact]
    public void Win_is_a_modifier_like_any_other() =>
        Assert.Equal(Chord.Win | Chord.Shift, Chord.Parse("Win+Shift+9").Value.Modifiers);

    // A bare key would steal it from every other app on the machine.
    [Fact]
    public void A_key_with_no_modifier_is_rejected()
    {
        var result = Chord.Parse("1");

        Assert.True(result.IsFailure);
        Assert.Contains("Ctrl", result.Error);
    }

    [Fact]
    public void Modifiers_with_no_key_are_rejected() =>
        Assert.True(Chord.Parse("Ctrl+Alt").IsFailure);

    // An allowlist, so a typo is reported rather than silently bound to something surprising.
    [Fact]
    public void An_unknown_key_name_is_rejected_by_name()
    {
        var result = Chord.Parse("Ctrl+Alt+Bananas");

        Assert.True(result.IsFailure);
        Assert.Contains("Bananas", result.Error);
    }

    [Fact]
    public void Two_different_keys_in_one_chord_are_rejected() =>
        Assert.True(Chord.Parse("Ctrl+1+2").IsFailure);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_at_all_is_a_failure_not_a_crash(string? text) =>
        Assert.True(Chord.Parse(text).IsFailure);
}
