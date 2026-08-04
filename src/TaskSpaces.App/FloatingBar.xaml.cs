using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using TaskSpaces.Core;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Geometry;
using TaskSpaces.Core.Overview;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Windows.Activation;
using TaskSpaces.Windows.Monitoring;

namespace TaskSpaces.App;

// Task 11 (spec §Floating icon bar): a small always-on-top, borderless, translucent
// bar showing ONLY app icons, one compact row per group (📌 Pinned first when
// non-empty; then EVERY workspace; then unbound desktops that have windows -- fix
// round 6, Petre: "show tabs from all workspaces", and his windows largely live on the
// unbound "Main" desktop, so the original workspaces-only design showed him a single
// row). Click an icon -> JumpTo (switch workspace if needed, then
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

    // Task 11 fix round 3 (reviewer, Petre: "can't drag it"): the screen-coordinate
    // press point, set on PreviewMouseLeftButtonDown and cleared on release or once a
    // drag actually starts. Null means "no press in progress" -- same sentinel pattern
    // as WindowGroupsView.SetupDragSource's dragStart.
    Point? dragStart;

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

    // Petre: "i want it to be on top of the taskbar, if i activate the taskbar, it hides the
    // floating window. i'm using startallback start menu".
    //
    // Topmost is a BAND, not a rank. Every topmost window lives in the same one and the most
    // recently activated sits at its top -- and the taskbar is topmost, as is StartAllBack's
    // menu (and the plain Windows 11 taskbar: same Shell_TrayWnd, same band, so this behaves
    // identically either way). Activating one therefore climbs over the bar, and WPF's
    // Topmost="True" cannot prevent it; there is no "more topmost" to ask for. The supported
    // higher z-bands need uiAccess, which needs a signed exe installed to a secure location --
    // impossible for a portable unsigned build, and far too much to pay for this.
    //
    // So the bar simply reclaims the top of the band whenever the foreground changes. Driven
    // by WindowMonitor.ForegroundChanged rather than a poll, because we already hook
    // EVENT_SYSTEM_FOREGROUND and re-asserting exactly when something else was activated is
    // both the cheapest and the most precise moment to do it. The 5s sweep calls this too, as
    // a backstop for any activation that fires no foreground event.
    public void ReclaimTopmost()
    {
        if (!IsVisible) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero) return;
        // SWP_NOACTIVATE is the load-bearing flag: without it this would yank focus off
        // whatever was just clicked -- including the taskbar the user was reaching for.
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    public void HideBar()
    {
        Hide();
        manager.SaveFloatingBar(new FloatingBarState(Left, Top, false));
    }

    void OnHideClick(object sender, RoutedEventArgs e) => HideBar();

    // Task 11 fix round 3 (reviewer, Petre: "can't drag it"): the ORIGINAL design put
    // the drag handler on the Border alone, betting on it having bare background to
    // grab -- but the bar is nearly all icon Buttons with only 6px of Padding around
    // them, so there was almost no pixel left to press-and-drag from. Fixed by wiring
    // drag at the WINDOW level instead: these Preview* (tunnelling) handlers see every
    // press/move/release anywhere in the window BEFORE any icon Button's own (bubbling)
    // Click processing, including presses that start ON an icon -- "drag from
    // anywhere, clicks still work" (spec ask). This replaces the old
    // Border.MouseLeftButtonDown handler outright (removed) rather than keeping both:
    // the window-level mechanism already covers the Border's own bare-background case
    // too, so a second, narrower mechanism would be pure redundancy.
    //
    // Records the press point but does NOT set e.Handled -- a row LABEL button must still
    // arm normally on press (matches WindowDragSource's PreviewMouseLeftButtonDown, which
    // does the same for row-drag).
    //
    // ...EXCEPT on icons. Once icons became drag sources for MOVING WINDOWS between rows
    // (Petre: "i also want to be able to drag them around across tabs"), one left-drag
    // gesture could no longer mean both "move the bar" and "move this window" -- so the
    // gesture is split by WHERE it starts: on an icon it drags the window; anywhere else
    // (row labels, the padding around them, the separators, the info line) it still drags
    // the bar, which is what made round 3's "drag from anywhere" fix work in the first
    // place. Ignoring the press here is what keeps OnPreviewMouseMove below from starting
    // a bar-move that would fight the icon's own DoDragDrop for the same mouse.
    void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        dragStart = StartedOnIcon(e.OriginalSource) ? null : PointToScreen(e.GetPosition(this));

    // Walks up from whatever element the press actually hit (an Image inside a Button
    // template, usually) looking for one of our tagged icon buttons. VisualTreeHelper
    // rather than the logical tree: the press lands on template-generated visuals, which
    // the logical tree does not connect to the Button.
    static bool StartedOnIcon(object source)
    {
        for (var node = source as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is FrameworkElement { Tag: IconTag }) return true;
        return false;
    }

    // Marks an icon Button for StartedOnIcon above. A private const string compared by
    // value (pattern-matched, so a null Tag can never match).
    const string IconTag = "icon";

    // Clears a finished press so a later, unrelated move never measures distance from
    // a stale point -- same hardening WindowGroupsView.SetupDragSource applies to its
    // own dragStart (pitfall #2 in that file's comments).
    void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => dragStart = null;

    void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || dragStart is not { } start) return;
        var current = PointToScreen(e.GetPosition(this));
        if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        dragStart = null;

        // If the press landed on an icon Button, ButtonBase.OnMouseLeftButtonDown
        // already called CaptureMouse() on it (same mechanism WindowGroupsView.
        // SetupDragSource documents at length). Releasing that capture before
        // DragMove() lets its own native move-loop take clean, uncontested control of
        // the mouse, and -- just as importantly -- means that Button never receives
        // the MouseLeftButtonUp DragMove()'s loop consumes, so it never raises Click
        // for this press. That IS the desired split: press-and-drag moves the bar;
        // press-and-release-in-place still reaches the icon's Click normally.
        Mouse.Capture(null);
        DragMove(); // blocks until the mouse button is released
        manager.SaveFloatingBar(new FloatingBarState(Left, Top, true)); // only draggable while shown
    }

    // Petre's screenshot: EVERY row rendered twice (GEPHA / Sparrow / Main / Unplaced,
    // then all four again). RebuildCore below clears Rows.Children at its top and adds the
    // new rows at its bottom, and four separate facts conspire to make that gap re-entrant:
    //
    //   1. WindowMonitor hooks with WINEVENT_OUTOFCONTEXT, so its callbacks arrive on THIS
    //      thread while messages are being pumped.
    //   2. WorkspaceManager.stateChanged is a plain Rx Subject: OnNext runs subscribers
    //      synchronously, inline, on the caller's thread.
    //   3. The subscription above uses Dispatcher.Invoke, which does NOT queue when the
    //      caller is already on the dispatcher thread -- it runs the delegate immediately.
    //   4. manager.WindowsByWorkspace() makes virtual-desktop COM calls, and COM calls on
    //      an STA thread PUMP THE MESSAGE QUEUE.
    //
    // So a window appearing or vanishing during the query re-entered Rebuild: the nested
    // call cleared an already-empty panel, added its rows and returned, after which the
    // outer call appended its own rows underneath. Doubled until the next clean pulse
    // repaired it -- which is why it looked transient and healed itself.
    //
    // Pinned by FloatingBarRebuildTests in TaskSpaces.Windows.Tests.
    bool rebuilding;
    bool rebuildRequested;

    void Rebuild()
    {
        // A pulse that arrives mid-rebuild is remembered, not executed: letting it run now
        // is precisely the doubling bug.
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

        // The suppressed pulse still carried real news (a window opened, closed or moved),
        // so serve it -- but via BeginInvoke rather than a synchronous loop. Queuing hands
        // the message pump a turn between passes, so a title-rewriting browser cannot spin
        // the UI thread in back-to-back COM-heavy rebuilds.
        if (!rebuildRequested) return;
        rebuildRequested = false;
        Dispatcher.BeginInvoke(new Action(() => { if (IsVisible) Rebuild(); }));
    }

    void RebuildCore()
    {
        Rows.Children.Clear();
        ClearInfo();
        manager.WindowsByWorkspace().Tap(overview =>
        {
            // Task 11 fix round 5 (Petre: "the rows are indistinguishable... i want to
            // tell which workspace i'm going to"): build the rows into a list first
            // (rather than adding straight to Rows.Children) so a hairline Separator
            // can be interleaved BETWEEN them below -- none before the first row, none
            // trailing after the last.
            var groupRows = new List<UIElement>();

            // Task 12: rendered UNCONDITIONALLY, empty or not. Dropping a window here is
            // the only way to pin one from the bar, so the old `Pinned.Count > 0` guard
            // was self-defeating: with nothing pinned there was no row, with no row there
            // was no drop target, and so the first window could never be pinned. Exactly
            // the trap fix round 6 removed for empty workspaces, whose labels are likewise
            // kept as legitimate targets rather than hidden as dead chrome.
            //
            // Visual label is just "📌" (brief) but the icon tooltips below still
            // say the full word "Pinned" -- a glyph reads fine as a compact row
            // label, but "Pinned · window title" is a nicer tooltip than "📌 ·
            // window title".
            groupRows.Add(GroupRow(visualLabel: "📌", groupLabel: "Pinned", isCurrent: false, switchTo: null,
                    groupKey: DraggedWindow.PinnedGroupKey,
                    onDrop: h => Report(manager.PinWindow(h)),
                    overview.Pinned));

            // Fix round 6 (Petre, screenshot showing ONE "Sparrow" row: "it does follow
            // across every workspace, but not showw all workspace tabs"). The original
            // design ("unbound desktops excluded -- it is a workspace bar", empty
            // workspaces skipped) collapsed to a single row on his machine because most
            // of his windows live on the unbound "Main" desktop. Superseded, spec
            // amended: EVERY workspace gets a row -- an empty one is just its label,
            // which since round 5 is a click-to-switch button, so it's a legitimate
            // switch target rather than dead chrome.
            overview.Workspaces
                .ToList()
                // Index carries the workspace's position so WorkspacePalette can colour by
                // ORDER: renaming a workspace must not recolour it, and reordering should move
                // its colour with it.
                .ForEach((g) => groupRows.Add(GroupRow(g.Workspace.Name, g.Workspace.Name, g.IsCurrent,
                    switchTo: () => manager.Switch(g.Workspace.Id),
                    groupKey: DraggedWindow.WorkspaceGroupKey(g.Workspace.Id),
                    onDrop: h => Report(manager.AssignWindow(h, g.Workspace.Id)),
                    g.Running,
                    tint: LaneTint(g.Workspace, overview.Workspaces.ToList().FindIndex(w => w.Workspace.Id == g.Workspace.Id)))));

            // ...and unbound desktops with windows (OverviewBuilder already drops empty
            // ones) get rows too, labeled with the desktop's actual name; label click
            // switches to that raw desktop. The "Unplaced" catch-all (DesktopId ==
            // Guid.Empty, windows whose desktop the COM API can't resolve) is not a
            // real desktop -- no switch target exists, so its label stays plain text.
            overview.OtherDesktops
                // "Unplaced" (Guid.Empty: windows whose desktop the COM API refuses to
                // resolve) is rendered here again, REVERSING the Task 12 decision to hide it.
                //
                // That decision was justified as "bar = actionable, panel = complete": the row
                // is not a switch or drop target, so it was noise on a permanently-visible
                // surface, and the switcher panel would still show it. Both of those surfaces
                // have since been deleted at Petre's request, so the premise is gone. Leaving
                // the filter in place would mean a window the API loses track of appears in NO
                // surface at all -- exactly the Task 10 defect Petre originally reported ("i
                // don't think i see windows in the non-workspace section"), reintroduced by a
                // ruling whose reasoning no longer holds.
                //
                // A row that can only be looked at beats a window that cannot be found. Its
                // label stays non-clickable and it is still not a drop target (see below).
                .ToList()
                .ForEach(g => groupRows.Add(GroupRow(g.Name, g.Name, g.IsCurrent,
                    // Guid.Empty == the "Unplaced" catch-all: not a real desktop, so
                    // neither a switch destination nor a drop target (same rule as the
                    // switcher panel's grouped view).
                    switchTo: g.DesktopId == Guid.Empty ? null : () => manager.SwitchToDesktop(g.DesktopId),
                    groupKey: DraggedWindow.DesktopGroupKey(g.DesktopId),
                    onDrop: g.DesktopId == Guid.Empty ? null : h => Report(manager.MoveToDesktop(h, g.DesktopId)),
                    g.Windows)));

            groupRows
                .SelectMany((row, i) => i == 0 ? new[] { row } : new[] { Separator(), row })
                .ToList()
                .ForEach(el => Rows.Children.Add(el));
        });
        // Overview query failure (e.g. a transient desktop-enumeration hiccup) just
        // leaves whatever the bar last showed -- there's no text area on this surface to
        // report an error into, and the next StateChanged pulse retries for free.
    }

    // Task 11 fix round 5: a 1px, ~20%-opacity hairline between rows so adjacent
    // workspace groups read as visually distinct at a glance, without adding real
    // borders/backgrounds that would compete with the icons themselves.
    static UIElement Separator() => new Border
    {
        Height = 1,
        Margin = new Thickness(0, 3, 0, 3),
        Background = Brushes.White,
        Opacity = 0.2,
    };

    // groupLabel is the group's full human name ("Pinned", "GEPHA", "Main") — used in the
    // hover info line and as the drop-target readout, where the visualLabel "📌" would be
    // too terse. groupKey/onDrop mirror WindowGroupsView.AddGroup: a null onDrop means
    // "rows here drag FROM this group, but nothing can be dropped ONTO it" (the Unplaced
    // catch-all).
    // tint (Petre: "i also want different colors for different workspaces in the lanes") is the
    // lane's own colour, or null for the rows that are not workspaces -- pinned, unbound
    // desktops, Unplaced -- which stay neutral so a coloured lane always means "a workspace".
    UIElement GroupRow(string visualLabel, string groupLabel, bool isCurrent, Func<Result>? switchTo, string groupKey, Action<WindowHandle>? onDrop, IEnumerable<WindowRow> rows, Brush? tint = null)
    {
        // Background MUST be non-null for a panel to take part in hit testing at all --
        // a null Background leaves gaps between icons that swallow nothing and report no
        // DragOver, making drops land unpredictably. Transparent is the standard fix.
        //
        // `idle` rather than a literal Transparent everywhere below: the drag highlight
        // replaces this Background and has to put the LANE COLOUR back on leave, not
        // transparent, or dragging over a workspace would permanently strip its tint.
        var idle = tint ?? Brushes.Transparent;
        var container = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Background = idle };
        container.Children.Add(RowLabel(visualLabel, isCurrent, switchTo));

        var icons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        rows.ToList().ForEach(r => icons.Children.Add(IconButton(groupLabel, groupKey, r)));
        container.Children.Add(icons);

        if (onDrop is not null)
        {
            container.AllowDrop = true;
            container.DragOver += (_, e) =>
            {
                var accepted = e.Data.GetDataPresent(DraggedWindow.DragFormat);
                e.Effects = accepted ? DragDropEffects.Move : DragDropEffects.None;
                e.Handled = true;
                if (!accepted) return;
                // The reserved info line doubles as the drop-target readout: on a bar this
                // small, "where will this land?" is otherwise pure guesswork -- rows are
                // ~28px tall and adjacent.
                container.Background = DropHighlight;
                Info.Text = $"→ move to {groupLabel}";
                DnDTrace.LogTargetChange(groupKey, e.Effects.ToString());
            };
            container.DragLeave += (_, _) => { container.Background = idle; ClearInfo(); };
            container.Drop += (_, e) =>
            {
                container.Background = idle;
                ClearInfo();
                DnDTrace.ResetTarget();
                if (e.Data.GetData(DraggedWindow.DragFormat) is not DraggedWindow dragged) { DnDTrace.Log($"bar drop on '{groupKey}': no drag payload present"); return; }
                if (dragged.SourceGroupKey == groupKey) { DnDTrace.Log($"bar drop '{dragged.Handle}' on '{groupKey}': no-op (own group)"); return; }
                DnDTrace.Log($"bar drop '{dragged.Handle}': '{dragged.SourceGroupKey}' -> '{groupKey}'");
                onDrop(dragged.Handle);
            };
        }

        return container;
    }

    // ~20% white: enough to read as "this row is armed" against the bar's #99202020
    // background without washing the icons out mid-drag.
    static readonly Brush DropHighlight = Frozen(0x33, 0xFF, 0xFF, 0xFF);

    // Active-window highlight. Kept dimmer than DropHighlight above and paired with a
    // brighter outline: the drop highlight is a transient answer to "where will this land",
    // while this one is always on screen somewhere, so it has to read as a marker rather
    // than compete with the icons themselves.
    static readonly Brush ActiveBackground = Frozen(0x22, 0xFF, 0xFF, 0xFF);
    static readonly Brush ActiveBorder = Frozen(0x99, 0xFF, 0xFF, 0xFF);

    // Every shared brush here is FROZEN. A Freezable that is still mutable takes on the
    // thread affinity of whoever created it, and these are `static` — created once, on
    // whichever thread happens to touch this class first — so an unfrozen brush assigned to
    // a control on any other thread throws "Cannot use a DependencyObject that belongs to a
    // different thread than its parent Freezable" during Arrange. That surfaced as
    // order-dependent test failures (each bar test passing alone, failing in a suite, since
    // StaThread gives every test its own STA thread), but the same hazard applies in the app
    // to any future surface built off the UI thread. Freezing also lets WPF skip
    // change-tracking on a value that never changes. Same reasoning as IconCache's frozen
    // bitmaps.
    // A workspace's lane colour, heavily diluted. WorkspacePalette gives an opaque "#RRGGBB";
    // painted at full strength behind app icons on a translucent bar it would drown them, so the
    // alpha is dropped to ~0x38. The result still separates lanes at a glance, which is the
    // request, without competing with the icons or with the active-window highlight.
    //
    // Frozen for the same thread-affinity reason every other brush here is (see Frozen below),
    // and cached per colour because Rebuild runs on every window event: an unbounded number of
    // new brushes per rebuild would be wasteful, and the set of workspace colours is tiny.
    static readonly Dictionary<string, Brush> LaneTints = [];

    static Brush? LaneTint(Workspace workspace, int index)
    {
        var hex = WorkspacePalette.For(workspace, index < 0 ? 0 : index);
        if (LaneTints.TryGetValue(hex, out var cached)) return cached;
        try
        {
            var solid = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(Color.FromArgb(0x38, solid.R, solid.G, solid.B));
            brush.Freeze();
            LaneTints[hex] = brush;
            return brush;
        }
        catch (FormatException)
        {
            // A hand-edited state.json can hold anything. An unreadable colour means "no tint",
            // never a crash on every rebuild.
            return null;
        }
    }

    static Brush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    // --- hover info line ------------------------------------------------------------

    // Petre: "add a small panel, when i hover over any icon, i want to see what it is."
    // Shows the window's full title plus, dimmed, its process and which group it is in --
    // enough to answer "what IS that icon", which is exactly how the mystery "Unplaced"
    // browser window got noticed in the first place. Renamed windows also show the
    // original title, since our own short name is precisely what makes a window hard to
    // identify from the icon alone.
    void ShowInfo(string groupLabel, WindowRow row)
    {
        Info.Inlines.Clear();
        Info.Inlines.Add(new Run(row.Window.Title));
        var detail = row.OriginalTitle.HasValue
            ? $"  —  {row.Window.ProcessName} · {groupLabel} · was: {row.OriginalTitle.Value}"
            : $"  —  {row.Window.ProcessName} · {groupLabel}";
        Info.Inlines.Add(new Run(detail) { Foreground = DimForeground });
    }

    // The idle state: a hint, not blank. It names both gestures the bar answers to --
    // hovering for identification and dragging icons between rows -- because neither is
    // discoverable from an icon-only surface, and it costs a line that is reserved anyway.
    void ClearInfo()
    {
        Info.Inlines.Clear();
        Info.Inlines.Add(new Run("hover an icon · drag icons between rows · drag labels to move") { Foreground = DimForeground });
    }

    static readonly Brush DimForeground = Frozen(0x8C, 0xFF, 0xFF, 0xFF);

    // Task 11 fix round 5 (Petre: "separated nicely, so i can tell which workspace i'm
    // going to"): tiny label to the LEFT of each row's icons, vertically centered so the
    // row's height stays governed by the 20px icons, not the label.
    //
    // Petre: "i want to make the active workspace bold, its text". It was ALREADY SemiBold,
    // which is precisely why the ask exists: at 10px behind a flat 0.55 opacity, half a step
    // of weight is invisible. So both dials move together -- full Bold, and the current row
    // is the one thing on this surface drawn at near-full strength. Weight cannot read as
    // emphasis until there is enough ink for the eye to compare.
    //
    // Everything else stays dim on purpose; this is a glance-only surface, not a reading
    // surface, and the point of the contrast is that exactly one row wins it.
    UIElement RowLabel(string text, bool isCurrent, Func<Result>? switchTo)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            FontSize = 10,
            Opacity = isCurrent ? 0.95 : 0.5,
            FontWeight = isCurrent ? FontWeights.Bold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 6, 0),
        };

        if (switchTo is null)
            // No destination -> no click target. Two callers pass null deliberately:
            // the 📌 Pinned row (pinned windows are, by definition, already on every
            // workspace, so there's no single place a click could go) and the
            // "Unplaced" catch-all (Guid.Empty is not a real desktop). A Button here
            // would be dead chrome pretending to do something.
            return textBlock;

        // Brief: "if it's trivial to make it switch to the workspace via
        // manager.Switch, DO make it switch -- that's an obvious affordance." A label
        // that reads as "this is workspace/desktop X" invites a click to go there --
        // so unlike the icon buttons (transparent, no visible chrome) this one keeps
        // the same borderless/transparent styling for visual consistency but wires
        // Click straight to the caller's switch action (manager.Switch for workspace
        // rows, manager.SwitchToDesktop for unbound-desktop rows -- fix round 6).
        var button = new Button
        {
            Content = textBlock,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = $"Switch to {text}",
        };
        button.Click += (_, _) => Report(switchTo());
        return button;
    }

    UIElement IconButton(string groupLabel, string groupKey, WindowRow row)
    {
        var button = new Button
        {
            Padding = new Thickness(2),
            Margin = new Thickness(2),
            // Petre: "active window should be highlighted in the floating window". On an
            // icon-only surface with three identical VS Code glyphs, "which one am I in" is
            // otherwise unanswerable. BorderThickness stays 1 for EVERY icon with a
            // transparent brush when inactive, so gaining the highlight cannot nudge the
            // row's layout (and thus the whole SizeToContent bar's position) by 2px.
            Background = row.IsActive ? ActiveBackground : Brushes.Transparent,
            BorderBrush = row.IsActive ? ActiveBorder : Brushes.Transparent,
            BorderThickness = new Thickness(1),
            ToolTip = $"{groupLabel} · {row.Window.Title}",
            Tag = IconTag, // marks this as an icon: press-drag moves the WINDOW, not the bar
        };

        // Hover -> identify (the info line above). MouseEnter/Leave rather than the
        // ToolTip alone: the tooltip stays (harmless, and it survives a hover that starts
        // outside the window), but it needs a dwell delay, disappears on its own timer,
        // and lives in a separate HWND -- see the Info panel comment in FloatingBar.xaml.
        button.MouseEnter += (_, _) => ShowInfo(groupLabel, row);
        button.MouseLeave += (_, _) => ClearInfo();
        // The HANDLE, not just the path: IconCache asks the window itself (WM_GETICON)
        // before falling back to extracting from the exe. Petre: "i also don't see an icon
        // for whatsapp app" -- WhatsApp.Root.exe is an MSIX launcher stub carrying no icon,
        // so the exe-only lookup returned Windows' generic default and there was nothing to
        // detect as a failure. Asking the window gets the icon the taskbar itself draws.
        var icon = IconCache.For(row.Window.Handle, row.Window.ProcessPath);
        // Petre: "something popped up in unplaced, then disappeared, but now i see the
        // unplaced section" -- with the row looking empty. It was NOT empty. OverviewBuilder
        // only emits a group that has at least one window, so there was a window there; the
        // bar just drew it as a Button with no Content at all, which on an icon-only surface
        // is about 4px of padding and nothing else. Invisible, and therefore unreachable.
        //
        // So: never render a window as nothing. A window whose icon cannot be resolved gets
        // a lettered placeholder, which is hoverable (the info line then says what it is),
        // clickable and draggable exactly like a real icon.
        //
        // This used to be commonplace and silent: the old icon lookup needed a readable exe
        // path, which is null for every elevated process, so those windows were ALWAYS
        // invisible here. Asking the window itself (see IconCache) fixes most of them
        // outright -- WM_GETICON works whether or not we can read the file -- and this
        // placeholder covers whatever is left.
        button.Content = icon is not null
            ? new Image { Source = icon, Width = 20, Height = 20 }
            : Placeholder(row.Window);
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

        // Petre: "i also want to be able to drag them around across tabs" -- the same drag
        // source the switcher panel's rows use, so an icon dragged onto another row lands
        // through the identical AssignWindow/PinWindow/MoveToDesktop path. Sharing the
        // payload FORMAT with WindowGroupsView also means a drag started on the bar can be
        // dropped on the switcher panel (and vice versa) if both happen to be open.
        // onDragStarting clears the info line: the icon under the cursor never raises
        // MouseLeave once the modal drag loop owns the mouse, so nothing else would.
        WindowDragSource.Attach(button, row.Window.Handle, groupKey, row.Window.Title, onDragStarting: ClearInfo);
        button.ContextMenu = IconMenu(row);
        return button;
    }

    // Stand-in for a window we could not get an icon for. Same 20x20 footprint as a real
    // icon so a row's height and the bar's overall size do not depend on whether a lookup
    // succeeded. The letter is the first character of the process name (falling back to the
    // title, then to "?"), which is usually enough to recognise it at a glance -- and if it
    // is not, hovering names it in full.
    static UIElement Placeholder(WindowInfo window) => new Border
    {
        Width = 20,
        Height = 20,
        CornerRadius = new CornerRadius(3),
        Background = PlaceholderBackground,
        Child = new TextBlock
        {
            Text = FirstLetter(window),
            Foreground = Brushes.White,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    static string FirstLetter(WindowInfo window) =>
        new[] { window.ProcessName, window.Title }
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text.Trim()[..1].ToUpperInvariant())
            .FirstOrDefault() ?? "?";

    static readonly Brush PlaceholderBackground = Frozen(0x55, 0xFF, 0xFF, 0xFF);

    // Task 12 (Petre: "right clicking on the icon should give me option to customize that
    // one - tab rename"). Exactly two entries, and the omissions are the point: Petre was
    // offered "Send to ▸ workspace" and "Pin / Unpin" here and rejected both -- "i can drag
    // and drop, no need for this" and "another drag to the pinned section". The rule that
    // settles it, and that future surfaces should follow:
    //
    //     drag expresses movement and pinning; right-click expresses naming.
    //
    // Naming is the ONE operation a drag cannot express, which is exactly why it earns a
    // menu. Same "a second, narrower mechanism would be pure redundancy" reasoning that
    // deleted the duplicate bar-drag handler in fix round 3. (Unpinning stays reachable by
    // dragging an icon OUT of the 📌 row onto any workspace or desktop row -- AssignWindow
    // and MoveToDesktop both unpin first.)
    //
    // Wording and semantics deliberately mirror WindowGroupsView.RunningMenu, including
    // greying "Restore title" out rather than hiding it: the bar is an icon-only surface
    // with nothing else to advertise the feature, and a menu whose shape changes per icon
    // is harder to learn than one whose unavailable entry is visibly unavailable.
    //
    // The bar's own background ContextMenu ("Hide floating bar", in XAML) is unaffected:
    // ContextMenuService opens the menu of the INNERMOST element that has one, so an icon
    // gets this menu and bare bar still gets Hide.
    ContextMenu IconMenu(WindowRow row)
    {
        var menu = new ContextMenu();

        var rename = new MenuItem { Header = "Rename this window…" };
        rename.Click += (_, _) => PromptDialog.Ask("Rename window", "Short name to show on the taskbar:", row.Window.Title, owner: this)
            .Tap(shortName => Report(manager.RenameWindow(row.Window.Handle, shortName)));
        menu.Items.Add(rename);

        // Petre: "i've renamed remote desktop manager to RDP yesterday, today it's still the
        // original name, why?" Because renaming THIS WINDOW records the exact title it had at
        // the time, and RDM rewrites its title with the current session -- so the record could
        // never match again. Renaming the APP is keyed on the process name instead, which is
        // the one thing about a window that cannot change while it runs.
        //
        // Named after the actual process so the difference between the two entries is visible
        // rather than something to be inferred from wording: "Rename this window…" versus
        // "Rename all RemoteDesktopManager windows…".
        var renameApp = new MenuItem { Header = $"Rename all {row.Window.ProcessName} windows…" };
        renameApp.Click += (_, _) => PromptDialog.Ask(
                $"Rename every {row.Window.ProcessName} window",
                "Short name to show on the taskbar (survives the app changing its own title):",
                row.Window.ProcessName, owner: this)
            .Tap(shortName => Report(manager.RenameApp(row.Window.Handle, shortName)));
        menu.Items.Add(renameApp);

        var restore = new MenuItem { Header = "Restore title", IsEnabled = row.OriginalTitle.HasValue };
        restore.Click += (_, _) => Report(manager.RestoreTitle(row.Window.Handle));
        menu.Items.Add(restore);

        return menu;
    }

    // Restores Left/Top from persisted state (or computes the bottom-right work-area
    // default when never configured) and clamps into the nearest monitor's work area.
    // Called after Show() (like SwitcherPanel.Peek does for PositionNear) so
    // ActualWidth/ActualHeight already reflect the SizeToContent layout pass -- but see
    // the GetDpiForMonitor comment below for why that ordering alone is NOT enough.
    //
    // Task 11 fix round 2 (reviewer): root-caused Petre's invisible bar. State.json had
    // FloatingBar = { Left: 2408, Top: 1396, Visible: true } -- his monitor is 2560x1440
    // at 125% scaling, whose real DIP-space work area is only ~2048x1152, so that
    // position sat ~360 DIPs past the right/bottom edge. This was the FIRST-EVER show
    // (brand new feature, no prior drag), so it came from the DEFAULT branch below:
    // workRight/workBottom computed with a DPI scale of 1.0 instead of 1.25 --
    // VisualTreeHelper.GetDpi(this), queried immediately after Show() returns, can
    // still report the window's stale/provisional per-monitor-DPI-context (scale 1.0)
    // before its WM_DPICHANGED round-trip has actually landed on the dispatcher, even
    // though Show() already ran. 2560/1.0 - ActualWidth and 1440/1.0 - ActualHeight
    // land almost exactly on the reported (2408, 1396) -- the raw physical rcWork was
    // written straight into DIP-valued Left/Top, unconverted. Reordering Show() before
    // positioning (already true here) does NOT fix this, because the race is in
    // GetDpi's window-scoped negotiation state, not in call order.
    //
    // Fix: query the MONITOR's own DPI directly (GetDpiForMonitor, Shcore.dll) using
    // the SAME HMONITOR already returned by MonitorFromPoint below, instead of asking
    // the window. A monitor-scoped query has no per-window negotiation state to race.
    void PositionFromState()
    {
        var stored = manager.State.FloatingBar;
        // Task 11 fix round 2 (reviewer, restore-path safety): the saved position is
        // run through MonitorFromPoint + the clamp on EVERY show, stored or not, so a
        // stale/bad save (like Petre's) self-heals here without editing state.json --
        // MONITOR_DEFAULTTONEAREST always returns a real, valid monitor no matter how
        // far outside every monitor's bounds the probe point falls.
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

        NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out var dpiY);
        var scaleX = dpiX / 96.0;
        var scaleY = dpiY / 96.0;
        var workLeft = info.rcWork.Left / scaleX;
        var workTop = info.rcWork.Top / scaleY;
        var workRight = info.rcWork.Right / scaleX;
        var workBottom = info.rcWork.Bottom / scaleY;

        // No persisted state at all (first run, or an old state.json): default to the
        // bottom-right corner of the work area minus the bar's own size (brief). This
        // default path gets the exact same clamp treatment as the restore path below --
        // one shared call, both branches feed into it.
        var (rawLeft, rawTop) = stored is { } s
            ? (s.Left, s.Top)
            : (workRight - ActualWidth, workBottom - ActualHeight);

        (Left, Top) = WorkAreaClamp.Clamp(rawLeft, rawTop, ActualWidth, ActualHeight, workLeft, workTop, workRight, workBottom);
    }

    // Owned by the bar for the same reason PromptDialog.Ask now takes an owner: this window
    // is Topmost, so an unowned message box can open behind it and strand the user with an
    // invisible modal.
    Result Report(Result result) => result.TapError(err => MessageBox.Show(this, err, "TaskSpaces"));
}
