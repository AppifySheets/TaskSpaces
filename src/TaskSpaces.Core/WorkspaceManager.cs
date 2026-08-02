using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rehydration;
using TaskSpaces.Core.Renaming;
using TaskSpaces.Core.Rules;

namespace TaskSpaces.Core;

// The heart of TaskSpaces: subscribes to window lifecycle events and applies the
// data-flow from the spec —
//   Appeared      -> workspace rule -> move to desktop -> record inventory
//   Appeared      -> rename rule    -> apply short name (ledger keeps the original)
//   TitleChanged  -> renamed window -> re-apply short name (apps rewrite their titles)
//   Disappeared   -> drop from inventory + ledger
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
        placement.Or(RulesEngine.MatchWorkspace(window, State.WorkspaceRules))
            .Tap(workspaceId => { Place(window, workspaceId); });

        // Fire-and-forget for the same reason as above.
        RulesEngine.MatchRename(window, State.RenameRules)
            .Tap(shortName => { ApplyRename(window, shortName); });
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
    }

    // Finding 3 (reviewer, Important): a window that merely left the taskbar (e.g.
    // Discord/Outlook minimizing to tray) still EXISTS — its hwnd stays valid and it may
    // reappear later. Drop it from live-window bookkeeping exactly like Disappeared
    // (it's not on any desktop's visible taskbar right now, so tracking it as "known" or
    // "in inventory" would be misleading), but deliberately do NOT touch the rename
    // ledger: if we forgot the original title here, a later re-show's rename-rule
    // re-application would record our OWN short name as the "original", permanently
    // breaking restore. Only a genuine Disappeared (the window is actually gone) forgets
    // the ledger entry. RestoreAllTitles on app exit still finds and restores hidden
    // windows because their ledger entry — and their hwnd — are both still valid.
    void OnHidden(WindowInfo window)
    {
        knownWindows.Remove(window.Handle);
        if (memberships.Remove(window.Handle, out var workspaceId))
            PersistInventory(workspaceId);
    }

    void OnDisappeared(WindowInfo window)
    {
        knownWindows.Remove(window.Handle);
        ledger = ledger.Remove(window.Handle);
        if (memberships.Remove(window.Handle, out var workspaceId))
            PersistInventory(workspaceId);
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
                PersistInventory(workspaceId);
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
            .Tap(w => Persist(State with
            {
                Workspaces = State.Workspaces.Where(x => x.Id != id).ToList(),
                WorkspaceRules = State.WorkspaceRules.Where(r => r.WorkspaceId != id).ToList(),
                Inventory = State.Inventory.Where(kv => kv.Key != id).ToDictionary(kv => kv.Key, kv => kv.Value),
            }));

    public Result SetRules(IReadOnlyList<WorkspaceRule> workspaceRules, IReadOnlyList<RenameRule> renameRules)
    {
        Persist(State with { WorkspaceRules = workspaceRules, RenameRules = renameRules });
        return Result.Success();
    }

    public Result AssignWindow(WindowHandle window, Guid workspaceId) =>
        knownWindows.TryGetValue(window, out var info)
            ? Place(info, workspaceId)
            : Result.Failure("Window no longer exists.");

    public Result RenameWindow(WindowHandle window, string shortName) =>
        knownWindows.TryGetValue(window, out var info)
            ? ApplyRename(info, shortName)
            : Result.Failure("Window no longer exists.");

    public Result RestoreTitle(WindowHandle window) =>
        ledger.OriginalTitle(window)
            .ToResult("Window was never renamed.")
            .Bind(original => titles.Set(window, original))
            .Tap(() => ledger = ledger.Remove(window));

    // App exit / crash-avoidance: leave every window exactly as we found it.
    public void RestoreAllTitles() => ledger.Handles.ToList().ForEach(h => RestoreTitle(h));

    // Rehydrator/StartWorkspace tell us "pid X (path Y, args Z) belongs to workspace W,
    // expect it soon". The command line lets two same-exe launches route separately.
    public void RegisterPendingLaunch(int processId, string processPath, Guid workspaceId, string? commandLine = null) =>
        pending = pending.Add(processId, processPath, workspaceId, now(), commandLine);

    // --- persistence helpers -----------------------------------------------------

    Result<Workspace> Workspace(Guid id) =>
        State.Workspaces.TryFirst(w => w.Id == id).ToResult($"Workspace {id} not found.");

    void PersistInventory(Guid workspaceId)
    {
        var entries = memberships
            .Where(kv => kv.Value == workspaceId)
            .Select(kv => knownWindows.GetValueOrDefault(kv.Key))
            .Where(w => w?.ProcessPath is not null)
            .Select(w => new InventoryEntry(w!.ProcessPath!, w.CommandLine, ledger.OriginalTitle(w.Handle).GetValueOrDefault(w.Title)))
            .ToList();
        var inventory = State.Inventory.Where(kv => kv.Key != workspaceId)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        inventory[workspaceId] = entries;
        Persist(State with { Inventory = inventory });
    }

    void Persist(AppState next)
    {
        State = next;
        store.Save(next); // small JSON, synchronous write on every mutation is fine for v1
        stateChanged.OnNext(Unit.Default);
    }
}
