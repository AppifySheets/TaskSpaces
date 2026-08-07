using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using TaskSpaces.Core.Domain;
using TaskSpaces.Windows.Monitoring;

namespace TaskSpaces.App;

// A workspace as this picker needs to draw it: nothing but a name and its lane colour.
// Resolved by the caller (WorkspaceSwitchGesture) so the colour comes from the SAME
// WorkspacePalette lookup the floating bar uses, keyed by the workspace's defined position
// rather than its position in the most-recently-used list -- a workspace must not change
// colour just because you visited it.
public sealed record SwitcherChoice(string Name, string Color);

// The Alt+Tab-style workspace picker. Purely a display: it knows how to draw a list and
// which row is highlighted, and nothing about hotkeys, timers or switching. The gesture
// (WorkspaceSwitchGesture) owns all of that.
public partial class WorkspaceSwitcher : Window
{
    readonly List<Border> rows = [];

    public WorkspaceSwitcher() => InitializeComponent();

    // Draw the list and show it BESIDE the floating bar. The chord is passed in rather than
    // assumed so the hint line names whatever is currently bound.
    //
    // Petre: "show the previous list but ONLY next to the floating window", "next to, meaning: on
    // the same screen as the floating window is", "either on the left or on the right".
    //
    // Two earlier answers to "where does this go" are both retired by that. Centring on the
    // CURSOR's monitor guessed which screen he was looking at, and guessed wrong whenever the
    // pointer was parked somewhere he was not. One picker per screen removed the guess by
    // answering everywhere at once, which is louder than the question deserved. The bar is the
    // thing he is already looking at, so the list belongs against it -- no guess, one window.
    public void Present(IReadOnlyList<SwitcherChoice> choices, int selected, Chord chord, Window anchor)
    {
        // Trimmed from "hold X · tap Y to walk · release to switch". Release-commits is Alt+Tab's
        // own convention and the least surprising half of the gesture, so it is the half worth
        // dropping to keep this narrow.
        Hint.Text = $"hold {chord.ModifiersText} · tap {chord.KeyText}";
        rows.Clear();
        Rows.Children.Clear();
        choices.ToList().ForEach(choice =>
        {
            var row = Row(choice);
            rows.Add(row);
            Rows.Children.Add(row);
        });
        Select(selected);

        Show();
        PlaceBeside(anchor); // after Show(), so ActualWidth/Height are real (same order as FloatingBar.ShowBar)
    }

    // Repaint only. Called on every tap of the key, so it must not relayout: a picker that
    // resized itself as the highlight moved would drift across the screen mid-gesture.
    public void Select(int selected) =>
        rows.Select((row, index) => (row, index)).ToList()
            .ForEach(x => x.row.Background = x.index == selected ? SelectedBackground : Brushes.Transparent);

    // Returns the Border rather than a UIElement: Select below repaints these by index, so
    // the row's background has to stay reachable without a cast.
    static Border Row(SwitcherChoice choice)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        // The lane colour at FULL strength here, unlike the floating bar's diluted tint:
        // this swatch sits on an opaque panel with nothing behind it to compete with, and
        // matching colour to colour is the whole point of showing it.
        content.Children.Add(new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(2),
            Background = Swatch(choice.Color),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        content.Children.Add(new TextBlock
        {
            Text = choice.Name,
            Foreground = Brushes.White,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        });

        return new Border
        {
            Child = content,
            Padding = new Thickness(7, 4, 10, 4),
            CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent,
            MinWidth = 150,
        };
    }

    // Frozen for the reason every shared brush in FloatingBar is frozen: an unfrozen static
    // Freezable takes the thread affinity of whichever thread created it, and then throws
    // when a control on another thread is given it.
    static readonly Brush SelectedBackground = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

    static readonly Dictionary<string, Brush> swatches = [];

    static Brush Swatch(string hex)
    {
        if (swatches.TryGetValue(hex, out var cached)) return cached;
        try
        {
            var brush = Frozen((Color)ColorConverter.ConvertFromString(hex));
            swatches[hex] = brush;
            return brush;
        }
        // A hand-edited state.json can hold anything; an unreadable colour is a grey dot,
        // never a crash on every keypress.
        catch (FormatException) { return Brushes.Gray; }
    }

    static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // Beside the bar, on the bar's own monitor, on whichever side has room.
    //
    // Which side is not a preference: the bar lives against a screen edge (it snaps there, and
    // grows leftwards from a right anchor), so one side of it is usually a few pixels of screen
    // and the other is the whole desktop. Picking the roomier side is what makes "next to the
    // floating window" mean the same thing whether the bar is parked left or right.
    //
    // Tops aligned rather than centred: the bar's first row and the list's first row then read
    // across at the same height, and the list grows downward the way a list should.
    //
    // Everything here is in WPF units, mixing this window's Left/Top with the anchor's, which is
    // sound because both are Windows in the same coordinate space. The monitor rect is divided by
    // the MONITOR's own DPI rather than this window's, for the reason FloatingBar.PositionFromState
    // documents at length: a window-scoped DPI query can still report a stale scale immediately
    // after Show(), and the bar may well be sitting on a screen scaled differently from the
    // primary.
    void PlaceBeside(Window anchor)
    {
        var monitor = NativeMethods.MonitorFromWindow(
            new WindowInteropHelper(anchor).Handle, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return;

        NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out var dpiY);
        var scaleX = dpiX / 96.0;
        var scaleY = dpiY / 96.0;
        var workLeft = info.rcWork.Left / scaleX;
        var workRight = info.rcWork.Right / scaleX;
        var workTop = info.rcWork.Top / scaleY;
        var workBottom = info.rcWork.Bottom / scaleY;

        const double gap = 8;
        var toLeft = anchor.Left - workLeft;
        var toRight = workRight - (anchor.Left + anchor.ActualWidth);
        Left = toRight >= toLeft
            ? anchor.Left + anchor.ActualWidth + gap
            : anchor.Left - gap - ActualWidth;

        // Clamped last, and unconditionally: on a bar wider than the space either side of it,
        // both candidates above land off-screen, and a picker you cannot see is worse than one
        // sitting slightly over the bar.
        Left = Math.Clamp(Left, workLeft, Math.Max(workLeft, workRight - ActualWidth));
        Top = Math.Clamp(anchor.Top, workTop, Math.Max(workTop, workBottom - ActualHeight));
    }
}
