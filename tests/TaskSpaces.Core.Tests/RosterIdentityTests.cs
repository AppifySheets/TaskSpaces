using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rehydration;

namespace TaskSpaces.Core.Tests;

public class RosterIdentityTests
{
    static WindowInfo Window(string path, string? cmd) =>
        new(new WindowHandle(1), 42, System.IO.Path.GetFileNameWithoutExtension(path), path, "t", cmd);

    [Fact]
    public void Same_app_different_content_is_different_identity() =>
        Assert.NotEqual(
            RosterIdentity.Of(@"C:\rider\rider64.exe", "\"C:\\rider\\rider64.exe\" C:\\repos\\X\\X.sln"),
            RosterIdentity.Of(@"C:\rider\rider64.exe", "\"C:\\rider\\rider64.exe\" C:\\repos\\Y\\Y.sln"));

    [Fact]
    public void Identity_is_case_insensitive_and_quote_insensitive() =>
        Assert.Equal(
            RosterIdentity.Of(@"C:\Rider\Rider64.exe", "\"C:\\Rider\\Rider64.exe\" C:\\Repos\\X\\X.sln"),
            RosterIdentity.Of(@"c:\rider\rider64.exe", @"c:\rider\rider64.exe c:\repos\x\x.sln"));

    [Fact]
    public void Browser_identity_is_profile_not_full_args()
    {
        // Chromium browsers spray session-specific args; only the profile identifies content.
        var a = RosterIdentity.Of(@"C:\chrome\chrome.exe", "\"C:\\chrome\\chrome.exe\" --profile-directory=\"Profile 2\" --restore-session");
        var b = RosterIdentity.Of(@"C:\chrome\chrome.exe", "\"C:\\chrome\\chrome.exe\" --profile-directory=\"Profile 2\" --flag-switches-begin");
        Assert.Equal(a, b);
        Assert.NotEqual(a, RosterIdentity.Of(@"C:\chrome\chrome.exe", "\"C:\\chrome\\chrome.exe\" --profile-directory=Default"));
    }

    [Fact]
    public void Window_without_process_path_has_no_identity() =>
        Assert.True(RosterIdentity.Of(Window(@"C:\a.exe", null) with { ProcessPath = null }).HasNoValue);

    [Fact]
    public void IsRunning_matches_identity_not_just_path()
    {
        var entry = new InventoryEntry(@"C:\rider\rider64.exe", "\"C:\\rider\\rider64.exe\" X.sln", "X");
        var otherContent = Window(@"C:\rider\rider64.exe", "\"C:\\rider\\rider64.exe\" Y.sln");
        Assert.False(RosterIdentity.IsRunning(entry, [otherContent]));
        Assert.True(RosterIdentity.IsRunning(entry, [otherContent, Window(@"C:\rider\rider64.exe", "\"C:\\rider\\rider64.exe\" X.sln")]));
    }
}
