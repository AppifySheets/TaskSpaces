using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using TaskSpaces.Core;
using TaskSpaces.Core.Overview;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Windows.Activation;
using TaskSpaces.Windows.Monitoring;

namespace TaskSpaces.App;

// Task 11 (spec §Floating icon bar): a small always-on-top, borderless, translucent
// bar showing ONLY app icons, one compact row per workspace (📌 Pinned first when
// non-empty; unbound/non-workspace desktops excluded -- it is a workspace bar, not the
// full switcher overview). Click an icon -> JumpTo (switch workspace if needed, then
// focus). No text, no roster (not-running) entries, no drag-and-drop of WINDOWS onto
// it -- only the bar's own background is draggable, to reposition itself. A glanceable
// jump surface, not a manager (spec, explicitly).
//
// One instance lives for the app's lifetime, created lazily on first show and then
// toggled via ShowBar()/HideBar() from the tray menu (App.xaml.cs) -- unlike
// SwitcherPanel it is not summon/dismiss-on-focus-loss; once shown it sits on screen
// until explicitly hidden.
public partial class FloatingBar : Window
{
    readonly WorkspaceManager manager;
    readonly WindowActivator activator = new();
    IDisposable? subscription;

    public FloatingBar(WorkspaceManager manager)
    {
        this.manager = manager;
        InitializeComponent();
        Rebuild();
        // Live-refresh while visible, same pattern as WindowGroupsView.Bind: windows
        // opening/closing (manual script item 36) must update the bar without Petre
        // having to toggle it off and on.
        subscription = manager.StateChanged.Subscribe(_ => Dispatcher.Invoke(() => { if (IsVisible) Rebuild(); }));
        Closed += (_, _) => subscription?.Dispose();
    }

    // Called both at startup (App.OnStartup, when persisted state says Visible: true)
    // and from the tray toggle. Rebuilds unconditionally first: StateChanged may have
    // fired while hidden (the subscription above skips rebuilds whenever !IsVisible),
    // so without this the bar could flash stale content the instant it reappears.
    // Persists right after positioning so Visible=true and the (possibly clamped)
    // position always land together.
    public void ShowBar()
    {
        Rebuild();
        Show();
        PositionFromState();
        manager.SaveFloatingBar(new FloatingBarState(Left, Top, true));
    }

    public void HideBar()
    {
        Hide();
        manager.SaveFloatingBar(new FloatingBarState(Left, Top, false));
    }

    void OnHideClick(object sender, RoutedEventArgs e) => HideBar();

    // Dragging the bar's translucent background repositions it. A WPF Button
    // (ButtonBase) marks its own MouseLeftButtonDown handled during OnMouseLeftButtonDown,
    // so this bubbling handler on the Border never fires for a press that started on an
    // icon button -- exactly the split the spec wants (background drags the bar, icons
    // jump). DragMove() blocks until the mouse button is released, so persisting right
    // after it returns captures the final dropped position -- no separate
    // LocationChanged-debounce needed, per the brief.
    void OnBorderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        DragMove();
        manager.SaveFloatingBar(new FloatingBarState(Left, Top, true)); // only draggable while shown
    }

    void Rebuild()
    {
        Rows.Children.Clear();
        manager.WindowsByWorkspace().Tap(overview =>
        {
            if (overview.Pinned.Count > 0)
                Rows.Children.Add(GroupRow("Pinned", overview.Pinned));

            // Spec: "unbound desktops excluded -- it is a workspace bar" (OtherDesktops
            // is never consulted here). Workspaces with nothing running are skipped
            // outright rather than shown as an empty placeholder row -- an icon bar
            // with nothing to click in a group is noise, not useful chrome.
            overview.Workspaces
                .Where(g => g.Running.Count > 0)
                .ToList()
                .ForEach(g => Rows.Children.Add(GroupRow(g.Workspace.Name, g.Running)));
        });
        // Overview query failure (e.g. a transient desktop-enumeration hiccup) just
        // leaves whatever the bar last showed -- there's no text area on this surface to
        // report an error into, and the next StateChanged pulse retries for free.
    }

    UIElement GroupRow(string workspaceName, IEnumerable<WindowRow> rows)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        rows.ToList().ForEach(r => panel.Children.Add(IconButton(workspaceName, r)));
        return panel;
    }

    UIElement IconButton(string workspaceName, WindowRow row)
    {
        var button = new Button
        {
            Padding = new Thickness(2),
            Margin = new Thickness(2),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ToolTip = $"{workspaceName} · {row.Window.Title}",
        };
        var icon = IconCache.For(row.Window.ProcessPath);
        if (icon is not null)
            // IconCache freezes icons at a fixed 16px source (shared with the switcher
            // panel and Manage window rows). Scaling that 16px bitmap up to the bar's
            // 20px target here is the simple option called out in the brief -- adding a
            // size parameter to the cache for this one caller isn't worth it, and a
            // 16->20px upscale of a small glyph is not visually distinguishable.
            button.Content = new Image { Source = icon, Width = 20, Height = 20 };
        // Click -> jump, with no Hide() afterwards: unlike the switcher panel, this bar
        // is a persistent surface (spec) -- it stays open across every jump so Petre can
        // click several icons in a row.
        //
        // Reviewer (Task 11 fix round 1, Minor): why WindowActivator.Activate's
        // SetForegroundWindow succeeds from here -- clicking the icon Button first
        // activates THIS window (FloatingBar, a normal top-level window with no
        // WS_EX_NOACTIVATE style), which grants our process the foreground-change
        // rights Windows normally restricts; the activator then hands that foreground
        // privilege on to the target window. Same rationale as SwitcherPanel's
        // running-row click.
        button.Click += (_, _) => Report(manager.JumpTo(row.Window.Handle, activator));
        return button;
    }

    // Restores Left/Top from persisted state (or computes the bottom-right work-area
    // default when never configured) and clamps into the nearest monitor's work area --
    // the same DPI-conversion pattern as SwitcherPanel.PositionNear, reused here because
    // the physical-pixel/DIP mismatch it guards against applies identically to this
    // window. Called after Show() (like SwitcherPanel.Peek does for PositionNear) so
    // ActualWidth/ActualHeight already reflect the SizeToContent layout pass.
    void PositionFromState()
    {
        var stored = manager.State.FloatingBar;
        var probe = new NativeMethods.POINT { X = (int)(stored?.Left ?? 0), Y = (int)(stored?.Top ?? 0) };
        var monitor = NativeMethods.MonitorFromPoint(probe, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            // Best-effort fallback if the API ever fails -- better than crashing the show.
            Left = stored?.Left ?? 0;
            Top = stored?.Top ?? 0;
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var workLeft = info.rcWork.Left / dpi.DpiScaleX;
        var workTop = info.rcWork.Top / dpi.DpiScaleY;
        var workRight = info.rcWork.Right / dpi.DpiScaleX;
        var workBottom = info.rcWork.Bottom / dpi.DpiScaleY;

        // No persisted state at all (first run, or an old state.json): default to the
        // bottom-right corner of the work area minus the bar's own size (brief).
        var (rawLeft, rawTop) = stored is { } s
            ? (s.Left, s.Top)
            : (workRight - ActualWidth, workBottom - ActualHeight);

        Left = Math.Clamp(rawLeft, workLeft, Math.Max(workLeft, workRight - ActualWidth));
        Top = Math.Clamp(rawTop, workTop, Math.Max(workTop, workBottom - ActualHeight));
    }

    Result Report(Result result) => result.TapError(err => MessageBox.Show(err, "TaskSpaces"));
}
