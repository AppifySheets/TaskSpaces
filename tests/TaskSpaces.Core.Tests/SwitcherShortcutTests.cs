using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Core.Tests;

// Petre: "i want it configurable" -- the Alt+Tab-style workspace switcher's chord.
//
// Two things have to hold for that to be safe. Nothing unusable may ever reach state.json
// (the editor validates first), and nothing unusable in state.json may ever leave the app
// without a working shortcut (the reader falls back). The second matters because the file
// is deliberately hand-editable.
public class SwitcherShortcutTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeStore store = new();

    WorkspaceManager Manager() => new(desktops, new FakeMonitor(), new FakeTitles(), store);

    // Petre: "i think ctrl+tab was commonly used for something, give me other something+tab."
    // Win+Ctrl+Tab was picked by trying every *+Tab candidate against RegisterHotKey on a real
    // machine; it was the only one with both its forward and reverse halves free. Asserted
    // literally, not against the constant, so changing the default stays a deliberate edit here
    // rather than something a test silently follows.
    [Fact]
    public void Out_of_the_box_it_is_the_documented_default()
    {
        Assert.Equal("Win+Ctrl+Tab", AppState.DefaultSwitcherShortcut);
        Assert.Equal("Win+Ctrl+Tab", Manager().SwitcherShortcut);
    }

    // The default must be a chord the gesture can actually run: one modifier at minimum, or
    // there would be nothing to hold and nothing whose release could commit the switch.
    [Fact]
    public void The_default_is_a_chord_the_gesture_can_hold()
    {
        var chord = Chord.Parse(AppState.DefaultSwitcherShortcut).Value;

        Assert.NotEqual(0u, chord.Modifiers);
        Assert.Equal(AppState.DefaultSwitcherShortcut, chord.ToString()); // already canonical
    }

    [Fact]
    public void A_valid_chord_is_accepted_and_persisted()
    {
        var manager = Manager();

        Assert.True(manager.SetSwitcherShortcut("Win+Tab").IsSuccess);

        Assert.Equal("Win+Tab", manager.SwitcherShortcut);
        Assert.Equal("Win+Tab", store.Stored.SwitcherShortcut);
    }

    // Stored canonically so one chord cannot sit in state.json in several spellings.
    [Theory]
    [InlineData("ctrl+alt+1")]
    [InlineData("Control + Alt + 1")]
    [InlineData("  ALT+CTRL+1  ")] // modifier order is normalised too
    public void However_it_is_typed_it_is_stored_in_one_canonical_form(string typed)
    {
        var manager = Manager();

        Assert.True(manager.SetSwitcherShortcut(typed).IsSuccess);

        Assert.Equal("Ctrl+Alt+1", manager.SwitcherShortcut);
    }

    // The editor shows this text as you type, which is the entire reason Chord.Parse
    // returns a Result rather than throwing.
    [Fact]
    public void A_nonsense_chord_is_refused_by_name_and_never_persisted()
    {
        var manager = Manager();

        var result = manager.SetSwitcherShortcut("Ctrl+Alt+Bananas");

        Assert.True(result.IsFailure);
        Assert.Contains("Bananas", result.Error);
        Assert.Equal(AppState.DefaultSwitcherShortcut, manager.SwitcherShortcut);
    }

    // A bare key would take that key away from every app on the machine, and a gesture with
    // no modifier could never detect a release either -- there would be nothing to hold.
    [Fact]
    public void A_chord_with_no_modifier_is_refused()
    {
        Assert.True(Manager().SetSwitcherShortcut("`").IsFailure);
    }

    // state.json is meant to be hand-editable, so someone WILL eventually put something
    // broken in it. Falling back beats leaving the app with no way to switch workspaces.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+Alt+Bananas")]
    [InlineData("gibberish")]
    public void A_hand_edited_file_holding_something_unusable_falls_back_to_the_default(string stored)
    {
        this.store.Stored = AppState.Empty with { SwitcherShortcut = stored };
        var manager = Manager();
        Assert.True(manager.LoadState().IsSuccess);

        Assert.Equal(AppState.DefaultSwitcherShortcut, manager.SwitcherShortcut);
    }

    // An older state.json predates the key entirely; it must load without migration.
    [Fact]
    public void A_state_file_written_before_this_setting_existed_still_loads()
    {
        this.store.Stored = AppState.Empty;
        var manager = Manager();
        Assert.True(manager.LoadState().IsSuccess);

        Assert.Equal(AppState.DefaultSwitcherShortcut, manager.SwitcherShortcut);
    }

    // --- the chord vocabulary the switcher needs ----------------------------------------

    // The default chord's own key. It could not be spelled at all until the shortcut became
    // configurable, which would have made the default unreachable through the editor.
    [Theory]
    [InlineData("Ctrl+Alt+`")]
    [InlineData("Ctrl+Alt+Grave")]
    [InlineData("Ctrl+Alt+Backtick")]
    public void The_backtick_key_can_be_spelled_three_ways(string text) =>
        Assert.Equal(0xC0u, Chord.Parse(text).Value.VirtualKey);

    // Modifier order follows Microsoft's own ("Win+Ctrl+D", "Ctrl+Alt+Del"), so Win leads --
    // otherwise the default would render as the "Ctrl+Win+Tab" nobody writes.
    [Theory]
    [InlineData("Ctrl+Alt+`", "Ctrl+Alt", "`")]
    [InlineData("win+shift+tab", "Win+Shift", "Tab")]
    [InlineData("ctrl+win+tab", "Win+Ctrl", "Tab")]
    [InlineData("alt+f4", "Alt", "F4")]
    public void A_chord_renders_back_to_canonical_text(string typed, string modifiers, string key)
    {
        var chord = Chord.Parse(typed).Value;

        Assert.Equal(modifiers, chord.ModifiersText);
        Assert.Equal(key, chord.KeyText);
        Assert.Equal($"{modifiers}+{key}", chord.ToString());
    }
}
