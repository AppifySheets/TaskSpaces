using TaskSpaces.App;
using TaskSpaces.Core.Updates;

namespace TaskSpaces.Windows.Tests;

// #71. UpdateCheck's own tests cover every decision without a network; these cover the two things
// only a real call can: that the version this build reports about itself is READABLE, and that
// GitHub's actual response still has the shape UpdateCheck expects.
//
// The second one is the reason this exists at all. The parse is written against a payload shape
// this app does not own, and a repo with no release, a renamed field or an API that starts
// answering 403 to our User-Agent would all look identical from inside the app: silence. A test
// that silence cannot pass is the only way to tell "no update" from "the check is broken".
public class UpdateServiceTests
{
    // The one half that needs no network. If this is wrong every comparison downstream is wrong,
    // and the symptom would be an app that either never updates or announces an update for ever.
    [Fact]
    public void The_running_version_is_something_a_version_check_can_read()
    {
        var running = UpdateService.RunningVersion;

        Assert.False(string.IsNullOrWhiteSpace(running));
        // Compared against an absurdly high version rather than a literal, so this keeps working
        // after every release bump: whatever we are, 999.0.0 is newer, and that can only be true
        // if the running version parsed.
        Assert.True(UpdateCheck.IsNewer(running, "999.0.0"), $"unreadable running version: {running}");
        Assert.False(UpdateCheck.IsNewer(running, running));
    }

    // Category=Integration: it talks to github.com, so it is excluded from the routine
    // `--filter "Category!=Integration"` run like every other test that touches the real world.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task The_real_release_endpoint_still_parses()
    {
        var latest = await UpdateService.NewerThanRunningAsync();

        // Deliberately NOT asserting that an update exists -- normally none does, and a test that
        // only passes between releases is a test nobody trusts. What is asserted is that the call
        // reached GitHub and the payload was understood, which is exactly what silence hides.
        Assert.True(latest.IsSuccess, $"release check failed: {(latest.IsFailure ? latest.Error : "")}");
    }
}
