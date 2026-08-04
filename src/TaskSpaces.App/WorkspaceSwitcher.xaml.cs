using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

    // Draw the list and show it centred on whichever monitor the cursor is on. The chord is
    // passed in rather than assumed so the hint line names whatever is currently bound.
    public void Present(IReadOnlyList<SwitcherChoice> choices, int selected, Chord chord)
    {
        Hint.Text = $"hold {chord.ModifiersText} · tap {chord.KeyText} to walk · release to switch";
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
        CenterOnCursorMonitor(); // after Show(), so ActualWidth/Height are real (same order as FloatingBar.ShowBar)
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
            Margin = new Thickness(0, 0, 8, 0),
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
            Padding = new Thickness(8, 5, 16, 5),
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

    // Centred on the monitor holding the CURSOR, not the primary monitor: Petre works across
    // more than one screen, and a picker that always appeared on the primary would be
    // somewhere he is not looking. Queries the monitor's own DPI rather than the window's,
    // for the reason FloatingBar.PositionFromState documents at length -- a window-scoped DPI
    // query can still report a stale scale immediately after Show().
    void CenterOnCursorMonitor()
    {
        if (!NativeMethods.GetCursorPos(out var cursor)) return;
        var monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return;

        NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out var dpiY);
        var scaleX = dpiX / 96.0;
        var scaleY = dpiY / 96.0;
        Left = (info.rcWork.Left + info.rcWork.Right) / 2.0 / scaleX - ActualWidth / 2;
        Top = (info.rcWork.Top + info.rcWork.Bottom) / 2.0 / scaleY - ActualHeight / 2;
    }
}
