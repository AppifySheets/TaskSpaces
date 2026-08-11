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
using TaskSpaces.Windows.Dialogs;
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

        // A width the user dragged in a previous session (Petre: "make the floatingwindow
        // resizeable in width and persist it in settings"). Null -- every state.json written
        // before this existed, and every fresh install -- leaves the bar exactly as it was:
        // SizeToContent in both axes, rows wrapping at the fixed five icons.
        //
        // Clamped on the way in rather than trusted: state.json is hand-editable, and a width
        // below the info line's own would clip it with no way to drag it back.
        if (manager.State.FloatingBar?.Width is { } stored) ApplyWidth(Math.Max(MinimumWidth, stored));

        // Starts dim, because at startup the pointer is wherever the user left it and almost never
        // on a bar that has just appeared. Waiting for the first MouseLeave instead would show a
        // fully opaque bar until the pointer happened to cross it once.
        idleOpacity = BarFading.Clamp(manager.State.BarIdleOpacity);
        Opacity = idleOpacity;
        MouseEnter += (_, _) => UpdateFade();
        MouseLeave += (_, _) => UpdateFade();

        // The ten-second grace, and then the slow fade (#46). One-shot: Stop() first, so a fade
        // that is interrupted and later restarted always gets a full ten seconds rather than
        // whatever was left of the last countdown.
        fadeDelay.Interval = FadeDelay;
        fadeDelay.Tick += (_, _) =>
        {
            fadeDelay.Stop();
            // Re-checked rather than assumed: ten seconds is long enough for the pointer to have
            // come back, or for a context menu to have opened, since the countdown began.
            if (IsMouseOver || HoldsFullStrength) return;
            fading = true;
            // Cleared when the fade actually lands, so `fading` means "on its way down" and never
            // outlives the animation. Nothing breaks if it did -- the brighten path clears it too
            // -- but a flag that says something untrue is how the next bug gets built on top.
            Animate(idleOpacity, FadeMs, onCompleted: () => fading = false);
        };

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
        Closed += (_, _) => { iconWatch.Stop(); fadeDelay.Stop(); subscription?.Dispose(); };
    }

    // --- fading while the pointer is elsewhere ---------------------------------------
    //
    // Petre: "when i leave the floating window i want it to fade away, still be visible, but much
    // dimmer, so i can see what's behind it better."
    //
    // On the WINDOW's opacity rather than on the content's, so the translucent background dims
    // with everything else -- dimming only the icons would leave the dark panel behind them at
    // full strength, which is most of what is actually in the way.
    //
    // Unlike the row-order freeze, this needs none of that feature's hit-test discipline: rebuilds
    // destroy rows, but the window itself outlives every one of them, so its own MouseEnter and
    // MouseLeave cannot be raised by anything but a pointer genuinely arriving or leaving.
    readonly double idleOpacity;

    // Instant on the way in, gentle on the way out. Reaching for the bar should feel like it was
    // already there; leaving should not flicker as the pointer clips a corner on its way past.
    // Petre: "delay 10 seconds, then dim gradually" (#46).
    //
    // The first version dimmed the instant the pointer left, over 180ms, and that turned out to be
    // the wrong shape: the bar is glanceable chrome, and the moment just after you stop pointing
    // at it is precisely when you are still reading it. So it now holds full strength for a while
    // and then goes down slowly enough that the change is never what catches your eye.
    //
    // Brightening stays instant, which is the asymmetry that was right the first time: reaching
    // for the bar should feel like it was already there.
    const int BrightenMs = 60;

    // Ten seconds of grace, then four of fading. "Gradually" was left open in the issue -- these
    // are a starting point chosen to be lived with, not a measurement, and they are consts rather
    // than settings until Petre has an opinion about the numbers.
    static readonly TimeSpan FadeDelay = TimeSpan.FromSeconds(10);
    const int FadeMs = 4000;

    // Three cases where the bar is in use without being touched, and dimming any of them would
    // hide the very thing being looked at:
    //
    //   * A switch gesture is in flight. Win+Ctrl+Tab is a KEYBOARD gesture -- the pointer is
    //     nowhere near the bar by definition -- and the amber candidate ring it paints is the
    //     entire feedback for it.
    //   * A window is being dragged between rows. The OLE loop owns the mouse, the pointer is
    //     over another row, and the bar is the drop target being aimed at.
    //   * A context menu is open. The menu is its own window, so the pointer standing in it
    //     counts as having left the bar -- which would fade the row the menu belongs to.
    bool HoldsFullStrength => candidate is not null || draggingWindow || openMenu?.IsOpen == true;

    bool draggingWindow;
    ContextMenu? openMenu;

    // A context menu is its own window, so a pointer standing in it has -- as far as WPF is
    // concerned -- left the bar, which would fade the very row the menu was opened on.
    //
    // The menu itself is remembered rather than a flag being set, because the flag could get
    // stuck: menus belong to elements that every rebuild throws away, and a menu destroyed with
    // its owner is not guaranteed to raise Closed. Asking the remembered menu whether it IsOpen
    // cannot get stuck the same way -- a menu that went away answers no.
    //
    // Shared by both menus on this surface (the icons' and the workspace rows'), which is the
    // whole reason it is a method rather than two lines inside IconMenu: a second menu that forgot
    // to call it would fade the bar out from under itself the moment it opened.
    void HoldFadeWhileOpen(ContextMenu menu)
    {
        menu.Opened += (_, _) => { openMenu = menu; UpdateFade(); };
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(openMenu, menu)) openMenu = null;
            UpdateFade();
            // Serve whatever the menu held off (#77). Rebuild defers while a menu is open, because
            // rebuilding destroys the row the menu is positioned against and WPF then closes it --
            // so this is the moment the postponed news is finally allowed through.
            FlushDeferredRebuild();
        };
    }

    // Waiting out the ten seconds. A timer rather than DoubleAnimation.BeginTime, which was the
    // obvious way and does not survive contact with this surface: a delayed animation has not
    // moved the property yet, so "am I already on my way down?" cannot be answered by reading
    // Opacity -- and UpdateFade is called from the 1s heartbeat, from every rebuild, and from
    // every hold changing. Each of those would have restarted a BeginTime animation, and the bar
    // would simply never dim. The timer makes "a fade is pending" a thing that can be ASKED.
    readonly System.Windows.Threading.DispatcherTimer fadeDelay = new();

    // ...and its other half: an animation in flight has to be distinguishable from a settled one,
    // because a fade that is halfway down is neither bright nor idle, and treating it as "not yet
    // dimmed" would restart the ten seconds on the next heartbeat -- leaving the bar stuck at
    // whatever grey it had reached, forever.
    bool fading;

    void UpdateFade()
    {
        if (IsMouseOver || HoldsFullStrength)
        {
            // Cancels both a pending fade and one already under way. Re-entering mid-fade is the
            // common case -- the fade is four seconds long now -- and it has to feel like the bar
            // was never going anywhere.
            fadeDelay.Stop();
            if (!fading && Opacity >= 1.0 - 0.001) return;
            fading = false;
            Animate(1.0, BrightenMs);
            return;
        }

        // Already dim, already fading, or already counting down: all three mean "there is nothing
        // new to start", and saying so here is what makes UpdateFade safe to call from anywhere.
        if (fading || fadeDelay.IsEnabled || Opacity <= idleOpacity + 0.001) return;
        fadeDelay.Start();
    }

    // BeginAnimation rather than assigning Opacity: an animation that is still running owns the
    // property, and a plain assignment underneath it would be overwritten mid-flight.
    void Animate(double target, int milliseconds, Action? onCompleted = null)
    {
        var animation = new System.Windows.Media.Animation.DoubleAnimation(target, TimeSpan.FromMilliseconds(milliseconds));
        if (onCompleted is not null) animation.Completed += (_, _) => onCompleted();
        BeginAnimation(OpacityProperty, animation);
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

    // Petre: "i can see the taskspaces app in alt+tab, can you hide it from there?"
    //
    // XAML's ShowInTaskbar="False" was already set and is NOT enough, which is worth spelling
    // out because it looks like it should be. Measured on the running app: the bar's hwnd came
    // back with ex-style 0x80008 (TOPMOST | LAYERED) and an owner -- the invisible "Hidden
    // Window" WPF manufactures when ShowInTaskbar is false and there is no real Owner. That
    // keeps it off the TASKBAR, and people assume owned windows are off Alt+Tab too, but the
    // shell's rule is one step longer: it walks each visible window to its root owner, takes
    // that owner's last active popup, and lists THAT if it is visible and not a tool window.
    // Our owner is invisible and the bar is its last active popup, so the bar was listed on its
    // own account. WS_EX_TOOLWINDOW is the flag that actually ends the argument -- it is the one
    // condition in that rule we can set on ourselves.
    //
    // Harmless here for the two things it normally changes: a tool window gets a thin caption
    // (we are WindowStyle="None", so there is none) and skips the taskbar (already skipped).
    //
    // OnSourceInitialized rather than the constructor because the hwnd does not exist until the
    // HwndSource is created, and rather than App.OnStartup's EnsureHandle site because what a
    // window is belongs to the window. It still lands before the first Show(): App calls
    // EnsureHandle() on the bar to hand its handle to WindowMonitor.Ignore, and that call is
    // what runs this.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE,
            ex | (nint)NativeMethods.WS_EX_TOOLWINDOW);
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

    // Petre: "the bar shouldn't be hidden, it should be impossible to hide that floating bar,
    // that's the app."
    //
    // HideBar() and its OnHideClick handler lived here and are gone. They were ALREADY
    // unreachable -- nothing in FloatingBar.xaml bound the handler, so HideBar's only caller was
    // one nobody could trigger -- which meant the bar was unhideable by accident rather than on
    // purpose. Deleted so it stays that way deliberately, and so nothing wires a close
    // affordance back onto the one surface this app exists to show. Same ruling that removed
    // Manage's "Show floating bar" checkbox.
    //
    // FloatingBarState.Visible is now vestigial: always written true, read by nothing. Left in
    // place rather than removed because it is a POSITIONAL member of a persisted record, so
    // dropping it would change the shape of everyone's state.json for no behavioural gain.

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
        // Stage 1 of the click trace (#48). The SOURCE type matters as much as the fact of a
        // press: it says which element the release will be delivered to, and two of the three
        // known ways to lose a click turn on that.
        if (ClickTrace.On)
            ClickTrace.Write($"press source={e.OriginalSource.GetType().Name} icon={StartedOnIcon(e.OriginalSource)} clickTarget={StartedOnClickTarget(e.OriginalSource)} rebuilding={rebuilding} pending={rebuildRequested}");

        // A new press: nothing has claimed it yet, and no row owns it until the row's own
        // tunnelling handler (wired in GroupRow) runs a moment later, further down this route.
        pressConsumedByChild = false;
        pressedRowKey = null;

        // Set HERE, beside the trace line, and not at the end of this method: the question it answers is
        // "did this window see the button go down", and the resize branch below returns without reaching
        // the end. A press on a resize grip that left this false would make the release after it look
        // orphaned, and an orphan release switches workspace -- so the honest reading is the only safe one.
        pressSeen = true;

        // A press in either side strip resizes instead of doing anything else at all -- checked
        // FIRST, and returning, so the same press cannot also arm a bar-drag below.
        if (ResizeSideAt(e.GetPosition(this)) is { } side)
        {
            NativeMethods.GetCursorPos(out var cursor);
            resizing = (side, cursor.X, Width, Left);
            // `moving` suppresses BOTH the 1s topmost re-assert and OnSizeChanged's growth
            // anchor. The anchor is the one that matters: it exists to hold the right edge still
            // while the bar grows, which is precisely what a left-edge resize must be allowed to
            // break, and without this the two would fight over Left on every mouse move.
            moving = true;
            CaptureMouse();
            e.Handled = true;
            return;
        }

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
    //   pressedRowKey        -- the row the press STARTED in. A release only switches for the
    //                           row that was pressed, so dragging across rows commits to none.
    //   pressConsumedByChild -- set when an icon or label Button actually raised Click for this
    //                           press. Click is raised while the bubbling MouseUp is still at
    //                           the child, so the flag is always set before the row reads it. A
    //                           clean icon click therefore jumps to the window and does NOT
    //                           also switch the workspace.
    //
    // Both are reset on every press, above.
    bool pressConsumedByChild;

    // A drag has just given the mouse back, so the release that follows belongs to it rather than to a
    // click. See onDragFinished, where it is set, and the row's release handler, which clears it.
    bool dragJustFinished;

    // Whether the press for the release now being handled ever reached this window, and its complement.
    // Written in the window's own tunnelling handlers, which are first on both routes, so every handler
    // further along can trust them. See OnPreviewMouseLeftButtonUp for what an orphan release means and
    // why it is acted on rather than discarded.
    bool pressSeen;
    bool orphanRelease;

    // The row's KEY, not the row's container object, and that is the whole point (#48).
    //
    // This used to hold the container and compare it with ReferenceEquals, which is the trap this
    // codebase has already paid for once with the amber candidate ring: state that has to outlive a
    // rebuild cannot live on the things a rebuild destroys. A rebuild between a press and its
    // release swaps every row container out, so the release arrives at a BRAND NEW object standing
    // in exactly the same place, showing exactly the same workspace -- and the reference comparison
    // called that a different row and threw the click away. The trace shows it: "row-up ...
    // ourPress=False" with no drag anywhere near it.
    //
    // The identity of a row is its groupKey. It survives the rebuild, so the click does too. A real
    // drag across rows still commits to nothing, because those are genuinely different keys.
    string? pressedRowKey;

    // Called by the icon and label Buttons from their own Click handlers.
    void MarkPressConsumed() => pressConsumedByChild = true;

    // Anything whose press must reach a click handler intact: the rows (every one of them
    // switches workspace, by label or by bare area) and the back button, which sits on the info
    // line and would otherwise be the one click target on a drag surface.
    internal bool StartedOnClickTarget(object source)
    {
        for (var node = source as DependencyObject; node is not null; node = ParentOf(node))
            if (ReferenceEquals(node, Rows) || ReferenceEquals(node, BackButton)) return true;
        return false;
    }

    // Walks up from whatever element the press actually hit (an Image inside a Button
    // template, usually) looking for one of our tagged icon buttons. VisualTreeHelper
    // rather than the logical tree: the press lands on template-generated visuals, which
    // the logical tree does not connect to the Button.
    internal static bool StartedOnIcon(object source)
    {
        for (var node = source as DependencyObject; node is not null; node = ParentOf(node))
            if (node is FrameworkElement { Tag: IconTag }) return true;
        return false;
    }

    // One step up from anything a press can land on, and the reason both walks above go through it
    // rather than calling VisualTreeHelper directly.
    //
    // Petre: "it crashed." Pressing the info line's own hint text killed the app outright:
    //
    //   System.InvalidOperationException: 'System.Windows.Documents.Run' is not a Visual or Visual3D
    //      at System.Windows.Media.VisualTreeHelper.GetParent(DependencyObject reference)
    //      at FloatingBar.StartedOnIcon(Object source)
    //
    // The info line is a TextBlock built out of Run inlines, and a Run is a ContentElement, not a
    // Visual. VisualTreeHelper.GetParent does not politely return null for one of those -- it
    // throws -- and an exception out of a mouse handler on the dispatcher takes the process with
    // it. It had been possible for as long as the info line has had Runs in it; nobody had reason
    // to press that text until the rows around it started moving.
    //
    // Text does not live in the visual tree, so the logical parent is the only way out of it: a
    // Run's logical parent is its TextBlock, which IS a Visual, and the walk carries on normally
    // from there. Neither caller wants to STOP at a Run -- an icon is never made of text -- they
    // just need to get past it.
    static DependencyObject? ParentOf(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);

    // Marks an icon Button for StartedOnIcon above. A private const string compared by
    // value (pattern-matched, so a null Tag can never match).
    const string IconTag = "icon";

    // Clears a finished press so a later, unrelated move never measures distance from
    // a stale point -- same hardening WindowGroupsView.SetupDragSource applies to its
    // own dragStart (pitfall #2 in that file's comments).
    void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // The half of the #48 trace that was missing, and the one that splits the remaining cases
        // in two. Today a lost click leaves a "press" line with no "row-up" after it, and that is
        // ambiguous: the release may have arrived at the bar and been declined somewhere before the
        // row, or it may never have reached this window at all (the pointer left the bar, another
        // window took it, a modal loop ate it).
        //
        // This is the window's own tunnelling handler, so it is FIRST on the route: if a release is
        // delivered to the bar in any form, this line is written. A "press" with no "up" after it
        // therefore means the release genuinely never got here -- which is a different bug from a
        // release that got here and went nowhere, and needs a different fix.
        // AN ORPHAN RELEASE: the button came up on the bar without the matching press ever arriving.
        //
        // The fourth and fifth mechanisms behind #48 both look like this in the log, and Petre could
        // finally reproduce them: "i clicked personal workspace and it didn't take me there", twice, and
        // "even icon clicking is still not working sometimes". Both times the log holds an `up` with no
        // `press` before it, 0.35 to 0.46s after a workspace switch.
        //
        // That timing is the explanation. A switch animates for a few hundred milliseconds, and the app
        // activates a window on the arriving desktop as it lands (see RestoreLastActive), so the bar is
        // no longer the foreground window. A press during that window is eaten before it reaches us --
        // by the shell's switch, or as the click that reactivates the bar. Nothing here can prevent it.
        //
        // So the release is honoured on its own. Both flags a normal click relies on are stale by
        // definition in this case -- pressedRowKey and pressConsumedByChild describe the click BEFORE the
        // one that went missing -- which is why the row previously either did nothing or switched to
        // wherever the last press happened to be. pressConsumedByChild is cleared here so that only a
        // handler running for THIS release can set it, which is what lets an icon still win over its row.
        orphanRelease = !pressSeen;
        pressSeen = false;
        if (orphanRelease) pressConsumedByChild = false;

        if (ClickTrace.On)
            ClickTrace.Write($"up source={e.OriginalSource.GetType().Name} clickTarget={StartedOnClickTarget(e.OriginalSource)} " +
                             $"pressedRow={pressedRowKey ?? "none"} orphan={orphanRelease}");

        dragStart = null;
        if (resizing is not null)
        {
            resizing = null;
            moving = false;
            ReleaseMouseCapture();
            // Finished exactly like a move does: the user has chosen a geometry, so snap to any
            // edge it now reaches and re-derive the growth anchor from where it ended up -- a bar
            // widened away from the right edge grows leftwards from a different x than before.
            SnapToEdges();
            Save();
        }
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
        // Belt and braces for the hover freeze, on the same principle as the flush below: a
        // release that depends on a MouseLeave arriving is a release that can fail to happen.
        // The pointer can end up off a frozen row with no leave event at all -- the container is
        // destroyed by a rebuild mid-move, the bar is hidden, another window takes the mouse --
        // and a freeze nobody clears would pin that row's order for the rest of the session.
        // This bounds every one of those at one second.
        if (hoveredRow is { } stale) ReleaseRowIfPointerLeft(stale.Key);

        // Same belt and braces for the fade. Every input it reads can change without an event
        // reaching this window -- a menu dismissed by clicking another application, a drag that
        // ended somewhere else -- and the cost of missing one is a bar stuck bright, which is
        // exactly the thing being fixed. Cheap: UpdateFade returns immediately unless the answer
        // has actually changed.
        UpdateFade();

        if (Mouse.LeftButton == MouseButtonState.Pressed) return;
        // An open menu holds the same way a held mouse button does (#77): flushing here would
        // rebuild the rows out from under it and close it, which is precisely what the deferral in
        // Rebuild exists to prevent. Its own Closed handler flushes instead.
        if (openMenu?.IsOpen == true) return;
        FlushDeferredRebuild();
    }

    // --- hover freeze ---------------------------------------------------------------
    //
    // Petre: "when an app becomes the top app, if i press on it in the workspace, it moves to the
    // first position, which is good, but i want that position changing to happen after i've left
    // the floating window with a mouse... so that i can minimize it back if i didn't want to use
    // it and am testing what it is." Then, on scope: on leaving THE ROW, not the bar.
    //
    // The ordering rule itself is pure and lives in RowOrderFreeze (with its own tests and its
    // own reasoning). This is the half that cannot be pure: which row the pointer is in.
    //
    // One slot, because one row is hovered at a time. Keyed by groupKey rather than by the row's
    // rowKey: rowKey is null for unbound-desktop rows, and an unbound desktop IS the current
    // desktop on Petre's machine most of the time -- which makes it exactly the row that sorts by
    // z-order and therefore the one that needs holding.
    // RowKey rides along for the hover ring (#41): the freeze is keyed by groupKey, which every row
    // has, while the rings are keyed by the workspace id, which only workspace rows have. Carrying
    // both means the ring needs no second piece of hover state to drift out of step with this one.
    (string Key, Guid? RowKey, IReadOnlyList<WindowHandle> Order)? hoveredRow;

    // The order to re-impose on this row, or nothing if it is not the hovered one.
    IReadOnlyList<WindowHandle> HeldOrder(string groupKey) =>
        hoveredRow is { } h && h.Key == groupKey ? h.Order : [];

    // Marks a row container for HoveredRowKey below. A dedicated type rather than the bare string:
    // icon Buttons already carry Tag = IconTag, so a walk up the tree looking for "any string Tag"
    // would stop at the icon and report "icon" as the row key -- releasing the freeze the instant
    // the pointer touched the very icons it exists to hold still.
    sealed record RowTag(string Key);

    void EnterRow(string groupKey, Guid? rowKey, IReadOnlyList<WindowRow> displayed)
    {
        // Re-entering the row we are already holding -- which happens every time a rebuild swaps
        // the container out from under a stationary pointer. Keeping the ORIGINAL snapshot
        // matters: re-capturing here would be capturing the same order anyway (the row is frozen,
        // so nothing has moved), but only by luck, and the luck runs out the moment anything else
        // is allowed to reorder a frozen row.
        if (hoveredRow is { } held && held.Key == groupKey) return;

        var wasHolding = hoveredRow is not null;
        hoveredRow = (groupKey, rowKey, RowOrderFreeze.Capture(displayed));
        // The hover ring (#41). Painted here rather than waiting for the rebuild below, because
        // arriving from OUTSIDE the bar rebuilds nothing -- there is no previous row to re-sort --
        // and that is the commonest way a row is entered.
        ApplyCandidate();
        // A rebuild triggered by the POINTER rather than by a window event, which is new since
        // this bug was last looked at: moving from one row to another re-sorts the row just left.
        // If a click can be lost by arriving during that, this line will sit immediately before
        // the press that goes missing.
        if (ClickTrace.On && wasHolding) ClickTrace.Write($"enter-row rebuild for={groupKey}");
        // Moving straight from one row to another: the row just left has to re-sort now, and its
        // own MouseLeave cannot do it -- by the time that deferred check runs, the freeze belongs
        // to this row and it will (correctly) leave it alone.
        if (wasHolding) Rebuild();
    }

    // Deferred to the next dispatcher turn, and that is the load-bearing part of this whole
    // feature. A rebuild removes the hovered container from the tree, and WPF raises MouseLeave on
    // removal -- so acting on the event directly would unfreeze a row the pointer never left, and
    // rebuilds are most frequent precisely while the user is working in the bar. By the next turn
    // the new row exists, so asking where the pointer ACTUALLY is answers correctly.
    void LeaveRow(string groupKey) =>
        Dispatcher.BeginInvoke(new Action(() => ReleaseRowIfPointerLeft(groupKey)));

    void ReleaseRowIfPointerLeft(string groupKey)
    {
        if (hoveredRow is not { } held || held.Key != groupKey) return; // another row already owns the freeze
        if (HoveredRowKey() == groupKey) return;                        // still inside it; the container was merely rebuilt
        hoveredRow = null;
        Rebuild(); // the promotion the user has been waiting for, served the moment they step off
    }

    // The icon ring's half of the same trap (#67), deferred for exactly the reason above: a
    // rebuild removes the icon from the tree and WPF raises MouseLeave on the way out, so acting
    // on the event directly would drop the ring off an icon the pointer never left -- and the ring
    // is at its most useful precisely while the bar is busy rebuilding under it.
    //
    // By the next turn the replacement icon exists and is registered, so asking whether the
    // pointer is over ANY icon showing this window answers correctly whether it was rebuilt or
    // not. Same shape as ReleaseRowIfPointerLeft, one line shorter because icons carry no freeze.
    void LeaveIcon(WindowHandle handle) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (hoveredIcon != handle) return;                                          // another icon already owns the ring
        if (iconRings.Any(i => i.Handle == handle && i.Button.IsMouseOver)) return;  // still on it; it was merely rebuilt
        hoveredIcon = null;
        ApplyCandidate();
    }));

    // Which row the pointer is actually inside, by hit test rather than by remembered events.
    string? HoveredRowKey()
    {
        if (!IsVisible || !IsMouseOver) return null;
        // ParentOf, not VisualTreeHelper directly, for the same reason the press walks do: a hit
        // test can land on text, and text is not in the visual tree (see ParentOf).
        for (var node = Rows.InputHitTest(Mouse.GetPosition(Rows)) as DependencyObject;
             node is not null;
             node = ParentOf(node))
            if (node is FrameworkElement { Tag: RowTag tag }) return tag.Key;
        return null;
    }

    // --- resizing the bar's width ---------------------------------------------------
    //
    // Petre: "make the floatingwindow resizeable in width and persist it in settings", and
    // "i want no less than 3 icons per row width".
    //
    // Hand-rolled rather than ResizeMode="CanResize", for the same reasons the middle-drag move
    // is hand-rolled: this window is WindowStyle=None + AllowsTransparency + SizeToContent, and
    // WPF's own resize chrome argues with all three -- it wants a border to grab on a window that
    // has none, and a window whose width follows its content has nothing to resize in the first
    // place. So the width becomes EXPLICIT the moment one is chosen (see ApplyWidth), and from
    // then on the rows wrap to fit it instead of at the fixed five.
    const double ResizeGrip = 4;

    // The narrowest the bar may be. The info line is what actually sets it -- a fixed 150 plus 18
    // for the ↩ button, sized when Petre asked for "3 icons wide by default", so the floor lands
    // almost exactly on his "no less than 3 icons per row" anyway. Below this WPF would simply
    // clip the info line, which is not a size, it is damage.
    //
    // Scaled, because BarScale is a LayoutTransform on the content: the window's own width is the
    // content's width times the scale.
    double MinimumWidth => (18 + 150 + 8) * BarScaling.Clamp(manager.State.BarScale);

    // What one icon occupies along a row: the 20x20 artwork, plus IconButton's 1px padding and
    // 1px border on each side. Its horizontal margin is deliberately zero (Petre: "the separation
    // between icons should be much smaller"), so this is the whole cell.
    const double IconCellWidth = 24;

    // ...and what one LINE of icons occupies down the row: the same 24 the cell is wide, plus
    // IconButton's 1px top and bottom margins.
    //
    // Exists as the floor for a row with NO icons (#76). Petre: a newly added workspace "appears
    // with a smaller row height", and "the bar window should grow when workspaces are added".
    //
    // The bar did grow -- height has been content-driven throughout -- but a new workspace has no
    // windows, an empty icon stack is zero pixels tall, and what was left holding the row open was
    // its 10pt label. So an empty row came out roughly half height, which read as the row being
    // squeezed in rather than as the row simply having nothing in it.
    //
    // Not a regression: empty workspace rows have been drawn since they became legitimate switch
    // and drop targets, and they have always been short. It only became obvious once workspaces
    // were being created from the bar's own menu, where the new row is the thing you are looking at.
    const double IconLineHeight = 26;

    // ...and what a monitor marker adds when an icon opens a group: MonitorMarker's fixed 3px box
    // plus its 1px margins. Constant whatever the stroke count, by the same design that keeps
    // every marked group's icons aligned with every other's.
    const double MonitorMarkerWidth = 5;

    // Root's Padding, from the XAML. Content coordinates, so it is inside the BarScale transform.
    const double RootPadding = 4;

    // How much of a row is left for icons once its label has taken what it needs.
    //
    // Measured rather than assumed: labels are workspace names, so the gutter is "Work" on one row
    // and "Messaging" on the next, and a fixed reservation would either waste the difference on
    // every short name or wrap early on every long one. Measure() with infinite space asks the
    // label what it WANTS, which is exactly what the Auto column will grant it.
    //
    // Width is divided by the scale because BarScale is a LayoutTransform on Root: the window's
    // width is the content's width times the scale, and everything on this line is content.
    // The name no longer shares the line with the icons -- it is a caption above them -- so nothing is
    // reserved for it here. That is what makes every row's icon area identical, and with it the hairline's
    // position: equal halves of an equal width land on one shared middle.
    // Everything on the way in from the window's edge is subtracted, and the row's own ring is part of
    // that: it is 2px on every row (transparent unless current, so the width is paid whatever is
    // selected) and it costs the icons 4 DIP that measurement caught this arithmetic keeping. A row
    // inside a group box, or indented as a child, gives up 6 or 9 more; those are NOT subtracted here,
    // and the slack is harmless only because an icon cell is 24 wide and no lane's content can land in
    // the gap. If the cell ever shrinks, this has to learn about the group chrome too.
    double IconRoom() =>
        Width / BarScaling.Clamp(manager.State.BarScale) - RootPadding * 2 - RowRingThickness * 2 - CaptionWidth;

    // The ring drawn around every row (see the `box` Border in GroupRow), named because the icon budget
    // has to know about it: it is uniform on all four sides and on all rows, so it is a constant cost
    // rather than something the current row alone pays.
    const double RowRingThickness = 2;

    // The caption gutter, the same on every row. Wide enough for most workspace names at this size and
    // narrow enough that the icons keep the bulk of the row; anything longer wraps onto a second line inside
    // it, which is what he asked for and what keeps the width a constant rather than a negotiation.
    const double CaptionWidth = 58;

    enum ResizeSide { Left, Right }

    (ResizeSide Side, int StartCursorX, double StartWidth, double StartLeft)? resizing;

    // Which side strip a point is in, or none. Uses ActualWidth rather than Width so it works
    // before a width has ever been set, when Width is NaN (SizeToContent).
    ResizeSide? ResizeSideAt(Point point) =>
        point.Y < 0 || point.Y > ActualHeight ? null
        : point.X >= 0 && point.X <= ResizeGrip ? ResizeSide.Left
        : point.X >= ActualWidth - ResizeGrip && point.X <= ActualWidth ? ResizeSide.Right
        : null;

    // Cursor from GetCursorPos, NOT from the event's position: the event's position is measured
    // relative to a window this very method is moving and resizing, so the delta would shrink to
    // nothing as the window caught up. Identical trap to the middle-drag move above, and it is
    // worse here -- a left-edge resize moves Left AND Width at once.
    bool Resizing()
    {
        if (resizing is not { } grip) return false;

        NativeMethods.GetCursorPos(out var cursor);
        var dpi = VisualTreeHelper.GetDpi(this);
        var travelled = (cursor.X - grip.StartCursorX) / dpi.DpiScaleX;

        // Dragging the LEFT edge holds the right edge still: the width grows by what the cursor
        // travelled leftwards, and Left follows it. Dragging the right edge is the plain case,
        // Left untouched.
        var wanted = grip.Side == ResizeSide.Left ? grip.StartWidth - travelled : grip.StartWidth + travelled;
        var width = Math.Max(MinimumWidth, MonitorBounds(Left, Top) is { } work ? Math.Min(wanted, work.Right - work.Left) : wanted);

        ApplyWidth(width);
        // From the RESTING geometry, not from the live Left: clamping the width above means the
        // cursor can outrun the edge, and deriving Left from a value that has already been
        // clamped would drift the bar a little further on every move.
        if (grip.Side == ResizeSide.Left) Left = grip.StartLeft + (grip.StartWidth - width);
        return true;
    }

    // The one place the bar stops being SizeToContent in the width axis. Height stays content-driven
    // -- rows wrap, and how tall that makes the bar is not something anyone wants to drag.
    void ApplyWidth(double width)
    {
        SizeToContent = SizeToContent.Height;
        Width = width;
    }

    void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (Resizing()) return;
        // Only when nothing else owns the mouse, so the cursor cannot flip to a resize arrow in
        // the middle of dragging the bar across the screen.
        if (e.LeftButton != MouseButtonState.Pressed)
            Cursor = ResizeSideAt(e.GetPosition(this)) is not null ? Cursors.SizeWE : Cursors.Arrow;
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

        // ...and the same deferral while a CONTEXT MENU is open (#77). Petre: the colour submenu
        // "sometimes disappears before the mouse can reach it".
        //
        // Same mechanism as the lost click, one layer up. A ContextMenu is positioned against a
        // PLACEMENT TARGET -- the row it was opened on -- and a rebuild throws every row away. WPF
        // closes a menu whose target has left the tree, so any window event arriving while someone
        // reads the menu shuts it, and the bar rebuilds on every window event. "Sometimes" is
        // exactly what that looks like from the outside: it depends on whether anything happened
        // to open, close or retitle a window in the second you were deciding.
        //
        // Deferred rather than skipped, and flushed when the menu closes -- the same
        // rebuildRequested flag the other two guards use.
        if (openMenu?.IsOpen == true)
        {
            rebuildRequested = true;
            return;
        }

        // Stage 2, and the one this bug keeps coming back to: a rebuild that runs BETWEEN a press
        // and its release destroys the pressed Button, and a Button that no longer exists raises
        // no Click. The deferral above is supposed to make that impossible while a button is down
        // over the bar -- so a line here with pressed=True is the smoking gun, and its absence
        // rules the mechanism out rather than leaving it suspected for a fourth time.
        if (ClickTrace.On && Mouse.LeftButton == MouseButtonState.Pressed)
            ClickTrace.Write($"REBUILD WHILE PRESSED mouseOver={IsMouseOver}");

        rebuilding = true;
        var clock = ClickTrace.On ? System.Diagnostics.Stopwatch.StartNew() : null;
        try
        {
            RebuildCore();
        }
        finally
        {
            rebuilding = false;
            // How long the UI thread was NOT available, which is the other half of #51: a dialog
            // whose frame is up and whose contents are missing is a dialog waiting for this. Only
            // the slow ones are logged -- a rebuild is COM-heavy (one DesktopOf per known window)
            // and there are many, so logging every one would bury the interesting ones.
            if (clock is { } elapsed && elapsed.ElapsedMilliseconds >= 150)
                ClickTrace.Write($"rebuild took {elapsed.ElapsedMilliseconds}ms");
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
        // The rows these pointed at have just been thrown away, so every entry is now a Border
        // that is no longer in the tree. Repopulated as the new rows are built.
        rowRings.Clear();
        iconRings.Clear();
        monitorLines.Clear();
        currentRow = null;
        ClearInfo();
        // BEFORE the overview query, and outside its Tap, deliberately: the back button reads
        // the MRU rather than the overview, so a transient desktop-enumeration failure (which
        // leaves the rows showing whatever they last showed) must not also leave the button
        // pointing at a workspace we have since left.
        RefreshBackButton();

        // A geometry dump once the layout has settled, behind the trace marker and only when it differs
        // from the last one (see DumpRowGeometry).
        if (ClickTrace.On)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, DumpRowGeometry);
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

            // Rank -> display number, for #89's drop targeting. The ranks come from the overview,
            // which computes them over the displays that actually hold windows (OverviewBuilder), so
            // this is a map of the screens the bar can show anything about, in reading order.
            //
            // Needed for one case only: a row that starts on a later screen draws an empty half in
            // front of its first group, and a drop in that empty half means the screen the row has
            // nothing on. Rank 0 is that screen, which is exactly right on two monitors. With three
            // or more the empty half stands in for every earlier screen at once and rank 0 is a
            // choice among them rather than the answer, which is a limit of the LAYOUT rather than
            // of this lookup: there is one empty half however many screens are missing from the row.
            monitorByRank = overview.Pinned
                .Concat(overview.Workspaces.SelectMany(w => w.Running))
                .Concat(overview.OtherDesktops.SelectMany(d => d.Windows))
                .Where(r => r.Monitor.HasValue && r.MonitorRank.HasValue)
                .GroupBy(r => r.MonitorRank!.Value)
                .ToDictionary(g => g.Key, g => g.First().Monitor!.Value);

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
                    // No screen argument: a pinned window is on every desktop, and #89 is about which
                    // screen a window sits on, which pinning has nothing to say about.
                    onDrop: (h, _) => Report(manager.PinWindow(h)),
                    overview.Pinned));

            // Fix round 6 (Petre, screenshot showing ONE "Sparrow" row: "it does follow
            // across every workspace, but not showw all workspace tabs"). The original
            // design ("unbound desktops excluded -- it is a workspace bar", empty
            // workspaces skipped) collapsed to a single row on his machine because most
            // of his windows live on the unbound "Main" desktop. Superseded, spec
            // amended: EVERY workspace gets a row -- an empty one is just its label,
            // which since round 5 is a click-to-switch button, so it's a legitimate
            // switch target rather than dead chrome.
            // Members of a group are drawn together (#42 anchored, #84 anchorless), which means the
            // flat list is walked once and a whole group is emitted when its first member is
            // reached. Everything after that member is skipped, so a group whose members are not
            // contiguous in the list still comes out as one box.
            //
            // Order is otherwise untouched: a workspace's position still drives its lane colour
            // (WorkspacePalette by index), so this re-SEQUENCES rows without renumbering them.
            // Grouping a workspace moves where it is drawn, not what colour it is.
            var state = manager.State;
            var byGroup = overview.Workspaces.ToLookup(g => g.Workspace.GroupId);

            // The position a group takes its colour from when it has none of its own, which the state
            // answers so the switcher cannot disagree with the bar about it (AppState.ColourSlotOf).
            // Indexing State.Workspaces rather than the overview is the same number: the overview
            // builds one row per workspace, in list order.
            int ColourSlotOf(Core.Domain.Group group) => state.ColourSlotOf(group);

            // Petre: "make the children more apparent, maybe group them together with an outline
            // or a ring."
            //
            // So a family is drawn as ONE thing: the parent and its children go inside a single
            // outlined box, in the parent's colour. The indent and the spine say "this row is a
            // child"; the outline says "these rows are a family", which is the question a glance
            // actually asks -- and it is the only one of the three that cannot be answered by
            // looking at a single row.
            //
            // Built per family and wrapped afterwards, rather than appended one row at a time,
            // because an outline has to be drawn around a set that already exists.
            // `group` is the group this row belongs to, or null for a row that stands on its own.
            // Everything a row inherits from its group is decided from it, so the two kinds differ
            // in exactly one place: whether there is an anchor to inherit a colour from.
            // `boxed` says the group around this row is drawn as a box, which is where the lane tint
            // then lives: the box paints it once behind the whole group instead of every member
            // painting the same colour separately (#91). Two coats of a translucent tint make a
            // member's row darker than the box it sits in, which is the surface saying "different"
            // about rows that are the same.
            UIElement WorkspaceRow(WorkspaceGroup g, Core.Domain.Group? group, bool boxed = false)
            {
                // Recorded so ApplyCandidate can restore the white ring here when the amber
                // one moves away, without needing another overview query to ask again.
                if (g.IsCurrent) currentRow = g.Workspace.Id;

                var ownSlot = overview.Workspaces.ToList().FindIndex(w => w.Workspace.Id == g.Workspace.Id);
                // The slot the row's colour comes from: its group's, or its own when ungrouped.
                var colourSlot = group is not null ? ColourSlotOf(group) : ownSlot;
                // And the override in force, which for a member is the GROUP's (#90): a group is one
                // colour whichever row set it, so a member's own Workspace.Color is ignored while it
                // is inside a box and waiting for it if it ever leaves.
                var colour = group is not null ? group.Color : g.Workspace.Color;

                // A member is drawn as nested unless it is the anchor. In an ANCHORLESS group every
                // member is nested, because none of them is the parent: the group's name carries
                // what the anchor's row would otherwise have said.
                var isAnchor = group?.AnchorWorkspaceId == g.Workspace.Id;
                var nested = group is not null && !isAnchor;

                // The spine answers "this row is under the row above it", which is only a question an
                // ANCHORED group raises. In an anchorless one there is no parent row to be under, and
                // the box's bracket already says the rows belong together (#91), so a spine on every
                // member would be the same claim twice down the same edge.
                var spine = nested && group!.IsAnchored ? LaneAccent(colour, colourSlot) : null;

                // The name, plainly. The ↳ prefix that was here first is gone: Petre asked for "a
                // better representation that it's a child", and a glyph inside a label that already
                // sits in a narrow gutter is the weakest place to put it. The spine, the shared
                // colour, the indent and the group outline all say it around the row instead.
                return GroupRow(
                    g.Workspace.Name,
                    g.Workspace.Name, g.IsCurrent,
                    switchTo: () => manager.Switch(g.Workspace.Id),
                    groupKey: DraggedWindow.WorkspaceGroupKey(g.Workspace.Id),
                    // The screen comes from WHERE in the row it was dropped (#89), and null means
                    // the row had no split to aim at, which is the workspace-only move it always was.
                    onDrop: (h, screen) => Report(manager.AssignWindow(h, g.Workspace.Id, screen)),
                    g.Running,
                    // Every member wears the GROUP's lane colour rather than its own. Colour is what
                    // groups things on this bar, so giving a member a colour of its own would be the
                    // surface saying "unrelated" while the layout says "belongs to". Inside a box the
                    // box wears it for all of them.
                    tint: boxed ? null : LaneTint(colour, colourSlot >= 0 ? colourSlot : ownSlot),
                    rowKey: g.Workspace.Id,
                    minimized: g.Workspace.Minimized,
                    nested: nested,
                    spine: spine);
            }

            // Walked in list order, emitting a whole group at its first member and skipping the
            // rest. A group is therefore drawn where it STARTS, which is what keeps a group's
            // position on the bar predictable while its members are reordered inside it.
            var drawn = new HashSet<Guid>();
            overview.Workspaces.ToList().ForEach(entry =>
            {
                if (!drawn.Add(entry.Workspace.Id)) return;

                if (entry.Workspace.GroupId is not { } id || state.Groups.FirstOrDefault(g => g.Id == id) is not { } group)
                {
                    groupRows.Add(WorkspaceRow(entry, null));
                    return;
                }

                // MembersOf puts the anchor first, so an anchored group draws its parent at the top
                // whatever order the members happen to sit in.
                var members = state.MembersOf(id)
                    .Select(w => overview.Workspaces.FirstOrDefault(x => x.Workspace.Id == w.Id))
                    .Where(x => x is not null)
                    .ToList();
                members.ForEach(m => drawn.Add(m!.Workspace.Id));

                // A group of one gets no box: an outline around a single row would be decoration
                // that means nothing, and most rows on the bar stand alone. It cannot normally
                // happen, since leaving a group of two dissolves it, but a hand-edited state.json
                // can produce one. Decided BEFORE the rows are built, because it is what decides
                // whether a row paints its own lane or the box paints it for the whole group.
                var boxed = members.Count + (group.IsAnchored ? 0 : 1) > 1;
                var rows = members.Select(m => WorkspaceRow(m!, group, boxed)).ToList();

                // An ANCHORLESS group needs a header, because nothing inside it carries the name.
                // Deliberately not a switch target: there is no desktop behind it, and a row that
                // looks clickable and does nothing is worse than one that plainly is not.
                if (!group.IsAnchored) rows.Insert(0, GroupHeader(group, ColourSlotOf(group)));

                groupRows.Add(boxed
                    ? FamilyBox(rows, LaneAccent(group.Color, ColourSlotOf(group)), LaneTint(group.Color, ColourSlotOf(group)))
                    : rows[0]);
            });

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
                    onDrop: g.DesktopId == Guid.Empty ? null : (h, _) => Report(manager.MoveToDesktop(h, g.DesktopId)),
                    g.Windows)));

            groupRows
                .SelectMany((row, i) => i == 0 ? new[] { row } : new[] { Separator(), row })
                .ToList()
                .ForEach(el => Rows.Children.Add(el));

            // Re-assert the switch candidate onto the rows that have just been built. Without
            // this, any window event arriving while the chord is held would rebuild the bar and
            // silently drop the amber ring -- the marker would vanish mid-gesture with nothing
            // to explain it. Same class of bug as the row click that used to disappear: state
            // that outlives a rebuild cannot live on the things the rebuild destroys.
            ApplyCandidate();
        });

        // Started HERE rather than when a window appears, because this is the only place that
        // knows whether the icons just drawn are real or placeholders. Stops itself on the
        // first tick where nothing is pending (see the ctor).
        if (IconCache.HasPendingIcons && !iconWatch.IsEnabled) iconWatch.Start();
        // Overview query failure (e.g. a transient desktop-enumeration hiccup) just
        // leaves whatever the bar last showed -- there's no text area on this surface to
        // report an error into, and the next StateChanged pulse retries for free.
    }

    // One line of a row's icons: one equal half per monitor, the mark on the middle between them,
    // and each half's icons packed left against it.
    //
    // Petre: "if apps are separated by monitor, let them be aligned to the left and right, not all
    // left... apps on each monitor will be left and right aligned, hairline in the middle between
    // them." Then #58, on seeing where that put things: "the right-screen icons should sit next to
    // the hairline, not drift right" -- and then, on the first attempt at it, "it's pretty bad,
    // separator has to sit in the middle."
    //
    // Both halves of that are load-bearing and the first attempt kept only one. Collapsing the gap
    // did pull the right-hand group back against the mark, but it took the mark with it: the
    // hairline then landed wherever the left-hand group happened to end, so it moved from row to
    // row and stopped reading as a boundary at all.
    //
    // A Grid rather than the StackPanel this used to be. Each monitor's group gets a STAR column,
    // which is what makes the halves equal and holds the mark between them on the middle; the
    // icons inside are aligned LEFT, which is what packs them against the mark rather than
    // carrying them to the far end of their half. Generalises past two monitors on its own -- three
    // groups are three equal thirds with a mark on each seam -- so there is no case here for "two
    // monitors" as such, which is why none is written.
    //
    // "On the middle" is a default and not a promise: a half with more icons than fit in it takes
    // the room it needs from the half that is not using it, and the mark moves with it. See the
    // MinWidth below for why that beats wrapping the overflow onto another line.
    //
    // On a bar with no width of its own (SizeToContent still owns it) the stars have no slack to
    // share and everything simply packs, which is the same thing this drew before any of it.
    UIElement LineOf(IReadOnlyList<WindowRow> line, ISet<WindowHandle> opensGroup, string groupLabel, string groupKey, Guid? rowKey, List<UIElement> iconButtons, bool isLastLine)
    {
        // Runs of consecutive icons that share a monitor. `opensGroup` already knows where each
        // one starts -- it is the same set the wrap arithmetic budgets a marker for -- so this
        // needs no second opinion about what a group is.
        var runs = new List<List<WindowRow>>();
        line.ToList().ForEach(r =>
        {
            if (runs.Count == 0 || opensGroup.Contains(r.Window.Handle)) runs.Add([]);
            runs[^1].Add(r);
        });

        var grid = new Grid();

        // #89's drop target: the half of the line that stands for one screen. Painted behind the
        // icons rather than over them (added first, so it is the bottom of the Grid's z-order), and
        // parked outside every column until a drag arms it.
        var aim = new Border { Background = Brushes.Transparent };
        grid.Children.Add(aim);

        // Which columns each screen's half occupies, in the order they are laid out. The mark that
        // opens a group counts as part of THAT group's half, so the boundary between two screens is
        // the hairline itself -- which is the rule Petre asked for: "the drop position relative to
        // the hairline picks the monitor."
        var zones = new List<MonitorZone>();

        int AddColumn(UIElement child, GridLength width, double minWidth = 0)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width, MinWidth = minWidth });
            Grid.SetColumn(child, grid.ColumnDefinitions.Count - 1);
            grid.Children.Add(child);
            return grid.ColumnDefinitions.Count - 1;
        }

        // ONE COLUMN PER SCREEN, in reading order, whether or not this row has anything on that screen.
        //
        // The old shape was "the runs this line happens to have, then empty halves appended for the screens
        // it does not" -- and appending is what broke it. Measured on Petre's bar:
        //
        //   zones=[screen1@start, screen2@106]     a row with windows on both
        //   zones=[screen2@start, screen1@106]     a row with windows only on the RIGHT screen
        //
        // The second row put its right-screen icons on the LEFT and the left screen's empty region on the
        // right, because the missing screen was tacked on at the end instead of taking its own place. Which
        // is also why the boundary looked wrong: it was, on those rows, and only on those.
        //
        // Laid out over every known screen instead, the column structure is IDENTICAL on every row -- N
        // equal halves divided by marks -- so a screen's region is at the same x everywhere and the
        // boundary cannot wander. That is what "always in the middle" needs to be true of the layout rather
        // than of one row's contents.
        // GroupBy rather than ToDictionary: two runs on one line can only share a monitor if the icons
        // arrive out of monitor order, which they do not -- but a duplicate key here would be an
        // exception on the UI thread, and losing the second run is the milder failure of the two.
        var byMonitor = runs
            .Where(run => run[0].Monitor.HasValue)
            .GroupBy(run => run[0].Monitor!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        // The lanes of this line: a screen each when the bar is naming screens, otherwise a run each.
        // `Run` is null for a screen this row has nothing on, which is the empty droppable half (#102).
        var lanes = new List<(int Rank, Maybe<int> Monitor, List<WindowRow>? Run)>();

        if (showMonitorMarkers && monitorByRank.Count > 0)
        {
            var mapped = monitorByRank.Values.ToHashSet();
            monitorByRank.OrderBy(x => x.Key).ToList().ForEach(screen =>
                lanes.Add((screen.Key, screen.Value, byMonitor.GetValueOrDefault(screen.Value))));

            // And then every run the screen map cannot account for: a window whose monitor never
            // resolved, or one on a display that holds nothing else. They get a lane of their own with
            // no mark rather than being dropped, and "dropped" is not hypothetical -- the first version
            // of this drew only the mapped screens, so a bar where NO window had a monitor rendered
            // every row with no icons in it at all. The bar tests caught exactly that.
            runs.Where(run => !run[0].Monitor.HasValue || !mapped.Contains(run[0].Monitor.Value))
                .ToList()
                .ForEach(run => lanes.Add((0, run[0].Monitor, run)));
        }
        else
            runs.ForEach(run => lanes.Add((run[0].MonitorRank.GetValueOrDefault(0), run[0].Monitor, run)));

        lanes.ForEach(lane =>
        {
            // The mark divides, so it belongs to the screen it precedes and never to the first one: absence
            // is the mark for the leftmost screen ("there should be no padding in the beginning of the icons
            // if it's on the first monitor").
            FrameworkElement? mark = lane.Rank > 0 && showMonitorMarkers ? MonitorMarker(lane.Rank) : null;
            var firstColumn = mark is null ? -1 : AddColumn(mark, GridLength.Auto);

            if (lane.Run is { } run)
            {
                var stack = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
                run.ForEach(r =>
                {
                    var button = IconButton(groupLabel, groupKey, r);
                    iconButtons.Add(button);
                    stack.Children.Add(button);
                    if (button is Button icon)
                        iconRings.Add(new IconRing(icon, rowKey, r.Window.Handle, r.IsActive, r.WillActivate));
                });

                var stackColumn = AddColumn(stack, new GridLength(1, GridUnitType.Star));
                if (firstColumn < 0) firstColumn = stackColumn;
                // No zone without a monitor: a zone is a drop target that names a screen, and there is no
                // screen to name here. The icons are still drawn, which is the part that matters.
                if (lane.Monitor.HasValue)
                    zones.Add(new MonitorZone(lane.Monitor.Value, mark, firstColumn, stackColumn - firstColumn + 1));
            }
            else
            {
                // Nothing on this screen: an empty half that is still a drop target (#102), and with no
                // hairline of its own beyond the divider above.
                var empty = new Border();
                var emptyColumn = AddColumn(empty, new GridLength(1, GridUnitType.Star));
                if (firstColumn < 0) firstColumn = emptyColumn;
                if (lane.Monitor.HasValue)
                    zones.Add(new MonitorZone(lane.Monitor.Value, mark ?? empty, firstColumn, emptyColumn - firstColumn + 1));
            }
        });

        // Registered per LINE rather than per row, because a wrapped row can hold one screen's icons
        // above and another's below, and then "which half" is a question about the line the pointer is
        // over. A row's own drop handler picks the line first and the half second.
        if (rowKey is { } key && zones.Count > 0)
        {
            if (!monitorLines.TryGetValue(key, out var lines)) monitorLines[key] = lines = [];
            lines.Add(new MonitorLine(grid, aim, zones));
        }

        return grid;
    }

    // #89. Petre: "dropping the icon onto a specific monitor of another workspace", and "dropping the
    // icon onto another monitor within the current workspace -- same drag, same row: drag the icon
    // across its own row's hairline to send the window to the other screen."
    //
    // The row already draws the geography this needs: icons grouped by screen, a hairline where the
    // screen changes. So the drop does not need a new surface, only a reading of where in the row it
    // landed, and that reading is these two records.
    //
    // Held per row and rebuilt with the rows, for the reason everything else on this bar is: a
    // rebuild throws every element away, and rebuilds fire on any window event.
    sealed record MonitorZone(int Monitor, FrameworkElement? Mark, int FirstColumn, int ColumnCount);
    sealed record MonitorLine(Grid Line, Border Aim, IReadOnlyList<MonitorZone> Zones);

    readonly Dictionary<Guid, List<MonitorLine>> monitorLines = [];

    // Rank -> display number for the screens the bar knows about. See where it is filled in.
    IReadOnlyDictionary<int, int> monitorByRank = new Dictionary<int, int>();

    // The half of the row a drop at this point is aiming at, or null for "no screen, just the
    // workspace".
    //
    // Null in three cases, and all three mean the same thing to the caller: the row shows only one
    // screen, so there is nothing to aim at; the row shows none at all; or the pointer is to the left
    // of every half, which happens over the row's own left edge.
    (MonitorLine Line, MonitorZone Zone)? Aimed(Guid rowKey, UIElement container, Point at)
    {
        if (!monitorLines.TryGetValue(rowKey, out var lines)) return null;

        // One screen is not a choice. Petre's own rule for this: "today's behaviour (workspace only)
        // would remain the meaning of a drop when the target row has no split to aim at."
        if (lines.SelectMany(l => l.Zones).Select(z => z.Monitor).Distinct().Count() < 2) return null;

        // The line under the pointer, or the nearest one: a row is a few pixels of padding taller
        // than its lines, and a drop in that padding still means the line beside it.
        var line = lines
            .Select(l => (Line: l, Bounds: BoundsIn(l.Line, container)))
            .Where(x => x.Bounds is not null)
            .OrderBy(x => VerticalDistance(x.Bounds!.Value, at.Y))
            .Select(x => x.Line)
            .FirstOrDefault();

        return line is not null && AimedZone(line, container, at) is { } zone ? (line, zone) : null;
    }

    int? AimedMonitor(Guid rowKey, UIElement container, Point at) => Aimed(rowKey, container, at)?.Zone.Monitor;

    // Every line's halves and where each one starts, in the container's own coordinates: the same numbers
    // AimedZone compares the pointer against. For the trace only (#101).
    string DescribeZones(Guid? rowKey, UIElement container) =>
        rowKey is { } key && monitorLines.TryGetValue(key, out var lines)
            ? "[" + string.Join(" | ", lines.Select(line =>
                $"line@{BoundsIn(line.Line, container)?.Top:F0}-{BoundsIn(line.Line, container)?.Bottom:F0}: " +
                string.Join(", ", line.Zones.Select(z =>
                    $"screen{z.Monitor}from{(z.Mark is null ? "start" : BoundsIn(z.Mark, container)?.Left.ToString("F0") ?? "?")}")))) + "]"
            : "none";

    // The last half whose left edge the pointer has passed. A zone with no mark starts at the line's
    // own left edge, which is why its start is negative infinity rather than zero: the row is padded,
    // so the pointer can legitimately be at a smaller x than the line.
    MonitorZone? AimedZone(MonitorLine line, UIElement container, Point at) =>
        line.Zones
            .Select(zone => (zone, Start: zone.Mark is null
                ? double.NegativeInfinity
                : BoundsIn(zone.Mark, container)?.Left ?? double.PositiveInfinity))
            .Where(x => at.X >= x.Start)
            .OrderByDescending(x => x.Start)
            .Select(x => x.zone)
            .FirstOrDefault();

    // Null when the element is not (or is no longer) inside this container, which a rebuild during a
    // drag can produce: the handler on the old row is still wired up while monitorLines already holds
    // the new one's elements. A null means "cannot tell", and the drop falls back to workspace-only.
    static Rect? BoundsIn(FrameworkElement element, UIElement container)
    {
        try
        {
            return element.TransformToAncestor((Visual)container).TransformBounds(new Rect(element.RenderSize));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    // The last geometry written, so an unchanged layout is not written again. The bar rebuilds on every
    // window event, which is dozens of times a minute while anyone is working, and this dump is ten lines
    // each time -- so without the comparison, turning tracing on for a lost click (which is what
    // ClickTrace is actually for) would bury that click's three lines under thousands of identical ones.
    string lastGeometry = "";

    // The measured shape of one rebuild: the bar's width, and per line the columns it was actually given
    // plus where each monitor mark sits in the bar's own coordinates. Trace only, and only on a CHANGE.
    //
    // Written because four attempts at "put the hairline in the middle" were judged by eye and two of them
    // were wrong in ways no reading of the code predicted -- a mark at seven tenths of the row, icons
    // running past the middle instead of wrapping. Both were settled here in one line of numbers.
    void DumpRowGeometry()
    {
        var geometry = $"geometry: width={Width:F0} actual={ActualWidth:F0} " +
                       $"scale={BarScaling.Clamp(manager.State.BarScale):F2} monitors={monitorByRank.Count}\n" +
                       string.Join("\n", monitorLines.SelectMany(row =>
                           row.Value.Select((line, at) =>
                           {
                               var columns = string.Join(", ", line.Line.ColumnDefinitions.Select(c =>
                                   $"{(c.Width.IsStar ? "*" : c.Width.IsAuto ? "auto" : "fixed")}:{c.ActualWidth:F0}"));
                               var marks = string.Join(", ", line.Zones.Select(z =>
                                   $"screen{z.Monitor}@{(z.Mark is null ? "start" : BoundsIn(z.Mark, this)?.Left.ToString("F0") ?? "?")}"));
                               return $"  line {at} x={BoundsIn(line.Line, this)?.Left:F0} width={line.Line.ActualWidth:F0} " +
                                      $"cols=[{columns}] zones=[{marks}]";
                           })));

        if (geometry == lastGeometry) return;
        lastGeometry = geometry;
        ClickTrace.Write(geometry);
    }

    static double VerticalDistance(Rect bounds, double y) =>
        y < bounds.Top ? bounds.Top - y : y > bounds.Bottom ? y - bounds.Bottom : 0;

    // Paints the half being aimed at and clears every other one on the row, so exactly one half is
    // ever lit. Called on every DragOver, which is why it repaints from the answer rather than
    // remembering what it lit last time: that is how a highlight gets stuck on a half the pointer left.
    void ArmAim(Guid? rowKey, UIElement container, Point at)
    {
        if (rowKey is not { } key || !monitorLines.TryGetValue(key, out var lines)) return;

        // ONE line, the one the pointer is on: a wrapped row can hold the same screen twice, and
        // lighting both halves would claim the window is about to go to two places.
        var aimed = Aimed(key, container, at);
        lines.ToList().ForEach(line => line.Aim.Background = Brushes.Transparent);
        if (aimed is not { } hit) return;

        Grid.SetColumn(hit.Line.Aim, hit.Zone.FirstColumn);
        Grid.SetColumnSpan(hit.Line.Aim, hit.Zone.ColumnCount);
        hit.Line.Aim.Background = DropHighlight;
    }

    void ClearAim(Guid? rowKey)
    {
        if (rowKey is { } key && monitorLines.TryGetValue(key, out var lines))
            lines.ToList().ForEach(line => line.Aim.Background = Brushes.Transparent);
    }

    // Group membership on the row's own menu (#83, #84). Petre: "all three live on the workspace
    // row's right-click context menu", and then "i want the bar to drive it".
    //
    // Which items appear depends on the row, so the menu never offers something that would be
    // refused. That is the same rule the rest of this menu follows: "Add child" is hidden on a row
    // that cannot have children rather than shown and then rejected.
    //
    //   in a group        -> Move out of group, and Ungroup
    //   not in a group    -> New group…, and Move into group for each group that exists
    //   an anchor         -> Move into group is withheld: its own members would become
    //                        grandchildren, which is the one level rule
    void AddGroupItems(ContextMenu menu, Guid workspaceId, Action<string, string, Action> add)
    {
        var state = manager.State;
        var group = state.GroupOf(workspaceId);

        if (group is not null)
        {
            add("⇤", "Move out of group", () => Report(manager.LeaveGroup(workspaceId)));
            // Named in the item, because "Ungroup" alone does not say what is about to dissolve and
            // this affects rows other than the one clicked.
            add("⊟", $"Ungroup '{group.Name}'", () => Report(manager.Ungroup(group.Id)));
            return;
        }

        add("⊞", "New group…", () =>
            PromptDialog.Ask("New group", "Name:", owner: this)
                .Tap(name => Report(manager.CreateGroup(name, workspaceId))));

        // Nothing to join yet, and an empty submenu reads as a broken one.
        var joinable = state.Groups.Where(g => g.Id != group?.Id).ToList();
        if (joinable.Count == 0 || state.IsAnchor(workspaceId)) return;

        var into = new MenuItem { Header = "Move into group", Icon = MenuGlyph("⇥") };
        joinable.ForEach(target =>
        {
            var item = new MenuItem { Header = target.Name };
            item.Click += (_, _) => Report(manager.MoveIntoGroup(workspaceId, target.Id));
            into.Items.Add(item);
        });
        menu.Items.Add(into);
    }

    // The name of an ANCHORLESS group (#84), which has no member to carry it: "the parent is not a
    // workspace, it's only the group's name."
    //
    // NOT a switch target and not a drop target, which is the constraint the issue itself set: there
    // is no desktop behind it. A row that looks like the others and then does nothing when clicked
    // is worse than one that plainly is not clickable, so this is a label and a lane tint with no
    // icons, no hover ring and no menu of its own.
    //
    // An anchored group needs none of this. Its anchor is a real workspace sitting at the top of
    // the box, and its name is already on that row.
    //
    // #91: "the group header is too big -- a name-only line doesn't need anywhere near a full row's
    // height. It should cost the bar almost nothing." So it is a caption, not a row: no lane of its
    // own (the box paints one behind the whole group now), no minimum height, a smaller face than a
    // workspace label, and its line box clamped so the font's own ascent and descent cannot pad it
    // out. What is left is roughly a third of a row.
    //
    // It cannot shrink to nothing, though, and that is not a compromise: this is where the group's
    // menu lives (#84, #90), so it has to stay big enough to right-click.
    UIElement GroupHeader(Core.Domain.Group group, int colourSlot)
    {
        // Transparent rather than untinted: a null Background would not take part in hit testing, and
        // then the right-click that reaches the group's menu would fall through to the bar.
        var header = new Grid { Background = Brushes.Transparent };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // In the right-hand gutter with the workspace names, so the group reads as belonging to the
        // same column of labels rather than as a banner across the bar.
        var label = new TextBlock
        {
            Text = group.Name,
            FontSize = GroupHeaderFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = GroupHeaderForeground,
            // The font's line box is taller than its letters, and on a caption this small that
            // difference is most of the height. Clamped to the glyphs, which is why the caption ends
            // up costing about four pixels more than the text itself.
            LineHeight = GroupHeaderFontSize + 1,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(4, 1, 4, 0),
        };
        Grid.SetColumn(label, 1);
        header.Children.Add(label);

        // #84: "where Ungroup lives when the group has no workspace row of its own -- presumably the
        // group's header gets its own small context menu." It does, and it holds only the two things
        // that are about the GROUP rather than about a workspace.
        //
        // Rename matters more here than it looks: for an anchorless group this name is the only
        // thing naming it, and there is no anchor row to rename instead.
        var menu = new ContextMenu();
        HoldFadeWhileOpen(menu);

        var rename = new MenuItem { Header = "Rename group…", Icon = MenuGlyph("✏") };
        rename.Click += (_, _) => PromptDialog.Ask("Rename group", "New name:", group.Name, owner: this)
            .Tap(name => Report(manager.RenameGroup(group.Id, name)));
        menu.Items.Add(rename);

        var ungroup = new MenuItem { Header = "Ungroup", Icon = MenuGlyph("⊟") };
        ungroup.Click += (_, _) => Report(manager.Ungroup(group.Id));
        menu.Items.Add(ungroup);

        // The group's colour belongs here too (#90): the header is a group's own row, and for an
        // anchorless one it is the closest thing it has to a parent to set it from.
        menu.Items.Add(new Separator());
        AddGroupColourPicker(menu, group.Id);

        header.ContextMenu = menu;
        return header;
    }

    // Dimmer than a workspace label, because it names something you cannot go to. The rows below it
    // are the targets, and the header should not compete with them for the eye.
    static readonly Brush GroupHeaderForeground = Frozen(0xB0, 0xE0, 0xE0, 0xE0);

    // Smaller than a workspace label for the same reason it is dimmer, and small enough to answer
    // #91's "should cost the bar almost nothing" while still being a right-click target.
    const double GroupHeaderFontSize = 9;

    // A parent and its children, drawn as one thing (#42). Petre: "make the children more
    // apparent, maybe group them together with an outline or a ring."
    //
    // The other three signals -- indent, spine, shared lane colour -- all answer "is this row a
    // child", one row at a time. This answers "which rows belong together", which is the question a
    // glance actually asks and the only one that cannot be answered by looking at a single row.
    //
    // In the parent's colour, at a third of the spine's strength: it has to be findable without
    // becoming the loudest thing on a surface whose whole job is showing icons. The current-row
    // ring is deliberately brighter and rectangular against this one's rounder, dimmer box, so the
    // two never read as the same claim.
    //
    // #91 added the two things that make membership readable at a glance rather than merely stated.
    // The LANE now belongs to the box: it is painted once behind every member, so a group is one
    // continuous band of colour instead of a stack of separately tinted rows that happen to share a
    // hue. And the left edge is drawn thicker than the other three, which turns the outline into a
    // bracket down the side of the group: the eye reads a bracket as containment without having to
    // trace a whole rectangle.
    //
    // Petre: the members "don't read as its children... they look like ordinary neighbouring rows
    // that happen to sit under a label."
    static UIElement FamilyBox(IReadOnlyList<UIElement> rows, Brush? outline, Brush? lane)
    {
        var stack = new StackPanel();
        rows.ToList().ForEach(row => stack.Children.Add(row));
        return new Border
        {
            Child = stack,
            Background = lane ?? Brushes.Transparent,
            BorderBrush = outline ?? Brushes.Transparent,
            BorderThickness = new Thickness(GroupBracketWidth, 1, 1, 1),
            CornerRadius = new CornerRadius(6),
            Opacity = 0.999, // forces its own layer, so the 1px border cannot be swallowed by the row backgrounds beneath it
            Padding = new Thickness(1),
            Margin = new Thickness(0, 1, 0, 1),
        };
    }

    // The bracket down the left of a group. Wide enough to read as a deliberate edge rather than as
    // the same hairline the other three sides are, narrow enough that it costs the icons almost
    // nothing: it eats into the row's width exactly like the nested spine does.
    const double GroupBracketWidth = 3;

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
    // rowKey identifies this row to the switch gesture (see rowRings). Null for rows the chord
    // can never land on: 📌 Pinned, "Unplaced", and unbound desktops -- the chord walks
    // WORKSPACES, so only those need to be repaintable.
    // Petre: "a minimized row is about a third of the regular row height... everything still works
    // in that state -- clicks, drags, highlights -- it is just tiny, icons included."
    //
    // A LayoutTransform on the whole row, which is what makes "everything still works" true for
    // free: hit testing, drag-and-drop and the rings all go through the same transform as the
    // pixels, so nothing needs a second, scaled-down implementation. It is also the reason this is
    // a transform rather than smaller icons and fonts -- that would be a second layout to keep in
    // step with the first, forever.
    //
    // LayoutTransform, not RenderTransform, for the third time in this file and the same reason
    // each time: only a layout transform is part of MEASURE, so the row genuinely occupies a third
    // of the height rather than painting small inside a full-size slot. The row still spans the
    // bar's full width, because the transform scales the space it is given as well as what it
    // draws there.
    const double MinimizedRowScale = 1.0 / 3;

    // The spine down the left of a nested row, in its parent's colour. Two pixels: a hairline
    // reads as an artefact and anything wider starts competing with the icons.
    const double SpineWidth = 2;

    // Petre: "maybe a little indented", then, on seeing it against the row above: "gap before the
    // beginning of the left edge and the browser icon."
    //
    // It was ten, which is small for a tree view and large for this bar -- a nested row's first
    // icon stood a whole third of an icon clear of its parent's, and on a surface where every row
    // is read against the row above it that gap is the loudest thing about the row. It cost icon
    // room on every nested row too.
    //
    // Just the spine's width and its margin now, so the icons begin exactly where the spine ends:
    // no gap at all, and no overlap either. The indent has stopped being a signal of its own,
    // which it can afford to be -- the family outline (#42) draws the relationship around the
    // whole family, which is a stronger statement than a per-row indent ever made, and the spine
    // and the shared lane colour are both still there saying it row by row.
    //
    // Taken out of the icons' own space rather than the row's, so a nested row still starts and
    // ends where every other row does and the monitor alignment (#39) still lines up across rows.
    const double NestedIndent = SpineWidth + 1;

    UIElement GroupRow(string visualLabel, string groupLabel, bool isCurrent, Func<Result>? switchTo, string groupKey, Action<WindowHandle, int?>? onDrop, IEnumerable<WindowRow> rows, Brush? tint = null, Guid? rowKey = null, bool minimized = false, bool nested = false, Brush? spine = null)
    {
        // Background MUST be non-null for a panel to take part in hit testing at all --
        // a null Background leaves gaps between icons that swallow nothing and report no
        // DragOver, making drops land unpredictably. Transparent is the standard fix.
        //
        // `idle` rather than a literal Transparent everywhere below: the drag highlight
        // replaces this Background and has to put the LANE COLOUR back on leave, not
        // transparent, or dragging over a workspace would permanently strip its tint.
        var idle = tint ?? Brushes.Transparent;

        // Held still while the pointer is inside this row (see the hover-freeze section above).
        // Applied HERE, before anything reads the rows, so the order drawn, the order captured on
        // enter and the order the wrap splits into lines are all the same list.
        var ordered = RowOrderFreeze.Apply(rows.ToList(), HeldOrder(groupKey));

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
        // Petre: "shall we try adding workspace captions at the top of every workspace, as it is with
        // sparrow group, but without groups?" -- the caption treatment #91 gave an anchorless group's
        // header, applied to every row.
        //
        // The name moves ABOVE the icons, so the icons get the row's FULL width. That is the thing three
        // earlier attempts were reaching for: with no gutter and no name in the line, every row's icon area
        // is the same width, so equal star halves put the monitor hairline on the SAME x in every row --
        // #99's shared middle, with nothing shared and nothing to keep in step. Petre, arriving at the same
        // place: "then, middle separation would make more sense, with a hairline separator."
        //
        // It also ends the collision that killed the centred name (#103): a caption on its own line cannot
        // be reached by icons, however many a workspace has.
        //
        // The cost is vertical, which is why it is a CAPTION: about a third of a row each, the height #91
        // measured for the group header, rather than a full row per name.
        // A FIXED caption gutter on the right, the same width on every row. Petre: "let's try giving captions
        // fixed space on the left, all captions get the same width. keep small text to and multiline if it
        // doesn't fit in that small space", then "on the right, not left".
        //
        // Fixed rather than Auto, and fixed rather than shared. Auto is what made the hairlines disagree:
        // each row's icon area was the row minus ITS OWN name, so every row split a different width in half.
        // Shared (#70) fixed that and cost the longest name's width on every row, which he called "quite
        // bad". A constant fixes it too and costs the same on every row whatever the names are -- and a name
        // that does not fit wraps inside it rather than widening anything.
        var container = new Grid { Background = idle };
        container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CaptionWidth) });

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
        // Stretch, not Left, since the monitor groups align to the row's ENDS (#39) -- and the
        // right-hand end has to be the bar's, not this row's own content width, or a row with few
        // icons would push its second group to a different x than the row above it.
        //
        // Costs nothing on a bar that has no width of its own: SizeToContent makes the star column
        // exactly as wide as the widest row, so stretching to it changes nothing anyone can see.
        var icons = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            // One icon line's worth, even with no icons in it (#76), so an empty workspace's row is
            // the same height as a row holding one window instead of collapsing onto its label.
            //
            // A floor rather than a fixed height: a row with icons already exceeds it, and a row
            // whose icons WRAP has to be free to grow. On the pinned row it also makes the drop
            // target full height, which is the row's whole purpose.
            //
            // Applied to the icons rather than the row so the minimized transform still scales it:
            // #52 scales the entire row by a third, and a floor placed on the row itself would
            // fight that.
            MinHeight = IconLineHeight,
            // The indent for a nested row (#42) is taken out of the ICONS' space, not the row's,
            // so the row itself still spans the bar and the monitor alignment still lines up
            // across every row.
            Margin = nested ? new Thickness(NestedIndent, 0, 0, 0) : default,
        };

        // Built BEFORE the icons, which it did not used to be. Once the bar has a width the user
        // dragged, how many icons fit on a line depends on what the label leaves them -- and the
        // label's own width depends on nothing, so measuring it first is safe and settles the
        // question with no layout circularity. setHover is null exactly when this row has no
        // destination (see RowLabel), which keeps the Pinned and Unplaced rows inert below.
        var (label, setHover) = RowLabel(visualLabel, isCurrent, switchTo, caption: true);

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
        // Which icons OPEN a monitor group, decided in one pass over the whole row instead of
        // while rendering it. The width-driven wrap below has to know what every icon costs before
        // any line exists, and an icon that carries a marker costs 5 DIP more than one that does
        // not -- a line that ignored that would overflow by exactly as many markers as it holds.
        //
        // Keyed on RANK, not on the display number: rank 0 is the primary display and draws
        // nothing. Grouping and ordering still follow the display number, which is what Petre
        // asked for originally ("first icons from monitor1, then monitor2"); only how loudly each
        // group announces itself has changed.
        //
        // The comparison spans the whole row rather than resetting per line, which is the same
        // rule the `previous` variable this replaces carried, and for the same reason: a group
        // split by a wrap must not re-announce itself on the continuation line (Petre: "gepha
        // workspace, second line has a line in front").
        var opensGroup = ordered
            .Where((r, i) => showMonitorMarkers && r.MonitorRank.GetValueOrDefault(0) > 0 && r.Monitor.HasValue
                             && r.Monitor.Value != (i == 0 ? -1 : ordered[i - 1].Monitor.GetValueOrDefault(-1)))
            .Select(r => r.Window.Handle)
            .ToHashSet();

        // WRAPPED PER SCREEN, against that screen's own share of the row. Petre: "newline if icons can't
        // fit."
        //
        // This is the other half of putting the hairline dead centre. Removing the width floor made the
        // halves exactly equal, which also removed what used to protect an overfull half from being clipped
        // (#58: "icons in sparrow are not fully shown"). Wrapping against the whole row could not help,
        // because a half measuring itself against space the other half owns never reaches the limit -- the
        // same mistake #103's flanks made. Each screen's group gets its equal share of the row, minus the
        // marks, and runs onto another line when it fills.
        // The lanes are EVERY KNOWN SCREEN, not the screens this row happens to use, and getting that
        // wrong is what made the wrap look broken. Petre: "personal has 4 icons on the left screen, only
        // part of the fourth icon fits." All four sat on one screen, so this saw a single group and gave
        // it the whole row -- 175 DIP, room for seven -- while LineOf divided that row into two equal
        // halves of 83 anyway and clipped the fourth at 3.4 cells. The wrap was not failing to fire; it
        // was budgeting against a row width that no row is ever laid out at.
        //
        // So the divisor has to be the same one LineOf uses, which means asking the same question it
        // asks: markers on, and screens known? then a lane per screen. Otherwise the runs pack, and pack
        // without marks between them.
        //
        // Still unprotected on a bar that has never been resized (Width NaN, the fixed rule below): the
        // lanes are stars, WPF measures a star under infinite width as its content, and the bar then
        // arranges every row at the WIDEST row's width -- so a lopsided row can still be squeezed below
        // what it measured. It resolves itself the moment a width is stored, which happens on the first
        // resize and is restored from state afterwards.
        var groups = ordered.GroupBy(r => r.MonitorRank.GetValueOrDefault(0)).OrderBy(g => g.Key).ToList();
        var lanes = showMonitorMarkers && monitorByRank.Count > 0 ? monitorByRank.Count : Math.Max(1, groups.Count);
        var share = lanes <= 1
            ? IconRoom()
            : (IconRoom() - (showMonitorMarkers ? (lanes - 1) * MonitorMarkerWidth : 0)) / lanes;

        var perGroup = groups
            .Select(g => (double.IsNaN(Width)
                    // No width of its own yet: the fixed five-per-line rule, now applied per screen rather
                    // than per row, which is the same rule doing the same job in a narrower space.
                    ? IconRowLimit.Lines(g.ToList())
                    // The 3-icon floor is a rule about a LINE, and the line is split between lanes, so
                    // each lane gets its share of it. Undivided it outvoted the width it was measuring:
                    // a 61 DIP lane holds two cells, the floor said three, and the third was clipped.
                    : IconRowLimit.LinesThatFit(g.ToList(), _ => IconCellWidth, share,
                        Math.Max(1, IconRowLimit.MinimumIconsPerLine / lanes)))
                .Select(line => line.ToList()).ToList())
            .ToList();

        // One row line per deepest group, with each group contributing its own line at that depth. A group
        // that has run out simply adds nothing, so its half of that line is empty -- which is exactly what
        // the empty half already means everywhere else on this bar.
        var lineCount = perGroup.Count == 0 ? 1 : perGroup.Max(g => g.Count);
        var drawnLines = Enumerable.Range(0, lineCount)
            .Select(i => perGroup.SelectMany(g => i < g.Count ? g[i] : []).ToList())
            .ToList();

        // Recomputed from the LINES, and it has to be: `opensGroup` above marks the first icon of each group
        // across the whole row, which was right when a continuation line deliberately inherited its
        // neighbour's grouping. Now every line needs the same column structure or the middle would only hold
        // on the first one -- so on each line, the first icon of every group after the leftmost opens a
        // group, and gets the mark that divides the halves.
        var opensLine = drawnLines
            .SelectMany(line => line
                .Select((r, at) => (r, at))
                .Where(x => x.at > 0 && x.r.MonitorRank.GetValueOrDefault(0) != line[x.at - 1].MonitorRank.GetValueOrDefault(0))
                .Select(x => x.r.Window.Handle))
            .ToHashSet();

        // isLastLine carries #102's empty tail halves, which belong to the row rather than to each of its
        // wrapped lines.
        drawnLines.Select((line, at) => (line, last: at == drawnLines.Count - 1)).ToList()
            .ForEach(x => icons.Children.Add(LineOf(x.line, showMonitorMarkers ? opensLine : [], groupLabel, groupKey, rowKey, iconButtons, x.last)));

        // The parent's windows USED to be drawn here, dimmed, on every nested row -- the issue
        // asked for "everything from the main workspace pinned to the nested ones". Petre, seeing
        // it: "ugly. i don't want to see parent's windows in the children. only children but with
        // a better representation that it's a child."
        //
        // Right, and worth keeping the reason rather than just the deletion: a row's icons are
        // what you aim at, so doubling them halves the value of every one. Belonging is a
        // relationship, and a relationship is better drawn once at the edge of the row than
        // restated by copying its contents.
        Grid.SetColumn(icons, 0);
        container.Children.Add(icons);

        // Petre: "only children but with a better representation that it's a child", then "maybe a
        // little indented".
        //
        // Three signals, and they are deliberately all about the row's EDGE rather than its
        // contents -- the icons are what you aim at, and the first version proved that adding to
        // them costs more than it says:
        //
        //   * a spine down the left, in the PARENT's colour;
        //   * the parent's lane tint on the row itself (passed in as `tint`), so a family reads as
        //     one colour rather than as two neighbours that happen to touch;
        //   * a small indent on the icons.
        //
        // The spine spans both columns of the row and sits at its left edge, inside the indent the
        // icons give up -- so it lands in space that would otherwise be empty, and costs the row
        // nothing at all.
        if (nested && spine is not null)
        {
            var mark = new Border
            {
                Width = SpineWidth,
                Background = spine,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(1, 1, 0, 1),
                CornerRadius = new CornerRadius(1),
            };
            Grid.SetColumn(mark, 0);
            container.Children.Add(mark);
        }

        Grid.SetColumn(label, 1);
        container.Children.Add(label);

        // Hover freeze: hold this row's icon order while the pointer is inside it. Wired for
        // EVERY row, including the ones that can never re-sort (📌 Pinned and Unplaced have no
        // z-order): entering an inert row still has to RELEASE the row you came from, and routing
        // that through the same pair of handlers is what makes "the row under the pointer" the
        // whole rule, with no row that quietly does not count.
        //
        // `ordered` rather than `rows`: the capture must be what is on screen, which is this list.
        // Right-click a workspace row -> rename it, put a new one before or after it, or move it
        // (#40). Only on real workspaces: rowKey is null for 📌 Pinned, for unbound desktops and
        // for the Unplaced catch-all, none of which HAS a workspace to rename or reorder.
        //
        // On the container, so the whole row answers -- label, lane and the empty space between.
        // The icons keep their own menu and win over this one, because ContextMenuService opens
        // the menu of the INNERMOST element that has one, which is exactly the right split:
        // right-clicking an icon is about that window, right-clicking anywhere else on the row is
        // about the workspace.
        if (rowKey is { } workspaceId) container.ContextMenu = WorkspaceMenu(workspaceId, visualLabel, minimized, nested);

        // A third of the height, everything included (#52). Applied to the whole row and applied
        // LAST, so nothing built above has to know it is being drawn small.
        if (minimized) container.LayoutTransform = new ScaleTransform(MinimizedRowScale, MinimizedRowScale);

        container.Tag = new RowTag(groupKey);
        container.MouseEnter += (_, _) => EnterRow(groupKey, rowKey, ordered);
        container.MouseLeave += (_, _) => LeaveRow(groupKey);


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
            container.PreviewMouseLeftButtonDown += (_, _) => pressedRowKey = groupKey;

            container.AddHandler(Mouse.MouseUpEvent, new MouseButtonEventHandler((_, e) =>
            {
                if (e.ChangedButton != MouseButton.Left) return;
                // Stage 3: the release reached THIS row's handler. Both flags are logged whatever
                // they say, because "the release arrived and was declined" and "the release never
                // arrived" are the two halves this bug has to be split into, and only the first
                // one leaves a line here.
                if (ClickTrace.On)
                    ClickTrace.Write($"row-up group={groupLabel} consumedByChild={pressConsumedByChild} " +
                                     $"ourPress={pressedRowKey == groupKey} afterDrag={dragJustFinished} orphan={orphanRelease}");

                // The release a drag leaves behind, refused on its own evidence rather than on a leftover
                // flag. Cleared here because this is the release it belongs to.
                if (dragJustFinished)
                {
                    dragJustFinished = false;
                    return;
                }

                // An orphan release skips the two press-derived guards, because for an orphan they
                // describe the PREVIOUS click and can only mislead: that is how a click on Personal came
                // to do nothing while pressedRowKey still named a row he had already left. What is trusted
                // instead is where the release landed, which is this row -- the handler is on it -- and
                // whether a child of this row claimed the same release, which pressConsumedByChild now
                // only reports for handlers that ran after the orphan was detected.
                if (orphanRelease ? pressConsumedByChild : pressConsumedByChild || pressedRowKey != groupKey) return;
                // Stage 4: the switch itself, and its RESULT -- a failure here is invisible today
                // because Report only shows a dialog, and a switch refused by a stale
                // virtual-desktop COM object would look exactly like a click that did nothing.
                var outcome = switchTo();
                if (ClickTrace.On)
                    ClickTrace.Write($"switch group={groupLabel} ok={outcome.IsSuccess}{(outcome.IsFailure ? $" error={outcome.Error}" : "")}");
                Report(outcome);
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

                // #89: which SCREEN the pointer is over, when the row draws more than one.
                var screen = rowKey is { } key ? AimedMonitor(key, container, e.GetPosition(container)) : null;
                var ownRow = e.Data.GetData(DraggedWindow.DragFormat) is DraggedWindow d && d.SourceGroupKey == groupKey;

                // The half is highlighted, not the row, whenever a screen is being aimed at. On the
                // window's OWN row that half-highlight is the only feedback there is -- the row is not
                // changing, only the screen -- and on another row it answers "which half of which
                // row", which is the question a split row raises and the plain row highlight cannot.
                ArmAim(rowKey, container, e.GetPosition(container));
                container.Background = screen is null && !ownRow ? DropHighlight : idle;

                // The reserved info line doubles as the drop-target readout: on a bar this
                // small, "where will this land?" is otherwise pure guesswork -- rows are
                // ~28px tall and adjacent.
                Info.Text = (screen, ownRow) switch
                {
                    (null, true) => "→ drop past the hairline for the other screen",
                    (null, false) => $"→ move to {groupLabel}",
                    ({ } s, true) => $"→ move to screen {s}",
                    ({ } s, false) => $"→ move to {groupLabel}, screen {s}",
                };
            };
            container.DragLeave += (_, _) => { container.Background = idle; ClearAim(rowKey); ClearInfo(); };
            container.Drop += (_, e) =>
            {
                container.Background = idle;
                ClearAim(rowKey);
                ClearInfo();
                if (e.Data.GetData(DraggedWindow.DragFormat) is not DraggedWindow dragged) return;

                var at = e.GetPosition(container);
                var screen = rowKey is { } key ? AimedMonitor(key, container, at) : null;

                // #101: the highlight said one screen and the window went to another. Both sides compute
                // from AimedMonitor, so what they disagree about is not the code but the INPUT -- and the
                // only way to tell which is to record the geometry at the moment of release.
                if (ClickTrace.On)
                    ClickTrace.Write($"drop row={groupLabel} x={at.X:F0} y={at.Y:F0} aimed={screen?.ToString() ?? "none"} " +
                                     $"ownRow={dragged.SourceGroupKey == groupKey} zones={DescribeZones(rowKey, container)}");

                // Asked BEFORE the drop is carried out, because carrying it out is what changes the
                // answer. A window on a desktop you are not standing on cannot be moved on screen at
                // all (it is cloaked), so the move is held until you get there -- and without saying
                // so, the drop looks like it did nothing. Petre: "it doesn't work, not even beeper
                // now", on a run where every one of his drops was held and applied correctly later.
                var waits = screen is not null && manager.ScreenMoveWouldWait(dragged.Handle);

                // Dropped onto its own row. Past the hairline that is #89's monitor move with no desktop
                // change; short of it, it is the CLICK the user meant.
                //
                // That second half is #48's fourth mechanism, and the one Petre could finally reproduce:
                // "i clicked two edge icons in the taskspace left monitor, I had 2 misses". The drag
                // threshold is the system's four DIPs, which is six physical pixels at 150% scale, so a
                // quick click on a 20px icon exceeds it easily. The press then becomes a drag, the icon
                // never raises Click, the drop lands back where it started, and this branch used to
                // return without doing anything at all -- a click that vanished, roughly one in ten.
                //
                // Raising the threshold was the other candidate and is worse: it would make deliberate
                // short drags fail instead, and any threshold is a guess about the hand. Honouring the
                // intent costs nothing, because a drag that ends where it began asked for nothing else.
                if (dragged.SourceGroupKey == groupKey)
                {
                    if (screen is { } target)
                    {
                        Report(manager.MoveWindowToMonitor(dragged.Handle, target));
                    }
                    else
                    {
                        if (ClickTrace.On) ClickTrace.Write($"drop-as-click row={groupLabel} hwnd={dragged.Handle.Value:X}");
                        Report(manager.ToggleWindow(dragged.Handle, activator));
                    }
                }
                else
                {
                    onDrop(dragged.Handle, screen);
                }

                // Left on the info line rather than cleared, so the one gesture with no immediate
                // effect says what it is waiting for. The next hover or rebuild replaces it.
                if (waits && screen is { } held)
                    Info.Text = $"screen {held} applies when you go to {groupLabel}";
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
        var box = new Border
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
            BorderThickness = new Thickness(RowRingThickness),
            // Slightly rounded rather than square, matching the bar's own 8px corners. At one
            // pixel the difference is barely there; it just stops the corners looking sharper
            // than the window they sit in.
            CornerRadius = new CornerRadius(3),
            // Petre: "make spacing a little less between different workspaces." The caption adds a line to
            // every row, so the gaps between rows have to give some of it back or the bar grows by a third.
            // The negative vertical margin overlaps the neighbouring rows' 2px rings, which are transparent
            // on every row but the current one and so have nothing to lose.
            Margin = new Thickness(0, -1, 0, -1),
        };

        // Registered so the switch gesture can repaint this row's ring WITHOUT a rebuild. A tap
        // of the chord must not relayout anything -- the same rule WorkspaceSwitcher.Select
        // followed when the picker was a window -- and a rebuild here would be far worse than
        // sluggish: it makes a DesktopOf COM call per known window, on every tap.
        //
        // Keyed on the switch DESTINATION rather than the row's label, because names are not
        // unique (an unbound desktop can share a name with a workspace) and the gesture only
        // ever knows ids.
        if (rowKey is { } key) rowRings[key] = box;
        return box;
    }

    // Rebuilt from scratch on every RebuildCore, because the rows are. Anything holding a Border
    // from a previous build is holding an element that is no longer in the tree.
    readonly Dictionary<Guid, Border> rowRings = [];

    // ...and every icon on the bar, for the same reason and with the same lifetime.
    //
    // Each entry carries the icon's RESTING appearance rather than just the button, so
    // ApplyCandidate can repaint the whole set from facts instead of undoing whatever it wrote
    // last time. RowKey says which workspace the icon sits in (null for the pinned row and for
    // unbound desktops), which is how "landing here also changes workspace" is answered.
    readonly List<IconRing> iconRings = [];

    sealed record IconRing(Button Button, Guid? RowKey, WindowHandle Handle, bool IsActive, bool WillActivate);

    // The icon the pointer is resting on, held by WINDOW HANDLE and not by button (#67).
    //
    // Same rule as the candidate below and as pressedRowKey above: a rebuild throws every icon
    // away, and rebuilds fire on any window event, so a Button reference kept across one points at
    // an element that has left the tree. The handle survives, so the ring lands back on the same
    // app in the newly built row.
    WindowHandle? hoveredIcon;

    // (The candidate itself is the gesture state -- see `candidate` below. It is held HERE
    // rather than on a row, because a rebuild throws every row away and builds new ones, and
    // rebuilds fire on any window event: one landing mid-gesture would silently drop the ring.
    // RebuildCore re-applies it after building, which is the only thing that makes it survive.)

    // Begin / step / end of the Win+Ctrl+Tab gesture, driven by WorkspaceSwitchGesture.
    //
    // The bar shows the rings; the picker beside it shows the ordered LIST and names the chord.
    // Petre tried the bar carrying the whole gesture -- rings plus a number on every row -- and
    // rejected it ("this is bad"), then: "show the previous list but ONLY next to the floating
    // window", "also maintain the yellow rings, remove the numbers". So the two surfaces split
    // the job rather than duplicating it: the list answers "what order", the rings answer "which
    // row, right now", on the rows themselves where the answer is finally acted on.
    public void BeginSwitch()
    {
        candidate = null;
        ApplyCandidate();
    }

    // Just the one row: where releasing the chord right now would take you.
    //
    // It briefly showed the next three stops at three strengths, and Petre cut it back -- "i
    // think only the next should have the yellow ring". Right call: the picker standing beside
    // the bar already lists the walk in order, so the extra rings were the same fact told twice,
    // and the weaker two were the least legible copy of it.
    public void ShowCandidate(Guid workspaceId)
    {
        candidate = workspaceId;
        ApplyCandidate();
    }

    public void EndSwitch()
    {
        candidate = null;
        ApplyCandidate();
        ClearInfo();
    }

    Guid? candidate;


    // Repaint only: every registered row is set back to its resting ring, then the candidate is
    // painted over the top.
    //
    // The candidate WINS over the current-workspace ring on the row you are standing on, and
    // that is correct rather than an oversight -- while the chord is held the question on screen
    // is "where would releasing take me", and answering "you are already here" in that moment is
    // the more useful of the two.
    // Precedence is load-bearing: the candidate outranks "you are here", because while the chord
    // is held the question on screen is where releasing would take you. The two collide
    // constantly -- the walk starts on the row you are standing on.
    void ApplyCandidate()
    {
        // Every begin/step/end of the switch gesture comes through here, so this is the one place
        // the fade has to learn that the bar is being used from the KEYBOARD -- where the pointer
        // is elsewhere by definition, and a dimmed bar would hide the rings this method is
        // painting.
        UpdateFade();

        // Petre: "hover over a workspace row shows the yellow ring the switcher uses" (#41).
        //
        // The SAME brush as the chord's candidate, deliberately, because it is the same claim in
        // the same tense: "this is where you would land". The chord answers it for the keyboard,
        // hover answers it for the mouse, and inventing a second colour would split one meaning in
        // two.
        //
        // Precedence, and both halves of it matter:
        //
        //   * The CHORD outranks hover. While Win+Ctrl+Tab is held, exactly one row wears the ring
        //     by design, and the pointer may be resting anywhere at all -- possibly on a different
        //     row, which would then make two rows claim the same thing while only one of them is
        //     true.
        //   * Hover does NOT ring the row you are already on. Everywhere else the amber means
        //     "you would land here", and on the current row that is a lie: clicking it goes
        //     nowhere. The white "you are here" ring stays, which is the honest answer.
        var hovered = candidate is null ? hoveredRow?.RowKey : null;
        rowRings.ToList().ForEach(row =>
            row.Value.BorderBrush = row.Key == candidate ? CandidateRowRing
                : row.Key == currentRow ? CurrentRowRing
                : row.Key == hovered ? CandidateRowRing
                : Brushes.Transparent);

        // Petre: "when adding a ring to the next workspace, make the active window in it visible
        // clearly, possibly with the same strength as it is in the currently active workspace."
        // Then (#67): "ring the app that will be activated, like the workspace candidate ring."
        //
        // So the ring answers ONE question, on both surfaces at once: what exactly happens when
        // you commit. The row says which workspace you land in; the icon says which app comes to
        // the front. Row plus icon when both change, icon alone when only focus does -- which
        // falls out of the rules rather than needing a case of its own, because the row rule above
        // already refuses to ring the workspace you are standing in.
        //
        // The app that would be activated, in the order the gestures outrank each other -- the
        // same precedence the row ring uses, for the same reason:
        //
        //   * the chord's candidate row lands on the icon it would restore focus to;
        //   * failing that, the icon under the POINTER is itself the target, because clicking one
        //     activates that window and nothing else decides it;
        //   * failing that, a hovered row lands on its own restore-focus icon, exactly as the
        //     chord would.
        var target = candidate is { } landing
            ? iconRings.FirstOrDefault(i => i.RowKey == landing && i.WillActivate)?.Handle
            : hoveredIcon ?? iconRings.FirstOrDefault(i => i.RowKey == hoveredRow?.RowKey && i.WillActivate)?.Handle;

        // Amber, and the same amber as the row, because it is the same claim in the same tense.
        // It replaces the white the landing icon used to be promoted to, which was borrowed from
        // the ACTIVE-window marker and therefore said "this window has focus" about a window that
        // does not have it yet. The background stays, though: strength was the point of the
        // original request, and only the colour was wrong.
        //
        // Everything else is repainted to its resting state from the row's own facts, so a ring
        // cannot be left behind on an icon the pointer has moved off.
        //
        // Costs no layout: every icon already carries a 1px border, transparent when it has
        // nothing to say, precisely so gaining a marker cannot nudge a SizeToContent bar.
        iconRings.ForEach(icon =>
        {
            var ringed = target is { } t && icon.Handle == t;
            icon.Button.BorderBrush = ringed ? CandidateRowRing
                : icon.IsActive ? ActiveBorder
                : icon.WillActivate ? WillActivateBorder
                : Brushes.Transparent;
            icon.Button.Background = ringed || icon.IsActive ? ActiveBackground : Brushes.Transparent;
        });
    }

    // Which row RebuildCore drew as current, so ApplyCandidate can put the white ring back when
    // the amber one moves off it.
    Guid? currentRow;

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
    // A workspace's lane colour, diluted. WorkspacePalette gives an opaque "#RRGGBB"; painted at
    // full strength behind app icons on a translucent bar it would drown them, so the alpha is
    // dropped. The result still separates lanes at a glance, which is the request, without
    // competing with the icons or with the active-window highlight.
    //
    // Frozen for the same thread-affinity reason every other brush here is (see Frozen below),
    // and cached per colour because Rebuild runs on every window event: an unbounded number of
    // new brushes per rebuild would be wasteful, and the set of workspace colours is tiny.
    static readonly Dictionary<string, Brush> LaneTints = [];

    // 0x38, and #68 spent four rounds establishing that this is right rather than merely inherited.
    //
    // Raising it to 0xC0 does make the lanes unmistakably coloured -- a lane becomes mostly its own
    // colour instead of mostly background -- and Petre's verdict on seeing it was "still bad", then
    // "old colors were better, they were darker and better". Which settles the trade: the lane is
    // background for icons, and a background that wins the eye is a worse background even when it
    // is a better colour. Anything lighter than this and the row stops being a lane and starts
    // being a block of paint with icons on it.
    static Brush? LaneTint(string? color, int index) => Lane(color, index, 0x38);

    // The same lane colour at full strength, for the spine down the left of a nested row and the
    // outline around a family (#42). Deriving it from the same hex rather than picking a second
    // colour is the whole point: it has to read as "this belongs to the lane above", and two
    // colours cannot say that.
    static Brush? LaneAccent(string? color, int index) => Lane(color, index, 0xC8);

    // NULL for a row that has opted out of a lane colour (#68), which every caller already handles:
    // the tint, the spine and the family outline are all Brush? and all already have a "this row has
    // no colour" path, because an unreadable hex in a hand-edited state.json has always been able to
    // produce one.
    //
    // Takes the colour override rather than the workspace because for a grouped row it comes from the
    // GROUP (#90, Group.Color), and the resolution is the same either way: the override if there is
    // one, otherwise the palette entry for the position.
    static Brush? Lane(string? color, int index, byte alpha) =>
        WorkspacePalette.IsNone(color)
            ? null
            : Tint(WorkspacePalette.For(color, index < 0 ? 0 : index), alpha);

    // Split out of Lane so the colour picker can draw a swatch from a bare hex, with no workspace
    // to ask and no position to look up -- and so it shares the same cache, since the picker's
    // nine swatches are the same nine colours the lanes are already using.
    static Brush? Tint(string hex, byte alpha)
    {
        var key = $"{hex}:{alpha:X2}";
        if (LaneTints.TryGetValue(key, out var cached)) return cached;
        try
        {
            var solid = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(Color.FromArgb(alpha, solid.R, solid.G, solid.B));
            brush.Freeze();
            LaneTints[key] = brush;
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
    // `caption` is the per-row name sitting ABOVE the icons rather than beside them: smaller, and with its
    // line box clamped to the glyphs, which is what #91 established for the group header and is where most
    // of the height of small text otherwise goes. Same colours, so "this is the row you are on" still reads
    // the same way, and the same click target.
    (UIElement Element, Action<bool>? SetHover) RowLabel(string text, bool isCurrent, Func<Result>? switchTo, bool caption = false)
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
            FontSize = caption ? GroupHeaderFontSize : 10,
            Foreground = resting,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (caption)
        {
            // WRAPS inside the fixed gutter instead of widening it, which is the whole point of fixing the
            // width: "multiline if it doesn't fit in that small space".
            textBlock.TextWrapping = TextWrapping.Wrap;
            textBlock.TextAlignment = TextAlignment.Right;
            // The font's line box is taller than its letters, and on text this small that difference is most
            // of the height -- the same clamp the group header uses (#91), and on two lines it matters twice.
            textBlock.LineHeight = GroupHeaderFontSize + 2;
            textBlock.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        }

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
            // Petre: "white space between the right monitor tab and the caption of the workspace
            // is too big", "narrow that whitespace", then "better, make is yet smaller".
            //
            // 11px of gutter (5 padding + 6 margin) down to 2, in two passes with him looking at
            // each. It was invisible for as long as the icons were packed at the far left of the
            // row, because the space before a caption was then whatever the row did not use;
            // aligning the second monitor's group to the caption end (#39) put icons against it
            // for the first time and made it the widest deliberate gap on the bar.
            //
            // "not zero, but half of what it is" -- so 2.5, literally half the 5 it had reached,
            // and a fraction rather than a round 2 because on a 150% display it lands on a whole
            // device pixel anyway. Not zero: the artwork runs to the edge of its 20px cell, so a
            // name flush against it would touch the icon it sits beside.
            Padding = caption ? new Thickness(2.5, 0, 5, 0) : new Thickness(2.5, 1, 5, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 2, 0),
            // LEFT. Centred was tried first, on his own instruction, and rejected on sight: "captions in the
            // middle are bad, move to the left." A GROUP's caption stays on the right ("group name, sparrow,
            // on the right is good"), which is not inconsistent -- a group's name labels a box below it and
            // reads as its heading, while a workspace's name labels the strip of icons beside it and belongs
            // where reading starts.
            HorizontalAlignment = HorizontalAlignment.Stretch,
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
            HorizontalAlignment = HorizontalAlignment.Stretch,
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
            // The label's own Click, which is the OTHER route to the same switch. A press that
            // lands on the label and is lost leaves this line missing while the row-up line above
            // says consumedByChild=False -- which distinguishes "the Button never raised Click"
            // from "the row declined it".
            if (ClickTrace.On) ClickTrace.Write($"label-click {text}");
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

    // Petre: "when switching, instead of showing the workspaces, focus the floating window
    // instead and do a cycle over those workspaces, in different color" -- because "i need to
    // change my focus to it eventually to make sure i've landed on the correct workspace".
    //
    // That last sentence is the whole argument for deleting the popup picker: if the bar is
    // where he looks to confirm the landing anyway, a second surface listing the same
    // workspaces only makes his eyes cross the screen twice. The same move that deleted the
    // tray switcher panel and Manage's Windows tab once the bar covered their jobs.
    //
    // The SAME SHAPE as the current-workspace ring, in a different colour, and that pairing is
    // deliberate rather than convenient: the two mean the same kind of thing in different
    // tenses -- "you are here" and "you would land here" -- which is exactly the relationship
    // the icon markers already express (ActiveBorder against WillActivateBorder). A different
    // shape would read as unrelated information.
    //
    // Amber rather than a brighter white, because white is already taken twice over: by the
    // current row's ring and by the active-window icon outline. Full strength because it exists
    // only for the few hundred milliseconds the chord is held, and in that time it has to win
    // the eye against a bar already showing lane tints, icon highlights and a white ring.
    //
    // Exactly ONE row wears it. Two further stops at weaker alphas, and a tap-count number on
    // every row, were both built and both cut -- "i think only the next should have the yellow
    // ring". The picker standing beside the bar lists the walk in order, so anything here beyond
    // "this one, now" was the same fact told twice, in the less legible of the two places.
    static readonly Brush CandidateRowRing = Frozen(0xFF, 0xFF, 0xB7, 0x4D);

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

        // ...and hover -> ring (#67). The pointer resting on an icon makes THAT window the thing a
        // click would activate, so it is the thing that wears the ring -- and if it lives in the
        // workspace we are already in, the row above it stays unringed, because nothing about the
        // workspace would change. That case needs no code: the row rule already declines to ring
        // the current row.
        button.MouseEnter += (_, _) => { hoveredIcon = row.Window.Handle; ApplyCandidate(); };
        button.MouseLeave += (_, _) => LeaveIcon(row.Window.Handle);
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
            // Which half of the toggle this is belongs to the manager, NOT to the row: a row is a
            // snapshot, and both facts the decision needs (is this the window you are in, is it
            // already down) can be older than the click by the time it happens. See
            // WorkspaceManager.ToggleWindow for what that cost -- a minimized window that could
            // not be brought back.
            var outcome = manager.ToggleWindow(row.Window.Handle, activator);

            // Traced for the same reason the row's switch is (#48, and Petre again: "i've clicked on
            // specific icons with no success, sometimes"). Without this an icon click that does nothing
            // is an absence with nothing to be absent FROM: the press line says icon=True and then the
            // log goes quiet, whether the Click never fired or the activation was refused. The refusal
            // is invisible otherwise, since Report raises a dialog behind a topmost bar.
            if (ClickTrace.On)
                ClickTrace.Write($"icon-click app={row.Window.ProcessName} " +
                                 $"hwnd={row.Window.Handle.Value:X} active={row.IsActive} " +
                                 $"ok={outcome.IsSuccess}{(outcome.IsFailure ? $" error={outcome.Error}" : "")}");

            Report(outcome);
        };

        // Petre: "i also want to be able to drag them around across tabs" -- the same drag
        // source the switcher panel's rows use, so an icon dragged onto another row lands
        // through the identical AssignWindow/PinWindow/MoveToDesktop path. Sharing the
        // payload FORMAT with WindowGroupsView also means a drag started on the bar can be
        // dropped on the switcher panel (and vice versa) if both happen to be open.
        // onDragStarting clears the info line: the icon under the cursor never raises
        // MouseLeave once the modal drag loop owns the mouse, so nothing else would.
        // The two callbacks are the same fact told to two different features: the pointer is about
        // to stop being ours (the info line has to be dismissed by hand, since the icon under the
        // cursor raises no MouseLeave during a drag) and the bar must not fade while it is being
        // dragged ONTO (the OLE loop owns the mouse and the pointer is over another row).
        WindowDragSource.Attach(button, row.Window.Handle, groupKey,
            onDragStarting: () =>
            {
                ClearInfo();
                draggingWindow = true;
                UpdateFade();
                if (ClickTrace.On) ClickTrace.Write($"drag-start app={row.Window.ProcessName} from={groupLabel}");
            },
            onDragFinished: () =>
            {
                draggingWindow = false;
                UpdateFade();
                // #48, fourth visit. A drag swallows the press that started it -- DoDragDrop's modal
                // loop takes the mouse-up as a native message, so the icon never raises Click -- but the
                // ROW still receives a bubbling release afterwards, with no press of its own. The trace
                // caught one: a `row-up` with no `press` and no `up` before it.
                //
                // Left alone, that stray release is decided by whatever pressConsumedByChild happens to
                // hold from the click BEFORE the drag, so the same gesture either does nothing or
                // switches workspace depending on history. Both are wrong. It is refused explicitly
                // instead.
                dragJustFinished = true;
                if (ClickTrace.On) ClickTrace.Write($"drag-end app={row.Window.ProcessName}");
            });
        // The icon's half of the orphan-release repair. ButtonBase raises Click from a matched
        // press-and-release pair, so a release whose press never arrived produces no Click at all and the
        // icon is dead -- Petre: "even icon clicking is still not working sometimes". This handler stands
        // in for the Click that could not happen, and only then.
        //
        // handledEventsToo, because the release is routinely marked handled before it gets here, and it
        // runs BEFORE the row's own handler on the same bubbling route, so MarkPressConsumed keeps one
        // gesture from both activating the window and switching workspace.
        button.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left || !orphanRelease || dragJustFinished) return;
            MarkPressConsumed();

            var outcome = manager.ToggleWindow(row.Window.Handle, activator);
            if (ClickTrace.On)
                ClickTrace.Write($"icon-orphan-click app={row.Window.ProcessName} hwnd={row.Window.Handle.Value:X} " +
                                 $"ok={outcome.IsSuccess}{(outcome.IsFailure ? $" error={outcome.Error}" : "")}");
            Report(outcome);
        }), handledEventsToo: true);

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

    // Bright and saturated, and painted at FULL strength -- which is the difference from the lane
    // tints, now that those are bright too (#68). A lane colour is diluted to ~22% before it goes
    // behind an icon, because it must not compete with it; these are three-pixel slivers drawn ON
    // TOP of arbitrary artwork and have to survive it, so they keep all of their alpha. Ordered so
    // the first two -- by far the commonest case, two windows of one app -- are as far apart in hue
    // as the list allows.
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
    static FrameworkElement MonitorMarker(int rank)
    {
        // CENTRED in the fixed 3px box below, and that is a fix rather than a detail. Petre: "space
        // between the hairline and icons following on the right is not consistent across
        // workspaces."
        //
        // The box is a constant 3px so that a monitor-2 group and a monitor-3 group reserve exactly
        // the same width and no row's icons shift against another's. But the tally inside it is as
        // wide as its STROKE COUNT -- 1px for monitor 2, 3px for monitor 3 -- and a stretched stack
        // lays its children out from the left. So a single stroke sat hard against the left of its
        // box with the other 2px trailing after it as dead space, which lands entirely on the right
        // of the mark: two pixels before the hairline, five after it.
        //
        // Centring spends that space evenly, which is what "before and after the hairline space
        // should be the same" asked for and what the fixed-width box has been quietly undoing ever
        // since -- and it makes the gap identical whatever the monitor number, which is the half
        // that varies from row to row.
        var tally = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
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
    // Petre: "ability to rename existing and add new workspaces from within the floating window,
    // on top or under the current workspace, something like a insert before/after, also move
    // workspaces up or down, in the right click."
    //
    // All four already existed on Manage and in WorkspaceManager; what was missing was reaching
    // them from the surface actually being looked at. Until now right-clicking a workspace name
    // did nothing at all -- only ICONS carried a menu, and the bar's own background menu went with
    // "Hide floating bar".
    //
    // Deliberately NOT here: Remove. It is the one item that cannot be undone, this menu now opens
    // on a click that used to do nothing, and a mis-aimed right-click landing on "Remove
    // workspace" is a bad way to find that out. Manage still has it.
    ContextMenu WorkspaceMenu(Guid workspaceId, string name, bool minimized, bool nested)
    {
        var menu = new ContextMenu();
        HoldFadeWhileOpen(menu);

        // Read at CLICK time rather than captured when the menu was built: a rebuild between the
        // two is routine on this bar, and a stale index would insert relative to a row that has
        // since moved. -1 when the workspace has gone entirely, which Insert clamps to the end and
        // Move reports as "no longer exists" -- both better than acting on the wrong row.
        int IndexOf() => manager.State.Workspaces.ToList().FindIndex(w => w.Id == workspaceId);

        void Add(string glyph, string header, Action click)
        {
            var item = new MenuItem { Header = header, Icon = MenuGlyph(glyph) };
            item.Click += (_, _) => click();
            menu.Items.Add(item);
        }

        // Petre: "remove the word 'workspace', just insert before and after."
        //
        // Right: the menu only opens on a workspace row, so every item saying so again is the
        // context repeating itself in six places. The glyphs carry what is left.
        Add("✏", "Rename…", () =>
            PromptDialog.Ask("Rename workspace", "New name:", name, owner: this)
                .Tap(renamed => Report(manager.RenameWorkspace(workspaceId, renamed))));

        menu.Items.Add(new Separator());

        // The GROUP the row belongs to, read at CLICK time for the same reason IndexOf is (#74).
        // Petre: "before/after is relative to the row it was invoked on, at that row's depth" -- so
        // beside a grouped row the new workspace joins the same group, instead of landing ungrouped
        // in the middle of somebody's box, which is what it used to do.
        //
        // "Same depth" became "same group" with the group model, and that is one answer for both
        // kinds: an anchored group and an anchorless one are joined the same way.
        //
        // Null for a row that stands on its own, which is the previous behaviour unchanged.
        Guid? GroupOfRow() => manager.State.GroupOf(workspaceId)?.Id;

        // "on top or under the current workspace" -- before is this row's own index, after is the
        // one past it, and InsertWorkspace clamps both.
        Add("✚", "Insert before…", () => InsertAt(IndexOf(), GroupOfRow()));
        Add("✚", "Insert after…", () => InsertAt(IndexOf() + 1, GroupOfRow()));
        // Petre: "add child as a right click menu item" (#42). Only offered on a row that CAN be a
        // parent -- workspaces nest one level deep, so a child row does not offer to have children
        // of its own rather than offering it and refusing afterwards.
        if (!nested)
            Add("↳", "Add child…", () =>
                PromptDialog.Ask("New nested workspace", "Name:", owner: this)
                    .Tap(child => Report(manager.AddChildWorkspace(workspaceId, child))));

        AddGroupItems(menu, workspaceId, Add);

        menu.Items.Add(new Separator());

        // Petre: "a minimized row is about a third of the regular row height... the right-click
        // menu on a minimized row offers Unminimize to restore it."
        //
        // One item that flips rather than two that argue: a row is one or the other, and a menu
        // offering "Minimize" on an already-minimized row would be asking a question the row has
        // already answered. `minimized` comes from the row this menu was built for, and rows are
        // rebuilt whenever state changes, so it cannot go stale in a way the user could reach.
        Add(minimized ? "⊞" : "⊖", minimized ? "Restore row height" : "Minimize row",
            () => Report(manager.SetWorkspaceMinimized(workspaceId, !minimized)));

        // Petre: "add color picker in the right click context menu" (#68).
        //
        // Workspace.Color has been honoured since colours existed and there has never been a way to
        // SET it -- the only route was hand-editing state.json, which is what left "i don't like
        // the green for sparrow" with nothing to do about it but re-pick the whole palette for
        // everyone. A per-workspace choice is the answer to a per-workspace complaint.
        AddColourPicker(menu, workspaceId);

        menu.Items.Add(new Separator());

        // Out-of-range moves succeed as no-ops by design (see MoveWorkspace), so "Move up" on the
        // top row does nothing rather than popping an error at someone who clicked it. That is why
        // nothing here is disabled: a disabled item at the edge of a list is a smaller kindness
        // than a menu whose shape never changes.
        Add("▲", "Move up", () => Report(manager.MoveWorkspace(workspaceId, -1)));
        Add("▼", "Move down", () => Report(manager.MoveWorkspace(workspaceId, +1)));
        // Petre: "add move to the end and to the top". A reposition rather than a run of swaps:
        // see MoveWorkspaceTo, which persists and pulses once for the whole gesture instead of
        // once per row it passes.
        //
        // All four move a row among the rows it is drawn beside (#85), which for a member of a group
        // is the inside of its box: top and end mean top and end of the group, not of the bar. On a
        // group's ANCHOR they move the whole group, since the anchor cannot move within its own box.
        // The list length passed below is deliberately larger than either range; MoveWorkspaceTo
        // clamps it to the last position available.
        Add("⤒", "Move to top", () => Report(manager.MoveWorkspaceTo(workspaceId, 0)));
        Add("⤓", "Move to end", () => Report(manager.MoveWorkspaceTo(workspaceId, manager.State.Workspaces.Count - 1)));

        menu.Items.Add(new Separator());

        // Delete (#73), which REVERSES a ruling rather than adding to the menu, so the reason is
        // worth stating. Remove was deliberately kept off this menu: "it is the one item that
        // cannot be undone, this menu now opens on a click that used to do nothing, and a mis-aimed
        // right-click landing on 'Remove workspace' is a bad way to find that out."
        //
        // What changes the answer is the GUARD. A workspace holding windows now refuses to be
        // deleted, so the expensive mistake -- windows silently scattered onto a neighbouring
        // desktop by Windows' own desktop-merge behaviour -- is no longer reachable from here at
        // all. What remains reachable is losing a NAME, plus its rules and its placement memory,
        // for a workspace that was already empty.
        //
        // That is still irreversible, which is why it is last, behind its own separator, and asks.
        // Petre confirmed the scope of what goes: "deleting a named workspace also discards its
        // roster entry (placement memory), yes."
        Add("🗑", "Delete workspace…", () =>
        {
            var workspace = manager.State.Workspaces.FirstOrDefault(w => w.Id == workspaceId);
            if (workspace is null) return; // deleted from elsewhere while the menu sat open

            // Asked BEFORE the emptiness check, so the answer to a mis-click is always the same
            // dialog rather than sometimes a refusal from deeper in.
            //
            // The note only appears when something else actually happens to the group, because a
            // warning about something that is not happening is worse than no warning. What happens
            // depends on how much of the group is left, so the wording follows the two real cases
            // rather than promising one of them.
            var group = manager.State.GroupOf(workspaceId);
            var others = group is null ? 0 : manager.State.Workspaces.Count(w => w.GroupId == group.Id) - 1;
            var groupNote = (group, others) switch
            {
                (null, _) or (_, 0) => "",
                // A group of one is not a group, so the last one left stands alone.
                (_, 1) => $"\n\n'{group!.Name}' is left with one workspace, so the group is dissolved and that workspace stands on its own. It keeps its windows.",
                // Losing the anchor is what stops the borrowing, and only the anchor lends windows.
                _ when manager.State.IsAnchor(workspaceId) =>
                    $"\n\n'{group!.Name}' keeps its other {others} workspaces and its name, but they will no longer show this workspace's windows.",
                _ => $"\n\n'{group!.Name}' keeps its other {others} workspaces.",
            };

            if (MessageBox.Show(this,
                    $"Delete '{workspace.Name}'?\n\n" +
                    $"Its virtual desktop, its name, its rules and its placement memory all go. " +
                    $"This cannot be undone." + groupNote,
                    "TaskSpaces", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            // The refusal path reports through the same Report() as everything else, so "it still
            // has windows" arrives as a plain message rather than as a silent no-op.
            Report(manager.DeleteWorkspaceIfEmpty(workspaceId));
        });

        return menu;
    }

    // Petre: "add nice icons."
    //
    // A TextBlock rather than an image, because MenuItem.Icon takes any content and these want to
    // follow the menu's own foreground and size -- an image would have to be recoloured by hand
    // for a theme change that this app never asks about.
    //
    // Glyphs chosen from blocks Segoe UI and its symbol fallback have covered for as long as
    // Windows 10 has existed (Dingbats, Geometric Shapes, Supplemental Arrows-B), rather than from
    // Segoe MDL2 Assets: MDL2 has prettier icons and needs its own FontFamily plus private-use
    // codepoints, and a private-use codepoint that turns out wrong renders as a hollow box rather
    // than as anything recognisable.
    // The nine palette colours as a submenu, plus the way back out of one (#68).
    //
    // "By position" is not decoration: it writes NULL and hands the workspace back to
    // WorkspacePalette's by-position default. A picker with no way to un-pick would make the first
    // click permanent, and this menu is one mis-aimed right-click away at all times.
    //
    // The current choice is marked by RINGING its swatch rather than by MenuItem.IsChecked. WPF
    // draws the check in the same column as the Icon, so a checked item with an icon shows the
    // icon and swallows the tick -- and the icon here is the whole point, since the colour is what
    // is being chosen. The ring is the same white "you are here" marker the bar uses on its
    // current row, which is the same claim.
    // INLINE, not a submenu any more (#77). Petre: it "doesn't open aligned with the Colour menu
    // item" and "sometimes disappears before the mouse can reach it".
    //
    // Those two were one bug, as the issue guessed. Eleven stacked items is a tall flyout, the bar
    // lives against a screen edge, and WPF flips a submenu that does not fit -- so it opened away
    // from the item it belonged to, the pointer then had to cross dead space to reach it, and
    // leaving the parent item is exactly what closes a submenu.
    //
    // Rather than fight the placement, there is no flyout: nine swatches in one horizontal strip,
    // in the menu itself. It cannot be misplaced because it is not positioned, it cannot be missed
    // on the way because there is no way, and a palette is better read as colours side by side than
    // as a list of colour names anyway.
    //
    // On a row inside a group this is the GROUP's picker (#90). Petre: "the parent can change the
    // group's colour, and so can any child... a group has one colour". So the mark comes from the
    // group's own override and the choice goes to the group, which is also what stops a member's
    // colour leaking into the group it joins (#92).
    void AddColourPicker(ContextMenu menu, Guid workspaceId) =>
        AddColourPicker(
            menu,
            // Read now rather than captured from the row, because the row was built with a lane colour
            // that may have come from the palette by position -- what this menu needs is whether there
            // is an override, which only the state can answer.
            manager.State.GroupOf(workspaceId) is { } group
                ? group.Color
                : manager.State.Workspaces.FirstOrDefault(w => w.Id == workspaceId)?.Color,
            // SetWorkspaceColor does the redirect itself, so a member's choice lands on its group
            // whichever surface asked.
            colour => manager.SetWorkspaceColor(workspaceId, colour));

    // The same picker on an anchorless group's header (#84, #90), which is the only row of its own a
    // group without an anchor has.
    void AddGroupColourPicker(ContextMenu menu, Guid groupId) =>
        AddColourPicker(
            menu,
            manager.State.Groups.FirstOrDefault(g => g.Id == groupId)?.Color,
            colour => manager.SetGroupColor(groupId, colour));

    // A colour in force that the palette does not offer, which is what makes the Custom… swatch worth
    // drawing: null means by-position and the sentinel means transparent, and neither is a colour.
    static bool IsCustom(string? colour) =>
        !string.IsNullOrWhiteSpace(colour)
        && !WorkspacePalette.IsNone(colour)
        && !WorkspacePalette.Swatches.Any(s => string.Equals(s.Hex, colour, StringComparison.OrdinalIgnoreCase));

    void AddColourPicker(ContextMenu menu, string? chosen, Func<string?, Result> choose)
    {
        var strip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(2, 2, 2, 2),
        };

        WorkspacePalette.Swatches.ToList().ForEach(swatch =>
        {
            var button = new Button
            {
                Content = ColourSwatch(swatch.Hex, string.Equals(chosen, swatch.Hex, StringComparison.OrdinalIgnoreCase)),
                // Chrome-less for the same reason the row icons are: the stock template's hover
                // layer would sit on top of the colour being chosen.
                Template = BareButton,
                Background = Brushes.Transparent,
                Padding = new Thickness(2),
                // The name has to be reachable somehow now that it is not written next to the
                // swatch. A tooltip is the right place for it: needed once, while learning.
                ToolTip = swatch.Name,
            };
            button.Click += (_, _) =>
            {
                Report(choose(swatch.Hex));
                // Closed by hand. The wrapper below stays open on click so this handler runs at
                // all, which means nothing else is going to close it.
                menu.IsOpen = false;
            };
            strip.Children.Add(button);
        });

        // Wrapped in a MenuItem so it sits in the menu's own layout and takes its background,
        // rather than as a bare panel that MenuBase would wrap anyway with less control.
        //
        // StaysOpenOnClick is load-bearing: without it the menu closes on the way DOWN, before the
        // swatch button's Click is raised, and no colour is ever chosen -- the same
        // ButtonBase-consumes-the-release trap the rows hit in #48, arriving from the other side.
        menu.Items.Add(new MenuItem
        {
            Header = strip,
            StaysOpenOnClick = true,
        });

        // #97: the whole colour space, because the palette has no room left in it.
        //
        // Petre asked for more colours; measuring said there were none to add. At the lane's 22% alpha
        // over the bar's dark background, twenty dark candidates all landed closer to one of the shipped
        // nine than that palette's own closest pair (Denim 0.0045 from Steel, Crimson 0.0097 from Plum),
        // and the only additions that separate properly are bright ones -- the register he rejected four
        // times in #68. So rather than nine more colours picked by guesswork, he picks.
        //
        // The swatch shows the CURRENT colour when it is not one of the nine, so a custom colour is
        // visible as the choice in force rather than looking like nothing is selected.
        var custom = new MenuItem
        {
            Header = "Custom…",
            Icon = IsCustom(chosen) ? ColourSwatch(chosen!, chosen: true) : MenuGlyph("🎨"),
        };
        custom.Click += (_, _) =>
        {
            // Null is cancel, which must not be confused with the null that means "by position": one
            // leaves the colour alone, the other clears it.
            if (ColourDialog.Pick(new WindowInteropHelper(this).Handle, chosen) is { } picked)
                Report(choose(picked));
        };
        menu.Items.Add(custom);

        // Petre: "add transparent as an option for color". A row that opts out keeps its icons and
        // its label and simply has no lane behind them -- worth having on a bar where the tint's
        // whole job is grouping, since a workspace can be one you never need to pick out.
        //
        // Not the same as "By position" below: this is a CHOICE and survives a reorder, while by
        // position means "whatever my place in the list hands me".
        var transparent = new MenuItem
        {
            Header = "Transparent",
            Icon = EmptySwatch(WorkspacePalette.IsNone(chosen)),
        };
        transparent.Click += (_, _) => Report(choose(WorkspacePalette.None));
        menu.Items.Add(transparent);

        var byPosition = new MenuItem
        {
            Header = "By position",
            // No swatch, because there is no one colour to draw: it is whatever this row's position
            // in the list happens to hand it, today and after the next reorder.
            Icon = MenuGlyph(string.IsNullOrWhiteSpace(chosen) ? "●" : "○"),
        };
        byPosition.Click += (_, _) => Report(choose(null));
        menu.Items.Add(byPosition);
    }

    // Drawn at FULL strength, unlike the lane it will paint: a 12px square diluted to a lane's
    // alpha would be nine near-identical greys, and the menu has no icons behind it to protect.
    static UIElement ColourSwatch(string hex, bool chosen) => new Border
    {
        Width = 12,
        Height = 12,
        CornerRadius = new CornerRadius(3),
        Background = Tint(hex, 0xFF) ?? Brushes.Transparent,
        BorderBrush = chosen ? ActiveBorder : Brushes.Transparent,
        BorderThickness = new Thickness(2),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // "Transparent" has to show ABSENCE, so it is an outline with nothing inside it. A square
    // filled with the bar's own background would read as a very dark colour, which is a different
    // claim from having none at all.
    static UIElement EmptySwatch(bool chosen) => new Border
    {
        Width = 12,
        Height = 12,
        CornerRadius = new CornerRadius(3),
        Background = Brushes.Transparent,
        BorderBrush = chosen ? ActiveBorder : EmptySwatchEdge,
        BorderThickness = new Thickness(2),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // Grey rather than the white the chosen ring uses, so the outline reads as the shape of the
    // swatch and not as a selection.
    static readonly Brush EmptySwatchEdge = Frozen(0x80, 0x88, 0x88, 0x88);

    static UIElement MenuGlyph(string glyph) => new TextBlock
    {
        Text = glyph,
        FontSize = 13,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // `parentId` is the DEPTH the new workspace is created at (#74): the parent of the row the
    // menu was invoked on, so a sibling of a nested row is nested under the same workspace, and
    // null on a top-level row exactly as before.
    void InsertAt(int index, Guid? parentId) =>
        PromptDialog.Ask("New workspace", "Name:", owner: this)
            // Result<Workspace> converts implicitly to Result, which is all Report needs: it only
            // ever shows the ERROR, and the new Workspace has no reader here -- the bar redraws
            // off the pulse that creating it sent.
            .Tap(fresh => Report(manager.InsertWorkspace(fresh, index, parentId)));

    ContextMenu IconMenu(WindowRow row)
    {
        var menu = new ContextMenu();
        HoldFadeWhileOpen(menu);


        var rename = new MenuItem { Header = "Rename this window…", Icon = MenuGlyph("✏") };
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
        var renameApp = new MenuItem { Header = $"Rename all {row.Window.ProcessName} windows…", Icon = MenuGlyph("✎") };
        renameApp.Click += (_, _) => PromptDialog.Ask(
                $"Rename every {row.Window.ProcessName} window",
                "Short name to show on the taskbar (survives the app changing its own title):",
                row.Window.ProcessName, owner: this)
            .Tap(shortName => Report(manager.RenameApp(row.Window.Handle, shortName)));
        menu.Items.Add(renameApp);

        var restore = new MenuItem { Header = "Restore title", IsEnabled = row.OriginalTitle.HasValue, Icon = MenuGlyph("↺") };
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
        // Top follows the same rule as Left, for the same reason and now with the same weight: the
        // bar's height follows its content too, so the BOTTOM edge is what a restore has to
        // reproduce (see FloatingBarState.Bottom). Top is still read for files written before that
        // key existed, and still written, so nothing is lost by going back to an older build.
        var rawTop = stored switch
        {
            { Bottom: { } bottom } => bottom - ActualHeight,
            { } s => s.Top,
            null => work.Bottom - ActualHeight,
        };

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
    void AnchorFromPosition((double Left, double Top, double Right, double Bottom) work)
    {
        anchorRight = EdgeSnap.GrowsLeftwards(Left, work.Left) ? Left + ActualWidth : null;
        // The vertical twin (#50). Derived on the same pass, from the same fact -- where the bar
        // actually is -- so the two axes cannot disagree and neither is persisted as a decision
        // that could go stale.
        anchorBottom = EdgeSnap.GrowsUpwards(Top, work.Top) ? Top + ActualHeight : null;
    }

    // The screen x the bar's right edge is pinned to. Null until the bar has been positioned,
    // which is what stops the initial layout passes -- several of them, as rows are built and
    // the info line is measured -- from being mistaken for growth and dragging the bar
    // leftwards before it has been placed at all.
    double? anchorRight;

    // ...and the screen y its bottom edge is pinned to, on exactly the same terms (#50).
    double? anchorBottom;

    // Keeps the far edges still while the bar changes size, which is the whole ask: the bar is
    // parked in the bottom-right corner, so growing rightwards or downwards walks it off.
    //
    // SizeChanged rather than a recalculation inside Rebuild: SizeToContent means the new size is
    // only known once WPF has measured the new content, and Rebuild returns long before that.
    //
    // Both axes, and each independently, because either can move on its own. Height used to be
    // left alone -- defensible while it rarely changed, since windows arriving widen a row and
    // only occasionally add one. Two things since then made height move as freely as width: a
    // workspace can be inserted from the bar's own right-click menu (#40), and a row can be made
    // to wrap by dragging an edge (#36). Both grow the bar downwards from a fixed top edge, and
    // the bar's home is the bottom of the work area.
    void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (moving) return;
        if (MonitorBounds(Left, Top) is not { } work) return;

        // A null anchor means "pin the near edge", which is WPF's own behaviour, so that axis is
        // simply left where it is.
        var left = e.WidthChanged && anchorRight is { } right ? right - ActualWidth : Left;
        var top = e.HeightChanged && anchorBottom is { } bottom ? bottom - ActualHeight : Top;
        if (left.Equals(Left) && top.Equals(Top)) return;

        // Clamped like every other placement here: a bar grown larger than the work area cannot
        // keep its far edge AND stay on screen, and staying on screen wins.
        (Left, Top) = WorkAreaClamp.Clamp(left, top, ActualWidth, ActualHeight, work.Left, work.Top, work.Right, work.Bottom);
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
    // double.NaN is what Width reads as while the bar is still SizeToContent, and it is the honest
    // "no width chosen" here too -- persisting it as a number would freeze the bar at whatever its
    // content happened to measure on the day, which is the opposite of following the content.
    void Save() => manager.SaveFloatingBar(new FloatingBarState(Left, Top, true)
    {
        Right = anchorRight,
        Bottom = anchorBottom,
        Width = double.IsNaN(Width) ? null : Width,
    });

    // Owned by the bar for the same reason PromptDialog.Ask now takes an owner: this window
    // is Topmost, so an unowned message box can open behind it and strand the user with an
    // invisible modal.
    Result Report(Result result) => result.TapError(err => MessageBox.Show(this, err, "TaskSpaces"));
}
