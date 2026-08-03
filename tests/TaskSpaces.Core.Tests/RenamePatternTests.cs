using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rules;

namespace TaskSpaces.Core.Tests;

// Petre: "'beeper | work chat' I'd change this to say 'beeper *' which would match all
// beepers and still rename to beeper".
public class RenamePatternTests
{
    [Theory]
    [InlineData("beeper *", true)]
    [InlineData("RDP", false)]
    public void A_star_is_what_makes_it_a_pattern(string input, bool expected) =>
        Assert.Equal(expected, RenamePattern.IsWildcard(input));

    // The one input does both jobs: the literal part becomes the taskbar name.
    [Theory]
    [InlineData("beeper *", "beeper")]
    [InlineData("* - Visual Studio Code", "Visual Studio Code")]
    [InlineData("beeper | *", "beeper")]          // the stranded separator goes too
    [InlineData("Remote Desktop Manager *", "Remote Desktop Manager")]
    public void The_short_name_is_the_pattern_without_its_wildcard(string input, string expected) =>
        Assert.Equal(expected, RenamePattern.ShortNameOf(input));

    [Fact]
    public void A_trailing_wildcard_matches_every_title_with_that_prefix()
    {
        var pattern = RenamePattern.ToRegex("beeper *");

        Assert.Matches(pattern, "beeper | work chat");
        Assert.Matches(pattern, "beeper | HRIS");
        Assert.DoesNotMatch(pattern, "Slack | general");
    }

    [Fact]
    public void A_leading_wildcard_matches_every_title_with_that_suffix()
    {
        var pattern = RenamePattern.ToRegex("* - Visual Studio Code");

        Assert.Matches(pattern, "state.json - TaskSpaces - Visual Studio Code");
        Assert.DoesNotMatch(pattern, "TaskSpaces - Rider");
    }

    // A title made almost entirely of regex metacharacters must not become an accidental
    // pattern, or crash the engine. Petre's real RDM title is exactly this shape.
    [Fact]
    public void Regex_metacharacters_in_a_title_are_escaped_not_interpreted()
    {
        var pattern = RenamePattern.ToRegex("Remote Desktop Manager [server-01 - fhd]*");

        Assert.Matches(pattern, "Remote Desktop Manager [server-01 - fhd] extra");
        // Would match if the brackets were treated as a character class.
        Assert.DoesNotMatch(pattern, "Remote Desktop Manager R");
    }
}

// The manager half: a wildcard rename must produce a durable RULE rather than a
// PersistedRename keyed on one exact title.
public class WildcardRenameTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    static WindowInfo Beeper(nint handle, string title) =>
        new(new WindowHandle(handle), (int)handle, "Beeper", @"C:\Beeper.exe", title, null);

    WorkspaceManager Started()
    {
        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    [Fact]
    public void A_wildcard_rename_creates_a_rule_and_leaves_no_exact_persisted_rename()
    {
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Beeper(0x1, "beeper | work chat")));

        Assert.True(manager.RenameWindow(new WindowHandle(0x1), "beeper *").IsSuccess);

        var rule = Assert.Single(store.Stored.RenameRules);
        Assert.Equal(RuleMatchKind.TitleRegex, rule.Kind);
        Assert.Equal("beeper", rule.ShortName);
        Assert.Empty(store.Stored.PersistedRenames); // the whole point: not keyed to one title
    }

    // Applied immediately, so the rename is visible now rather than on the next sweep.
    [Fact]
    public void A_wildcard_rename_renames_the_window_that_prompted_it()
    {
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Beeper(0x1, "beeper | work chat")));

        Assert.True(manager.RenameWindow(new WindowHandle(0x1), "beeper *").IsSuccess);

        Assert.Equal("beeper", titles.Titles[new WindowHandle(0x1)]);
    }

    // The reason for the feature: a DIFFERENT window of the same app, with a title that never
    // existed when the rename was made, still gets named.
    [Fact]
    public void A_later_window_with_a_different_title_is_renamed_too()
    {
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Beeper(0x1, "beeper | work chat")));
        Assert.True(manager.RenameWindow(new WindowHandle(0x1), "beeper *").IsSuccess);

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Beeper(0x2, "beeper | someone else entirely")));

        Assert.Equal("beeper", titles.Titles[new WindowHandle(0x2)]);
    }

    // Without a wildcard, nothing about the old behaviour changes.
    [Fact]
    public void A_plain_rename_still_uses_the_exact_title_mechanism()
    {
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Beeper(0x1, "beeper | work chat")));

        Assert.True(manager.RenameWindow(new WindowHandle(0x1), "Beeper").IsSuccess);

        Assert.Empty(store.Stored.RenameRules);
        Assert.Single(store.Stored.PersistedRenames);
    }

    [Fact]
    public void A_bare_star_is_rejected_rather_than_naming_every_window_nothing()
    {
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Beeper(0x1, "beeper | work chat")));

        var result = manager.RenameWindow(new WindowHandle(0x1), "*");

        Assert.True(result.IsFailure);
        Assert.Empty(store.Stored.RenameRules);
    }
}
