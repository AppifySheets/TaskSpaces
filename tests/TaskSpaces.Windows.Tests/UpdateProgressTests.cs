using System.Windows.Controls;
using TaskSpaces.App;
using TaskSpaces.Core.Updates;

namespace TaskSpaces.Windows.Tests;

// Petre: "add a notification to downloading, then restarting, so the user knows what's happening", and,
// reading the update dialog: "that sha next to the version is strange, get rid of it in the notification."
//
// The window is real WPF, so these run on the STA thread like every other surface test here.
public class UpdateProgressTests
{
    static ProgressBar BarOf(UpdateProgress window) =>
        ((StackPanel)window.Content).Children.OfType<ProgressBar>().Single();

    static IReadOnlyList<string> TextOf(UpdateProgress window) =>
        ((StackPanel)window.Content).Children.OfType<TextBlock>().Select(t => t.Text).ToList();

    // Before the first byte, there is nothing to be a percentage OF, and a bar sitting at zero reads as
    // stuck where a moving one reads as working.
    [Fact]
    public void It_starts_indeterminate() => StaThread.Run(() =>
    {
        var window = new UpdateProgress("1.10.4");

        Assert.True(BarOf(window).IsIndeterminate);
        Assert.Contains(TextOf(window), text => text.Contains("Downloading") && text.Contains("1.10.4"));

        window.Close();
    });

    [Fact]
    public void Progress_becomes_a_percentage_once_the_size_is_known() => StaThread.Run(() =>
    {
        var window = new UpdateProgress("1.10.4");

        window.Downloaded(bytes: 40_000_000, total: 80_000_000);

        Assert.False(BarOf(window).IsIndeterminate);
        Assert.Equal(50, BarOf(window).Value);
        Assert.Contains(TextOf(window), text => text.Contains("38.1 of 76.3 MB"));

        window.Close();
    });

    // A server that omits Content-Length leaves no percentage to compute, and inventing one would be a
    // lie. The megabyte count carries the news instead, and the bar keeps moving on its own.
    [Fact]
    public void With_no_total_it_counts_megabytes_and_keeps_moving() => StaThread.Run(() =>
    {
        var window = new UpdateProgress("1.10.4");

        window.Downloaded(bytes: 10_485_760, total: 0);

        Assert.True(BarOf(window).IsIndeterminate);
        Assert.Contains(TextOf(window), text => text.Contains("10.0 MB"));

        window.Close();
    });

    // The second half of the ask: say the handover is happening. An app that vanishes without a word
    // looks like a crash, not an update.
    [Fact]
    public void The_handover_is_announced_before_the_app_stands_down() => StaThread.Run(() =>
    {
        var window = new UpdateProgress("1.10.4");
        window.Downloaded(80_000_000, 80_000_000);

        window.HandingOver("1.10.4");

        Assert.Contains(TextOf(window), text => text.Contains("Starting") && text.Contains("1.10.4"));
        // ...and nothing else. Petre, of the sentence that used to describe the windows closing: "too much
        // information." The headline and a moving bar are the message.
        Assert.DoesNotContain(TextOf(window), text => text.Contains("close"));
        Assert.True(BarOf(window).IsIndeterminate);

        window.Close();
    });

    // The sha, which is what he actually saw: a build from source stamps its commit into
    // InformationalVersion, and that is the right thing to compare and the wrong thing to read.
    [Theory]
    [InlineData("1.10.2+f3fe1779b7e0f15f8fb369573e49ae16e4af69a0", "1.10.2")]
    [InlineData("1.10.3", "1.10.3")]
    public void The_version_shown_to_a_user_carries_no_commit(string informational, string shown) =>
        Assert.Equal(shown, UpdateCheck.ForDisplay(informational));
}
