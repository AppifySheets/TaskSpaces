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
        store.Load()
            .Tap(s => State = s)
            .Bind(_ => Reconcile())
            .Tap(() =>
            {
                monitor.Snapshot().ToList().ForEach(w => knownWindows[w.Handle] = w);
                subscription = monitor.Events.Subscribe(OnWindowEvent);
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
            case WindowEventKind.Disappeared: OnDisappeared(e.Window); break;
        }
    }

    void OnAppeared(WindowInfo window)
    {
        knownWindows[window.Handle] = window;

        // Rehydrated launches win over rules: we KNOW where that app belongs.
        var (remaining, placement) = pending.Match(window, now());
        pending = remaining;
        placement.Or(RulesEngine.MatchWorkspace(window, State.WorkspaceRules))
            .Tap(workspaceId => Place(window, workspaceId));

        RulesEngine.MatchRename(window, State.RenameRules)
            .Tap(shortName => ApplyRename(window, shortName));
    }

    void OnTitleChanged(WindowInfo window)
    {
        var previouslyUnknown = !knownWindows.ContainsKey(window.Handle);
        knownWindows[window.Handle] = window;
        if (previouslyUnknown) { OnAppeared(window); return; } // became taskbar-worthy late

        if (ledger.NeedsReapply(window.Handle, window.Title))
            ledger.AppliedName(window.Handle).Tap(name => titles.Set(window.Handle, name));
        else if (ledger.AppliedName(window.Handle).HasNoValue)
            // Not renamed yet — but the new title may now match a rename rule.
            RulesEngine.MatchRename(window, State.RenameRules)
                .Tap(shortName => ApplyRename(window, shortName));
    }

    void OnDisappeared(WindowInfo window)
    {
        knownWindows.Remove(window.Handle);
        ledger = ledger.Remove(window.Handle);
        if (memberships.Remove(window.Handle, out var workspaceId))
            PersistInventory(workspaceId);
    }

    void Place(WindowInfo window, Guid workspaceId) =>
        Workspace(workspaceId)
            .Bind(w => w.DesktopId is { } desktopId
                ? desktops.MoveWindow(window.Handle, desktopId)
                : Result.Failure("Workspace has no desktop (compatibility mode)."))
            .Tap(() =>
            {
                memberships[window.Handle] = workspaceId;
                PersistInventory(workspaceId);
            });

    void ApplyRename(WindowInfo window, string shortName)
    {
        // Ledger first (captures the original title), then the actual write.
        ledger = ledger.Apply(window.Handle, window.Title, shortName);
        titles.Set(window.Handle, shortName);
    }

    // --- UI-facing operations ---------------------------------------------------

    public Result Switch(Guid workspaceId) =>
        Workspace(workspaceId).Bind(w => w.DesktopId is { } id
            ? desktops.Switch(id)
            : Result.Failure("Workspace has no desktop (compatibility mode)."));

    public Result<Workspace> AddWorkspace(string name) =>
        Result.FailureIf(string.IsNullOrWhiteSpace(name), "Workspace name required")
            .Bind(() => desktops.Create(name))
            .Map(d => new Workspace(Guid.NewGuid(), name, d.Id))
            .Tap(w => Persist(State with { Workspaces = [.. State.Workspaces, w] }));

    public Result RenameWorkspace(Guid id, string name) =>
        Workspace(id)
            .Tap(w => { if (w.DesktopId is { } d) desktops.Rename(d, name); })
            .Tap(w => Persist(State with
            {
                Workspaces = State.Workspaces.Select(x => x.Id == id ? x with { Name = name } : x).ToList(),
            }));

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
            ? Result.Success().Tap(() => Place(info, workspaceId))
            : Result.Failure("Window no longer exists.");

    public Result RenameWindow(WindowHandle window, string shortName) =>
        knownWindows.TryGetValue(window, out var info)
            ? Result.Success().Tap(() => ApplyRename(info, shortName))
            : Result.Failure("Window no longer exists.");

    public Result RestoreTitle(WindowHandle window) =>
        ledger.OriginalTitle(window)
            .ToResult("Window was never renamed.")
            .Bind(original => titles.Set(window, original))
            .Tap(() => ledger = ledger.Remove(window));

    // App exit / crash-avoidance: leave every window exactly as we found it.
    public void RestoreAllTitles() => ledger.Handles.ToList().ForEach(h => RestoreTitle(h));

    // Rehydrator (Task 9) tells us "pid X / path Y belongs to workspace Z, expect it soon".
    public void RegisterPendingLaunch(int processId, string processPath, Guid workspaceId) =>
        pending = pending.Add(processId, processPath, workspaceId, now());

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
