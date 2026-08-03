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
// data-flow from the spec —
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
    Func<DateTimeOffset>? clock = null)
{
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.Now);
    readonly Subject<Unit> stateChanged = new();
    readonly Dictionary<WindowHandle, WindowInfo> knownWindows = [];
    readonly Dictionary<WindowHandle, Guid> memberships = []; // window -> workspace

    // Windows Petre deliberately dragged OUT of every workspace onto a plain OS desktop
    // (MoveToDesktop). Without this, removing the membership alone would make the window
    // auto-placeable again, so the very next rule evaluation — for a browser, its next
    // title change, i.e. seconds later — would yank it straight back to the workspace he
    // just dragged it out of. Live-only, exactly like `memberships`: a restart re-derives
    // placement from the OS, and rules legitimately own a window again next launch.
    readonly HashSet<WindowHandle> detached = [];
    RenameLedger ledger = RenameLedger.Empty;
    PendingPlacements pending = PendingPlacements.Empty;      // rehydration (Task 9)
    IDisposable? subscription;

    public AppState State { get; private set; } = AppState.Empty;
    public IObservable<Unit> StateChanged => stateChanged.AsObservable();
    public IReadOnlyList<WindowInfo> KnownWindows => knownWindows.Values.ToList();

    public Result Start() =>
        LoadState()
            .Bind(Reconcile)
            .Tap(() =>
            {
                monitor.Snapshot().ToList().ForEach(w => knownWindows[w.Handle] = w);
                subscription = monitor.Events.Subscribe(OnWindowEvent);
                ReapplyRenames();
            });

    // Finding 1 (reviewer, Critical): split out of Start() so the composition root can
    // load persisted state WITHOUT reconciling desktops or subscribing to the monitor —
    // needed for compatibility mode (Finding 2: still list workspaces read-only, no
    // desktop operations) and so a failed load can be distinguished from a failed
    // reconcile/subscribe. Deliberately does NOT touch `State` on failure — a corrupt
    // store must never quietly become "empty workspace list" in the field the UI reads;
    // the caller (App) decides what to do (back up the corrupt file, inform the user,
    // retry) before anything gets a chance to persist over it.
    public Result LoadState() =>
        store.Load().Bind(s =>
        {
            State = s;
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
        }
    }

    void OnAppeared(WindowInfo window)
    {
        knownWindows[window.Handle] = window;

        // Rehydrated launches win over rules: we KNOW where that app belongs.
        var (remaining, placement) = pending.Match(window, now());
        pending = remaining;
        // Fire-and-forget: the event pipeline has no caller waiting on a Result, and a
        // failed auto-placement (e.g. stale workspace, desktop move rejected) has
        // nowhere to surface here — it's silently skipped, unlike the UI-facing
        // AssignWindow, which must propagate the same failure to the caller.
        if (AutoPlaceable(window.Handle))
            placement.Or(RulesEngine.MatchWorkspace(window, State.WorkspaceRules))
                .Tap(workspaceId => { Place(window, workspaceId); });

        // Fire-and-forget for the same reason as above.
        RulesEngine.MatchRename(window, State.RenameRules)
            .Tap(shortName => { ApplyRename(window, shortName); });

        // Fix wave (reviewer, Important): pulse unconditionally, even when neither branch
        // above ran (no placement rule, no rename rule). Persist() above already pulses
        // when it fires, so this can double-pulse — harmless, a tray-menu/panel rebuild
        // triggered by StateChanged is a cheap, idempotent re-read of current state. What
        // it fixes: an open panel/Windows tab must learn a new window appeared even when
        // nothing about it was auto-placed or renamed, or its row never shows up.
        stateChanged.OnNext(Unit.Default);
    }

    void OnTitleChanged(WindowInfo window)
    {
        var previouslyUnknown = !knownWindows.ContainsKey(window.Handle);
        knownWindows[window.Handle] = window;
        if (previouslyUnknown) { OnAppeared(window); return; } // became taskbar-worthy late

        // Fire-and-forget: same rationale as OnAppeared — no caller awaits this path.
        if (ledger.NeedsReapply(window.Handle, window.Title))
            ledger.AppliedName(window.Handle).Tap(name => { titles.Set(window.Handle, name); });
        else if (ledger.AppliedName(window.Handle).HasNoValue)
            // Not renamed yet — but the new title may now match a rename rule.
            RulesEngine.MatchRename(window, State.RenameRules)
                .Tap(shortName => { ApplyRename(window, shortName); });

        // Late placement (spec): a window that appeared bare may only now reveal what it
        // is showing — Rider loading a solution rewrites its title. Only UNPLACED windows
        // are eligible: once placed (rule, launch, or hand), a title change must never
        // teleport a window between workspaces (browsers rewrite titles every tab switch).
        //
        // Fix round 1 (reviewer, Important): ApplyRename's titles.Set produces a genuine
        // NAMECHANGE, which re-enters here as an "echo" carrying OUR OWN short name as
        // window.Title. A still-unplaced window must not have workspace rules run
        // against that synthetic title — only titles the APP itself wrote are legitimate
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
    // Discord/Outlook minimizing to tray) still EXISTS — its hwnd stays valid and it may
    // reappear later. Drop it from live-window bookkeeping exactly like Disappeared
    // (it's not on any desktop's visible taskbar right now, so tracking it as "known" or
    // "placed" would be misleading), but deliberately do NOT touch the rename
    // ledger: if we forgot the original title here, a later re-show's rename-rule
    // re-application would record our OWN short name as the "original", permanently
    // breaking restore. Only a genuine Disappeared (the window is actually gone) forgets
    // the ledger entry. RestoreAllTitles on app exit still finds and restores hidden
    // windows because their ledger entry — and their hwnd — are both still valid. The
    // roster (spec) is unaffected either way: it lists what BELONGS to a workspace, not
    // what's currently live, so Hidden never touches it.
    void OnHidden(WindowInfo window)
    {
        knownWindows.Remove(window.Handle);
        memberships.Remove(window.Handle); // roster entry stays — that's the point (spec)

        // Fix wave (reviewer, Important): the live panel/Windows tab must lose this row
        // (it's no longer a live window) — nothing else in this path calls Persist(), so
        // without this pulse the UI would keep showing a window that's gone to tray.
        stateChanged.OnNext(Unit.Default);
    }

    void OnDisappeared(WindowInfo window)
    {
        knownWindows.Remove(window.Handle);
        ledger = ledger.Remove(window.Handle);
        memberships.Remove(window.Handle); // roster entry stays — ▶ Start relaunches it
        // A closed window's hwnd can be recycled by Windows for an entirely different
        // window later; a stale "detached" entry would silently exempt that new window
        // from rules. Cleared here for the same reason memberships is.
        detached.Remove(window.Handle);

        // Fix wave (reviewer, Important): same rationale as OnHidden above — a closed
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
    // entry claiming the rename succeeded — order matters here. We attempt the actual
    // write FIRST, and only update the ledger (which captures the original title) once
    // that succeeds; RenameWindow propagates the failure, OnAppeared/OnTitleChanged
    // discard it deliberately (see comments there).
    Result ApplyRename(WindowInfo window, string shortName) =>
        titles.Set(window.Handle, shortName)
            .Tap(() => ledger = ledger.Apply(window.Handle, window.Title, shortName));

    // --- UI-facing operations ---------------------------------------------------

    public Result Switch(Guid workspaceId) =>
        Workspace(workspaceId).Bind(w => w.DesktopId is { } id
            ? desktops.Switch(id)
            : Result.Failure("Workspace has no desktop (compatibility mode)."));

    // Floating-bar fix round 6 (Petre: the bar must "show tabs from all workspaces" —
    // including windows on UNBOUND desktops like his "Main"): a desktop group's label
    // needs a click-to-go-there affordance just like a workspace label, but Switch()
    // above takes a WORKSPACE id. This is the raw-desktop counterpart for
    // Overview.DesktopGroup rows — same delegate, no persistence (like Switch, "current
    // workspace" is always derived live from CurrentDesktop, never stored).
    public Result SwitchToDesktop(Guid desktopId) => desktops.Switch(desktopId);

    // Ctrl+Alt+arrows (spec §Tray interaction): cycle through OUR workspaces in their
    // defined order — unlike native Win+Ctrl+arrows, which walks every OS desktop
    // including unbound ones. Wrapping; a non-workspace current desktop enters the
    // ring at the edge matching travel direction.
    public Result CycleWorkspace(int direction) =>
        State.Workspaces.Count == 0
            ? Result.Failure("No workspaces to cycle through.")
            : desktops.CurrentDesktop().Bind(current =>
            {
                var index = State.Workspaces.ToList().FindIndex(w => w.DesktopId == current);
                var next = index < 0
                    ? (direction > 0 ? 0 : State.Workspaces.Count - 1)
                    : (index + direction + State.Workspaces.Count) % State.Workspaces.Count;
                return Switch(State.Workspaces[next].Id);
            });

    // Ctrl+Alt+1..9: direct switch by defined order (hotkey digit - 1).
    public Result SwitchToIndex(int index) =>
        index >= 0 && index < State.Workspaces.Count
            ? Switch(State.Workspaces[index].Id)
            : Result.Failure($"No workspace #{index + 1}.");

    public Result<Workspace> AddWorkspace(string name) =>
        Result.FailureIf(string.IsNullOrWhiteSpace(name), "Workspace name required")
            .Bind(() => Result.FailureIf(NameTaken(name, excluding: null), $"A workspace named '{name.Trim()}' already exists."))
            .Bind(() => desktops.Create(name))
            .Map(d => new Workspace(Guid.NewGuid(), name, d.Id))
            .Tap(w => Persist(State with { Workspaces = [.. State.Workspaces, w] }));

    // Reviewer (fix round 1, Critical): duplicate names used to be unchecked, so two
    // workspaces could share a name; ManageWindow.OnSaveRules' `ToDictionary(w => w.Name)`
    // then threw ArgumentException with no handler, killing the process before renamed
    // titles could be restored. Guarded here (case-insensitive, trimmed) — the ROOT CAUSE
    // fix — with a defense-in-depth duplicate-safe dictionary added in ManageWindow too,
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

    // Case-insensitive, trim-tolerant name collision check. `excluding` lets
    // RenameWorkspace allow renaming a workspace to (a variant of) its own current name —
    // it must only reject collisions with *other* workspaces.
    bool NameTaken(string name, Guid? excluding) =>
        State.Workspaces.Any(w => w.Id != excluding && w.Name.Trim().Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    // Removing a workspace never removes its desktop implicitly — windows live there.
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
                    Workspaces = State.Workspaces.Where(x => x.Id != id).ToList(),
                    WorkspaceRules = State.WorkspaceRules.Where(r => r.WorkspaceId != id).ToList(),
                    Inventory = State.Inventory.Where(kv => kv.Key != id).ToDictionary(kv => kv.Key, kv => kv.Value),
                });
            });

    public Result SetRules(IReadOnlyList<WorkspaceRule> workspaceRules, IReadOnlyList<RenameRule> renameRules)
    {
        Persist(State with { WorkspaceRules = workspaceRules, RenameRules = renameRules });
        return Result.Success();
    }

    // Task 11 (floating icon bar): called after every drag (position) and every
    // tray-menu toggle (visibility) — same fire-and-persist shape as SetRules above.
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
            // should no longer be on ALL of them — unpin first, then place (spec).
            ? desktops.IsPinned(window)
                .Bind(pinned => pinned ? desktops.Unpin(window) : Result.Success())
                .Bind(() => Place(info, workspaceId))
            : Result.Failure("Window no longer exists.");

    // Drag-and-drop onto a plain OS desktop row (e.g. Petre's unbound "Main"): the
    // counterpart to AssignWindow for destinations that aren't workspaces. Same shape —
    // unpin first, because putting a window on ONE desktop contradicts "on all of
    // them" — then move it and drop the workspace membership.
    //
    // The workspace's ROSTER entry is deliberately left alone: a roster lists what
    // BELONGS to a workspace even when it isn't running there (spec), and it has its own
    // explicit editing UI ("Add app…" / "Remove from workspace"). A drag says where this
    // window should be right now, not what the workspace is made of — so ▶ Start still
    // relaunches the app later, exactly as before the drag.
    public Result MoveToDesktop(WindowHandle window, Guid desktopId) =>
        desktops.IsPinned(window)
            .Bind(pinned => pinned ? desktops.Unpin(window) : Result.Success())
            .Bind(() => desktops.MoveWindow(window, desktopId))
            .Tap(() =>
            {
                memberships.Remove(window);
                detached.Add(window);
                stateChanged.OnNext(Unit.Default); // no Persist() on this path — pulse the UI ourselves
            });

    // Rules (and late placement) only touch windows that are neither placed, pinned, nor
    // deliberately detached: pinned windows live on ALL desktops — moving one to a
    // workspace desktop would silently defeat the pin Petre set by hand — and a detached
    // window is one he dragged out of every workspace by hand (see `detached`).
    bool AutoPlaceable(WindowHandle handle) =>
        !memberships.ContainsKey(handle)
        && !detached.Contains(handle)
        && !desktops.IsPinned(handle).GetValueOrDefault(false);

    public Result RenameWindow(WindowHandle window, string shortName) =>
        knownWindows.TryGetValue(window, out var info)
            ? ApplyRename(info, shortName)
                // Manual renames persist (spec: survive restarts). Rule-based renames never
                // pass through here — the rule itself is already durable. Keyed by process +
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
                // Only remove the persisted entry if we know the process name — better a stale
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

    // Rehydrator/StartWorkspace tell us "pid X (path Y, args Z) belongs to workspace W,
    // expect it soon". The command line lets two same-exe launches route separately.
    public void RegisterPendingLaunch(int processId, string processPath, Guid workspaceId, string? commandLine = null) =>
        pending = pending.Add(processId, processPath, workspaceId, now(), commandLine);

    // --- overview / switcher-facing operations -----------------------------------

    // Ground truth for "which workspace is this window in": ASK THE OS which desktop
    // it is on (memberships only knows what WE placed). Pinned first — pinned windows
    // are on all desktops, DesktopOf is meaningless for them.
    public Result<Core.Overview.Overview> WindowsByWorkspace() =>
        desktops.GetDesktops().Bind(live => desktops.CurrentDesktop().Map(current =>
        {
            var windows = knownWindows.Values.ToList();
            var pinned = windows
                .Where(w => desktops.IsPinned(w.Handle).GetValueOrDefault(false))
                .Select(w => w.Handle).ToHashSet();
            var desktopOf = windows
                .Where(w => !pinned.Contains(w.Handle))
                .Select(w => (w.Handle, Desktop: desktops.DesktopOf(w.Handle)))
                .Where(x => x.Desktop.IsSuccess) // closed mid-query: just not shown this round
                .ToDictionary(x => x.Handle, x => x.Desktop.Value);
            return OverviewBuilder.Build(State, windows, h => ledger.OriginalTitle(h), pinned, desktopOf, live, current);
        }));

    public Result PinWindow(WindowHandle window) =>
        desktops.Pin(window).Tap(() => stateChanged.OnNext(Unit.Default));

    public Result UnpinWindow(WindowHandle window) =>
        desktops.Unpin(window).Tap(() => stateChanged.OnNext(Unit.Default));

    // Jump = what clicking a taskbar button does, but across workspaces: land on the
    // window's desktop (skip the no-op switch), then bring it to the foreground.
    public Result JumpTo(WindowHandle window, IWindowActivator activator) =>
        desktops.IsPinned(window).Bind(pinned => pinned
            ? activator.Activate(window) // pinned windows are already wherever Petre is
            : desktops.DesktopOf(window)
                .Bind(desktopId => desktops.CurrentDesktop()
                    .Bind(current => desktopId == current ? Result.Success() : desktops.Switch(desktopId)))
                .Bind(() => activator.Activate(window)));

    // "Not running" checks ALL known windows, not just this workspace's — Rider-on-X
    // sitting in another workspace still means Start must not launch a duplicate.
    public IReadOnlyList<InventoryEntry> NotRunningRoster(Guid workspaceId) =>
        State.Inventory.GetValueOrDefault(workspaceId, [])
            .Where(e => !RosterIdentity.IsRunning(e, knownWindows.Values))
            .ToList();

    public Result StartRosterEntry(Guid workspaceId, InventoryEntry entry, IAppLauncher launcher) =>
        launcher.Launch(entry)
            .ToResult($"Could not launch {entry.ProcessPath} (moved or uninstalled?)")
            .Tap(pid => RegisterPendingLaunch(pid, entry.ProcessPath, workspaceId, entry.CommandLine))
            .Bind(_ => Result.Success());

    // ▶ Start: launch everything missing (best-effort per entry — one bad exe never
    // aborts the batch, v1 rehydrator rule), then take Petre there.
    public Result StartWorkspace(Guid workspaceId, IAppLauncher launcher) =>
        Workspace(workspaceId).Bind(_ =>
        {
            foreach (var entry in NotRunningRoster(workspaceId))
                StartRosterEntry(workspaceId, entry, launcher); // per-entry Result deliberately dropped
            return Switch(workspaceId);
        });

    // --- persistence helpers -----------------------------------------------------

    Result<Workspace> Workspace(Guid id) =>
        State.Workspaces.TryFirst(w => w.Id == id).ToResult($"Workspace {id} not found.");

    // Roster (spec): a workspace lists the apps that BELONG to it even when they are
    // not running. An entry is added/updated when a window is PLACED here and SURVIVES
    // the window closing; identity = path+args (browser: path+profile), and the same
    // identity landing in another workspace MOVES (a window can't belong to two —
    // last placement wins). Entries leave only via user removal or workspace deletion.
    void RosterAdd(WindowInfo window, Guid workspaceId)
    {
        if (window.ProcessPath is null) return; // elevated/inaccessible: nothing relaunchable to remember
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
        Persist(State with { Inventory = inventory });
    }

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
