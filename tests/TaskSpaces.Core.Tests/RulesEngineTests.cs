using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Rules;

namespace TaskSpaces.Core.Tests;

public class RulesEngineTests
{
    static readonly Guid Work = Guid.NewGuid();
    static readonly Guid Personal = Guid.NewGuid();

    static WindowInfo Window(string process = "notepad", string title = "Untitled", string? commandLine = null) =>
        new(new WindowHandle(1), 42, process, null, title, commandLine);

    [Fact]
    public void First_matching_rule_wins_in_list_order()
    {
        var rules = new[]
        {
            new WorkspaceRule(Work, RuleMatchKind.TitleRegex, "Unt.*"),
            new WorkspaceRule(Personal, RuleMatchKind.ProcessName, "notepad"),
        };
        Assert.Equal(Work, RulesEngine.MatchWorkspace(Window(), rules).Value);
    }

    [Fact]
    public void Process_name_match_is_case_insensitive() =>
        Assert.Equal(Work, RulesEngine.MatchWorkspace(
            Window(process: "NOTEPAD"),
            [new WorkspaceRule(Work, RuleMatchKind.ProcessName, "notepad")]).Value);

    [Fact]
    public void Title_regex_matches_anywhere_in_title() =>
        Assert.Equal(Work, RulesEngine.MatchWorkspace(
            Window(title: "Sparrow-SLIP39 - Visual Studio"),
            [new WorkspaceRule(Work, RuleMatchKind.TitleRegex, "sparrow")]).Value);

    [Fact]
    public void Browser_profile_rule_matches_profile_directory() =>
        Assert.Equal(Personal, RulesEngine.MatchWorkspace(
            Window(process: "chrome", commandLine: "\"C:\\chrome.exe\" --profile-directory=\"Profile 2\""),
            [new WorkspaceRule(Personal, RuleMatchKind.BrowserProfile, "Profile 2")]).Value);

    [Fact]
    public void No_matching_rule_returns_none() =>
        Assert.True(RulesEngine.MatchWorkspace(
            Window(),
            [new WorkspaceRule(Work, RuleMatchKind.ProcessName, "chrome")]).HasNoValue);

    [Fact]
    public void Invalid_regex_is_treated_as_no_match_not_an_exception() =>
        Assert.True(RulesEngine.MatchWorkspace(
            Window(),
            [new WorkspaceRule(Work, RuleMatchKind.TitleRegex, "([unclosed")]).HasNoValue);

    [Fact]
    public void Rename_rules_produce_the_short_name()
    {
        var rules = new[] { new RenameRule(RuleMatchKind.TitleRegex, "Remote Desktop", "RDP") };
        Assert.Equal("RDP", RulesEngine.MatchRename(Window(title: "myserver - Remote Desktop Connection"), rules).Value);
    }

    [Fact]
    public void Rename_without_match_returns_none() =>
        Assert.True(RulesEngine.MatchRename(Window(), []).HasNoValue);
}
