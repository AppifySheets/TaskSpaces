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
}
