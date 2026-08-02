using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using Microsoft.Win32;
using TaskSpaces.Core;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Overview;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Windows.Activation;
using TaskSpaces.Windows.Monitoring;

namespace TaskSpaces.App;

// The switcher: every window across every workspace in one place (spec) — the answer
// to "I need to see all windows, similar to taskbar, without changing desktop first".
// One instance lives for the app's lifetime; each summon rebuilds content fresh.
public partial class SwitcherPanel : Window
{
    readonly WorkspaceManager manager;
    readonly WindowActivator activator = new();
    readonly AppLauncher launcher = new();

    // Task 7 fix round 1 (reviewer, Important): PromptDialog/OpenFileDialog/MessageBox all
    // steal focus from the panel, which fires OnDeactivated -> Hide() the instant they open —
    // defeating the "panel stays open so Petre can act on several rows in a row" design (spec).
    // Every child-dialog invocation runs through RunChildDialog, which sets this flag first;
    // OnDeactivated checks it and skips Hide() while it's true.
    bool childDialogOpen;

    // Task 9 (spec §Tray interaction & hotkeys): hover-to-peek. While true, the panel is
    // visible but was never Activate()d — OnDeactivated must not hide it (it's not
    // focused anyway, so a stray deactivation means nothing), and the proximity timer
    // (not OnDeactivated) governs when it disappears.
    bool peekMode;
    readonly System.Windows.Threading.DispatcherTimer proximityTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    // DIP margin around the panel's own bounds inside which the cursor keeps it open —
    // without slack the panel would vanish the instant the mouse crosses its own edge
    // while moving in to click something near the border.
    const double ProximityMarginDip = 24;

    public SwitcherPanel(WorkspaceManager manager)
    {
        this.manager = manager;
        InitializeComponent();
        // Live refresh while open: windows appear/close and renames land as Petre watches.
        manager.StateChanged.Subscribe(_ => Dispatcher.Invoke(() => { if (IsVisible) Rebuild(); }));
        proximityTimer.Tick += (_, _) => ProximityTick();
    }

    public void Summon(double screenX, double screenY)
    {
        Rebuild();
        // Task 7 fix round 1 (reviewer, Important): SizeToContent means ActualWidth/Height
        // are unknown until the window has actually been shown once — so Show() first, THEN
        // compute where it should sit. (Known trade-off: the window briefly appears at its
        // previous position/default before this repositions it — a first-Show flicker that
        // cannot be verified without a human at the keyboard; noted in the report rather than
        // fabricating a "looks fine" claim.)
        Show();
        Activate();
        PositionNear(screenX, screenY);
    }

    // Task 9: hover summon. Deliberately mirrors Summon() but WITHOUT Activate() — spec:
    // "opens the switcher panel WITHOUT stealing focus". A no-op if already visible (a
    // click-opened or already-peeking panel isn't repositioned out from under the cursor
    // mid-interaction).
    public void Peek(double screenX, double screenY)
    {
        if (IsVisible) return;
        Rebuild();
        ShowActivated = false;
        peekMode = true;
        Show();
        PositionNear(screenX, screenY);
        proximityTimer.Start();
    }

    // Polls the cursor rather than relying on WPF mouse-leave events: the panel was never
    // activated, and MouseLeave on an unfocused, unowned popup-style window is unreliable
    // across monitors/DPI boundaries — GetCursorPos + a bounds check is simple and correct.
    void ProximityTick()
    {
        NativeMethods.GetCursorPos(out var cursor);
        var dpi = VisualTreeHelper.GetDpi(this);
        var cursorX = cursor.X / dpi.DpiScaleX;
        var cursorY = cursor.Y / dpi.DpiScaleY;
        var inside = cursorX >= Left - ProximityMarginDip && cursorX <= Left + ActualWidth + ProximityMarginDip
            && cursorY >= Top - ProximityMarginDip && cursorY <= Top + ActualHeight + ProximityMarginDip;
        if (inside) return;

        proximityTimer.Stop();
        peekMode = false;
        ShowActivated = true; // restore normal click-open behavior for the next summon
        Hide();
    }

    // Anchors the panel's bottom-right corner at the cursor (tray icons live bottom-right of
    // their own monitor) and clamps it fully inside THAT monitor's work area — never the
    // primary monitor's — so a monitor placed left/above primary (negative virtual-screen
    // coordinates) still gets the panel on the correct screen, never spilling over the taskbar.
    void PositionNear(double screenX, double screenY)
    {
        var cursor = new NativeMethods.POINT { X = (int)screenX, Y = (int)screenY };
        var monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            // Best-effort fallback if the API ever fails — better than crashing the summon.
            Left = Math.Max(0, screenX - 320);
            Top = Math.Max(0, screenY - 24 - 660);
            return;
        }

        // GetMonitorInfo answers in physical pixels; WPF's Left/Top are device-independent
        // units (DIPs). Divide by this window's DPI scale or a scaled display places the
        // panel off by the scale factor (e.g. wrong by 25% at 125% scaling).
        var dpi = VisualTreeHelper.GetDpi(this);
        var workLeft = info.rcWork.Left / dpi.DpiScaleX;
        var workTop = info.rcWork.Top / dpi.DpiScaleY;
        var workRight = info.rcWork.Right / dpi.DpiScaleX;
        var workBottom = info.rcWork.Bottom / dpi.DpiScaleY;

        Left = Math.Clamp(screenX - ActualWidth, workLeft, Math.Max(workLeft, workRight - ActualWidth));
        Top = Math.Clamp(screenY - ActualHeight, workTop, Math.Max(workTop, workBottom - ActualHeight));
    }

    // Task 9: peekMode added alongside the existing childDialogOpen guard — a peeked
    // panel was never Activate()d, so it can't legitimately Deactivate() either; any
    // event here while peeking is spurious and must not hide it (the proximity timer
    // owns hide-on-leave for peek mode instead).
    void OnDeactivated(object? s, EventArgs e) { if (!childDialogOpen && !peekMode) Hide(); }
    void OnKeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Escape) Hide(); }

    // Task 9: clicking inside a peeked panel "graduates" it to a normal, focused panel —
    // spec: "Clicking inside the peeked panel activates it, after which normal
    // focus/dismiss behavior applies." From here on OnDeactivated's Hide() governs again.
    void OnPreviewMouseDown(object s, MouseButtonEventArgs e)
    {
        if (!peekMode) return;
        peekMode = false;
        proximityTimer.Stop();
        Activate();
    }

    // Runs a child dialog (PromptDialog.Ask, OpenFileDialog.ShowDialog, the Report
    // MessageBoxes) without the panel disappearing out from under it: sets childDialogOpen so
    // OnDeactivated ignores the deactivation the dialog causes, then re-Activate()s the panel
    // afterwards so focus/foreground comes back to it once the dialog closes.
    T RunChildDialog<T>(Func<T> show)
    {
        childDialogOpen = true;
        try { return show(); }
        finally
        {
            childDialogOpen = false;
            Activate();
        }
    }

    void Rebuild()
    {
        GroupsHost.Children.Clear();
        manager.WindowsByWorkspace()
            .Tap(overview =>
            {
                if (overview.Pinned.Count > 0)
                    AddGroup("📌 Pinned", isCurrent: false, header: null, overview.Pinned.Select(r => RunningRow(r, pinned: true)));
                overview.Workspaces.ToList().ForEach(g => AddGroup(
                    $"{g.Workspace.Name} ({g.Running.Count})", g.IsCurrent, WorkspaceHeader(g),
                    g.Running.Select(r => RunningRow(r, pinned: false)).Concat(g.NotRunning.Select(e => RosterRow(g.Workspace.Id, e)))));
                overview.OtherDesktops.ToList().ForEach(g => AddGroup(
                    $"{g.Name} ({g.Windows.Count})", g.IsCurrent, header: null, g.Windows.Select(r => RunningRow(r, pinned: false))));
            })
            .TapError(err => GroupsHost.Children.Add(new TextBlock { Text = err, Margin = new Thickness(4) }));
    }

    // --- group scaffolding -------------------------------------------------------

    void AddGroup(string title, bool isCurrent, UIElement? header, IEnumerable<UIElement> rows)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        panel.Children.Add(header ?? new TextBlock { Text = title, FontWeight = isCurrent ? FontWeights.Bold : FontWeights.SemiBold, Margin = new Thickness(4, 2, 4, 2) });
        rows.ToList().ForEach(r => panel.Children.Add(r));
        GroupsHost.Children.Add(panel);
    }

    // Workspace headers are interactive: click = switch there; ▶ = start missing apps;
    // ＋ = manually roster an exe. Bold marks the workspace Petre is on right now.
    UIElement WorkspaceHeader(WorkspaceGroup group)
    {
        var header = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };

        var start = new Button { Content = "▶", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(4, 0, 0, 0), ToolTip = $"Start {group.Workspace.Name}: launch its {group.NotRunning.Count} not-running app(s) and switch there", Visibility = group.NotRunning.Count > 0 ? Visibility.Visible : Visibility.Collapsed };
        start.Click += (_, _) => Report(manager.StartWorkspace(group.Workspace.Id, launcher)).Tap(Hide);
        DockPanel.SetDock(start, Dock.Right);

        var add = new Button { Content = "＋", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Add app… (roster an exe in this workspace)" };
        add.Click += (_, _) => OnAddApp(group.Workspace.Id);
        DockPanel.SetDock(add, Dock.Right);

        var name = new Button { Content = $"{group.Workspace.Name} ({group.Running.Count})", FontWeight = group.IsCurrent ? FontWeights.Bold : FontWeights.SemiBold, HorizontalContentAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(0), Background = Brushes.Transparent, ToolTip = "Switch to this workspace" };
        name.Click += (_, _) => Report(manager.Switch(group.Workspace.Id)).Tap(Hide);

        header.Children.Add(start);
        header.Children.Add(add);
        header.Children.Add(name);
        return header;
    }

    // --- rows ----------------------------------------------------------------------

    UIElement RunningRow(WindowRow row, bool pinned)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = IconCache.For(row.Window.ProcessPath);
        if (icon is not null) content.Children.Add(new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) });
        content.Children.Add(new TextBlock { Text = row.Window.Title, FontWeight = row.OriginalTitle.HasValue ? FontWeights.SemiBold : FontWeights.Normal });
        // Renamed window: short name prominent, original title dimmed beside it (spec).
        row.OriginalTitle.Tap(original => content.Children.Add(new TextBlock { Text = $"  ·  was: {original}", Opacity = 0.55, TextTrimming = TextTrimming.CharacterEllipsis }));

        var button = new Button { Content = content, HorizontalContentAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(16, 2, 4, 2), ToolTip = row.Window.Title };
        button.Click += (_, _) => Report(manager.JumpTo(row.Window.Handle, activator)).Tap(Hide);
        button.ContextMenu = RunningMenu(row, pinned);
        return button;
    }

    ContextMenu RunningMenu(WindowRow row, bool pinned)
    {
        var menu = new ContextMenu();

        var pin = new MenuItem { Header = pinned ? "Unpin from all workspaces" : "Pin to all workspaces" };
        pin.Click += (_, _) => Report(pinned ? manager.UnpinWindow(row.Window.Handle) : manager.PinWindow(row.Window.Handle));
        menu.Items.Add(pin);

        var sendTo = new MenuItem { Header = "Send to" };
        manager.State.Workspaces.ToList().ForEach(w =>
        {
            var item = new MenuItem { Header = w.Name };
            item.Click += (_, _) => Report(manager.AssignWindow(row.Window.Handle, w.Id));
            sendTo.Items.Add(item);
        });
        menu.Items.Add(sendTo);
        menu.Items.Add(new Separator());

        var rename = new MenuItem { Header = "Rename…" };
        rename.Click += (_, _) => RunChildDialog(() => PromptDialog.Ask("Rename window", "Short name to show on the taskbar:", row.Window.Title))
            .Tap(shortName => Report(manager.RenameWindow(row.Window.Handle, shortName)));
        menu.Items.Add(rename);

        var restore = new MenuItem { Header = "Restore title", IsEnabled = row.OriginalTitle.HasValue };
        restore.Click += (_, _) => Report(manager.RestoreTitle(row.Window.Handle));
        menu.Items.Add(restore);
        return menu;
    }

    // Roster-only entry: the app BELONGS here but isn't running — dimmed, click to launch.
    // The panel stays open on purpose: the row flips to running as the window arrives,
    // and Petre can start several apps in a row (spec).
    UIElement RosterRow(Guid workspaceId, InventoryEntry entry)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Opacity = 0.55 };
        var icon = IconCache.For(entry.ProcessPath);
        if (icon is not null) content.Children.Add(new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) });
        content.Children.Add(new TextBlock { Text = $"{entry.Title}  (not running)", FontStyle = FontStyles.Italic });

        var button = new Button { Content = content, HorizontalContentAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(16, 2, 4, 2), ToolTip = entry.CommandLine ?? entry.ProcessPath };
        button.Click += (_, _) => Report(manager.StartRosterEntry(workspaceId, entry, launcher));

        var menu = new ContextMenu();
        var startOne = new MenuItem { Header = "Start" };
        startOne.Click += (_, _) => Report(manager.StartRosterEntry(workspaceId, entry, launcher));
        menu.Items.Add(startOne);
        var remove = new MenuItem { Header = "Remove from workspace" };
        remove.Click += (_, _) => Report(manager.RemoveRosterEntry(workspaceId, entry));
        menu.Items.Add(remove);
        button.ContextMenu = menu;
        return button;
    }

    void OnAddApp(Guid workspaceId)
    {
        var picker = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe", Title = "Add app to workspace" };
        if (RunChildDialog(() => picker.ShowDialog()) != true) return;
        var arguments = RunChildDialog(() => PromptDialog.Ask("Arguments", "Optional command-line arguments (path+args identify WHAT the app shows):"))
            .GetValueOrDefault("");
        Report(manager.AddRosterEntry(workspaceId, picker.FileName, arguments).Map(_ => true));
    }

    // Instance methods (not static — fix round 1): the failure MessageBox is itself a child
    // dialog and must go through RunChildDialog too, else every reported error closed the panel.
    Result Report(Result result) => result.TapError(err => RunChildDialog(() => MessageBox.Show(err, "TaskSpaces")));
    Result<T> Report<T>(Result<T> result) => result.TapError(err => RunChildDialog(() => MessageBox.Show(err, "TaskSpaces")));
}
