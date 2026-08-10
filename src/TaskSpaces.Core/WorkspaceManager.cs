using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Overview;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rehydration;
using TaskSpaces.Core.Renaming;
using TaskSpaces.Core.Rules;

namespace TaskSpaces.Core;

// The heart of TaskSpaces: subscribes to window lifecycle events and applies the
// data-flow from the spec --
//   Appeared      -> workspace rule -> move to desktop -> add/move roster entry
//   Appeared      -> rename rule    -> apply short name (ledger keeps the original)
//   TitleChanged  -> renamed window -> re-apply short name (apps rewrite their titles)
//   TitleChanged  -> unplaced window -> late placement (rules re-run once the title
//                    reveals what the window actually is)
//   Disappeared   -> drop from live bookkeeping + ledger; roster entry SURVIVES (spec:
//                    a workspace lists what belongs to it even when nothing is running)
// Single-threaded by design: all events arrive on the UI dispatcher thread (WinEvent
// hooks deliver there), and all UI calls originate there too. No locks needed.
public sealed class WorkspaceManager(
    IVirtualDesktopService desktops,
    IWindowMonitor monitor,
    IWindowTitles titles,
    IPersistenceStore store,
    Func<DateTimeOffset>? clock = null,
    // Petre: "why isn't the taskspaces window in the floating window?" -- so WindowMonitor
    // stopped passing WINEVENT_SKIPOWNPROCESS and our own Manage window now flows through
    // this pipeline like anyone else's. Which means this class has to know which windows
    // are OURS, because three things it does to every other window must never happen to
    // one of ours. See IsOurs below. Injectable purely so tests can name a pid.
    int? ownProcessId = null,
    // Petre: "when switching workspaces, i want you to activate the window which was last
    // active last time this workspace was active." Optional and last so every pre-existing
    // caller and test compiles unchanged; null simply means "restore nothing", which is what
    // compatibility mode wants anyway (no desktops to switch between).
    IWindowActivator? activator = null,
    // Monitor number, minimised state and z-order for the bar. Optional and last for the usual
    // reason; null leaves every row's screen facts blank, which is what compatibility mode and
    // the pre-existing tests want.
    IScreenLayout? screenLayout = null,
    // Flashing taskbar buttons -- "somebody messaged me". Optional and last, as ever; null means
    // no window ever asks for attention, which is what compatibility mode and the existing tests
    // want.
    IAttentionMonitor? attention = null)
{
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.Now);
    readonly int ownProcess = ownProcessId ?? Environment.ProcessId;
    readonly Subject<Unit> stateChanged = new();
    readonly Dictionary<WindowHandle, WindowInfo> knownWindows = [];
    readonly Dictionary<WindowHandle, Guid> memberships = []; // window -> workspace

    // Windows Petre deliberately dragged OUT of every workspace onto a plain OS desktop
    // (MoveToDesktop). Without this, removing the membership alone would make the window
    // auto-placeable again, so the very next rule evaluation -- for a browser, its next
    // title change, i.e. seconds later -- would yank it straight back to the workspace he
    // just dragged it out of. Live-only, exactly like `memberships`: a restart re-derives
    // placement from the OS, and rules legitimately own a window again next launch.
    readonly HashSet<WindowHandle> detached = [];
    // The focused window, for the active-row highlight. Live-only: focus is a fact about
    // right now, so there is nothing to persist or reconcile.
    Maybe<WindowHandle> activeWindow = Maybe<WindowHandle>.None;

    // Petre: "when switching workspaces, i want you to activate the window which was last
    // active last time this workspace was active", and "so i know what i'm going to have
    // activated when i land on that workspace" -- the bar marks the same window, so this map
    // drives BOTH the restore and the marker, and they therefore cannot disagree.
    //
    // Keyed by DESKTOP id, not workspace id: Petre's own windows largely live on the unbound
    // "Main" desktop, so a workspaces-only version would not cover the desktop he uses most.
    //
    // Live-only, like `activeWindow` and the MRU above: which window you were last looking at
    // is a fact about this session, and a handle does not survive a restart anyway.
    readonly Dictionary<Guid, WindowHandle> lastActiveByDesktop = [];

    // Where we are RIGHT NOW, so a desktop change can stamp the desktop being LEFT. Kept here
    // rather than asked of the OS at switch time because by the time CurrentChanged fires,
    // CurrentDesktop() already reports the new one -- the outgoing id is only knowable if we
    // were already holding it.
    Maybe<Guid> currentDesktopId = Maybe<Guid>.None;

    // Windows whose taskbar button has flashed and that you have not looked at since. Petre:
    // "can you also identify if an app has something to say, a notification, and say it on the
    // icon?"
    //
    // Live-only, like everything else about the present moment. And a SET rather than a
    // timestamp, because Windows never says a flash has stopped (measured -- see
    // IAttentionMonitor): the end of attention is our rule, not an event, and the rule is that
    // looking at the window clears it.
    readonly HashSet<WindowHandle> wantsAttention = [];
    IDisposable? attentionSubscription;
    // Alt+Tab-style switching order. Live-only for the reason WorkspaceMru documents:
    // "where I was a moment ago" is a fact about this session.
    WorkspaceMru mru = WorkspaceMru.Empty;
    RenameLedger ledger = RenameLedger.Empty;
    IDisposable? subscription;
    // Separate from `subscription` above: window events and desktop-change events are two
    // different sources, and one must be disposable without the other.
    IDisposable? currentDesktop;

    public AppState State { get; private set; } = AppState.Empty;

    // Which WORKSPACE the user is standing in, or None (#53). None is a real, ordinary state
    // rather than an error: most of Petre's windows live on a desktop he never named, and time
    // spent there is attributed to nothing rather than being guessed onto a neighbour.
    //
    // Derived from currentDesktopId rather than asked of the OS, because those two can disagree
    // for exactly one moment -- during a switch -- and this is read by a timer that does not care
    // to know about that race (see the comment on currentDesktopId).
    public Maybe<Guid> CurrentWorkspaceId =>
        currentDesktopId.Bind(desktop => State.Workspaces.TryFirst(w => w.DesktopId == desktop).Map(w => w.Id));

    public IObservable<Unit> StateChanged => stateChanged.AsObservable();
    public IReadOnlyList<WindowInfo> KnownWindows => knownWindows.Values.ToList();

    public Result Start() =>
        LoadState()
            .Bind(Reconcile)
            .Tap(() =>
            {
                monitor.Snapshot().ToList().ForEach(w => knownWindows[w.Handle] = w);
                // Before anything else looks at where windows are: a crash while standing inside a
                // nested workspace leaves the parent's windows pinned to every desktop, and every
                // sweep and overview after that would be reading a machine we left in a borrowed
                // state (#42).
                RepairInheritedPins();
                // Seeded before the first UI build: foreground events only report CHANGES, so
                // without this the active-window highlight would stay blank from launch until
                // Petre next switched windows -- indistinguishable from the feature not working.
                activeWindow = monitor.Foreground();
                subscription = monitor.Events.Subscribe(OnWindowEvent);

                // Petre: "when i press the shortcut, it shows me the previous workspace which
                // was active." The switch was working; the UI was stale. Switch() changes the
                // desktop and pulses NOTHING, so every surface kept rendering the overview it
                // last built -- old workspace still bold as current, windows still grouped by
                // where they were before -- until some unrelated window event happened to pulse.
                //
                // Subscribed HERE rather than adding a pulse inside Switch() because that would
                // only cover switches WE perform. CurrentChanged fires for any means at all:
                // this app, Win+Ctrl+arrows, Task View. That is what the observable was declared
                // for, and until now nothing in the app consumed it.
                //
                // The same subscription also feeds the MRU, and that is the RIGHT place for
                // it: switching by Task View or Win+Ctrl+arrows is just as much a visit as
                // one we performed, and an Alt+Tab-style list that ignored those would send
                // the first tap somewhere Petre did not expect.
                currentDesktop = desktops.CurrentChanged.Subscribe(desktopId =>
                {
                    OnDesktopChanged(desktopId);
                    RememberVisit(desktopId);
                    stateChanged.OnNext(Unit.Default);
                });
                // Seed it with wherever we started, so the very first switcher tap of a session
                // already knows which workspace it is leaving.
                desktops.CurrentDesktop().Tap(id =>
                {
                    RememberVisit(id);
                    // Seeded for the same reason RememberVisit is: the FIRST switch away from
                    // wherever we launched must be able to stamp that desktop, and it can only
                    // do that if we were already holding its id.
                    currentDesktopId = id;
                });
                // Filtered to windows we actually track, so our own chrome and the shell's
                // helper windows can never light up a row -- and a flash for something with no
                // row would pulse every open surface for nothing.
                attentionSubscription = attention?.Flashed
                    .Where(w => knownWindows.ContainsKey(w))
                    .Subscribe(OnFlashed);

                ReapplyRenames();
                RestorePlacements();
            });

    // Finding 1 (reviewer, Critical): split out of Start() so the composition root can
    // load persisted state WITHOUT reconciling desktops or subscribing to the monitor --
    // needed for compatibility mode (Finding 2: still list workspaces read-only, no
    // desktop operations) and so a failed load can be distinguished from a failed
    // reconcile/subscribe. Deliberately does NOT touch `State` on failure -- a corrupt
    // store must never quietly become "empty workspace list" in the field the UI reads;
    // the caller (App) decides what to do (back up the corrupt file, inform the user,
    // retry) before anything gets a chance to persist over it.
    public Result LoadState() =>
        store.Load().Bind(s =>
        {
            // Migrated on the way in, before anything reads it. A state.json written while #42
            // shipped records nesting as ParentId on the child, and everything since groups reads
            // GroupId, so a file that skipped this would come up with every group dissolved.
            //
            // NOT persisted here. The next thing that changes anything writes the migrated shape,
            // and until then the file on disk stays readable by the build the user just upgraded
            // from. Migrated() is idempotent, so loading twice cannot double the groups.
            State = s.Migrated();
            stateChanged.OnNext(Unit.Default);
            return Result.Success();
        });

    // --- workspace <-> desktop reconciliation -------------------------------------
    // Desktops don't survive reboots and ids go stale across app restarts. For each
    // workspace: keep a still-valid DesktopId; else adopt a live desktop with the same
    // name; else create one. Runs once at startup.
    Result Reconcile() =>
        desktops.GetDesktops().Bind(live =>
        {
            var reconciled = State.Workspaces
                .Select(w => BindDesktop(w, live))
                .ToList();
            return reconciled.Combine()
                .Tap(workspaces => Persist(State with { Workspaces = workspaces.ToList() }));
        });

    Result<Workspace> BindDesktop(Workspace workspace, IReadOnlyList<DesktopInfo> live) =>
        live.Any(d => d.Id == workspace.DesktopId)
            ? workspace
            : live.TryFirst(d => d.Name.Equals(workspace.Name, StringComparison.OrdinalIgnoreCase))
                .Match(
                    adopted => Result.Success(workspace with { DesktopId = adopted.Id }),
                    () => desktops.Create(workspace.Name).Map(created => workspace with { DesktopId = created.Id }));

    // --- event pipeline -------------------------------------------------------------
    void OnWindowEvent(WindowEvent e)
    {
        switch (e.Kind)
        {
            case WindowEventKind.Appeared: OnAppeared(e.Window); break;
            case WindowEventKind.TitleChanged: OnTitleChanged(e.Window); break;
            case WindowEventKind.Hidden: OnHidden(e.Window); break;
            case WindowEventKind.Disappeared: OnDisappeared(e.Window); break;
            case WindowEventKind.Activated: OnActivated(e.Window); break;
            // Purely a redraw trigger, which is why it has no OnMoved of its own: a window
            // finishing a drag changes nothing this class owns -- not membership, not placement
            // memory, not the rename ledger. What it can change is which MONITOR the window is
            // on, and that is read fresh from the OS on every overview build, so the only thing
            // ever missing was a reason to build one.
            case WindowEventKind.Moved: stateChanged.OnNext(Unit.Default); break;
            // Same deal, same reason: a window going down or coming back up changes nothing this
            // class owns, and whether it is iconic is read fresh from the OS on every overview
            // build. All that was missing was a reason to build one -- without it the icon kept
            // whatever brightness it had when something unrelated last pulsed.
            case WindowEventKind.MinimizeChanged: stateChanged.OnNext(Unit.Default); break;
        }
    }

    void OnAppeared(WindowInfo window)
    {
        knownWindows[window.Handle] = window;

        // Fire-and-forget: the event pipeline has no caller waiting on a Result, and a
        // failed auto-placement (e.g. stale workspace, desktop move rejected) has
        // nowhere to surface here -- it's silently skipped, unlike the UI-facing
        // AssignWindow, which must propagate the same failure to the caller.
        // Precedence (Petre: "last placement beats rules, last placement IS the rule"):
        //   1. last placement  -- an explicit act by Petre, keyed by identity. Beats rules:
        //                        a standing guess must never yank back a window he moved
        //                        by hand. This is also what re-pins an app whose window
        //                        was destroyed and recreated (Electron closing to tray),
        //                        which the OS pin cannot survive since it is HWND-keyed.
        //   2. rule            -- first sight only: the one case memory cannot cover, since
        //                        a never-seen window has no placement to remember.
        //
        // There used to be a tier above both: a pending LAUNCH ("we started this app for
        // workspace W, expect its window"). It went with the restore prompt, the only thing
        // that ever launched anything.
        if (AutoPlaceable(window.Handle))
            Remembered(window)
                .Or(RulesEngine.MatchWorkspace(window, State.WorkspaceRules).Map(Placement.In))
                .Tap(remembered => ApplyPlacement(window, remembered));

        // Fire-and-forget for the same reason as above.
        RulesEngine.MatchRename(window, State.RenameRules)
            .Tap(shortName => { ApplyRename(window, shortName); });

        // Fix wave (reviewer, Important): pulse unconditionally, even when neither branch
        // above ran (no placement rule, no rename rule). Persist() above already pulses
        // when it fires, so this can double-pulse -- harmless, a tray-menu/panel rebuild
        // triggered by StateChanged is a cheap, idempotent re-read of current state. What
        // it fixes: an open panel/Windows tab must learn a new window appeared even when
        // nothing about it was auto-placed or renamed, or its row never shows up.
        stateChanged.OnNext(Unit.Default);
    }

    // Purely presentational: remember which window has focus so WindowsByWorkspace can mark
    // its row active. Deliberately does NOT place, rename or inventory anything -- focus
    // moving is not a statement about where a window belongs.
    //
    // Guarded on CHANGE: alt-tabbing fires foreground events continuously, and every pulse
    // makes each open surface rebuild, which costs one DesktopOf COM call per known window.
    // Pulsing only when the active window actually differs keeps a burst of alt-tabs from
    // turning into a burst of COM sweeps.
    void OnActivated(WindowInfo window)
    {
        knownWindows[window.Handle] = window;
        MarkActive(window.Handle);
    }

    // Petre: "sometimes, active window is not being updated... right now, vscode is active but
    // i am seeing a browser is active in the floating window. i want to always refresh what's
    // the active window, i can't afford to think that one window is active and another being
    // highlighted."
    //
    // The highlight used to hang off the Activated event ALONE, and that event cannot be
    // trusted to arrive: EVENT_SYSTEM_FOREGROUND is delivered WINEVENT_OUTOFCONTEXT through the
    // dispatcher's message queue, so a busy queue simply drops it (the identical lossiness that
    // made WindowMonitor.Resync necessary for the window list itself). One drop was permanent
    // damage rather than a blip, because the window that really has focus will not be activated
    // AGAIN while it already has focus -- so no future event was ever going to correct it.
    //
    // So the OS gets asked directly, on a timer. Same division of labour as everywhere else
    // here: the event is the fast path, the periodic re-read is the truth.
    //
    // Two properties this must preserve, both of them load-bearing:
    //
    //   * NEVER clear on None. Foreground() reports None for anything untracked -- the taskbar,
    //     the Start menu, and the floating bar itself (opted out by hwnd in WindowMonitor.
    //     Ignore). Those are precisely what Petre clicks while looking at the bar, so treating
    //     None as "nothing is active" would blink the highlight off every tick he touched it.
    //     Staying put matches the deliberate choice the event path already makes.
    //   * Pulse only on CHANGE, for the same reason OnActivated does: a pulse rebuilds every
    //     open surface, and each rebuild costs one DesktopOf COM call per known window. In the
    //     steady state -- which is almost always -- this is a GetForegroundWindow and a
    //     dictionary lookup, and nothing else happens at all.
    public void ResyncActiveWindow() => monitor.Foreground().Tap(MarkActive);

    // The one place activeWindow is written after startup, so the event path and the sweep can
    // never disagree about what "became active" means.
    void MarkActive(WindowHandle window)
    {
        // Looking at a window is what answers it, so this is also where attention ends. Done
        // before the change guard below, and that ordering matters: re-activating the window you
        // are already in is a no-op for the highlight but must still clear a flash that arrived
        // while you sat there.
        var settled = wantsAttention.Remove(window);
        if (activeWindow.Equals(Maybe<WindowHandle>.From(window)))
        {
            if (settled) stateChanged.OnNext(Unit.Default);
            return;
        }
        activeWindow = window;
        RememberActiveOn(window);
        stateChanged.OnNext(Unit.Default);
    }

    // Petre: "ctrl+win+tab now lands on the first window on the left screen", "that workspace
    // lost the active window".
    //
    // Recorded HERE, when a window becomes active, and NOT on the way out of a desktop, which is
    // what this replaces. The old placement was chosen to be cheap -- one stamp per switch rather
    // than a DesktopOf call per focus change -- and it raced the very event it depended on.
    //
    // From the probe log, leaving f69c9222 for f60a8bea:
    //
    //   record active=1045C desktopOf=f60a8bea leaving=f69c9222 match=False
    //
    // activeWindow ALREADY named a window on the destination. CurrentChanged comes from a
    // virtual-desktop COM event while foreground changes come from a WinEvent hook, and the
    // WinEvent wins: by the time we are told the desktop changed, Windows has moved focus to the
    // new desktop and we have seen it. So "whatever was active as we left" read the window we
    // were arriving AT. That is why match=False on nearly every switch, and it is not something a
    // stricter guard could rescue -- the fact was simply gone by the time it was asked for.
    //
    // A window's desktop is knowable exactly when it becomes active, so that is when to ask. Each
    // desktop's entry is then correct by construction and no ordering between the two event
    // sources can disturb it.
    //
    // The cost this was avoiding is not real at this scale: two COM calls on a CHANGE of
    // foreground, next to the stateChanged pulse on the very next line, which rebuilds every open
    // surface at one DesktopOf per known window.
    //
    // Pinned windows are skipped for the reason they always were: a pinned window is on every
    // desktop, so it is already wherever you land, and claiming it as one desktop's last-active
    // would promise a landing spot that was never in question.
    void RememberActiveOn(WindowHandle window)
    {
        if (desktops.IsPinned(window).GetValueOrDefault(false)) return;
        desktops.DesktopOf(window).Tap(desktop => lastActiveByDesktop[desktop] = window);
    }

    // A taskbar button flashed. Petre: "let's say somebody has messaged me, or vscode is asking
    // for my attention somewhere."
    //
    // HSHELL_FLASH repeats for as long as the window keeps flashing, so this is called many
    // times per notification. Pulsing only on the FIRST one keeps a flashing window from
    // rebuilding every open surface several times a second -- each rebuild costs a DesktopOf COM
    // call per known window.
    //
    // A window you are already looking at is ignored outright. Some apps flash regardless of
    // focus, and a dot on the icon you are currently in would be telling you to go somewhere you
    // already are.
    void OnFlashed(WindowHandle window)
    {
        if (activeWindow.Equals(Maybe<WindowHandle>.From(window))) return;
        if (wantsAttention.Add(window)) stateChanged.OnNext(Unit.Default);
    }

    void OnTitleChanged(WindowInfo window)
    {
        var previouslyUnknown = !knownWindows.ContainsKey(window.Handle);
        knownWindows[window.Handle] = window;
        if (previouslyUnknown) { OnAppeared(window); return; } // became taskbar-worthy late

        // Fire-and-forget: same rationale as OnAppeared -- no caller awaits this path.
        if (ledger.NeedsReapply(window.Handle, window.Title))
            ledger.AppliedName(window.Handle).Tap(name => { titles.Set(window.Handle, name); });
        else if (ledger.AppliedName(window.Handle).HasNoValue)
            // Not renamed yet -- but the new title may now match a rename rule.
            RulesEngine.MatchRename(window, State.RenameRules)
                .Tap(shortName => { ApplyRename(window, shortName); });

        // Late placement (spec): a window that appeared bare may only now reveal what it
        // is showing -- Rider loading a solution rewrites its title. Only UNPLACED windows
        // are eligible: once placed (rule, launch, or hand), a title change must never
        // teleport a window between workspaces (browsers rewrite titles every tab switch).
        //
        // Fix round 1 (reviewer, Important): ApplyRename's titles.Set produces a genuine
        // NAMECHANGE, which re-enters here as an "echo" carrying OUR OWN short name as
        // window.Title. A still-unplaced window must not have workspace rules run
        // against that synthetic title -- only titles the APP itself wrote are legitimate
        // placement signals. `ledger.AppliedName(...) != window.Title` guards this: if we
        // renamed this window and the observed title IS our applied name, skip late
        // placement entirely (no rename recorded, or observed title differs from what we
        // set, both fall through to the normal rule match).
        if (AutoPlaceable(window.Handle)
            && ledger.AppliedName(window.Handle).Map(applied => applied != window.Title).GetValueOrDefault(true))
            RulesEngine.MatchWorkspace(window, State.WorkspaceRules)
                .Tap(workspaceId => { Place(window, workspaceId); }); // fire-and-forget, as above
    }

    // Finding 3 (reviewer, Important): a window that merely left the taskbar (e.g.
    // Discord/Outlook minimizing to tray) still EXISTS -- its hwnd stays valid and it may
    // reappear later. Drop it from live-window bookkeeping exactly like Disappeared
    // (it's not on any desktop's visible taskbar right now, so tracking it as "known" or
    // "placed" would be misleading), but deliberately do NOT touch the rename
    // ledger: if we forgot the original title here, a later re-show's rename-rule
    // re-application would record our OWN short name as the "original", permanently
    // breaking restore. Only a genuine Disappeared (the window is actually gone) forgets
    // the ledger entry. RestoreAllTitles on app exit still finds and restores hidden
    // windows because their ledger entry -- and their hwnd -- are both still valid. The
    // roster (spec) is unaffected either way: it lists what BELONGS to a workspace, not
    // what's currently live, so Hidden never touches it.
    void OnHidden(WindowInfo window)
    {
        knownWindows.Remove(window.Handle);
        memberships.Remove(window.Handle); // roster entry stays -- that's the point (spec)

        // Fix wave (reviewer, Important): the live panel/Windows tab must lose this row
        // (it's no longer a live window) -- nothing else in this path calls Persist(), so
        // without this pulse the UI would keep showing a window that's gone to tray.
        stateChanged.OnNext(Unit.Default);
    }

    void OnDisappeared(WindowInfo window)
    {
        knownWindows.Remove(window.Handle);
        ledger = ledger.Remove(window.Handle);
        memberships.Remove(window.Handle); // roster entry stays -- ▶ Start relaunches it
        // A closed window's hwnd can be recycled by Windows for an entirely different
        // window later; a stale "detached" entry would silently exempt that new window
        // from rules. Cleared here for the same reason memberships is.
        detached.Remove(window.Handle);

        // Fix wave (reviewer, Important): same rationale as OnHidden above -- a closed
        // window's running-row must disappear from any open panel/Windows tab, and a
        // workspace header's running-count must drop, even though this path doesn't
        // otherwise call Persist().
        stateChanged.OnNext(Unit.Default);
    }

    // Returns Result: workspace-lookup and desktop-move failures must reach the caller
    // (Task 8's UI shows these). OnAppeared discards this deliberately (see comment
    // there); AssignWindow propagates it.
    Result Place(WindowInfo window, Guid workspaceId) =>
        Workspace(workspaceId)
            .Bind(w => w.DesktopId is { } desktopId
                ? desktops.MoveWindow(window.Handle, desktopId)
                : Result.Failure("Workspace has no desktop (compatibility mode)."))
            .Tap(() =>
            {
                memberships[window.Handle] = workspaceId;
                detached.Remove(window.Handle); // a workspace claims it again
                RosterAdd(window, workspaceId);
            });

    // Returns Result: a failed WM_SETTEXT (hung/closed window) must not leave a ledger
    // entry claiming the rename succeeded -- order matters here. We attempt the actual
    // write FIRST, and only update the ledger (which captures the original title) once
    // that succeeds; RenameWindow propagates the failure, OnAppeared/OnTitleChanged
    // discard it deliberately (see comments there).
    //
    // Refuses our OWN windows outright (see IsOurs): WPF owns the Manage window's Title, so
    // our WM_SETTEXT and its own binding would overwrite each other indefinitely. Guarded
    // HERE rather than at each of the four callers (Appeared, TitleChanged, the rule sweep,
    // and Petre right-clicking the icon) so the exemption cannot be forgotten by a fifth --
    // and it is a Failure, not a silent skip, so the one caller with a human waiting on it
    // says why instead of appearing to do nothing.
    Result ApplyRename(WindowInfo window, string shortName) =>
        IsOurs(window.Handle)
            ? Result.Failure("TaskSpaces does not rename its own windows.")
            : titles.Set(window.Handle, shortName)
                .Tap(() => ledger = ledger.Apply(window.Handle, window.Title, shortName));

    // --- UI-facing operations ---------------------------------------------------

    public Result Switch(Guid workspaceId) =>
        Workspace(workspaceId).Bind(w => w.DesktopId is { } id
            ? desktops.Switch(id)
            : Result.Failure("Workspace has no desktop (compatibility mode)."))
            // Recorded here AS WELL AS in the CurrentChanged subscription above, and the
            // redundancy is deliberate: Touch is idempotent (it moves an id to the front),
            // and this path must not depend on the OS raising a change notification we did
            // not ask for. The subscription covers switches made outside the app; this
            // covers the ones we make.
            .Tap(() => mru = mru.Touch(workspaceId));

    // Alt+Tab-style switching (Petre: "maybe an alt-tab like shortcut for me to switch
    // through workspaces"): the workspaces in most-recently-used order, plus where the
    // highlight should start. One call rather than two, because the index is only meaningful
    // against the very list returned alongside it.
    public RecentWorkspaces ByRecentUse()
    {
        var ordered = mru.Order(State.Workspaces);
        var current = desktops.CurrentDesktop();
        return new RecentWorkspaces(
            ordered,
            current.IsFailure ? -1 : ordered.ToList().FindIndex(w => w.DesktopId == current.Value));
    }

    // Petre: "i want it configurable". The chord that drives the switcher above.
    //
    // Every consumer reads it HERE rather than from AppState, because this is where the
    // fallback lives: blank, missing, or a hand-edited state.json holding nonsense all come
    // back as the default. A bad string in a file must never leave the app with no way to
    // switch workspaces at all -- and since it is validated on the way in (below), the only
    // route to a bad one is editing the file by hand.
    public string SwitcherShortcut =>
        Chord.Parse(State.SwitcherShortcut).IsSuccess
            ? State.SwitcherShortcut.Trim()
            : AppState.DefaultSwitcherShortcut;

    // Validated BEFORE it is persisted, which is the whole reason Chord.Parse returns a
    // Result rather than throwing: the editor gets to say exactly why "Ctrl+Bananas" is not
    // a shortcut, and nothing unusable ever reaches the file.
    // Stored in Chord's canonical spelling rather than verbatim, so "control + alt+1" and
    // "Ctrl+Alt+1" cannot sit in state.json as two different-looking versions of one chord.
    public Result SetSwitcherShortcut(string shortcut) =>
        Chord.Parse(shortcut)
            .Tap(chord => Persist(State with { SwitcherShortcut = chord.ToString() }));

    // Petre: "when switching workspaces, i want you to activate the window which was last
    // active last time this workspace was active."
    //
    // Both halves live in one method because they are two ends of the same move and the order
    // between them matters: the desktop being LEFT must be stamped before `currentDesktopId`
    // advances, or the stamp lands on the wrong desktop.
    //
    // Driven off CurrentChanged rather than Switch() so it covers switches made by ANY means --
    // our hotkey, the bar, Win+Ctrl+arrows, Task View -- for the same reason the MRU is fed
    // from here. A restore that only worked for our own switches would be the kind of
    // half-working that is worse than absent.
    // No longer stamps the desktop being left: that is done as focus moves, by RememberActiveOn,
    // because the stamp raced this very notification (see there). currentDesktopId is still
    // tracked, for the marker suppression on the desktop you are standing on.
    void OnDesktopChanged(Guid arriving)
    {
        currentDesktopId = arriving;
        // Before the focus restore, because this is what puts the parent's windows ON this desktop
        // -- and restoring focus to one of them only makes sense once it is here.
        ApplyInheritance(arriving);
        RestoreLastActive(arriving);
    }

    // --- a nested workspace borrows its parent's windows (#42) ---------------------------------
    //
    // Petre: "i want parent's windows to be present in the child workspace... but on the desktop."
    // Not icons on a row -- actually there, alongside the child's own windows.
    //
    // Windows' own pin is the only mechanism that puts one window on more than one desktop, and it
    // is ALL-OR-NOTHING: a pinned window is on every desktop, not on a chosen few. So "the parent's
    // windows, on its children only" is emulated by pinning them while you are inside a child and
    // unpinning them when you leave. The over-share exists, but only for as long as you are
    // standing in the subtree that wants it.
    //
    // Two facts make this survivable, and both had to be built rather than assumed:
    //
    //   * UNPINNING DOES NOT SEND A WINDOW HOME. It leaves it on whatever desktop is current, so
    //     unpinning while standing in the child would quietly MOVE the parent's windows into the
    //     child. Every release therefore unpins AND moves the window back to the desktop it was
    //     borrowed from.
    //   * A CRASH WOULD STRAND THEM pinned and homeless, which is precisely the failure this app
    //     avoids by being built on desktops rather than on hiding windows. So every borrow is
    //     written to state.json with its home desktop, and RepairInheritedPins puts them back at
    //     the next start.
    //
    // Kept live as well as persisted: the set is what the next switch releases, and reading it
    // back from state on every switch would be slower and no more correct.
    readonly Dictionary<WindowHandle, Guid> borrowed = [];

    void ApplyInheritance(Guid arriving)
    {
        var parentDesktop = ParentDesktopOf(arriving);

        // Standing somewhere that borrows nothing, or somewhere that borrows from a DIFFERENT
        // parent: either way, give back what is held before taking anything new.
        if (borrowed.Count > 0 && borrowed.Values.FirstOrDefault() != parentDesktop) ReleaseBorrowed();
        if (parentDesktop is not { } home || borrowed.Count > 0) return;

        // Every window we know to be on the parent's desktop right now. Asked of the OS rather
        // than of `memberships`, which records where a window was PLACED and not where it is.
        knownWindows.Keys
            .Where(w => desktops.DesktopOf(w).Map(d => d == home).GetValueOrDefault(false))
            .ToList()
            .ForEach(w => desktops.Pin(w).Tap(() => borrowed[w] = home));

        PersistBorrowed();
    }

    void ReleaseBorrowed()
    {
        borrowed.ToList().ForEach(b =>
        {
            // Order matters and is the whole trick: unpin first, then put it back. Unpinning alone
            // would leave the window on whichever desktop is current -- which, on a switch out of a
            // nested workspace, is the wrong one.
            desktops.Unpin(b.Key).Tap(() => desktops.MoveWindow(b.Key, b.Value));
        });
        borrowed.Clear();
        PersistBorrowed();
    }

    void PersistBorrowed() =>
        Persist(State with
        {
            InheritedPins = borrowed.Select(b => new InheritedPin(b.Key.Value, b.Value)).ToList(),
        });

    Guid? ParentDesktopOf(Guid desktop) =>
        State.Workspaces.FirstOrDefault(w => w.DesktopId == desktop)?.ParentId is { } parentId
            ? State.Workspaces.FirstOrDefault(w => w.Id == parentId)?.DesktopId
            : null;

    // Called once at startup. Anything still recorded as borrowed is the residue of a crash or a
    // kill while standing inside a nested workspace: unpin it and put it back where it came from.
    // A window that has since closed is simply dropped -- IsWindow is not asked, because Unpin and
    // MoveWindow both fail harmlessly for a dead handle and their Results are already ignored here.
    void RepairInheritedPins()
    {
        if (State.InheritedPins.Count == 0) return;
        State.InheritedPins.ToList().ForEach(pin =>
            desktops.Unpin(new WindowHandle((nint)pin.Window))
                .Tap(() => desktops.MoveWindow(new WindowHandle((nint)pin.Window), pin.HomeDesktop)));
        Persist(State with { InheritedPins = [] });
    }

    // ...and put focus back on arrival.
    //
    // Re-validated against the OS rather than trusted: the remembered window may have been
    // closed, or dragged to another desktop, since we left. DesktopOf answers both at once (it
    // fails outright for a dead hwnd), and a stale entry is DROPPED rather than merely skipped
    // so the bar's marker stops promising a landing spot that no longer exists.
    //
    // Best-effort by design, exactly like every other Activate call here. SetForegroundWindow
    // is only granted to a process that currently holds foreground rights, and on a switch WE
    // did not initiate (Win+Ctrl+arrows, Task View) we may well not -- in which case this
    // degrades to a taskbar flash rather than failing loudly.
    void RestoreLastActive(Guid arriving)
    {
        if (activator is null) return;
        Remembered(arriving).Or(() => FrontmostOnMainMonitor(arriving)).Tap(window => activator.Activate(window));
    }

    Maybe<WindowHandle> Remembered(Guid arriving)
    {
        if (!lastActiveByDesktop.TryGetValue(arriving, out var window)) return Maybe<WindowHandle>.None;
        if (desktops.DesktopOf(window).GetValueOrDefault() == arriving) return window;
        // Closed, or dragged to another desktop while we were away. Dropped rather than merely
        // skipped so the bar's marker stops promising a landing spot that no longer exists.
        lastActiveByDesktop.Remove(arriving);
        return Maybe<WindowHandle>.None;
    }

    // Petre: "if we don't have the information about what was the last active window on that
    // monitor, activate the foreground window on the main monitor... rather than not focus any
    // window."
    //
    // Applies on the first visit of a session to any desktop, which used to leave focus wherever
    // Windows happened to drop it.
    //
    // EVERY candidate is checked against the desktop we are arriving on, and that check is the
    // whole reason this method still works. Petre, on the first version: "i can't switch
    // workspaces anymore... it pushes me back to the current workspace."
    //
    // What went wrong is worth keeping, because the reasoning was plausible and wrong. That
    // version took no DesktopOf calls at all, on the argument that we have already ARRIVED by
    // the time this runs, so the un-cloaked windows a ScreenFacts snapshot reports must be this
    // desktop's windows. Cloaking is not synchronous with the desktop-change notification. The
    // snapshot still described the desktop we had just LEFT, so the fallback activated a window
    // over there -- and activating a window on another virtual desktop makes Windows follow it.
    // Every switch bounced straight back, which is indistinguishable from switching being
    // broken outright.
    //
    // So the arrival is now asked about rather than inferred. Note the shape this restores:
    // Remembered() has always validated its candidate this way, and the fallback was the one
    // path that did not.
    //
    // Narrowed to knownWindows as well, and that is not belt-and-braces either: ScreenLayout
    // enumerates through TopLevelWindows directly, which has never heard of WindowMonitor's
    // ignore list, so the floating bar is in that snapshot -- and being permanently topmost, it
    // heads it. Unfiltered, the fallback would hand focus to our own bar every time.
    Maybe<WindowHandle> FrontmostOnMainMonitor(Guid arriving) =>
        screenLayout is null
            ? Maybe<WindowHandle>.None
            : screenLayout.Snapshot()
                .OnPrimaryFrontToBack(knownWindows.Keys)
                .TryFirst(w => desktops.DesktopOf(w).GetValueOrDefault() == arriving);

    // A desktop became current: if it belongs to a workspace, that is a visit.
    void RememberVisit(Guid desktopId) =>
        State.Workspaces.TryFirst(w => w.DesktopId == desktopId).Tap(w => mru = mru.Touch(w.Id));

    // Floating-bar fix round 6 (Petre: the bar must "show tabs from all workspaces" --
    // including windows on UNBOUND desktops like his "Main"): a desktop group's label
    // needs a click-to-go-there affordance just like a workspace label, but Switch()
    // above takes a WORKSPACE id. This is the raw-desktop counterpart for
    // Overview.DesktopGroup rows -- same delegate, no persistence (like Switch, "current
    // workspace" is always derived live from CurrentDesktop, never stored).
    public Result SwitchToDesktop(Guid desktopId) => desktops.Switch(desktopId);

    // CycleWorkspace(direction) and SwitchToIndex(index) used to live here, driving
    // Ctrl+Alt+arrows and Ctrl+Alt+1..9. Both are gone with those chords (Petre: "i don't
    // think we need ctrl+alt and those, ctrl+tab is good enough"), and removed rather than
    // left as unreachable public API so nothing has to wonder later which switching path is
    // the live one. There are now exactly two: Switch(workspaceId) and SwitchToDesktop.
    //
    // If keyboard direct-jump comes back it should bind a NAMED chord to a workspace's id
    // (Workspace.Shortcut and Chord already exist for it) rather than to its list position,
    // which is the wart that made SwitchToIndex change meaning whenever anyone reordered the
    // Workspaces tab. And MRU walking already replaces cycling: see ByRecentUse.
    public Result<Workspace> AddWorkspace(string name) =>
        InsertWorkspace(name, State.Workspaces.Count);

    // Petre: "ability to rename existing and add new workspaces from within the floating window,
    // on top or under the current workspace, something like a insert before/after."
    //
    // The same creation as AddWorkspace, which is now a call to this one with the end as the
    // index -- rather than a second copy of the validate/create/persist chain that would have to
    // be kept in step with it.
    //
    // The index is OURS, not Windows'. desktops.Create always appends a virtual desktop at the end
    // of the shell's own list, and nothing here tries to reorder that: the bar's rows come from
    // State.Workspaces, so this list's order is the only one that has ever been visible. The two
    // have always been free to disagree.
    //
    // Clamped rather than validated, because every caller derives the index from a row that was on
    // screen when the menu opened, and a workspace removed in between should land the new one at
    // the end instead of raising an error at somebody who asked for something reasonable.
    // `parentId` makes the new workspace a SIBLING of the row it was invoked on rather than a
    // top-level workspace (#74). Petre: "before/after is relative to the row it was invoked on, at
    // that row's depth."
    //
    // Null for a top-level row, which is the whole previous behaviour and why it defaults.
    //
    // The parent is validated exactly as NestWorkspace and AddChildWorkspace validate it -- it must
    // exist and must not itself be nested -- rather than trusted because a caller derived it from a
    // row. A UI that only offers legal choices is not a guarantee; it is a UI.
    public Result<Workspace> InsertWorkspace(string name, int index, Guid? parentId = null) =>
        Result.FailureIf(string.IsNullOrWhiteSpace(name), "Workspace name required")
            .Bind(() => Result.FailureIf(NameTaken(name, excluding: null), $"A workspace named '{name.Trim()}' already exists."))
            .Bind(() => parentId is { } parent
                ? Workspace(parent).Bind(p => p.ParentId is not null
                    ? Result.Failure($"'{p.Name}' is itself nested. Workspaces nest one level deep.")
                    : Result.Success())
                : Result.Success())
            .Bind(() => desktops.Create(name))
            .Map(d => new Workspace(Guid.NewGuid(), name, d.Id) { ParentId = parentId })
            .Tap(w => Persist(State with { Workspaces = Inserted(w, Math.Clamp(index, 0, State.Workspaces.Count)) }));

    IReadOnlyList<Workspace> Inserted(Workspace workspace, int index)
    {
        var next = State.Workspaces.ToList();
        next.Insert(index, workspace);
        return next;
    }

    // Reviewer (fix round 1, Critical): duplicate names used to be unchecked, so two
    // workspaces could share a name; ManageWindow.OnSaveRules' `ToDictionary(w => w.Name)`
    // then threw ArgumentException with no handler, killing the process before renamed
    // titles could be restored. Guarded here (case-insensitive, trimmed) -- the ROOT CAUSE
    // fix -- with a defense-in-depth duplicate-safe dictionary added in ManageWindow too,
    // and a last-ditch DispatcherUnhandledException handler in App as a backstop.
    public Result RenameWorkspace(Guid id, string name) =>
        Result.FailureIf(string.IsNullOrWhiteSpace(name), "Workspace name required")
            .Bind(() => Workspace(id))
            .Bind(w => Result.FailureIf(NameTaken(name, excluding: id), $"A workspace named '{name.Trim()}' already exists.")
                .Map(() => w))
            .Tap(w => { if (w.DesktopId is { } d) desktops.Rename(d, name); })
            .Tap(w => Persist(State with
            {
                Workspaces = State.Workspaces.Select(x => x.Id == id ? x with { Name = name } : x).ToList(),
            }));

    // Petre: "i need to be able to move workspaces up or down in the manage window".
    // delta is -1 (up) or +1 (down). This one list drives the floating bar's row order and,
    // through WorkspacePalette, each workspace's lane colour -- so persisting it here is all
    // the other surfaces need. It no longer decides what any shortcut does: Ctrl+Alt+1..9 bound
    // by position and has been removed, which is exactly why reordering is now safe.
    //
    // Out-of-range moves SUCCEED as no-ops rather than failing: the Up button on the first
    // row should do nothing, not raise an error dialog at someone who clicked it.
    public Result MoveWorkspace(Guid id, int delta) =>
        MoveAmongPeers(id, from => from + delta, clampIntoRange: false);

    // Petre: "menu options: add move to the end and to the top."
    //
    // A REPOSITION, not a run of swaps, and the difference is visible: bubbling a workspace to the
    // top one swap at a time reaches the same order but persists and pulses once per step, so
    // every open surface rebuilds N times and state.json is rewritten N times for one gesture.
    //
    // The workspaces it passes keep their relative order, which is what "move to the top" means --
    // taking it out and putting it back is the whole operation, and the rest close the gap.
    //
    // Clamped like InsertWorkspace, and for the same reason: the caller derives the target from a
    // list that may have changed since the menu opened, and landing at an end is a better answer
    // than an error dialog.
    // `index` is a position AMONG PEERS, not in the whole list, so "move to top" on a nested
    // workspace means the top of its group. Callers that mean "the end" may pass any number past
    // the last peer, including the full list's length, and it is clamped.
    public Result MoveWorkspaceTo(Guid id, int index) =>
        MoveAmongPeers(id, _ => index, clampIntoRange: true);

    // Both moves reorder a workspace among its PEERS, which is the fix for #85: Petre reported that
    // "move up / move down on a workspace inside a group doesn't work".
    //
    // They used to move it one place in the flat list, which predates nesting. The bar re-groups
    // rows by parent when it draws them, so moving a child one place up swapped it with its parent
    // in the list and changed nothing at all on screen: the child was still the first child. Moving
    // a top-level workspace had the mirror problem, swapping it with somebody else's child and
    // leaving the top-level order alone.
    //
    // Peers means "same ParentId", so a top-level workspace moves among top-level workspaces and a
    // nested one moves among its siblings. Both cases go through here rather than being special
    // cases of each other.
    //
    // Every non-peer keeps its exact slot in the list (see PeersReordered), which matters for two
    // reasons: moving a child cannot disturb another group, and lane colours follow list position,
    // so nothing outside the group changes colour. Within a group nothing changes colour either,
    // because a nested row wears its parent's colour rather than its own.
    Result MoveAmongPeers(Guid id, Func<int, int> targetOf, bool clampIntoRange)
    {
        if (State.Workspaces.FirstOrDefault(w => w.Id == id) is not { } workspace)
            return Result.Failure("Workspace no longer exists.");

        var slots = PeerSlots(workspace.ParentId);
        var from = slots.FindIndex(slot => State.Workspaces[slot].Id == id);
        var to = targetOf(from);

        // Out of range is a SUCCESSFUL no-op for the up/down buttons, unchanged from before: "Move
        // up" on the first row should do nothing rather than raise an error at someone who clicked
        // it. Move-to-top/end clamps instead, since an end is what it was asking for anyway.
        if (clampIntoRange) to = Math.Clamp(to, 0, slots.Count - 1);
        else if (to < 0 || to >= slots.Count) return Result.Success();
        if (to == from) return Result.Success();

        Persist(State with { Workspaces = PeersReordered(slots, from, to) });
        return Result.Success();
    }

    // The positions in State.Workspaces held by everything sharing this parent, in list order.
    List<int> PeerSlots(Guid? parentId) =>
        State.Workspaces.Select((w, slot) => (w, slot)).Where(x => x.w.ParentId == parentId).Select(x => x.slot).ToList();

    // Reorders only the workspaces sitting in `slots`, writing them back into those same slots.
    //
    // A plain remove-and-insert on the whole list would drag a moved child across other groups'
    // positions and shuffle their colours. Permuting the occupants of a fixed set of slots keeps
    // every other row exactly where it was.
    IReadOnlyList<Workspace> PeersReordered(List<int> slots, int from, int to)
    {
        var peers = slots.Select(slot => State.Workspaces[slot]).ToList();
        var moved = peers[from];
        peers.RemoveAt(from);
        peers.Insert(to, moved);

        var next = State.Workspaces.ToList();
        slots.Select((slot, position) => (slot, position)).ToList().ForEach(x => next[x.slot] = peers[x.position]);
        return next;
    }

    // Petre: "minimized workspace rows: right-click to shrink a row to a third of its height",
    // and "the right-click menu on a minimized row offers Unminimize to restore it".
    //
    // Presentational, like the active-window highlight and unlike most of its neighbours here: it
    // changes how tall a row is DRAWN and nothing else. Membership, placement and the roster are
    // untouched, and a minimized workspace is still switched to, dropped on and walked by the
    // chord exactly as before.
    //
    // Idempotent by construction -- setting it to what it already is still persists and pulses,
    // which costs one redraw and saves a special case that could only ever fire on a menu click
    // nobody makes twice.
    // Petre: "a workspace can be nested under a main (parent) workspace... it lives under the main
    // one rather than sitting as a peer in the flat list" (#42).
    //
    // Three refusals, and each one is a shape the model would otherwise accept and the UI could
    // never draw:
    //
    //   * ITSELF. A workspace parented to itself is a cycle of length one, and every walk over the
    //     tree would run forever.
    //   * A workspace that is ALREADY NESTED. One level only (see Workspace.ParentId): the bar is
    //     ten rows tall and a second level of indentation is unreadable without collapsing, which
    //     is an interaction nobody has asked for.
    //   * A workspace that HAS CHILDREN. The mirror of the above -- nesting a parent under someone
    //     else would create the second level from the other end.
    //
    // Deliberately NOT refused: nesting a workspace that has windows, or that is current. Both are
    // ordinary, and neither means anything different afterwards.
    public Result NestWorkspace(Guid child, Guid parent) =>
        child == parent
            ? Result.Failure("A workspace cannot be nested under itself.")
            : Workspace(child).Bind(_ => Workspace(parent))
                .Bind(target => target.ParentId is not null
                    ? Result.Failure($"'{target.Name}' is itself nested. Workspaces nest one level deep.")
                    : Result.Success())
                .Bind(() => State.Workspaces.Any(w => w.ParentId == child)
                    ? Result.Failure("That workspace has workspaces nested under it. Workspaces nest one level deep.")
                    : Result.Success())
                .Tap(() => Persist(State with
                {
                    Workspaces = State.Workspaces.Select(w => w.Id == child ? w with { ParentId = parent } : w).ToList(),
                }));

    // Petre: "add child as a right click menu item" (#42).
    //
    // Create-and-nest in one call, and inserted directly AFTER the parent's existing children
    // rather than at the end of the list -- a child created from a row should appear under that
    // row, not at the bottom of the bar where the eye has to go looking for it.
    //
    // Not a convenience wrapper the UI could have assembled itself: two persists would pulse
    // twice, and the intermediate state -- a new top-level workspace that exists for one frame
    // before becoming a child -- would be visible on the bar as a row that jumps.
    public Result<Workspace> AddChildWorkspace(Guid parent, string name) =>
        Workspace(parent)
            .Bind(p => p.ParentId is not null
                ? Result.Failure<Workspace>($"'{p.Name}' is itself nested. Workspaces nest one level deep.")
                : Result.Success(p))
            .Bind(_ => Result.FailureIf(string.IsNullOrWhiteSpace(name), "Workspace name required"))
            .Bind(() => Result.FailureIf(NameTaken(name, excluding: null), $"A workspace named '{name.Trim()}' already exists."))
            .Bind(() => desktops.Create(name))
            .Map(d => new Workspace(Guid.NewGuid(), name, d.Id) { ParentId = parent })
            .Tap(child => Persist(State with { Workspaces = Inserted(child, AfterLastChildOf(parent)) }));

    // One past the parent, then one past each child it already has.
    int AfterLastChildOf(Guid parent) =>
        State.Workspaces.ToList().FindIndex(w => w.Id == parent) is var at && at < 0
            ? State.Workspaces.Count
            : at + 1 + State.Workspaces.Count(w => w.ParentId == parent);

    // Back to the top level. Its own method rather than NestWorkspace(child, null), because
    // "un-nest" is a thing a person does and a nullable parameter is a thing a compiler accepts.
    public Result UnnestWorkspace(Guid child) =>
        Workspace(child).Tap(_ => Persist(State with
        {
            Workspaces = State.Workspaces.Select(w => w.Id == child ? w with { ParentId = null } : w).ToList(),
        }));

    public Result SetWorkspaceMinimized(Guid id, bool minimized) =>
        Workspace(id).Tap(_ => Persist(State with
        {
            Workspaces = State.Workspaces.Select(w => w.Id == id ? w with { Minimized = minimized } : w).ToList(),
        }));

    // The update check's opt-out (#71). Petre: "an opt-out setting -- this would be the app's only
    // phone-home behaviour."
    //
    // A no-op when the value has not actually changed, and that is not micro-optimisation: Manage
    // sets the checkbox from state when it opens, which raises Checked, which lands here. Without
    // this, merely opening the window would write state.json and pulse every subscriber -- one of
    // which rebuilds the whole bar.
    public Result SetCheckForUpdates(bool enabled) =>
        State.CheckForUpdates == enabled
            ? Result.Success()
            : Result.Success().Tap(() => Persist(State with { CheckForUpdates = enabled }));

    // The colour picker on the bar's right-click menu (#68). Petre: "add color picker in the right
    // click context menu."
    //
    // NULL is a real argument and not a missing one: it clears the override and hands the workspace
    // back to WorkspacePalette's by-position default. Without that there would be no way back from
    // a colour once chosen except hand-editing state.json, which is the state this issue found the
    // app in.
    //
    // The hex is stored exactly as given rather than parsed here: Core has no colour type, and the
    // bar already treats an unreadable value as "no tint" rather than as a crash (see
    // FloatingBar.Lane), which is the right answer for a file a user can edit anyway.
    public Result SetWorkspaceColor(Guid id, string? color) =>
        Workspace(id).Tap(_ => Persist(State with
        {
            Workspaces = State.Workspaces.Select(w => w.Id == id ? w with { Color = color } : w).ToList(),
        }));

    // Case-insensitive, trim-tolerant name collision check. `excluding` lets
    // RenameWorkspace allow renaming a workspace to (a variant of) its own current name --
    // it must only reject collisions with *other* workspaces.
    bool NameTaken(string name, Guid? excluding) =>
        State.Workspaces.Any(w => w.Id != excluding && w.Name.Trim().Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    // Removing a workspace never removes its desktop implicitly -- windows live there.
    // The desktop merge behavior (Windows moves its windows to the previous desktop)
    // is exactly what we want, so removal = remove desktop + forget definition.
    public Result RemoveWorkspace(Guid id) =>
        Workspace(id)
            .Tap(w => { if (w.DesktopId is { } d) desktops.Remove(d); })
            .Tap(w =>
            {
                // Prune live bookkeeping too: without this, a later Disappeared for one of
                // this workspace's windows would resurrect a phantom inventory key.
                memberships.Where(kv => kv.Value == id).Select(kv => kv.Key).ToList()
                    .ForEach(h => memberships.Remove(h));
                Persist(State with
                {
                    // Children are PROMOTED, never removed with the parent (#42). Deleting a
                    // workspace deletes one workspace; taking its children with it would destroy
                    // desktops full of windows on the strength of a grouping decision, and a
                    // dangling ParentId would leave rows that render as nested under nothing.
                    Workspaces = State.Workspaces
                        .Where(x => x.Id != id)
                        .Select(x => x.ParentId == id ? x with { ParentId = null } : x)
                        .ToList(),
                    WorkspaceRules = State.WorkspaceRules.Where(r => r.WorkspaceId != id).ToList(),
                    Inventory = State.Inventory.Where(kv => kv.Key != id).ToDictionary(kv => kv.Key, kv => kv.Value),
                });
            });

    // Delete, but only an EMPTY one (#73). Petre: "if it still has windows in it, deletion is
    // refused with a message: it can't be deleted until its contents are moved elsewhere."
    //
    // The refusal is the feature, not a safety rail bolted on. Windows' own desktop deletion
    // silently reparents that desktop's windows onto a neighbour, so a plain delete scatters
    // whatever was in the workspace across another one -- and the user finds out later, by
    // discovering windows somewhere they never put them. There is deliberately no "delete anyway":
    // moving the windows is a decision, and dragging their icons between rows already exists.
    //
    // "Has windows" is answered by the OVERVIEW rather than by counting memberships, because the
    // question the user is really asking is about the row in front of them. The overview is what
    // draws that row, so this cannot disagree with it -- including for a window minimized to the
    // tray, which the OS may not report as on any desktop but which the bar still shows.
    //
    // Costs one overview query, which is COM-heavy. Fine for a deliberate delete; it would not be
    // fine on a rebuild path.
    //
    // A workspace with CHILDREN but no windows of its own is deletable, and its children are
    // promoted to the top level (see RemoveWorkspace). Nothing is lost -- they keep their desktops
    // and their windows -- and refusing would leave no way to undo a nesting decision from the bar.
    public Result DeleteWorkspaceIfEmpty(Guid id) =>
        Workspace(id).Bind(workspace => WindowsByWorkspace()
            .Bind(overview => overview.Workspaces.FirstOrDefault(g => g.Workspace.Id == id) is { Running.Count: > 0 } group
                ? Result.Failure(
                    $"'{workspace.Name}' still has {group.Running.Count} " +
                    $"{(group.Running.Count == 1 ? "window" : "windows")} in it.\n\n" +
                    "Move them to another workspace first — drag their icons to another row on the bar — then delete it.")
                : RemoveWorkspace(id)));

    public Result SetRules(IReadOnlyList<WorkspaceRule> workspaceRules, IReadOnlyList<RenameRule> renameRules)
    {
        Persist(State with { WorkspaceRules = workspaceRules, RenameRules = renameRules });
        return Result.Success();
    }

    // Task 11 (floating icon bar): called after every drag (position) and every
    // tray-menu toggle (visibility) -- same fire-and-persist shape as SetRules above.
    // Persist() already pulses StateChanged, which nothing here needs to react to
    // (the bar's own drag/toggle handlers already know their own new state), but any
    // future surface that reads FloatingBar gets live updates for free.
    public Result SaveFloatingBar(FloatingBarState state)
    {
        Persist(State with { FloatingBar = state });
        return Result.Success();
    }

    public Result AssignWindow(WindowHandle window, Guid workspaceId) =>
        knownWindows.TryGetValue(window, out var info)
            // Explicitly moving a pinned window to ONE workspace is a statement that it
            // should no longer be on ALL of them -- unpin first, then place (spec).
            ? desktops.IsPinned(window)
                .Bind(pinned => pinned ? desktops.Unpin(window) : Result.Success())
                .Bind(() => Place(info, workspaceId))
            : Result.Failure("Window no longer exists.");

    // Drag-and-drop onto a plain OS desktop row (e.g. Petre's unbound "Main"): the
    // counterpart to AssignWindow for destinations that aren't workspaces. Same shape --
    // unpin first, because putting a window on ONE desktop contradicts "on all of
    // them" -- then move it and drop the workspace membership.
    //
    // The workspace's ROSTER entry is deliberately left alone: a roster lists what
    // BELONGS to a workspace even when it isn't running there (spec), and it has its own
    // explicit editing UI ("Add app…" / "Remove from workspace"). A drag says where this
    // window should be right now, not what the workspace is made of -- so ▶ Start still
    // relaunches the app later, exactly as before the drag.
    public Result MoveToDesktop(WindowHandle window, Guid desktopId) =>
        desktops.IsPinned(window)
            .Bind(pinned => pinned ? desktops.Unpin(window) : Result.Success())
            .Bind(() => desktops.MoveWindow(window, desktopId))
            .Tap(() =>
            {
                memberships.Remove(window);
                detached.Add(window);
                // Persisted too, for the same reason pinning is: `detached` is live-only, so
                // without this a restart would let a rule (or the roster) reclaim a window
                // Petre deliberately dragged out onto a plain desktop. RememberPlacement
                // persists and pulses, replacing the bare pulse this path used to do.
                RememberPlacement(window, pinned: false);
            });

    // TaskSpaces' own windows, now that WindowMonitor reports them (it used to hook with
    // WINEVENT_SKIPOWNPROCESS, which is why the Manage window was missing from the bar).
    //
    // They are shown, jumped to and dragged like any other window. What they are exempt
    // from is everything the app does BEHIND Petre's back, and there are exactly three such
    // things, each guarded at its own single choke point:
    //
    //   AutoPlaceable  we never move our own window between desktops on our own initiative.
    //                  Yanking the window someone is currently reading is hostile, and it
    //                  would happen on every launch via RestorePlacements.
    //   ApplyRename    we never rewrite our own titles. Our WM_SETTEXT would fight WPF's own
    //                  Title, and a ledger entry for a window we control is a loop waiting
    //                  to happen.
    //   RosterAdd /    we never remember ourselves as an app that BELONGS to a workspace.
    //   EntryFor       Otherwise "▶ Start" would try to relaunch TaskSpaces and hit its own
    //                  single-instance guard, and PlacementMemory would try to re-place us
    //                  at every startup.
    //
    // Note this is deliberately NOT a filter on visibility: an exclusion at the monitor
    // would have put us right back where Petre started.
    bool IsOurs(WindowHandle handle) =>
        knownWindows.TryGetValue(handle, out var window) && window.ProcessId == ownProcess;

    // Placement memory, but only when the identity it keys on actually identifies THIS window.
    //
    // Petre: "i'm starting the edge browser and it immediately goes to personal, i'm starting
    // it in messaging, why?" Because membership identity for a Chromium browser is the PROFILE
    // (deliberately -- session args vary run to run and would make every launch a new app), so
    // all four of his Default-profile Edge windows share one identity. One of them had been
    // dragged to Personal, AddEntry strips an identity from every other workspace so exactly
    // one can claim it, and from then on Personal owned every Edge window he would ever open.
    //
    // The fix is not a finer identity -- two plain browser windows on one profile are
    // genuinely indistinguishable by content, which is the whole premise of identity here.
    // It is recognising what memory is FOR. Memory restores where an app lives when it comes
    // BACK: Beeper recreating the window it destroyed on close-to-tray, or a post-reboot
    // relaunch. It was never meant to herd extra windows of an app that is already open. So
    // when another live window already shares the identity, the identity is not a placement
    // key for this one, and the new window stays where it was opened.
    //
    // Beeper's case is unaffected, which is the test that matters: it closes to tray, so no
    // other Beeper window is live when the replacement appears, and memory still places it.
    Maybe<Placement> Remembered(WindowInfo window) =>
        SharesIdentityWithAnotherLiveWindow(window) ? Maybe<Placement>.None : PlacementMemory.For(window, State);

    bool SharesIdentityWithAnotherLiveWindow(WindowInfo window) =>
        RosterIdentity.Of(window).Match(
            identity => knownWindows.Values.Any(other =>
                other.Handle != window.Handle
                && RosterIdentity.Of(other).Map(id => id == identity).GetValueOrDefault(false)),
            // No readable path means no identity at all, so there is nothing to be ambiguous
            // about -- PlacementMemory will return None for it anyway.
            () => false);

    // Rules (and late placement) only touch windows that are neither ours, placed, pinned,
    // nor deliberately detached: pinned windows live on ALL desktops -- moving one to a
    // workspace desktop would silently defeat the pin Petre set by hand -- and a detached
    // window is one he dragged out of every workspace by hand (see `detached`).
    bool AutoPlaceable(WindowHandle handle) =>
        !IsOurs(handle)
        && !memberships.ContainsKey(handle)
        && !detached.Contains(handle)
        && !desktops.IsPinned(handle).GetValueOrDefault(false);

    // Petre: "i want to have the ability to specify a wildcard instead of the full window
    // name... 'beeper *' would match all beepers and still rename to beeper".
    //
    // Branching HERE rather than in each surface is deliberate: the floating bar's icon menu
    // and Manage's row menu both call RenameWindow, so wildcard support arrives in both from
    // one place and cannot end up implemented twice with different rules.
    //
    // A wildcard produces a durable RenameRule instead of a PersistedRename. That difference
    // is the whole point: a PersistedRename is keyed on the exact title at the moment of
    // renaming, so it lapses as soon as the app rewrites its title (Beeper carries the current
    // chat, RDM the session), whereas a rule keeps matching. Everything downstream already
    // applies rename rules on Appeared and on TitleChanged, so nothing else needed changing.
    public Result RenameWindow(WindowHandle window, string input) =>
        RenamePattern.IsWildcard(input)
            ? AddRenameRule(input)
            : RenameExactly(window, input);

    public Result AddRenameRule(string input) =>
        Result.FailureIf(RenamePattern.ShortNameOf(input).Length == 0,
                "A wildcard rename still needs a name: \"*\" on its own matches everything and names it nothing.")
            .Bind(() => AddRule(new RenameRule(RuleMatchKind.TitleRegex, RenamePattern.ToRegex(input), RenamePattern.ShortNameOf(input))));

    // Petre: "i've renamed remote desktop manager to RDP yesterday, today it's still the
    // original name, why?"
    //
    // Because an exact-title rename CANNOT survive that app. His state.json recorded
    // OriginalTitle "Remote Desktop Manager [_Richard - fhd]" while the window now reads
    // "Remote Desktop Manager [i7-petre]" -- RDM puts the current session in its title, so the
    // exact-match adoption in ReapplyRenames could never fire again. RenamePattern's own header
    // predicted this and named RDM as the example; what was missing was a way to ACT on it,
    // because the wildcard form derives the short name FROM the pattern ("beeper *" names it
    // "beeper"), and no pattern matching "Remote Desktop Manager [...]" can be spelled so that
    // it yields "RDP".
    //
    // So: rename by APP. One rule keyed on the process name, which is the one thing about a
    // window that never changes while it runs. Immune to every title rewrite, and it covers
    // every window of that app -- which for Beeper, Teams and Edge (18 dead exact-title
    // entries between them in his file) is what was wanted all along.
    public Result RenameApp(WindowHandle window, string shortName) =>
        Result.FailureIf(string.IsNullOrWhiteSpace(shortName), "A name is required.")
            .Bind(() => knownWindows.TryGetValue(window, out var info)
                ? AddRule(new RenameRule(RuleMatchKind.ProcessName, info.ProcessName, shortName.Trim()))
                    // The per-title renames for this app are now superseded, and leaving them
                    // would mean two records disagreeing about one app forever. Removed in the
                    // same breath as adding the rule, so state.json never holds both.
                    .Tap(() => Persist(State with
                    {
                        PersistedRenames = State.PersistedRenames
                            .Where(r => !r.ProcessName.Equals(info.ProcessName, StringComparison.OrdinalIgnoreCase))
                            .ToList(),
                    }))
                : Result.Failure("Window no longer exists."));

    // Prepended, not appended: rules are matched first-hit-wins in list order, so the newest
    // and most specific intent should win over an older broader pattern. An existing rule with
    // the same kind AND pattern is REPLACED rather than shadowed -- otherwise renaming the same
    // app twice would leave the list accumulating rules that can never match, which is exactly
    // how PersistedRenames turned into a graveyard.
    Result AddRule(RenameRule rule) =>
        Result.Success()
            .Tap(() => Persist(State with
            {
                RenameRules = [rule, .. State.RenameRules.Where(r => !(r.Kind == rule.Kind && r.Pattern == rule.Pattern))],
            }))
            // Apply it immediately to everything already open, so the rename is visible now
            // rather than only on the next Appeared or TitleChanged.
            //
            // NOT ReapplyRenames(): that sweep refreshes titles for windows ALREADY in the
            // ledger and adopts persisted renames after a restart. It never evaluates rules,
            // so a brand-new rule would have left the very window Petre was renaming
            // untouched until its title happened to change.
            //
            // Skips windows we have already renamed, for the same reason OnTitleChanged does:
            // ApplyRename records the current title as the ORIGINAL, so applying twice would
            // capture our own short name as the thing to restore later.
            .Tap(() => knownWindows.Values
                .Where(w => ledger.AppliedName(w.Handle).HasNoValue)
                .ToList()
                .ForEach(w => RulesEngine.MatchRename(w, State.RenameRules)
                    .Tap(shortName => { ApplyRename(w, shortName); })));

    Result RenameExactly(WindowHandle window, string shortName) =>
        knownWindows.TryGetValue(window, out var info)
            ? ApplyRename(info, shortName)
                // Manual renames persist (spec: survive restarts). Rule-based renames never
                // pass through here -- the rule itself is already durable. Keyed by process +
                // the title the window had before ANY rename (ledger's original).
                .Tap(() =>
                {
                    var original = ledger.OriginalTitle(window).GetValueOrDefault(info.Title);
                    Persist(State with
                    {
                        PersistedRenames =
                        [
                            .. State.PersistedRenames.Where(r => !(r.ProcessName.Equals(info.ProcessName, StringComparison.OrdinalIgnoreCase) && r.OriginalTitle == original)),
                            new PersistedRename(info.ProcessName, original, shortName),
                        ],
                    });
                })
            : Result.Failure("Window no longer exists.");

    // Restore a window's title to its original (called on user un-rename AND app exit).
    // Internal helper that restores the title and ledger entry, but leaves PersistedRenames alone.
    // Preserves the Set-before-ledger ordering: a failed WM_SETTEXT must not claim success in the ledger.
    Result RestoreTitleOnly(WindowHandle window) =>
        ledger.OriginalTitle(window)
            .ToResult("Window was never renamed.")
            .Bind(original => titles.Set(window, original)
                .Tap(() => ledger = ledger.Remove(window)));

    // User explicitly un-renames a window: restore the title AND remove the durable entry,
    // else the sweep would re-apply it seconds later.
    public Result RestoreTitle(WindowHandle window)
    {
        // Capture the original title before RestoreTitleOnly removes the ledger entry.
        var original = ledger.OriginalTitle(window);
        return RestoreTitleOnly(window)
            .Tap(() => original.Tap(originalTitle =>
            {
                // Only remove the persisted entry if we know the process name -- better a stale
                // persisted rename than deleting another app's entry if the window is hidden.
                var processName = knownWindows.TryGetValue(window, out var info) ? info.ProcessName : null;
                if (processName is not null)
                {
                    var remaining = State.PersistedRenames
                        .Where(r => !(r.OriginalTitle == originalTitle && r.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                    if (remaining.Count != State.PersistedRenames.Count)
                        Persist(State with { PersistedRenames = remaining });
                }
            }));
    }

    // App exit / crash-avoidance: leave every window exactly as we found it. Exit-time restoration
    // is temporary housekeeping; the durable PersistedRenames record must survive so renames re-apply
    // at the next startup (spec: "renames survive app restarts").
    public void RestoreAllTitles() => ledger.Handles.ToList().ForEach(h => RestoreTitleOnly(h));

    // The safety-net sweep (Petre: "applying those renamed titles every several
    // seconds"). Event-driven NAMECHANGE re-apply is the fast path; this catches missed
    // events AND adopts persisted renames after a restart. App calls it on a ~5s timer.
    public void ReapplyRenames()
    {
        // 1. Active renames whose on-screen title drifted without us hearing about it.
        //    Fire-and-forget Sets: a hung/closed window just misses this sweep round.
        ledger.Handles.ToList().ForEach(h =>
            titles.Get(h).Tap(current =>
            {
                if (ledger.NeedsReapply(h, current))
                    ledger.AppliedName(h).Tap(name => { titles.Set(h, name); });
            }));

        // 2. Persisted renames not yet active this session (the restart case): adopt any
        //    window whose process + current title exactly match a recorded rename.
        knownWindows.Values
            .Where(w => ledger.AppliedName(w.Handle).HasNoValue)
            .ToList()
            .ForEach(w => State.PersistedRenames
                .TryFirst(r => r.ProcessName.Equals(w.ProcessName, StringComparison.OrdinalIgnoreCase) && r.OriginalTitle == w.Title)
                .Tap(r => { ApplyRename(w, r.ShortName); }));
    }

    // --- overview / switcher-facing operations -----------------------------------

    // Ground truth for "which workspace is this window in": ASK THE OS which desktop
    // it is on (memberships only knows what WE placed). Pinned first -- pinned windows
    // are on all desktops, DesktopOf is meaningless for them.
    public Result<Core.Overview.Overview> WindowsByWorkspace() =>
        desktops.GetDesktops().Bind(live => desktops.CurrentDesktop().Map(current =>
        {
            var windows = knownWindows.Values.ToList();
            var pinned = windows
                .Where(w => desktops.IsPinned(w.Handle).GetValueOrDefault(false))
                // A window we BORROWED for a nested workspace is pinned as a mechanism, not as a
                // statement (#42) -- so it must not surface in the 📌 row, which means "you asked
                // for this everywhere". Without this the parent's windows would vanish out of the
                // row you are standing in and reappear under a pin nobody asked for, which is the
                // opposite of "present in the child workspace".
                .Where(w => !borrowed.ContainsKey(w.Handle))
                .Select(w => w.Handle).ToHashSet();
            var desktopOf = windows
                .Where(w => !pinned.Contains(w.Handle))
                .Select(w => (w.Handle, Desktop: borrowed.TryGetValue(w.Handle, out var home)
                    // ...and it still belongs to the desktop it came FROM.
                    //
                    // This line said `current` for one build, and Petre saw the result immediately:
                    // "sparrow loses all windows and they're moved to the child, don't do that."
                    // Nothing had moved -- the windows were pinned, so they were on both desktops
                    // -- but the BAR was told they belonged to whichever desktop he was standing
                    // on, so the parent's row emptied and they piled into the child's.
                    //
                    // The rule that survives is the one his two corrections add up to: the bar
                    // shows where a window LIVES, the desktop shows what is borrowed. A borrowed
                    // window is present on the child's screen and stays in its parent's row, which
                    // is also why the child's row is not doubled up -- the thing he rejected first.
                    ? Result.Success(home)
                    : desktops.DesktopOf(w.Handle)))
                .Where(x => x.Desktop.IsSuccess) // closed mid-query: just not shown this round
                .ToDictionary(x => x.Handle, x => x.Desktop.Value);
            // One screen sweep per build, alongside the DesktopOf calls above -- and far cheaper
            // than they are, being user32 rather than COM.
            var screen = screenLayout?.Snapshot() ?? ScreenFacts.Empty;
            return OverviewBuilder.Build(State, windows, h => ledger.OriginalTitle(h), pinned, desktopOf, live, current, activeWindow, lastActiveByDesktop, screen, wantsAttention);
        }));

    // Both now RECORD the placement as well as performing it. Petre's defect: "move Beeper
    // to pinned, close the app, open again, it is incorrectly placed in GEPHA workspace" --
    // Windows' pin is keyed to the HWND, and Beeper (Electron) destroys and recreates its
    // window when closed to tray, so the pin died with the handle and the fresh window
    // surfaced on whatever desktop was current. Remembering the pin by IDENTITY is what
    // makes it survive that, here and across app restarts.
    public Result PinWindow(WindowHandle window) =>
        desktops.Pin(window).Tap(() => RememberPlacement(window, pinned: true));

    public Result UnpinWindow(WindowHandle window) =>
        desktops.Unpin(window).Tap(() => ForgetPlacement(window));

    // Jump = what clicking a taskbar button does, but across workspaces: land on the
    // window's desktop (skip the no-op switch), then bring it to the foreground.
    public Result JumpTo(WindowHandle window, IWindowActivator activator) =>
        desktops.IsPinned(window).Bind(pinned => pinned
            ? activator.Activate(window) // pinned windows are already wherever Petre is
            : desktops.DesktopOf(window)
                // Claim the target as its desktop's last-active BEFORE switching, and that
                // ordering is the whole point. Switching fires CurrentChanged, whose
                // RestoreLastActive would otherwise activate whatever was remembered from the
                // previous visit -- racing this method's own Activate below for the same
                // foreground, with the winner decided by when the OS delivers the COM
                // notification. Writing the answer first makes the two agree instead of
                // compete: the restore either no-ops on the same handle or is beaten by an
                // identical call. Recording it is correct on its own terms anyway -- jumping
                // to a window IS that desktop's most recent focus.
                .Tap(desktopId => lastActiveByDesktop[desktopId] = window)
                .Bind(desktopId => desktops.CurrentDesktop()
                    .Bind(current => desktopId == current ? Result.Success() : desktops.Switch(desktopId)))
                .Bind(() => activator.Activate(window)));

    // Petre: "i want to be able to minimize windows from the floating bar", clicking the icon of
    // the window he is already in -- the taskbar's own toggle, and the reason JumpTo has always
    // restored a minimized window on the way in. Both halves of the gesture now exist.
    //
    // The bar only calls this for a row that IsActive, and Petre's condition -- "but only if
    // we're on that workspace" -- is already carried by that flag rather than needing a check of
    // its own: a window cannot hold focus on a desktop you are not looking at. That also means
    // pinned windows keep working, which an explicit is-this-row-current test would have broken
    // (the Pinned row is deliberately never "current", being no workspace at all).
    //
    // Pulsing rather than trusting the OS to tell us: minimizing fires no window event this app
    // hooks -- EVENT_OBJECT_HIDE does not fire, the window is still visible in the taskbar sense
    // -- so without this the icon would not dim until focus happened to land somewhere else.
    public Result MinimizeWindow(WindowHandle window, IWindowActivator windowActivator) =>
        windowActivator.Minimize(window).Tap(() => stateChanged.OnNext(Unit.Default));

    // Which half of that toggle a click on the bar means. It used to be decided by the BAR, from
    // the IsActive flag on the row it had drawn, and Petre found what that costs: "when i minimize
    // an app in the floatingwindow, i can't bring it back up... after several attempts, it does
    // come up and back down minimized, and then nothing, stays minimized."
    //
    // Neither fact the decision needs survives being read from a rendered row:
    //
    //   * IsActive is STICKY, deliberately. Clicking a bar icon activates the bar, which
    //     WindowMonitor ignores by hwnd, so Foreground() reports None and MarkActive -- which
    //     never clears on None, for good reasons of its own -- goes on naming the window that was
    //     just put away.
    //   * IsMinimized is a SNAPSHOT, and a stale one here. Minimizing raises no event this app
    //     hooks, so the only rebuild that follows is the pulse MinimizeWindow sends itself, and
    //     that races ShowWindowAsync -- it is usually taken before the window is iconic.
    //
    // So the row went on saying "active, not minimized" and each further click re-minimized an
    // already-minimized window, which Windows treats as a no-op: nothing happened, repeatedly.
    // It recovered only when some other tracked window took the foreground, which is exactly the
    // intermittency Petre reported.
    //
    // Decided here, from the live active window and a live IsIconic, so neither input can be
    // older than the click. A minimized window is never the window you are in, whatever the
    // highlight still claims -- and that ordering makes restore the fallback, which is the safe
    // way round: the worst a wrong guess can now do is raise a window you meant to put away.
    public Result ToggleWindow(WindowHandle window, IWindowActivator windowActivator) =>
        activeWindow.GetValueOrDefault() == window && !windowActivator.IsMinimized(window)
            ? MinimizeWindow(window, windowActivator)
            : JumpTo(window, windowActivator);

    // Which of a workspace's roster apps are not currently running anywhere. Checks ALL
    // known windows rather than just this workspace's, because Rider-on-X sitting in another
    // workspace still counts as running.
    //
    // Kept even though nothing LAUNCHES any more (the ▶ Start surfaces are gone): this is how
    // the roster is inspected, and a future surface that lists "belongs here but closed" wants
    // exactly this. It is also the one place the roster's own contents are queryable.
    public IReadOnlyList<InventoryEntry> NotRunningRoster(Guid workspaceId) =>
        State.Inventory.GetValueOrDefault(workspaceId, [])
            .Where(e => !RosterIdentity.IsRunning(e, knownWindows.Values))
            .ToList();

    // --- persistence helpers -----------------------------------------------------

    Result<Workspace> Workspace(Guid id) =>
        State.Workspaces.TryFirst(w => w.Id == id).ToResult($"Workspace {id} not found.");

    // Roster (spec): a workspace lists the apps that BELONG to it even when they are
    // not running. An entry is added/updated when a window is PLACED here and SURVIVES
    // the window closing; identity = path+args (browser: path+profile), and the same
    // identity landing in another workspace MOVES (a window can't belong to two --
    // last placement wins). Entries leave only via user removal or workspace deletion.
    void RosterAdd(WindowInfo window, Guid workspaceId)
    {
        if (window.ProcessPath is null) return; // elevated/inaccessible: nothing relaunchable to remember
        // Never roster OURSELVES (see IsOurs). Dragging the Manage window onto a workspace
        // row is allowed -- it is an explicit act, and moving a window between desktops is
        // harmless -- but recording TaskSpaces as an app that BELONGS to that workspace
        // would make "▶ Start" relaunch it into its own "already running" dialog.
        if (IsOurs(window.Handle)) return;
        AddEntry(workspaceId, new InventoryEntry(window.ProcessPath, window.CommandLine,
            ledger.OriginalTitle(window.Handle).GetValueOrDefault(window.Title)));
    }

    void AddEntry(Guid workspaceId, InventoryEntry entry)
    {
        var identity = RosterIdentity.Of(entry);
        var inventory = State.Inventory.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<InventoryEntry>)kv.Value.Where(e => RosterIdentity.Of(e) != identity).ToList());
        inventory[workspaceId] = [.. inventory.GetValueOrDefault(workspaceId, []), entry];
        // Belonging to a workspace is mutually exclusive with being pinned to all of them or
        // detached from all of them, so claiming an identity here clears the other two --
        // in the SAME Persist, so one placement is one write and one pulse.
        Persist(State with
        {
            Inventory = inventory,
            PinnedApps = Forget(State.PinnedApps, identity),
            DetachedApps = Forget(State.DetachedApps, identity),
        });
    }

    // --- placement memory (identity-keyed, so it outlives any single window handle) ------

    // Records "Petre put this app HERE": pinned to every workspace, or detached onto a plain
    // desktop. The workspace case needs nothing extra -- Place() -> RosterAdd() -> AddEntry()
    // already writes identity -> workspace into Inventory.
    void RememberPlacement(WindowHandle window, bool pinned) =>
        EntryFor(window).Match(
            entry => Persist(State with
            {
                PinnedApps = pinned ? [.. Forget(State.PinnedApps, RosterIdentity.Of(entry)), entry] : Forget(State.PinnedApps, RosterIdentity.Of(entry)),
                DetachedApps = pinned ? Forget(State.DetachedApps, RosterIdentity.Of(entry)) : [.. Forget(State.DetachedApps, RosterIdentity.Of(entry)), entry],
            }),
            // No readable process path (elevated): nothing identifiable to remember, but the
            // OS-level pin/move DID happen, so the UI still needs telling.
            () => stateChanged.OnNext(Unit.Default));

    void ForgetPlacement(WindowHandle window) =>
        EntryFor(window).Match(
            entry => Persist(State with
            {
                PinnedApps = Forget(State.PinnedApps, RosterIdentity.Of(entry)),
                DetachedApps = Forget(State.DetachedApps, RosterIdentity.Of(entry)),
            }),
            () => stateChanged.OnNext(Unit.Default));

    // The roster-shaped record of a live window: original title where we renamed it, so
    // state.json keeps showing what Petre would recognise rather than our short name.
    //
    // None for our OWN windows (see IsOurs), which is what keeps TaskSpaces out of
    // PinnedApps/DetachedApps: pinning the Manage window by hand still pins it in Windows,
    // it just isn't remembered as a standing instruction to re-pin us at every launch.
    Maybe<InventoryEntry> EntryFor(WindowHandle window) =>
        !IsOurs(window) && knownWindows.TryGetValue(window, out var info) && info.ProcessPath is not null
            ? new InventoryEntry(info.ProcessPath, info.CommandLine, ledger.OriginalTitle(window).GetValueOrDefault(info.Title))
            : Maybe<InventoryEntry>.None;

    static IReadOnlyList<InventoryEntry> Forget(IReadOnlyList<InventoryEntry> apps, string identity) =>
        apps.Where(entry => RosterIdentity.Of(entry) != identity).ToList();

    Result ApplyPlacement(WindowInfo window, Placement placement) => placement.Kind switch
    {
        PlacementKind.Workspace => Place(window, placement.WorkspaceId),
        // Pin the OS window only: memory ALREADY says pinned, so re-persisting would be a
        // pointless write on every tray-restore of an Electron app.
        PlacementKind.Pinned => desktops.Pin(window.Handle),
        // Live-only flag; the persisted DetachedApps entry is what survives restarts.
        _ => Result.Success().Tap(() => detached.Add(window.Handle)),
    };

    // Windows already open when TaskSpaces starts never pass through OnAppeared -- Start()
    // only recorded them into knownWindows -- so nothing had ever placed them. That was two
    // gaps at once: rules never touched pre-existing windows, and the roster (which is the
    // workspace half of placement memory) was never consulted at all. Petre: "when starting
    // up, all those apps, when started, should be redistributed to the correct workspaces."
    void RestorePlacements() =>
        knownWindows.Values
            .Where(window => AutoPlaceable(window.Handle))
            .ToList()
            // Same ambiguity guard as the live path (see Remembered): after a reboot Edge
            // restores four windows at once, all on whatever desktop is current, and memory
            // knows one workspace for all four -- so applying it would herd every one of them
            // into that workspace. Leaving them where the session restored them is the lesser
            // wrong, and the only honest answer when the key cannot tell them apart.
            .ForEach(window => Remembered(window)
                .Or(RulesEngine.MatchWorkspace(window, State.WorkspaceRules).Map(Placement.In))
                .Where(remembered => SafeToRestore(window, remembered))
                .Tap(remembered => ApplyPlacement(window, remembered)));

    // A pin is ADDITIVE (it makes a window appear everywhere; it does not move it) and
    // detaching is just a flag, so both are always safe to re-apply. A WORKSPACE placement is
    // a real move, so it only happens when the window currently sits on a desktop no
    // workspace owns: Petre may have moved it there with Windows' own Task View, which we
    // never saw, and a restart must not undo that. A window whose desktop cannot be resolved
    // at all is fair game -- it is the "Unplaced" case, where placing it can only help.
    bool SafeToRestore(WindowInfo window, Placement placement) =>
        placement.Kind != PlacementKind.Workspace
        || desktops.DesktopOf(window.Handle)
            .Map(current => !State.Workspaces.Any(w => w.DesktopId == current))
            .GetValueOrDefault(true);

    public Result<InventoryEntry> AddRosterEntry(Guid workspaceId, string exePath, string? arguments) =>
        Result.FailureIf(string.IsNullOrWhiteSpace(exePath), "Executable path required")
            .Bind(() => Workspace(workspaceId))
            .Map(_ => new InventoryEntry(
                exePath,
                string.IsNullOrWhiteSpace(arguments) ? $"\"{exePath}\"" : $"\"{exePath}\" {arguments}",
                Path.GetFileNameWithoutExtension(exePath)))
            .Tap(entry => AddEntry(workspaceId, entry));

    public Result RemoveRosterEntry(Guid workspaceId, InventoryEntry entry)
    {
        var identity = RosterIdentity.Of(entry);
        var current = State.Inventory.GetValueOrDefault(workspaceId, []);
        var remaining = current.Where(e => RosterIdentity.Of(e) != identity).ToList();
        if (remaining.Count == current.Count) return Result.Failure("That app is no longer in this workspace's list.");
        var inventory = State.Inventory.ToDictionary(kv => kv.Key, kv => kv.Value);
        inventory[workspaceId] = remaining;
        Persist(State with { Inventory = inventory });
        return Result.Success();
    }

    void Persist(AppState next)
    {
        State = next;
        store.Save(next); // small JSON, synchronous write on every mutation is fine for v1
        stateChanged.OnNext(Unit.Default);
    }
}
