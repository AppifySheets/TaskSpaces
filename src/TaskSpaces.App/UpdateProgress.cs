using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TaskSpaces.App;

// What the user watches while an update downloads. Petre: "add a notification to downloading, then
// restarting, so the user knows what's happening."
//
// A WINDOW rather than a balloon, and the reason is settled rather than a preference: this app cannot
// deliver a balloon at all (#123). It has no per-app notification key, no AppUserModelID and no Start-menu
// shortcut, so Windows has nothing to attribute a toast to and drops it. The update ANNOUNCEMENT became a
// dialog for exactly that reason, and progress has to travel the same road.
//
// The tray tooltip it replaces was worse than nothing here: it only appears if you happen to hover the
// tray icon, which nobody does while waiting for something they just asked for. A ~75MB download over a
// slow link is long enough to look like a click that did nothing, which is the complaint that started this
// whole flow.
//
// Deliberately NOT a dialog: modeless, no buttons, no cancel. It reports; it does not ask. A cancel button
// would need a CancellationToken threaded through the download and a decision about the half-written part
// file, which is real work for a once-a-release wait. Closing it is what the handover does.
public sealed class UpdateProgress : Window
{
    readonly TextBlock headline = new()
    {
        FontWeight = FontWeights.SemiBold,
        Foreground = Brushes.White,
        TextWrapping = TextWrapping.Wrap,
    };

    readonly TextBlock detail = new()
    {
        Foreground = new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)),
        Margin = new Thickness(0, 6, 0, 0),
        TextWrapping = TextWrapping.Wrap,
    };

    readonly ProgressBar bar = new()
    {
        Height = 6,
        Minimum = 0,
        Maximum = 100,
        Margin = new Thickness(0, 12, 0, 0),
        // Indeterminate until the first byte arrives: a bar sitting at zero for two seconds reads as
        // stuck, where a moving one reads as working.
        IsIndeterminate = true,
    };

    public UpdateProgress(string version)
    {
        Title = "TaskSpaces";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        // Topmost for the same reason the bar is: this app's whole surface lives above other windows, and
        // an update the user just asked for should not open behind the thing they were reading.
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));

        headline.Text = $"Downloading TaskSpaces {version}…";
        detail.Text = "Starting the download.";

        var body = new StackPanel { Margin = new Thickness(16) };
        body.Children.Add(headline);
        body.Children.Add(bar);
        body.Children.Add(detail);
        Content = body;
    }

    /// <summary>How far the download has got, in bytes. A total of zero means the server did not say.</summary>
    public void Downloaded(long bytes, long total)
    {
        // A server that omits Content-Length leaves nothing to compute a percentage from, so the bar keeps
        // moving on its own and the megabyte count carries the news instead.
        if (total > 0)
        {
            bar.IsIndeterminate = false;
            bar.Value = Math.Clamp(bytes * 100.0 / total, 0, 100);
            detail.Text = $"{Megabytes(bytes)} of {Megabytes(total)} MB";
            return;
        }

        detail.Text = $"{Megabytes(bytes)} MB";
    }

    /// <summary>The download is done and the new version is about to take over.</summary>
    public void HandingOver(string version)
    {
        bar.IsIndeterminate = true;
        headline.Text = $"Starting TaskSpaces {version}…";
        detail.Text = "This window and the bar will close, and the new version will open in their place.";
    }

    static string Megabytes(long bytes) => (bytes / 1024.0 / 1024.0).ToString("F1");
}
