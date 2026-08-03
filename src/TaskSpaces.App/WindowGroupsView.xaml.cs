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

namespace TaskSpaces.App;

// The grouped Pinned / workspace / other-desktop view: every window across every
// workspace, taskbar-style (spec). Extracted from SwitcherPanel (Task 10) so the
// Manage window's Windows tab can host the EXACT same rows, headers, context menus and
// drag-and-drop instead of a bespoke flat list that could drift out of sync.
//
// Two hooks let a host coordinate what it needs without this control knowing about
// SwitcherPanel or ManageWindow specifically:
//   - runChildDialog: wraps every dialog this view opens (rename prompt, add-app file
//     picker, failure message boxes). SwitcherPanel needs this to set its childDialogOpen
//     guard (so OnDeactivated doesn't hide the panel out from under the dialog) and to
//     re-Activate() itself afterwards; ManageWindow has no such concern (it doesn't hide
//     on deactivation) and can leave it at the default pass-through. Non-generic
//     (Func<object?>) because a delegate FIELD can't be generic — RunChildDialog<T> below
//     adapts each call site's concrete T to/from object? at the boundary.
//   - afterAction: invoked after a successful jump/switch/start (the three actions that
//     close SwitcherPanel today via Hide()). ManageWindow leaves this null; its own
//     Reload() already happens for free via this view's StateChanged subscription.
public partial class WindowGroupsView : UserControl
{
    WorkspaceManager manager = null!;
    readonly WindowActivator activator = new();
    readonly AppLauncher launcher = new();
    Func<Func<object?>, object?> runChildDialog = show => show();
    Action? afterAction;
    IDisposable? subscription;

    // Drag payload + group-key vocabulary live in DraggedWindow.cs — shared with
    // FloatingBar so drags interoperate across surfaces (see that file's comment).

    public WindowGroupsView()
    {
        InitializeComponent();
        // Window.Close() unloads its content's visual tree — this is what tears down a
        // ManageWindow instance (a fresh one is created every time "Manage" is opened).
        // Without disposing here, every open would leave one more StateChanged
        // subscription alive forever, each holding this (closed) view and rebuilding it
        // for nothing. SwitcherPanel's single long-lived instance never unloads, so its
        // subscription lives for the app's lifetime exactly like it did before extraction.
        Unloaded += (_, _) => subscription?.Dispose();

        // Auto-scroll while a drag is near the top/bottom edge (root-cause fix for
        // Petre's "drag DOWN works, drag UP doesn't" report): WPF's DragDrop has no
        // built-in auto-scroll, and SwitcherPanel caps this control's height at 640
        // (see SwitcherPanel.xaml) — once the roster is tall enough to scroll, a group
        // scrolled out of view is a group nothing can be dropped onto, full stop. The
        // panel is anchored near the BOTTOM-right of the screen (SwitcherPanel.
        // PositionNear), so scrolling is far more likely to push groups ABOVE the fold
        // out of view than below it — a strong candidate for the reported asymmetry
        // once enough windows are open to overflow the panel.
        //
        // Hooked on PreviewDragOver (tunneling: root-to-leaf) rather than DragOver
        // (bubbling): AddGroup's own per-group DragOver handlers below set
        // e.Handled = true, which would stop a bubbling handler here from ever seeing
        // the event once some inner group panel handles it first. Tunneling fires
        // before that bubble phase even starts, so this always sees every DragOver
        // regardless of which group is currently under the cursor.
        Scroller.PreviewDragOver += OnAutoScrollDragOver;
    }

    // DIP distance from the ScrollViewer's own top/bottom edge that counts as "near
    // enough to auto-scroll", and how far each qualifying DragOver nudges the scroll
    // offset. Small values keep the scroll feeling continuous (DragOver fires often)
    // without overshooting past the group the user is trying to reach.
    const double AutoScrollEdgeDip = 24;
    const double AutoScrollStepDip = 16;

    void OnAutoScrollDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DraggedWindow.DragFormat)) return;
        var y = e.GetPosition(Scroller).Y;
        if (y < AutoScrollEdgeDip)
            Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - AutoScrollStepDip);
        else if (y > Scroller.ActualHeight - AutoScrollEdgeDip)
            Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + AutoScrollStepDip);
    }

    // Called once by each host after construction. Rebuilds immediately, then keeps
    // rebuilding live while visible as manager.StateChanged fires (windows appear/close,
    // renames land) — the same live-refresh SwitcherPanel had, now owned by the view
    // itself so ManageWindow gets it for free instead of wiring its own copy.
    public void Bind(WorkspaceManager manager, Func<Func<object?>, object?>? runChildDialog = null, Action? afterAction = null)
    {
        this.manager = manager;
        this.runChildDialog = runChildDialog ?? (show => show());
        this.afterAction = afterAction;
        subscription?.Dispose();
        subscription = manager.StateChanged.Subscribe(_ => Dispatcher.Invoke(() => { if (IsVisible) Rebuild(); }));
        Rebuild();
    }

    // Forces an immediate rebuild even while hidden — SwitcherPanel.Peek() needs this
    // BEFORE Show() (while IsVisible is still false, so the live-refresh subscription
    // above wouldn't fire), so the panel never flashes stale content as it appears.
    public void Refresh() => Rebuild();

    // Same re-entrancy defect FloatingBar.Rebuild documents at length, in the same shape:
    // clear at the top, add at the bottom, with a message-pumping COM query in between,
    // reached through a Dispatcher.Invoke subscription that runs INLINE when already on the
    // dispatcher thread (line 101 above). A window event delivered mid-query re-entered
    // here, so the nested rebuild's groups survived and the outer rebuild's were appended
    // after them. Petre caught it on the floating bar first because that surface is on
    // screen permanently; this one had the identical bug waiting for the same coincidence.
    bool rebuilding;
    bool rebuildRequested;

    void Rebuild()
    {
        if (rebuilding)
        {
            rebuildRequested = true;
            return;
        }

        rebuilding = true;
        try
        {
            RebuildCore();
        }
        finally
        {
            rebuilding = false;
        }

        // Queued, not looped — see FloatingBar.Rebuild for why the pump needs a turn here.
        if (!rebuildRequested) return;
        rebuildRequested = false;
        Dispatcher.BeginInvoke(new Action(() => { if (IsVisible) Rebuild(); }));
    }

    void RebuildCore()
    {
        GroupsHost.Children.Clear();
        manager.WindowsByWorkspace()
            .Tap(overview =>
            {
                if (overview.Pinned.Count > 0)
                    AddGroup("📌 Pinned", isCurrent: false, header: null,
                        overview.Pinned.Select(r => RunningRow(r, pinned: true, groupKey: DraggedWindow.PinnedGroupKey)),
                        groupKey: DraggedWindow.PinnedGroupKey,
                        onDrop: h => Report(manager.PinWindow(h)));

                overview.Workspaces.ToList().ForEach(g =>
                {
                    var key = DraggedWindow.WorkspaceGroupKey(g.Workspace.Id);
                    AddGroup($"{g.Workspace.Name} ({g.Running.Count})", g.IsCurrent, WorkspaceHeader(g),
                        g.Running.Select(r => RunningRow(r, pinned: false, groupKey: key))
                            .Concat(g.NotRunning.Select(e => RosterRow(g.Workspace.Id, e))),
                        groupKey: key,
                        onDrop: h => Report(manager.AssignWindow(h, g.Workspace.Id)));
                });

                // Unbound-desktop groups (e.g. Petre's "Main") ARE drop targets now, via
                // MoveToDesktop — dragging a window OUT of a workspace has to be possible,
                // or drag-and-drop is a one-way street into workspaces. The exception is
                // the "Unplaced" catch-all (Task 10's bug fix, DesktopId == Guid.Empty):
                // it is not a real desktop, so there is nowhere to move a window TO.
                // Its rows still carry a groupKey and stay draggable as SOURCES.
                overview.OtherDesktops.ToList().ForEach(g =>
                {
                    var key = DraggedWindow.DesktopGroupKey(g.DesktopId);
                    AddGroup($"{g.Name} ({g.Windows.Count})", g.IsCurrent, header: null,
                        g.Windows.Select(r => RunningRow(r, pinned: false, groupKey: key)),
                        groupKey: key,
                        onDrop: g.DesktopId == Guid.Empty ? null : h => Report(manager.MoveToDesktop(h, g.DesktopId)));
                });
            })
            .TapError(err => GroupsHost.Children.Add(new TextBlock { Text = err, Margin = new Thickness(4) }));
    }

    // --- group scaffolding -------------------------------------------------------

    // groupKey/onDrop opt a group INTO being a drop target: null onDrop (the OtherDesktops
    // call site above) means "rows here can be dragged FROM, but nothing can be dropped
    // HERE" — no AllowDrop, no DragOver/Drop handlers wired at all.
    void AddGroup(string title, bool isCurrent, UIElement? header, IEnumerable<UIElement> rows, string? groupKey = null, Action<WindowHandle>? onDrop = null)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        panel.Children.Add(header ?? new TextBlock { Text = title, FontWeight = isCurrent ? FontWeights.Bold : FontWeights.SemiBold, Margin = new Thickness(4, 2, 4, 2) });
        rows.ToList().ForEach(r => panel.Children.Add(r));

        if (onDrop is not null)
        {
            var key = groupKey ?? "(unknown)";
            panel.AllowDrop = true;
            panel.DragOver += (_, e) =>
            {
                e.Effects = e.Data.GetDataPresent(DraggedWindow.DragFormat) ? DragDropEffects.Move : DragDropEffects.None;
                e.Handled = true;
                DnDTrace.LogTargetChange(key, e.Effects.ToString());
            };
            panel.Drop += (_, e) =>
            {
                DnDTrace.ResetTarget();
                if (e.Data.GetData(DraggedWindow.DragFormat) is not DraggedWindow dragged) { DnDTrace.Log($"drop on '{key}': no drag payload present"); return; }
                if (dragged.SourceGroupKey == groupKey) { DnDTrace.Log($"drop '{dragged.Handle}' on '{key}': no-op (own group)"); return; } // no-op: dropped onto its own group
                DnDTrace.Log($"drop '{dragged.Handle}': '{dragged.SourceGroupKey}' -> '{key}'");
                onDrop(dragged.Handle);
            };
        }

        GroupsHost.Children.Add(panel);
    }

    // Workspace headers are interactive: click = switch there; ▶ = start missing apps;
    // right-click = Add app… (moved off the removed ＋ button per spec — DnD replaces it
    // as the everyday way to populate a workspace). Bold marks the current workspace.
    UIElement WorkspaceHeader(WorkspaceGroup group)
    {
        var header = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };

        var start = new Button { Content = "▶", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(4, 0, 0, 0), ToolTip = $"Start {group.Workspace.Name}: launch its {group.NotRunning.Count} not-running app(s) and switch there", Visibility = group.NotRunning.Count > 0 ? Visibility.Visible : Visibility.Collapsed };
        start.Click += (_, _) => Report(manager.StartWorkspace(group.Workspace.Id, launcher)).Tap(() => afterAction?.Invoke());
        DockPanel.SetDock(start, Dock.Right);

        var name = new Button { Content = $"{group.Workspace.Name} ({group.Running.Count})", FontWeight = group.IsCurrent ? FontWeights.Bold : FontWeights.SemiBold, HorizontalContentAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(0), Background = Brushes.Transparent, ToolTip = "Switch to this workspace" };
        name.Click += (_, _) => Report(manager.Switch(group.Workspace.Id)).Tap(() => afterAction?.Invoke());

        header.Children.Add(start);
        header.Children.Add(name);

        var menu = new ContextMenu();
        var addApp = new MenuItem { Header = "Add app…" };
        addApp.Click += (_, _) => OnAddApp(group.Workspace.Id);
        menu.Items.Add(addApp);
        header.ContextMenu = menu;

        return header;
    }

    // --- rows ----------------------------------------------------------------------

    UIElement RunningRow(WindowRow row, bool pinned, string groupKey)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = IconCache.For(row.Window.ProcessPath);
        if (icon is not null) content.Children.Add(new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) });
        content.Children.Add(new TextBlock { Text = row.Window.Title, FontWeight = row.OriginalTitle.HasValue ? FontWeights.SemiBold : FontWeights.Normal });
        // Renamed window: short name prominent, original title dimmed beside it (spec).
        row.OriginalTitle.Tap(original => content.Children.Add(new TextBlock { Text = $"  ·  was: {original}", Opacity = 0.55, TextTrimming = TextTrimming.CharacterEllipsis }));

        // Active-window highlight, same fact the floating bar uses (Overview.WindowRow.IsActive)
        // so the two surfaces agree about which window has focus.
        var button = new Button { Content = content, HorizontalContentAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(0), Background = row.IsActive ? ActiveBackground : Brushes.Transparent, Padding = new Thickness(16, 2, 4, 2), ToolTip = row.Window.Title };
        button.Click += (_, _) => Report(manager.JumpTo(row.Window.Handle, activator)).Tap(() => afterAction?.Invoke());
        button.ContextMenu = RunningMenu(row, pinned);
        // Shared with FloatingBar's icons — see WindowDragSource.cs for the press/
        // threshold/capture logic (extracted from this file, unchanged).
        WindowDragSource.Attach(button, row.Window.Handle, groupKey, row.Window.Title);
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
    // and Petre can start several apps in a row (spec). Not draggable — there is no
    // window yet to drag.
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

    // Subtler than the bar's equivalent: these rows carry titles and icons on a full-width
    // surface, so a background tint alone reads clearly without an outline.
    //
    // FROZEN for the same reason FloatingBar freezes its brushes: an unfrozen Freezable takes
    // the thread affinity of whichever thread created it, and a `static` one is created once
    // per process, so assigning it from any other thread throws during Arrange.
    static readonly Brush ActiveBackground = Frozen();

    static Brush Frozen()
    {
        var brush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        brush.Freeze();
        return brush;
    }

    void OnAddApp(Guid workspaceId)
    {
        var picker = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe", Title = "Add app to workspace" };
        if (RunChildDialog(() => picker.ShowDialog()) != true) return;
        var arguments = RunChildDialog(() => PromptDialog.Ask("Arguments", "Optional command-line arguments (path+args identify WHAT the app shows):"))
            .GetValueOrDefault("");
        Report(manager.AddRosterEntry(workspaceId, picker.FileName, arguments).Map(_ => true));
    }

    // Adapts the host's non-generic runChildDialog (a delegate field can't be generic)
    // back to a typed call at each use site via box/unbox through object?.
    T RunChildDialog<T>(Func<T> show) => (T)runChildDialog(() => show())!;

    // Instance methods (not static, matching the pre-extraction panel): the failure
    // MessageBox is itself a child dialog and must go through RunChildDialog too, else a
    // reported error would defeat whatever guard the host's runChildDialog provides.
    Result Report(Result result) => result.TapError(err => RunChildDialog(() => MessageBox.Show(err, "TaskSpaces")));
    Result<T> Report<T>(Result<T> result) => result.TapError(err => RunChildDialog(() => MessageBox.Show(err, "TaskSpaces")));
}
