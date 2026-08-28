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
    public void Firefox_identity_uses_generic_args_not_profile_directory()
    {
        // Firefox has no --profile-directory (that's Chromium-only) -- it uses -P/-profile,
        // which BrowserProfile doesn't parse. Routing Firefox through the Chromium
        // profile-only path would collapse every Firefox window to the same identity
        // regardless of profile; it must go through the generic args-based path instead,
        // where -P work vs -P home naturally differ.
        var work = RosterIdentity.Of(@"C:\firefox\firefox.exe", "\"C:\\firefox\\firefox.exe\" -P work");
        var home = RosterIdentity.Of(@"C:\firefox\firefox.exe", "\"C:\\firefox\\firefox.exe\" -P home");
        Assert.NotEqual(work, home);
    }

    // An automated browser is not the browser. Petre: "opening chrome in a workspace, i think claude
    // opened it, opened up in my current workspace, not its own workspace."
    //
    // A Chrome that Playwright starts runs out of its own --user-data-dir and carries no
    // --profile-directory at all -- and neither does an ordinary Chrome on Petre's machine, so on
    // profile alone the two were one identity. Placement memory stands down when another live window
    // shares an identity, so his roster entry for the automated Chrome could never be applied while any
    // Chrome window was open, which on his machine is always.
    [Fact]
    public void An_automated_browser_has_its_own_identity()
    {
        var mine = RosterIdentity.Of(@"C:\chrome\chrome.exe", @"""C:\chrome\chrome.exe"" --restore-session");
        var automated = RosterIdentity.Of(@"C:\chrome\chrome.exe",
            @"""C:\chrome\chrome.exe"" --user-data-dir=C:\x\ms-playwright-mcp\mcp-chrome-829010e --enable-automation");

        Assert.NotEqual(mine, automated);
    }

    // ...and it is the SAME identity from one automated session to the next, which is the half that
    // makes it useful: the directory's trailing token is new every launch, so keying on the raw path
    // would give every session a fresh identity and remember nothing.
    [Fact]
    public void Two_automated_sessions_share_one_identity() =>
        Assert.Equal(
            RosterIdentity.Of(@"C:\chrome\chrome.exe", @"""C:\chrome\chrome.exe"" --user-data-dir=C:\x\ms-playwright-mcp\mcp-chrome-829010e"),
            RosterIdentity.Of(@"C:\chrome\chrome.exe", @"""C:\chrome\chrome.exe"" --user-data-dir=C:\x\ms-playwright-mcp\mcp-chrome-5adf218"));

    // Two ordinary windows are still one identity, which is the ruling this must not disturb: session
    // arguments vary run to run, and a browser that got a new identity every launch would be a new app
    // every launch.
    [Fact]
    public void Two_ordinary_browser_windows_are_still_one_identity() =>
        Assert.Equal(
            RosterIdentity.Of(@"C:\chrome\chrome.exe", @"""C:\chrome\chrome.exe"" --restore-session"),
            RosterIdentity.Of(@"C:\chrome\chrome.exe", @"""C:\chrome\chrome.exe"" --flag-switches-begin"));

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
