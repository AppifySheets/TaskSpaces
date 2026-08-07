using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
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

        // Petre: "my window has gotten quite too large... shrink by twenty percent."
        //
        // LayoutTransform, NOT RenderTransform, and that is the whole trick. A RenderTransform
        // scales the pixels but leaves the element's measured size alone, so this window --
        // which is SizeToContent -- would draw a smaller bar inside a full-size window, with a
        // margin of dead translucent space around it. LayoutTransform participates in measure,
        // so the window itself actually shrinks.
        //
        // Applied once here rather than on every rebuild: a live change would have to re-run
        // edge snapping and the work-area clamp mid-flight, which is a lot of machinery for a
        // value that gets set once. Editing it in state.json takes effect on the next start.
        var scale = BarScaling.Clamp(manager.State.BarScale);
        if (Math.Abs(scale - 1.0) > 0.001) Root.LayoutTransform = new ScaleTransform(scale, scale);

        Rebuild();
        // Live-refresh while visible, same pattern as WindowGroupsView.Bind: windows
        // opening/closing (manual script item 36) must update the bar without Petre
        // having to toggle it off and on.
        subscription = manager.StateChanged.Subscribe(_ => Dispatcher.Invoke(() => { if (IsVisible) Rebuild(); }));

        // Petre: "Same as before, edge icon shows up instead of [the] music icon, it never
        // changes. when you restarted the app, it seems that it picked up the correct
        // YouTube music icon."
        //
        // The reason it never changed, and the false premise two earlier fixes rested on:
        // THE BAR HAS NO PERIODIC REBUILD. It rebuilds on window EVENTS only -- the 5s sweep
        // calls Resync (which emits events just for drift) and ReapplyRenames (which pulses
        // nothing at all). IconCache's own comment claimed rebuilds happen "at least every
        // 5s"; they do not.
        //
        // A loading PWA therefore got exactly ONE probe -- the rebuild caused by its own
        // Appeared event, when its icon is still blank -- and on a quiet machine nothing ever
        // asked again, so the browser placeholder stayed for the life of the window.
        //
        // This is that missing clock, and it is deliberately self-stopping: it runs only while
        // some window is still without an icon of its own, which is a few seconds after a PWA
        // launches and never otherwise. Idle cost is one bool per tick, then it turns itself
        // off. Driving it from the existing 1s topmost timer was the alternative and was
        // rejected: that one runs forever and this concern has a natural end.
        iconWatch.Tick += (_, _) =>
        {
            if (IconCache.HasPendingIcons && IsVisible) Rebuild();
            else iconWatch.Stop();
        };
        Closed += (_, _) => { iconWatch.Stop(); subscription?.Dispose(); };
    }

    // 1s, matching IconCache's own probe interval: ticking faster would just be throttled
    // there and rebuild the bar for nothing.
    readonly System.Windows.Threading.DispatcherTimer iconWatch =
        new() { Interval = TimeSpan.FromSeconds(1) };

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
        Save();
    }

    // Petre: "i want it to be on top of the taskbar, if i activate the taskbar, it hides the
    // floating window. i'm using startallback start menu".
    //
    // Topmost is a BAND, not a rank. Every topmost window lives in the same one and the most
    // recently activated sits at its top -- and the taskbar is topmost, as is StartAllBack's
    // menu (and the plain Windows 11 taskbar: same Shell_TrayWnd, same band, so this behaves
    // identically either way). Activating one therefore climbs over the bar, and WPF's
    // Topmost="True" cannot prevent it; there is no "more topmost" to ask for.
    //
    // The START MENU turns out to be in that same band too, which I had claimed it was not.
    // I told Petre twice that covering it was impossible without uiAccess (a higher z-band,
    // needing a signed exe in a protected directory) and wrote that into a PR as a known
    // limitation. Then the 1s timer below shipped and he reported "start menu is now covered".
    // The band theory was wrong: nothing was stopping us, we simply never re-asserted after the
    // menu opened. Recorded here because the false version was more plausible than the truth,
    // and the next person to hit a z-order problem should not go looking for uiAccess.
    //
    // So the bar reclaims the top of the band, on two triggers.
    //
    // WindowMonitor.ForegroundChanged is the fast one: we already hook
    // EVENT_SYSTEM_FOREGROUND, and the instant something else is activated is the most precise
    // moment to re-assert. But it is not sufficient, and Petre found exactly how: "taskbar
    // makes its way over the floating window if i click the taskbar twice". The SECOND click
    // does not change the foreground window -- the taskbar already had it -- so no event fires
    // at all, while the shell still re-raises the taskbar inside the band.
    //
    // Hence a 1s timer as well (his suggestion). The event-driven alternative would be
    // EVENT_OBJECT_REORDER, and it is worse: it fires constantly on a busy desktop, and our own
    // SetWindowPos changes z-order, so we would be feeding our own hook. One SetWindowPos per
    // second is both cheaper and impossible to make loop.
    public void ReclaimTopmost()
    {
        // Not while the user is dragging the bar: DragMove's native loop owns the window's
        // position, and a SetWindowPos arriving mid-drag fights it.
        if (!IsVisible || moving) return;
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
        manager.SaveFloatingBar(new FloatingBarState(Left, Top, false) { Right = anchorRight });
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
    // Petre, twice: "clicking another workspace... doesn't take me there", then after the first
    // round of fixes, "it's better than before, but it still doesn't work sometimes."
    //
    // The first round treated symptoms -- it raised the drag threshold so ordinary jitter stopped
    // crossing it. That made the failure rarer without removing it, because the real problem is
    // structural: a row was BOTH a click target and a handle for dragging the bar, so every press
    // on one was a gesture the bar had to guess the meaning of. Any guess has a wrong case, and
    // the wrong case here silently eats the click -- DragMove's native loop swallows the mouse-up,
    // so no Click is ever raised.
    //
    // So the guessing is gone. A press inside the ROWS is only ever a click; the bar is dragged
    // from the surfaces that are not click targets -- the frame, the separator band above the
    // info line, and the info line itself, which spans most of the width. That is a real loss
    // ("drag labels to move" was a documented gesture) and it is worth it: switching workspaces
    // is the thing this bar exists for, and it now cannot fail.
    //
    // The icon exclusion below predates all this and stays for a different reason: a press on an
    // icon drags the WINDOW between workspaces.
    // Petre: "can't drag it", then "maybe make draggable with ctrl+drag".
    //
    // His answer is better than the two this went through. Excluding rows made clicks reliable
    // and left the bar hard to grab; a bigger threshold made both merely probable. A MODIFIER
    // settles it outright: the two gestures stop overlapping, so neither has to be guessed at
    // from movement. Ctrl+drag moves the bar from anywhere at all -- rows, labels, icons -- and a
    // press without Ctrl can only ever be what the thing under it does.
    //
    // Plain drag still works on the surfaces that are not click targets, so the discoverable way
    // to move the bar (grab its edge, or the info line that says so) survives alongside the
    // deliberate one.
    void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // A new press: nothing has claimed it yet, and no row owns it until the row's own
        // tunnelling handler (wired in GroupRow) runs a moment later, further down this route.
        pressConsumedByChild = false;
        pressedRow = null;

        dragStart = Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
                    || !(StartedOnIcon(e.OriginalSource) || StartedOnClickTarget(e.OriginalSource))
            ? PointToScreen(e.GetPosition(this))
            : null;
    }

    // Petre, a third time: "i still sometimes click on a workspace and it doesn't switch."
    //
    // MEASURED, not reasoned about -- a throwaway WPF app driven by real SendInput, reproducing
    // one row (a Grid with a bubbling mouse-up handler, holding chrome-less Buttons). Four
    // gestures out of seven did NOTHING AT ALL: no Button Click, and no row switch either.
    //
    //   press blank row -> release over an icon      dead
    //   press an icon   -> release over blank row    dead
    //   press the label -> release over blank row    dead
    //   press blank row -> release over the label    dead
    //
    // Two facts combine, and neither is visible from the row's own code:
    //
    //   1. ButtonBase.OnMouseLeftButtonUp sets e.Handled = true UNCONDITIONALLY (for any
    //      ClickMode but Hover) -- including the case where it decides NOT to raise Click,
    //      which is every release whose press began somewhere else. It consumes the event and
    //      gives nothing back.
    //   2. MouseLeftButtonUp is a DIRECT routed event. It does not travel. WPF sends the
    //      bubbling MouseUp along the real route and each element on the way re-raises a Direct
    //      MouseLeftButtonUp on ITSELF -- and stops doing so once the event is handled. So a
    //      release consumed at an icon means the row's MouseLeftButtonUp is never raised at all.
    //
    // Which is why `container.MouseLeftButtonUp += ...` could not work, and why AddHandler with
    // handledEventsToo on that same event would not have fixed it either: there is no event
    // left to hear. The row listens on the BUBBLING MouseUp instead, which travels the whole
    // route regardless of Handled -- and which is also the route mouse CAPTURE builds, so the
    // two press-a-child-and-drift-off cases are covered by the same handler.
    //
    // A row is mostly icons with a ~10px label in the right gutter, and an ordinary click drifts
    // a pixel or two, so this fired constantly. It was never the drag gesture: moving the bar
    // onto Ctrl+drag left every one of these dead, which is exactly what Petre saw next.
    //
    // Two pieces of state make the row handler safe to run on every release:
    //
    //   pressedRow           -- the row the press STARTED in. A release only switches for the
    //                           row that was pressed, so dragging across rows commits to none.
    //   pressConsumedByChild -- set when an icon or label Button actually raised Click for this
    //                           press. Click is raised while the bubbling MouseUp is still at
    //                           the child, so the flag is always set before the row reads it. A
    //                           clean icon click therefore jumps to the window and does NOT
    //                           also switch the workspace.
    //
    // Both are reset on every press, above.
    bool pressConsumedByChild;
    DependencyObject? pressedRow;

    // Called by the icon and label Buttons from their own Click handlers.
    void MarkPressConsumed() => pressConsumedByChild = true;

    // Anything whose press must reach a click handler intact: the rows (every one of them
    // switches workspace, by label or by bare area) and the back button, which sits on the info
    // line and would otherwise be the one click target on a drag surface.
    bool StartedOnClickTarget(object source)
    {
        for (var node = source as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, Rows) || ReferenceEquals(node, BackButton)) return true;
        return false;
    }

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
    void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        dragStart = null;
        FlushDeferredRebuild();
    }

    // Serve any rebuild that was postponed while a mouse button was down (see Rebuild).
    //
    // Queued rather than run inline, and that matters on the mouse-up path: that is a PREVIEW
    // handler, so the Button's own Click has not been raised yet, and rebuilding there would
    // destroy the Button an instant before it fires -- which is the very bug the deferral exists
    // to prevent.
    void FlushDeferredRebuild()
    {
        if (!rebuildRequested || rebuilding) return;
        rebuildRequested = false;
        Dispatcher.BeginInvoke(new Action(() => { if (IsVisible) Rebuild(); }));
    }

    // Petre: "drag and drop doesn't work now."
    //
    // It did work -- the window moved -- but the bar never redrew, so nothing appeared to happen.
    // The deferral above was flushed only from the mouse-up handler, and a drag has no mouse-up
    // to flush from: DoDragDrop runs a modal OLE loop that intercepts the terminating click as a
    // native message, so WPF never turns it into a routed event at all (WindowDragSource
    // documents this at length -- it is the same fact that stops a dragged icon raising Click).
    // The pending rebuild then sat there until some unrelated window event happened along.
    //
    // So the flush no longer depends on an event that may never arrive. Called from the same 1s
    // heartbeat that re-asserts topmost, it bounds staleness at one second for EVERY way a
    // mouse-up can go missing, not just the one that was noticed.
    public void FlushIfIdle()
    {
        if (Mouse.LeftButton == MouseButtonState.Pressed) return;
        FlushDeferredRebuild();
    }

    void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (MiddleDragging(e)) return;
        if (e.LeftButton != MouseButtonState.Pressed || dragStart is not { } start) return;
        var current = PointToScreen(e.GetPosition(this));
        // Back to the plain system threshold. It was tripled while rows were still drag handles,
        // to stop ordinary click jitter from being read as a drag; now that a press on a row
        // never starts a drag at all (see OnPreviewMouseLeftButtonDown), the only presses
        // reaching here are on surfaces that do nothing else -- so waiting twelve pixels before
        // the bar moves would be sluggishness bought for nothing.
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
        // `moving` suppresses ReclaimTopmost for the duration. DragMove runs a NATIVE move
        // loop that pumps messages, so the 1s topmost timer does keep ticking inside it, and a
        // SetWindowPos landing mid-drag would be fighting that loop for the same window.
        moving = true;
        try
        {
            DragMove(); // blocks until the mouse button is released
        }
        finally
        {
            moving = false;
        }
        // A drag is the user choosing a new position, so snap it to any edge it came close to
        // and re-derive the growth anchor from where it actually landed -- otherwise the next
        // window to open would yank the bar back to the anchor it had before the drag.
        SnapToEdges();
        Save(); // only draggable while shown
    }

    // Petre: "make the drag possible with middleclick as well... middledrag."
    //
    // A third way to move the bar, and the least conditional of them: the middle button does
    // nothing else anywhere on this surface, so unlike a left press it needs no modifier and no
    // exclusion list. Grab anything -- an icon, a label, the frame -- and move.
    //
    // It cannot use DragMove(), which is why this exists at all rather than being three lines in
    // the handler above: DragMove drives Windows' own move loop, and that loop is defined in
    // terms of the LEFT button. Called with the left button up it throws outright.
    //
    // So the window is moved by hand, and the arithmetic has one trap in it. Cursor position is
    // taken from GetCursorPos, NOT from PointToScreen(e.GetPosition(this)): the latter is
    // measured relative to a window that this code is simultaneously moving, so the delta would
    // shrink to nothing as the window caught up with the cursor and the bar would refuse to
    // travel. GetCursorPos is absolute and has no such feedback.
    //
    // Left/Top are in DIPs while the cursor is in physical pixels, hence the DPI divide -- on
    // this 150% machine, omitting it would move the bar half as far as the mouse.
    Point? middleStart;   // cursor at press, physical pixels
    Point middleOrigin;   // window position at press, DIPs
    bool middleDragging;

    bool MiddleDragging(MouseEventArgs e)
    {
        if (e.MiddleButton != MouseButtonState.Pressed || middleStart is not { } start) return false;

        NativeMethods.GetCursorPos(out var cursor);
        if (!middleDragging)
        {
            if (Math.Abs(cursor.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(cursor.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return false;
            middleDragging = true;
            // `moving` suppresses ReclaimTopmost, whose SetWindowPos would otherwise fight these
            // Left/Top writes for the same window -- the same reason the left-drag path sets it.
            moving = true;
            // Capture so the moves keep arriving even if the pointer outruns the window, which
            // it does on a fast throw across a 4K screen.
            CaptureMouse();
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        Left = middleOrigin.X + (cursor.X - start.X) / dpi.DpiScaleX;
        Top = middleOrigin.Y + (cursor.Y - start.Y) / dpi.DpiScaleY;
        return true;
    }

    void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        NativeMethods.GetCursorPos(out var cursor);
        middleStart = new Point(cursor.X, cursor.Y);
        middleOrigin = new Point(Left, Top);
    }

    void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        middleStart = null;
        if (!middleDragging) return;
        middleDragging = false;
        moving = false;
        ReleaseMouseCapture();
        // Same finish as a left-drag: the user has chosen a new position, so snap to any edge it
        // landed near and re-derive the growth anchor from where it ended up.
        SnapToEdges();
        Save();
    }

    bool moving;

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

        // Petre: "sometimes, quite often, i'm clicking another workspace and it doesn't take me
        // there... i need to click twice, not always."
        //
        // Rebuilding throws away every row and builds new ones, so a rebuild that lands between
        // a press and its release destroys the very Button that was pressed -- and a Button that
        // no longer exists never raises Click. The press is simply lost, and the second click
        // works because nothing happens to interrupt it.
        //
        // It became likely enough to notice once the icons started re-sorting by z-order, which
        // makes almost any change of focus a reason to rebuild -- and clicking the bar changes
        // focus. The mechanism was always there; nothing pulsed often enough to expose it.
        //
        // So a rebuild is postponed while a mouse button is down over the bar, and flushed on
        // release. Deferring is safe in a way that skipping would not be: the same
        // rebuildRequested flag the re-entrancy guard uses already means "there is news to serve
        // later", and the release path below serves it.
        if (Mouse.LeftButton == MouseButtonState.Pressed && IsMouseOver)
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
        // BEFORE the overview query, and outside its Tap, deliberately: the back button reads
        // the MRU rather than the overview, so a transient desktop-enumeration failure (which
        // leaves the rows showing whatever they last showed) must not also leave the button
        // pointing at a workspace we have since left.
        RefreshBackButton();
        manager.WindowsByWorkspace().Tap(overview =>
        {
            // Decided ONCE per rebuild, across every row, so the whole bar agrees. A machine
            // with one display needs no marker at all -- there is no "which monitor" to answer
            // -- and deciding per row instead would let a workspace whose windows happen to
            // share a screen drop its markers while its neighbour kept them, which reads as a
            // rendering fault rather than as information.
            showMonitorMarkers = overview.Pinned
                .Concat(overview.Workspaces.SelectMany(w => w.Running))
                .Concat(overview.OtherDesktops.SelectMany(d => d.Windows))
                .Select(r => r.Monitor)
                .Where(m => m.HasValue)
                .Select(m => m.Value)
                .Distinct()
                .Skip(1)
                .Any();

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

        // Started HERE rather than when a window appears, because this is the only place that
        // knows whether the icons just drawn are real or placeholders. Stops itself on the
        // first tick where nothing is pending (see the ctor).
        if (IconCache.HasPendingIcons && !iconWatch.IsEnabled) iconWatch.Start();
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
        // 3 -> 2 per side, so the gap between rows goes 6px to 4px. The hairline still has to
        // read as a divider at a glance, which is why this is tightened rather than removed.
        Margin = new Thickness(0, 2, 0, 2),
        Background = Brushes.White,
        Opacity = 0.2,
    };

    // groupLabel is the group's full human name ("Pinned", "GEPHA", "Main") -- used in the
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

        // Petre: "do you think it would make more sense if the captions for the spaces were on
        // the right and icons started from the left edge?"
        //
        // Yes, and for a reason worth writing down. Labels differ in width -- "Messaging" against
        // "Work" -- so with the label first, the ICONS started at a different x on every row: a
        // ragged column of the one thing on this surface you aim at and click. Icons are the
        // content; they get the clean edge. Labels become a right-hand gutter, which suits them,
        // since they are secondary once every lane carries its own colour and the current one is
        // bold.
        //
        // A Grid rather than the StackPanel this used to be: two columns, icons in a star-width
        // one pinned left, label in an auto-width one on the right. Because rows stretch to the
        // bar's full width, that right column lines every label up against the same right edge,
        // so the raggedness moves to where nothing is aimed at.
        var container = new Grid { Background = idle };
        container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        container.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Petre: "if any one workspace grows too wide, then it's inefficient... there's an icon
        // limit, and if that's exceeded, then it's the next row that needs to be added."
        //
        // So the icons column is now a VERTICAL stack of horizontal lines rather than one long
        // horizontal strip. A WrapPanel would be the obvious control and is wrong here: it
        // wraps against an available width, and this window is SizeToContent, so the width it
        // would wrap against is the width it is trying to compute. Chunking by count sidesteps
        // that circularity entirely and is deterministic.
        //
        // Centred vertically as a block, so a wrapped workspace keeps ONE label beside the
        // whole lane rather than one per line.
        var icons = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        // Collected as they are built, because the hover wiring below needs the BUTTONS and
        // icons.Children now holds line panels. Reading icons.Children there instead would
        // still compile, match nothing, and silently stop suppressing the label highlight over
        // an icon -- a failure with no error and no crash.
        var iconButtons = new List<UIElement>();

        // Petre: "sort icons in workspaces by monitors", then -- after the numbered badges that
        // first carried it were rejected -- "let's go with the hairline separator", and "let that
        // separator be not in the middle, each workspace has its own place for it".
        //
        // The mark is drawn LEADING each monitor's group ("show a hairline at the beginning or
        // end"), which is what lets a row whose windows all sit on one screen still say WHICH
        // screen -- the case a divider-between-groups structurally could not answer. Rows are
        // already sorted by monitor, so a group begins wherever the number changes.
        //
        // Nothing at all for the first monitor: "there should be no padding in the beginning of
        // the icons if it's on the first monitor". Absence is the mark, and monitor 1 can only
        // ever be the first group, since the sort is ascending.
        //
        // `previous` deliberately spans ALL the lines of this row rather than resetting per line.
        // Petre: "gepha workspace, second line has a line in front." A marker was forced at the
        // start of every line, so a group split by the five-icon wrap re-announced itself
        // underneath even though nothing had changed -- a mark that looked like a boundary and
        // was really just a line break. A continuation line now inherits from the line above it,
        // which is how wrapped text reads anyway, and a marker appears only where the monitor
        // genuinely changes -- including at a line start, when the change happens to fall there.
        Maybe<int> previous = Maybe<int>.None;

        IconRowLimit.Lines(rows.ToList()).ToList().ForEach(line =>
        {
            var linedUp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
            line.ToList().ForEach(r =>
            {
                // Keyed on RANK, not on the display number: rank 0 is the primary display and
                // draws nothing. Grouping and ordering still follow the display number, which is
                // what Petre asked for originally ("first icons from monitor1, then monitor2");
                // only how loudly each group announces itself has changed.
                if (showMonitorMarkers && r.MonitorRank.GetValueOrDefault(0) > 0
                    && r.Monitor.HasValue && r.Monitor.Value != previous.GetValueOrDefault(-1))
                    linedUp.Children.Add(MonitorMarker(r.MonitorRank.Value));
                previous = r.Monitor;

                var button = IconButton(groupLabel, groupKey, r);
                iconButtons.Add(button);
                linedUp.Children.Add(button);
            });
            icons.Children.Add(linedUp);
        });
        Grid.SetColumn(icons, 0);
        container.Children.Add(icons);

        // setHover is null exactly when this row has no destination (see RowLabel), which is
        // what keeps the Pinned and Unplaced rows inert below without a second null check.
        var (label, setHover) = RowLabel(visualLabel, isCurrent, switchTo);
        Grid.SetColumn(label, 1);
        container.Children.Add(label);

        // Petre: "i'd prefer to be able to click on the empty row as well and it takes me to
        // the right place... let the text be highlighted as it is now when i am over a row and
        // take me there when i click it."
        //
        // The label alone used to be the click target: a ~10px word at the right end of the
        // row, carrying the bar's second most common action. Now the whole row does it.
        //
        // The click target is the whole row, and it listens on the BUBBLING MouseUp rather than
        // the row's own MouseLeftButtonUp. That is the fix for "sometimes it doesn't switch":
        // MouseLeftButtonUp is Direct and is never raised here at all once an icon or the label
        // has marked the release handled -- which ButtonBase does even when it raises no Click.
        // OnPreviewMouseLeftButtonDown above carries the full measurement and reasoning.
        //
        // handledEventsToo, necessarily: a release over a child arrives here already handled,
        // and that is precisely the case being rescued.
        //
        // Still cannot fire when you were dragging the BAR: a press that passes the drag
        // threshold hands the mouse to DragMove(), whose native move loop consumes the mouse-up
        // outright, so no MouseUp is ever routed. Same for an icon drag, whose modal OLE loop
        // does the same (WindowDragSource documents it).
        if (switchTo is not null && setHover is not null)
        {
            // Tunnels, so this runs after the window-level handler that clears the two flags and
            // before any child of the row sees the press: whatever happens next, the press is
            // stamped as belonging to THIS row.
            container.PreviewMouseLeftButtonDown += (_, _) => pressedRow = container;

            container.AddHandler(Mouse.MouseUpEvent, new MouseButtonEventHandler((_, e) =>
            {
                if (e.ChangedButton != MouseButton.Left) return;
                // Not our press (started in another row, or outside the bar entirely), or a
                // child already turned it into a jump/switch of its own.
                if (pressConsumedByChild || !ReferenceEquals(pressedRow, container)) return;
                Report(switchTo());
            }), handledEventsToo: true);

            // Hover feedback is the LABEL brightening, never a row background: the background
            // already means "a dragged window will land here" (DropHighlight above), and one
            // channel cannot carry two meanings on a surface this small.
            container.MouseEnter += (_, _) => { setHover(true); ShowRowActions(visualLabel); };
            container.MouseLeave += (_, _) => { setHover(false); ClearInfo(); };

            // ...and the icons punch holes in that hover area. Clicking an icon jumps to a
            // WINDOW, so lighting the label there would advertise an action the click does not
            // perform. The info line already says which group an icon belongs to, which is the
            // information the highlight would have carried anyway.
            //
            // Wired here rather than inside IconButton: IconButton has no business knowing
            // about the row it happens to sit in. Note the deliberate inversion -- entering an
            // icon CLEARS the highlight, because the container's own MouseEnter has already
            // set it (entering a child counts as entering the parent).
            // iconButtons, NOT icons.Children: since rows wrap, icons.Children holds one panel
            // per LINE, and hooking those would match no icon at all.
            iconButtons.ForEach(icon =>
            {
                icon.MouseEnter += (_, _) => setHover(false);
                // Restores the ROW's readout, not the bar-wide default. IconButton's own
                // MouseLeave calls ClearInfo, and this handler is added after it, so it wins --
                // without it, sliding off an icon into the empty part of the same row would drop
                // you back to the generic hint while still standing on the row.
                icon.MouseLeave += (_, _) => { setHover(true); ShowRowActions(visualLabel); };
            });
        }

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
            };
            container.DragLeave += (_, _) => { container.Background = idle; ClearInfo(); };
            container.Drop += (_, e) =>
            {
                container.Background = idle;
                ClearInfo();
                if (e.Data.GetData(DraggedWindow.DragFormat) is not DraggedWindow dragged) return;
                if (dragged.SourceGroupKey == groupKey) return; // dropped onto its own group
                onDrop(dragged.Handle);
            };
        }

        // Petre: "let the entire row be outlined / squared, when the workspace is active", because
        // "when looking at the left edge, i can't really see what's active".
        //
        // That second sentence is the whole design. The current workspace was marked by a pill
        // around its LABEL, and labels live in the right-hand gutter -- deliberately, so the
        // icons get the clean left edge. But the icons are what you look at, so the one marker
        // saying "you are here" was as far from your eye as it could be while still being on the
        // bar. A box round the row reaches the left edge; a pill in the gutter never can.
        //
        // Drawn on EVERY row, transparent when not current, for the reason this bar keeps
        // relearning: it is SizeToContent, so a border that only existed on the current row
        // would add its thickness to that row alone and resize the whole window on every switch.
        // Same rule as the icons' outlines and the labels' weight.
        //
        // Wrapping rather than bordering the Grid itself, because a Grid has no border of its
        // own -- and wrapping keeps the drop highlight where it belongs, on the Grid's
        // background, so an armed drop target still fills the row inside its outline.
        return new Border
        {
            Child = container,
            BorderBrush = isCurrent ? CurrentRowRing : Brushes.Transparent,
            // Two pixels, not one, and paid by EVERY row (transparent when not current) for the
            // rule this file keeps relearning: the bar is SizeToContent, so a thickness only the
            // current row carried would resize the whole window on every switch. Uniform, it
            // costs 2px of row height once and nothing thereafter.
            //
            // The second pixel is also what keeps this mark distinct from the active-WINDOW
            // outline, which is near-white too but 1px and drawn around a 22px icon rather than
            // a whole row. Scale separates them; weight makes sure of it.
            BorderThickness = new Thickness(2),
            // Slightly rounded rather than square, matching the bar's own 8px corners. At one
            // pixel the difference is barely there; it just stops the corners looking sharper
            // than the window they sit in.
            CornerRadius = new CornerRadius(3),
        };
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

    // Petre: "make the last active window in that workspace look a bit different... so i know
    // what i'm going to have activated when i land on that workspace."
    //
    // Outline only, no background, at roughly a third of ActiveBorder's strength. The two
    // markers are deliberately the SAME SHAPE at different weights rather than two different
    // shapes, because they mean the same thing at different tenses -- "you are here" and "you
    // will be here". Reading them as a pair is the point; a differently-shaped badge would read
    // as an unrelated piece of information.
    //
    // Costs no layout, which is why it can be a border at all: BorderThickness is already 1 on
    // every icon with a transparent brush (see the icon button below), so filling that brush in
    // cannot nudge the row -- and the bar is SizeToContent, so a nudged row moves the window.
    static readonly Brush WillActivateBorder = Frozen(0x38, 0xFF, 0xFF, 0xFF);

    // Every shared brush here is FROZEN. A Freezable that is still mutable takes on the
    // thread affinity of whoever created it, and these are `static` -- created once, on
    // whichever thread happens to touch this class first -- so an unfrozen brush assigned to
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
            ? $"  ·  {row.Window.ProcessName} · {groupLabel} · was: {row.OriginalTitle.Value}"
            : $"  ·  {row.Window.ProcessName} · {groupLabel}";
        Info.Inlines.Add(new Run(detail) { Foreground = DimForeground });
    }

    // The idle state: a hint, not blank. It names both gestures the bar answers to --
    // hovering for identification and dragging icons between rows -- because neither is
    // discoverable from an icon-only surface, and it costs a line that is reserved anyway.
    void ClearInfo()
    {
        Info.Inlines.Clear();
        // Names the gesture that works from ANYWHERE, rather than the edge-and-info-line one that
        // also works but has to be found. "drag labels to move" was true until rows stopped being
        // drag handles, and a hint advertising a gesture that no longer exists is worse than none.
        Info.Inlines.Add(new Run("hover an icon · drag icons between rows · ctrl+drag to move") { Foreground = DimForeground });
    }

    static readonly Brush DimForeground = Frozen(0x8C, 0xFF, 0xFF, 0xFF);

    // Petre: "show the ctrl+ thing in the notification pane when over an empty area of workspace."
    //
    // The bar-wide hint at the bottom names ctrl+drag, but it is only on screen when nothing is
    // hovered -- which is never the moment you are reaching for the bar to move it. Hovering a
    // row's bare area is exactly that moment: you are on the surface, with your hand on it.
    //
    // The gesture ONLY. Petre: "don't say click to switch, only say ctrl+drag to move." Naming
    // the click as well was padding: the row is already brightening its label under the cursor,
    // and a hint is worth reading only for the thing that is not obvious.
    //
    // Only for rows that can be switched to -- Pinned and Unplaced never reach here (see the
    // caller's null guard).
    void ShowRowActions(string label)
    {
        Info.Inlines.Clear();
        Info.Inlines.Add(new Run($"{label}  ") { Foreground = Brushes.White });
        Info.Inlines.Add(new Run("ctrl+drag to move") { Foreground = DimForeground });
    }

    // Petre: "i want a go back to previous button... basically the same as ctrl+win+tab tap
    // once, without the kb." So this deliberately holds NO history of its own: it asks the same
    // MRU the chord asks, through the same RecentWorkspaces.Back, and is therefore incapable of
    // disagreeing with the keyboard.
    //
    // It also self-toggles without any extra work: the switch below touches the MRU, so the
    // next refresh points this button back at the workspace we just left.
    void OnBackClick(object sender, RoutedEventArgs e) =>
        manager.ByRecentUse().Back.Tap(target => Report(manager.Switch(target.Id)));

    // Called from RebuildCore on every pulse, which includes a desktop change -- so the button
    // starts naming the right destination the moment you land somewhere.
    //
    // Dimmed-and-disabled rather than hidden when there is nowhere to go, following the ruling
    // the icon context menu already follows for a greyed "Restore title": a surface whose shape
    // shifts is harder to learn than one with a control that is visibly unavailable. The only
    // way to reach that state is a single workspace you are already on.
    void RefreshBackButton()
    {
        var back = manager.ByRecentUse().Back;
        BackButton.IsEnabled = back.HasValue;
        // The glyph cannot say WHICH workspace it means, and the info line's own text is
        // overwritten whenever an icon is hovered, so the tooltip is the one stable place the
        // destination can be named.
        BackButton.ToolTip = back.HasValue ? $"Back to {back.Value.Name}" : "Nowhere to go back to";
        BackButton.Opacity = back.HasValue ? 1.0 : 0.3;
    }

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
    // Returns the element plus, when the row has somewhere to go, a setter its row uses to
    // raise the text to its hovered look and drop it back (see GroupRow). The setter is null
    // for the rows that cannot be clicked, which is what makes them inert end to end: no
    // highlight, no click, one null check.
    (UIElement Element, Action<bool>? SetHover) RowLabel(string text, bool isCurrent, Func<Result>? switchTo)
    {
        // Petre: "when switching workspaces, because the caption of the workspace gets bolded,
        // it increases the width of the floating window a little if that workspace is full,
        // which is a little bad. let's make all text bold and only change its color if it's
        // active."
        //
        // Weight used to carry "this is the row you are on", and weight participates in MEASURE:
        // a bold label is wider than the same text regular, the row is wider by the difference,
        // and the bar is SizeToContent -- so every workspace switch resized the whole window and
        // shifted it. Colour carries no width at all, so the bar now measures identically
        // whichever row is current. Same reasoning as the icons' constant BorderThickness.
        //
        // Bold for EVERY row rather than regular for every row: the bar is small, translucent
        // and read at a glance, and the labels were already the hardest thing on it to read.
        var resting = isCurrent ? CurrentRowForeground : RestingRowForeground;
        var textBlock = new TextBlock
        {
            Text = text,
            FontSize = 10,
            Foreground = resting,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // What used to be a pill around the current row's label ("maybe even circle that
        // workspace so i know it's active") is now a box around the whole ROW, drawn in
        // GroupRow. Petre: "when looking at the left edge, i can't really see what's active" --
        // the pill sat in the right-hand gutter, which is the far side of the bar from the icons
        // anyone actually looks at.
        //
        // This Border survives as pure spacing, with no brush of its own: it carries the label's
        // padding and margin, which the ring used to justify. Collapsing it into the TextBlock
        // would work and is not worth the churn.
        var pill = new Border
        {
            Child = textBlock,
            Padding = new Thickness(5, 1, 5, 1),
            VerticalAlignment = VerticalAlignment.Center,
            // 8 -> 6 on the left: the gutter still has to separate the label from the icons,
            // but it was the widest single gap on the bar.
            Margin = new Thickness(6, 0, 2, 0),
        };

        if (switchTo is null)
            // No destination -> no click target. Two callers pass null deliberately:
            // the 📌 Pinned row (pinned windows are, by definition, already on every
            // workspace, so there's no single place a click could go) and the
            // "Unplaced" catch-all (Guid.Empty is not a real desktop). A Button here
            // would be dead chrome pretending to do something.
            return (pill, null);

        // Hover raises the label to the same strength the CURRENT row is drawn at, and only the
        // colour moves: the ring already means "this is the workspace you are on", so a hover
        // that also drew one would impersonate it. On the current row itself the hovered and
        // resting values coincide, which is correct -- there is no state to preview when you are
        // already there.
        void SetHover(bool hovered) => textBlock.Foreground = hovered ? CurrentRowForeground : resting;

        // Brief: "if it's trivial to make it switch to the workspace via
        // manager.Switch, DO make it switch -- that's an obvious affordance." A label
        // that reads as "this is workspace/desktop X" invites a click to go there --
        // so unlike the icon buttons (transparent, no visible chrome) this one keeps
        // the same borderless/transparent styling for visual consistency but wires
        // Click straight to the caller's switch action (manager.Switch for workspace
        // rows, manager.SwitchToDesktop for unbound-desktop rows -- fix round 6).
        var button = new Button
        {
            Content = pill,
            // Chrome-less: WPF's stock template would paint a square hover highlight around the
            // rounded pill (see BareButton). Background stays Transparent rather than null so
            // the whole label area is still hit-testable -- a null Background is invisible to
            // the mouse, and this is a click target first.
            Template = BareButton,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = $"Switch to {text}",
        };
        // Same claim the icons make: this Click already IS the switch, so the row's own mouse-up
        // handler must not perform a second one.
        button.Click += (_, _) =>
        {
            MarkPressConsumed();
            Report(switchTo());
        };
        return (button, SetHover);
    }

    // The colour the current row's label is drawn in, and therefore also the colour a hovered
    // label rises to. Two places have to agree on it exactly: if they drift, hover starts
    // reading as "this row is current".
    //
    // These replaced an element Opacity, and the swap is not cosmetic. Weight was carrying the
    // current-row state and weight changes the measure; Petre asked for colour to carry it
    // instead, so the state now lives entirely in brushes -- none of which can change how wide
    // anything is. Alpha rather than grey values so the labels sit on the bar's translucency the
    // same way the rest of its chrome does.
    static readonly Brush CurrentRowForeground = Frozen(0xF2, 0xFF, 0xFF, 0xFF);
    static readonly Brush RestingRowForeground = Frozen(0x80, 0xFF, 0xFF, 0xFF);

    // Petre: "current white border is quite unnoticeable."
    //
    // It was drawn dim on purpose -- "a locator, not a thing to read" -- and that reasoning was
    // simply wrong about the surface it sits on. The bar is translucent dark and only a few
    // pixels of it are chrome, so 40% white at one pixel does not register at all next to lane
    // tints and app icons. The marker answering "which workspace am I on" is the one thing on
    // this bar that must never need looking for.
    //
    // Tuned by eye against the running bar, in both directions: 0x66 was invisible, a first pass
    // at the label's own 0xF2 was "a little less loud, please", and this sits between them.
    //
    // So it lands just UNDER the current row's label (CurrentRowForeground, 0xF2) rather than
    // level with it, and that ordering is worth keeping if these are ever retuned: the label
    // names the workspace and the ring only locates it, so the ring leading the pair would be
    // the loudest thing on the bar saying the least.
    //
    // A per-workspace COLOURED ring was designed and rejected. It would have added information
    // the lane tint and the label already carry, and worse, its loudness would have varied by
    // workspace -- an indigo-derived ring and an amber-derived one do not have the same contrast
    // against this background, so "can I see where I am" would have depended on where you were.
    // White is the same brightness on every row.
    static readonly Brush CurrentRowRing = Frozen(0xC0, 0xFF, 0xFF, 0xFF);

    UIElement IconButton(string groupLabel, string groupKey, WindowRow row)
    {
        var button = new Button
        {
            // Both 2 -> 1. Margins do NOT collapse in a StackPanel, so the margin is paid
            // twice between neighbours: this takes the gap between adjacent icons from 4px to
            // 2px and each icon's cell from 28px to 22px, which is where most of the width
            // saving comes from. Not zero: the padding is the icon's own breathing room and
            // the active-window outline is drawn in it, so at 0 the highlight would touch the
            // artwork.
            Padding = new Thickness(1),
            // Petre: "the separation between icons should be much smaller." Horizontal margin
            // dropped to nothing; vertical kept, because that one is separating wrapped LINES
            // rather than neighbours and losing it would jam the lines together.
            //
            // Margins do not collapse in a StackPanel, so this was paid twice between every pair
            // of icons: it takes the gap from 2px to 0, and what remains between two icons is
            // each one's own 1px padding -- which has to stay, since the active-window outline is
            // drawn in it and at zero the highlight would sit directly on the artwork.
            Margin = new Thickness(0, 1, 0, 1),
            // Petre: "active window should be highlighted in the floating window". On an
            // icon-only surface with three identical VS Code glyphs, "which one am I in" is
            // otherwise unanswerable. BorderThickness stays 1 for EVERY icon with a
            // transparent brush when inactive, so gaining the highlight cannot nudge the
            // row's layout (and thus the whole SizeToContent bar's position) by 2px.
            // Three states, and IsActive wins where both could apply. An icon can only be both
            // if the suppression in OverviewBuilder.WillActivate ever lapses, and "you are in
            // this window" is the stronger, more immediate claim of the two.
            Background = row.IsActive ? ActiveBackground : Brushes.Transparent,
            BorderBrush = row.IsActive ? ActiveBorder : row.WillActivate ? WillActivateBorder : Brushes.Transparent,
            BorderThickness = new Thickness(1),
            // Chrome-less for the same reason as the row labels, and here it matters more: the
            // stock template's hover layer would sit on top of the active-window fill and the
            // landing-spot outline, muddying the two states this icon exists to show.
            Template = BareButton,
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
        button.Content = WithState(
            icon is not null
                ? new Image { Source = icon, Width = 20, Height = 20 }
                : Placeholder(row.Window),
            row);
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
        //
        // ...unless you are already IN that window, in which case the click puts it away
        // instead. Petre: "i want to be able to minimize windows from the floating bar", and
        // "only if we're on that workspace" -- which IsActive already guarantees, since a window
        // cannot hold focus on a desktop you are not looking at.
        //
        // This is the taskbar's own toggle, and the bar now has both halves of it: Activate has
        // always restored a minimized window on the way in.
        //
        // Known rough edge, left alone deliberately rather than pre-solved: a DOUBLE-click on a
        // 20px icon is jump-then-minimize, so the window appears to vanish. The fix is a short
        // guard ignoring a toggle that lands within a few hundred ms of the jump that focused
        // the window -- worth adding if it turns out to bite in practice, not worth the extra
        // state if it does not.
        // MarkPressConsumed first: this Click is raised from inside ButtonBase's handling of the
        // release, while the bubbling MouseUp is still at this icon, so the row's own handler
        // (which sits further up that same route) reads the flag afterwards and stands down.
        // Without it, one click on an icon would jump to the window AND switch to the row.
        button.Click += (_, _) =>
        {
            MarkPressConsumed();
            Report(row.IsActive
                ? manager.MinimizeWindow(row.Window.Handle, activator)
                : manager.JumpTo(row.Window.Handle, activator));
        };

        // Petre: "i also want to be able to drag them around across tabs" -- the same drag
        // source the switcher panel's rows use, so an icon dragged onto another row lands
        // through the identical AssignWindow/PinWindow/MoveToDesktop path. Sharing the
        // payload FORMAT with WindowGroupsView also means a drag started on the bar can be
        // dropped on the switcher panel (and vice versa) if both happen to be open.
        // onDragStarting clears the info line: the icon under the cursor never raises
        // MouseLeave once the modal drag loop owns the mouse, so nothing else would.
        WindowDragSource.Attach(button, row.Window.Handle, groupKey, onDragStarting: ClearInfo);
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
            // DARK on the light chip. It was white, which is illegible against a 33%-white
            // background -- the placeholder read as an empty box in Petre's screenshot, which
            // is how a rendered-but-unidentifiable icon looks exactly like a missing one.
            Foreground = PlaceholderForeground,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    static string FirstLetter(WindowInfo window) =>
        new[] { window.ProcessName, window.Title }
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text.Trim()[..1].ToUpperInvariant())
            .FirstOrDefault() ?? "?";

    // The window's state, drawn onto its icon. Petre: "can you also identify which window is
    // minimized, vs not? or which one is on top?"
    //
    // A numbered badge carried both the monitor number and (in bold) the front-most flag for one
    // round. It went -- "those numbers are bad" -- and monitor grouping moved to the separator
    // between groups in GroupRow, which says the same thing without writing on the artwork.
    //
    // What could not move to the separator is "front-most", since that is per WINDOW.
    static UIElement WithState(UIElement artwork, WindowRow row)
    {
        // Petre: "make non-topmost apps on each monitor dim", on top of the earlier ask to dim
        // minimised ones. Two asks, one channel -- so rather than two competing meanings for
        // "faded", opacity became a LADDER, and it reads as a single question: how present is
        // this window on screen right now?
        //
        //   front-most on its monitor  -> full strength
        //   open but behind something  -> dimmed
        //   minimised                  -> dimmest, because it is not on screen at all
        //
        // The ordering is what makes it legible without a legend: a minimised window is further
        // away than a merely covered one, and it looks it.
        //
        // GetValueOrDefault(TRUE) is the important bit, and it is not a "sensible default": None
        // means the desktop has no z-order to consult (see WindowRow.IsFrontmostOnMonitor), and
        // treating unknown as "behind" would dim every icon on every other workspace at once.
        // Unknown therefore renders exactly as it did before any of this existed.
        artwork.Opacity =
            row.IsMinimized ? MinimizedIconOpacity
            : row.IsFrontmostOnMonitor.GetValueOrDefault(true) ? 1.0
            : CoveredIconOpacity;

        // An underline used to mark the front-most window here, and before that a bold digit on
        // a numbered badge. Both are gone -- Petre: "you can also take away the badges, i think,
        // zindex does it well." Sorting each monitor's icons front-most-first means POSITION
        // already says which window is on top, so a mark saying the same thing was one more
        // thing to read on a 20px square for no new information.
        //
        // The opacity ladder above stays: position tells you which is in front, opacity tells
        // you how far back the others are, and minimised windows still have to be distinguished
        // from merely covered ones.
        // Petre: "can you also identify if an app has something to say, a notification, and say
        // it on the icon?"
        //
        // A dot in the TOP-right, which is the one corner of the icon still unspoken for -- the
        // bottom edge carries the same-app colour band, and the outline states ring the whole
        // thing. It is also where every messaging app in existence puts its own badge, so it
        // needs no explaining.
        //
        // Warm and fully opaque, against a bar where everything else is a shade of white: this
        // is the only mark here that is asking you to DO something, so it is the only one
        // allowed to be a colour that draws the eye.
        //
        // Both halves of the rule live elsewhere and are worth knowing here: it is set when the
        // taskbar button flashes, and cleared when you look at the window -- never by Windows,
        // which has no "stopped flashing" notification to give.
        var attention = row.WantsAttention
            ? new Border
            {
                Width = 7,
                Height = 7,
                CornerRadius = new CornerRadius(4),
                Background = AttentionDot,
                // A dark rim, so the dot survives landing on light app artwork.
                BorderBrush = AttentionDotRim,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
            }
            : null;

        if (row.Ordinal is not { HasValue: true } ordinal)
        {
            if (attention is null) return artwork;
            var dotted = new Grid();
            dotted.Children.Add(artwork);
            dotted.Children.Add(attention);
            return dotted;
        }

        // Petre: "when there are multiple similar icons, multiple edges, i want them numbered,
        // arbitrarily, if i'm selecting the second browser, i can see that the other, first got
        // demoted in the bar."
        //
        // This is a NUMBER on an icon again, which the monitor badges were rejected for -- and
        // the difference is the whole reason it earns its place. Those appeared on every icon
        // and told you something the separator could say instead. This one appears ONLY where
        // the artwork itself is ambiguous, two or more windows of one app in a row, and it is
        // the only thing on the bar that can tell them apart. "No numbers for one-instance
        // apps": OverviewBuilder leaves Ordinal unset for those, so they never reach here.
        //
        // Because the icons re-sort by z-order, this is what makes the movement legible: the
        // number belongs to the WINDOW, so watching 2 change places with 1 is watching the
        // window you left get demoted.
        // A COLOUR BAND, not a digit. Petre: "instead of numbers, you could do different
        // underline colors... numbers are hard to read." He is right, and the arithmetic says
        // why: the digit rendered at 7px and his bar runs at 90% scale, so it was about six
        // pixels of text to read on a translucent surface. A colour is not read at all, it is
        // just seen -- and seeing is the entire job here, since the only question being asked is
        // "is the one that was in front now second".
        //
        // The colour carries no meaning of its own and does not need to be memorised. It is a
        // name for the window that happens not to be a word, and it is stable for as long as the
        // window lives because Ordinal is.
        //
        // Reusing the underline slot the front-most marker used to occupy, which fell vacant
        // when z-order sorting made it redundant -- so this costs no new visual channel.
        var band = new Border
        {
            Width = 16,
            Height = 3,
            CornerRadius = new CornerRadius(1),
            Background = OrdinalBands[(ordinal.Value - 1) % OrdinalBands.Count],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
        };

        // Fixed size in a Grid cell shared with the artwork, so it overlays rather than occupies
        // -- a row of icons measures exactly as it did before, which on a SizeToContent bar is
        // the difference between a marker and the window resizing.
        var cell = new Grid();
        cell.Children.Add(artwork);
        cell.Children.Add(band);
        if (attention is not null) cell.Children.Add(attention);
        return cell;
    }

    // Amber rather than the red a notification badge usually is: red on this bar would read as
    // an error, and "someone messaged you" is not one.
    static readonly Brush AttentionDot = Frozen(0xFF, 0xFF, 0xA7, 0x26);
    static readonly Brush AttentionDotRim = Frozen(0xB0, 0x20, 0x20, 0x24);

    // Bright and saturated, deliberately unlike WorkspacePalette's muted lane tints: those sit
    // BEHIND icons and must not compete, while these are three-pixel slivers on top of arbitrary
    // artwork and have to survive it. Ordered so the first two -- by far the commonest case, two
    // windows of one app -- are as far apart in hue as the list allows.
    static readonly IReadOnlyList<Brush> OrdinalBands =
    [
        Frozen(0xFF, 0x4F, 0xC3, 0xF7), // sky
        Frozen(0xFF, 0xFF, 0xB7, 0x4D), // amber
        Frozen(0xFF, 0x81, 0xC7, 0x84), // green
        Frozen(0xFF, 0xF0, 0x62, 0x92), // pink
        Frozen(0xFF, 0xBA, 0x68, 0xC8), // violet
        Frozen(0xFF, 0xFF, 0xF1, 0x76), // yellow
    ];

    // Which monitor the icons after it are on, drawn as a TALLY: one hairline for monitor 1, two
    // for monitor 2, and so on.
    //
    // Petre: "when all windows are on one screen, i can't tell which monitor has those windows
    // and i need to." The plain divider this replaces could only appear at a BOUNDARY, so a row
    // whose windows all sat on one screen showed nothing at all -- the grouping was visible but
    // never which group.
    //
    // A tally rather than the obvious alternatives, both of which were tried and rejected on
    // this bar already. A digit is what the monitor badges were ("numbers are hard to read" at
    // 7px on a 90%-scaled window). A colour would need a legend, and would compete with the
    // same-app bands under the icons, which are already colour. Marks you can count need
    // neither: two strokes means monitor two, and nothing has to be learned.
    //
    // It also absorbs the divider's old job. Every group that has a mark opens with it, so the
    // boundary between two groups is visible without anything being drawn between them.
    //
    // ZERO-BASED, which is Petre's refinement: "no leading hairline if it's on the first one,
    // leading hairline if it's on the second." Monitor 1 is silent and monitor 2 gets one
    // stroke, so on two displays the ABSENCE of a mark carries the first monitor -- complete
    // information for half the ink, on much the commoner row. It still generalises: monitor 3
    // would take two strokes.
    //
    // The cost, accepted rather than solved: a window whose monitor could not be resolved also
    // draws nothing, so at the end of a row it is indistinguishable from monitor 1. That needs
    // MonitorFromWindow to have failed for a live window, which is rare enough not to buy a
    // permanent mark on every primary-monitor group.
    // Only ever called for monitor 2 and up; the first monitor draws nothing and reserves nothing
    // (see the caller). Width is still fixed so that a monitor-2 group and a monitor-3 group
    // indent their icons identically -- the alignment that survived is between the groups that
    // actually carry a mark.
    //
    // This began as the fix for "hairline is not in the middle always", which turned out not to
    // be about the hairline at all: a zero-stroke marker was still being added for monitor 1, so
    // those rows got an indent of its margin alone, matching no other row's. Reserving the gutter
    // for every group fixed the alignment and left an empty indent on the commonest row; Petre
    // saw both and chose flush.
    static UIElement MonitorMarker(int rank)
    {
        var tally = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        // Capped at two strokes, which is also what sets the reserved width below. Past the
        // third display the exact count matters less than "not one of the first two", and an
        // uncapped tally would widen every row on a machine with many screens.
        // The gap goes BEFORE each stroke but the first, so two strokes measure exactly 3px with
        // no trailing space. A trailing margin would pad the gutter from the inside, which is
        // half of what made it too wide.
        Enumerable.Range(0, Math.Clamp(rank, 0, 2)).ToList().ForEach(i => tally.Children.Add(new Border
        {
            Width = 1,
            Height = 14,
            Background = MonitorMarkerBrush,
            Margin = new Thickness(i == 0 ? 0 : 1, 0, 0, 0),
        }));

        return new Border
        {
            Child = tally,
            // Two 1px strokes and the 1px between them. Constant whatever the count, so a
            // monitor-1 group reserves exactly what a monitor-3 group occupies and no row's
            // icons shift relative to another's.
            Width = 3,
            // Petre: "it has too much padding", then "before and after the hairline space should
            // be the same... before is good." Was 3 and 2 around a 4px box -- nine pixels per
            // group, on a bar that counts them; five now.
            //
            // Symmetric, which reverses an earlier deliberate choice. The asymmetry was meant to
            // tie the mark to the icons FOLLOWING it, since that is what it names -- but a
            // one-pixel lean is not enough to read as meaning anything, and it was plainly
            // visible as a lopsided gap. A mark that sits evenly between two clumps reads as
            // separating them, which it also does.
            Margin = new Thickness(1, 0, 1, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    static readonly Brush MonitorMarkerBrush = Frozen(0x59, 0xFF, 0xFF, 0xFF);

    // Petre: "when hovering, it highlights outside the circle for the selected workspace."
    //
    // That grey rectangle is WPF's OWN button chrome, and setting Background="Transparent" does
    // not remove it -- the stock template paints its own hover and pressed layers on top of
    // whatever Background is bound. On the current workspace, whose label sits inside a rounded
    // pill, that chrome is a square drawn around a circle, which is exactly what he saw.
    //
    // So both button kinds on this bar are re-templated down to what they actually need: a
    // border that honours the Background/BorderBrush/BorderThickness the code sets -- those are
    // load-bearing, they are how the active window and the landing spot are drawn -- wrapped
    // round the content, and NO triggers of any kind. Every visual state on this surface is
    // decided by us from the overview; there is nothing left for a theme to contribute.
    //
    // Built from markup rather than FrameworkElementFactory because TemplateBinding is
    // unreadable that way, and Sealed so one instance can be shared by every button.
    static readonly ControlTemplate BareButton = Sealed((ControlTemplate)XamlReader.Parse(
        """
        <ControlTemplate TargetType="Button" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <Border Background="{TemplateBinding Background}"
                  BorderBrush="{TemplateBinding BorderBrush}"
                  BorderThickness="{TemplateBinding BorderThickness}"
                  Padding="{TemplateBinding Padding}">
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
          </Border>
        </ControlTemplate>
        """));

    static ControlTemplate Sealed(ControlTemplate template)
    {
        template.Seal();
        return template;
    }

    // Set once per rebuild (see RebuildCore): true only when the windows on screen are actually
    // spread across more than one display, since with one display there is nothing to say.
    bool showMonitorMarkers;

    // Faded enough to read as "put away" at a glance, not so faded the icon stops being
    // identifiable -- it still has to be a click target.
    const double MinimizedIconOpacity = 0.4;

    // Open, just not in front. Deliberately much closer to full than to minimised: this is the
    // NORMAL state for most icons on the bar at any moment, so it has to read as ordinary rather
    // than as something being wrong.
    const double CoveredIconOpacity = 0.72;


    // Opaque enough for dark text to sit on, since the bar behind it is dark.
    static readonly Brush PlaceholderBackground = Frozen(0xCC, 0xE8, 0xE8, 0xEC);
    static readonly Brush PlaceholderForeground = Frozen(0xFF, 0x20, 0x20, 0x24);

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
        if (MonitorBounds(stored?.Left ?? 0, stored?.Top ?? 0) is not { } work)
        {
            // Best-effort fallback if the API ever fails -- better than crashing the show.
            Left = stored?.Left ?? 0;
            Top = stored?.Top ?? 0;
            return;
        }

        // Petre: "when adding more windows, the floating window should grow to the left, not
        // to the right... it'll be stacked next to the right edge of the screen".
        //
        // So the RIGHT edge is the anchor, and that is what gets restored when it is known.
        // Restoring Left instead would put the left edge back where it was and let the right
        // edge land wherever this session's width happens to reach -- which, for a bar parked
        // against the screen edge, is off it.
        //
        // Left is still read for state.json files written before Right existed, and still
        // written, so nothing is lost by going back to an older build.
        //
        // Task 11 fix round 2 (reviewer, restore-path safety): whichever branch supplies the
        // position, it goes through MonitorFromPoint and the clamp on EVERY show, so a stale
        // or impossible save self-heals here without anyone editing state.json.
        // MONITOR_DEFAULTTONEAREST always returns a real monitor however far outside every
        // monitor's bounds the probe point falls.
        //
        // No persisted state at all (first run, or a pre-bar state.json): the bottom-right
        // corner of the work area, minus the bar's own size.
        var rawLeft = stored switch
        {
            { Right: { } right } => right - ActualWidth,
            { } s => s.Left,
            null => work.Right - ActualWidth,
        };
        var rawTop = stored?.Top ?? work.Bottom - ActualHeight;

        (Left, Top) = WorkAreaClamp.Clamp(rawLeft, rawTop, ActualWidth, ActualHeight, work.Left, work.Top, work.Right, work.Bottom);
        AnchorFromPosition(work);
    }

    // Petre: "can you snap to edges?" Called when a drag ends. EdgeSnap holds the maths (pure,
    // in Core, tested); this supplies the work area and applies the result.
    void SnapToEdges()
    {
        if (MonitorBounds(Left, Top) is not { } work) return;
        (Left, Top) = EdgeSnap.Snap(Left, Top, ActualWidth, ActualHeight, work.Left, work.Top, work.Right, work.Bottom);
        AnchorFromPosition(work);
    }

    // Which edge the bar grows from, derived from where it is rather than remembered.
    //
    // A bar snapped to the LEFT edge has to grow rightwards, or it walks straight off the
    // screen -- precisely the bug that made the right edge the anchor for every other case.
    // Deriving the choice means the two cannot disagree and nothing extra is persisted: a null
    // anchor is "pin the left edge", which is WPF's own behaviour, so OnSizeChanged does
    // nothing at all.
    void AnchorFromPosition((double Left, double Top, double Right, double Bottom) work) =>
        anchorRight = EdgeSnap.GrowsLeftwards(Left, work.Left) ? Left + ActualWidth : null;

    // The screen x the bar's right edge is pinned to. Null until the bar has been positioned,
    // which is what stops the initial layout passes -- several of them, as rows are built and
    // the info line is measured -- from being mistaken for growth and dragging the bar
    // leftwards before it has been placed at all.
    double? anchorRight;

    // Keeps the right edge still while the bar gets wider or narrower, which is the whole ask:
    // the bar is parked against the right of the screen, so growing rightwards walks it off.
    //
    // SizeChanged rather than a recalculation inside Rebuild: SizeToContent means the width is
    // only known once WPF has measured the new content, and Rebuild returns long before that.
    void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged || anchorRight is not { } anchor || moving) return;
        if (MonitorBounds(Left, Top) is not { } work) return;
        // Clamped like every other placement here: a bar grown wider than the work area cannot
        // keep its right edge AND stay on screen, and staying on screen wins.
        (Left, Top) = WorkAreaClamp.Clamp(anchor - ActualWidth, Top, ActualWidth, ActualHeight, work.Left, work.Top, work.Right, work.Bottom);
    }

    // The work area of whichever monitor holds the given point, in DIPs. Null when Windows
    // refuses to answer, which callers treat as "do not move anything".
    //
    // Task 11 fix round 2 (reviewer, root cause of Petre's invisible bar): the DPI comes from
    // GetDpiForMonitor on the SAME HMONITOR, not from VisualTreeHelper.GetDpi(window). A
    // window-scoped DPI query can still report a stale scale immediately after Show(), before
    // its WM_DPICHANGED round trip has landed, which is how a monitor's raw physical rcWork
    // once ended up written into DIP-valued Left/Top unconverted.
    (double Left, double Top, double Right, double Bottom)? MonitorBounds(double probeX, double probeY)
    {
        var probe = new NativeMethods.POINT { X = (int)probeX, Y = (int)probeY };
        var monitor = NativeMethods.MonitorFromPoint(probe, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return null;

        NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out var dpiY);
        var scaleX = dpiX / 96.0;
        var scaleY = dpiY / 96.0;

        // rcMonitor, NOT rcWork, and that one word is the whole of this fix.
        //
        // Petre: "When I place it over a taskbar, because taskbar is mostly empty space, I have
        // vertical taskbars in the middle of the screen, it keeps being moved... it keeps being
        // moved right next to the taskbar because taskbar kind of wants to reclaim the space,
        // it seems. So I just want it to be positioned in such a way where I place it."
        //
        // The taskbar was reclaiming nothing; we were evicting ourselves. rcWork is the desktop
        // MINUS the taskbar, and both callers of this force the bar inside it -- SnapToEdges on
        // every drop and PositionFromState on every show. Drop the bar onto a taskbar and the
        // nearest legal point is, precisely, flush beside that taskbar. The behaviour looked
        // like an outside force because it was applied on a later event than the drop.
        //
        // rcMonitor is the physical screen, so the taskbar strip is placeable and the bar stays
        // where it was put. The clamp still does its real job -- keeping the window on a
        // monitor, which is what it was written for (a stale DPI scale once parked it at
        // Left=2408 on a ~2048-DIP-wide screen).
        //
        // Safe here specifically because this window re-asserts HWND_TOPMOST on foreground
        // change and on a 1s timer: sharing the taskbar's band is a solved problem for it, and
        // sitting over one is no different from sitting over the Start menu, which it already
        // does. Edge snapping now snaps to the screen's own edges, which is arguably what "snap
        // to edges" should always have meant.
        return (info.rcMonitor.Left / scaleX, info.rcMonitor.Top / scaleY, info.rcMonitor.Right / scaleX, info.rcMonitor.Bottom / scaleY);
    }

    // One place that writes the position, so Right can never be persisted out of step with
    // Left. Visible is always true here: both callers are showing or moving the bar.
    void Save() => manager.SaveFloatingBar(new FloatingBarState(Left, Top, true) { Right = anchorRight });

    // Owned by the bar for the same reason PromptDialog.Ask now takes an owner: this window
    // is Topmost, so an unowned message box can open behind it and strand the user with an
    // invisible modal.
    Result Report(Result result) => result.TapError(err => MessageBox.Show(this, err, "TaskSpaces"));
}
