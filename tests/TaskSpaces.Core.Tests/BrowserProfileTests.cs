using TaskSpaces.Core.Rules;

namespace TaskSpaces.Core.Tests;

public class BrowserProfileTests
{
    [Theory]
    [InlineData("chrome.exe --profile-directory=Default", "Default")]
    [InlineData("chrome.exe --profile-directory=\"Profile 2\" --restore-session", "Profile 2")]
    [InlineData("msedge.exe --no-first-run --profile-directory=Work", "Work")]
    public void Extracts_profile_directory(string commandLine, string expected) =>
        Assert.Equal(expected, BrowserProfile.FromCommandLine(commandLine).Value);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("notepad.exe C:\\notes.txt")]
    public void No_profile_returns_none(string? commandLine) =>
        Assert.True(BrowserProfile.FromCommandLine(commandLine).HasNoValue);

    // --- the user data dir ---------------------------------------------------------------------
    //
    // Petre: "opening chrome in a workspace, i think claude opened it, opened up in my current
    // workspace, not its own workspace... but it has worked correctly in the past."
    //
    // A browser told to keep its profile somewhere else entirely, which matters for the same reason
    // the PWA app id does: an automated Chrome and Petre's own Chrome are different apps that happen
    // to share an executable, and until this was read they shared an IDENTITY too. Measured on his
    // machine, not one of fifteen live Chrome processes carried a --profile-directory, so every Chrome
    // window there was the single identity "chrome.exe|" -- and placement memory stands down whenever
    // another live window shares an identity, so it could never place any of them.
    [Theory]
    [InlineData(@"chrome.exe --user-data-dir=C:\temp\mcp-chrome-829010e", @"C:\temp\mcp-chrome")]
    [InlineData(@"chrome.exe --user-data-dir=""C:\my profiles\work"" --restore-session", @"C:\my profiles\work")]
    public void Extracts_the_user_data_dir_without_its_per_session_suffix(string commandLine, string expected) =>
        Assert.Equal(expected, BrowserProfile.UserDataDirFromCommandLine(commandLine).Value);

    // The suffix Playwright MCP appends is new on every launch, which is what makes the raw path
    // useless as an identity: a roster entry saved on Monday would match nothing on Tuesday. Two
    // sessions of one tool have to normalise to one directory.
    [Fact]
    public void Two_sessions_of_the_same_tool_normalise_to_one_dir() =>
        Assert.Equal(
            BrowserProfile.UserDataDirFromCommandLine(@"chrome.exe --user-data-dir=C:\x\ms-playwright-mcp\mcp-chrome-829010e").Value,
            BrowserProfile.UserDataDirFromCommandLine(@"chrome.exe --user-data-dir=C:\x\ms-playwright-mcp\mcp-chrome-5adf218").Value);

    // ...and the stripping stays narrow, or it would merge profiles a person named by hand. A trailing
    // dash and four or more characters with a digit among them is what a random session token looks
    // like and what a chosen name almost never does.
    [Theory]
    [InlineData(@"chrome.exe --user-data-dir=C:\profiles\chrome-beta", @"C:\profiles\chrome-beta")]
    [InlineData(@"chrome.exe --user-data-dir=C:\profiles\chrome-2", @"C:\profiles\chrome-2")]
    public void A_hand_named_dir_is_left_alone(string commandLine, string expected) =>
        Assert.Equal(expected, BrowserProfile.UserDataDirFromCommandLine(commandLine).Value);

    // Two profiles side by side under one parent are two profiles. Keying on the PARENT directory was
    // the first idea and would have collapsed exactly this case.
    [Fact]
    public void Two_dirs_under_one_parent_stay_distinct() =>
        Assert.NotEqual(
            BrowserProfile.UserDataDirFromCommandLine(@"chrome.exe --user-data-dir=C:\profiles\a").Value,
            BrowserProfile.UserDataDirFromCommandLine(@"chrome.exe --user-data-dir=C:\profiles\b").Value);

    // The ordinary case, and the reason this can be read at all without disturbing anything: a browser
    // started normally has no --user-data-dir, so its identity is exactly what it was.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"chrome.exe --profile-directory=Default --restore-session")]
    public void No_user_data_dir_returns_none(string? commandLine) =>
        Assert.True(BrowserProfile.UserDataDirFromCommandLine(commandLine).HasNoValue);
}
