using System.Reactive.Linq;
using System.Reactive.Subjects;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Core.Tests;

// In-memory desktop "shell": desktops exist, windows sit on desktops, switches recorded.
public sealed class FakeDesktops : IVirtualDesktopService
{
    public List<DesktopInfo> Desktops { get; } = [];
    public Dictionary<WindowHandle, Guid> WindowPlacements { get; } = [];
    public List<Guid> Switches { get; } = [];
    public Subject<Guid> CurrentChangedSubject { get; } = new();

    // Fix round 1: lets tests force MoveWindow to fail for a specific handle, so
    // WorkspaceManager's failure-propagation path can be exercised on demand.
    public HashSet<WindowHandle> RejectMoveFor { get; } = [];

    public Result Initialize() => Result.Success();
    public Result<IReadOnlyList<DesktopInfo>> GetDesktops() => Result.Success<IReadOnlyList<DesktopInfo>>(Desktops.ToList());
    public Result<DesktopInfo> Create(string name)
    {
        var d = new DesktopInfo(Guid.NewGuid(), name);
        Desktops.Add(d);
        return d;
    }
    public Result Rename(Guid id, string name) =>
        Result.SuccessIf(Desktops.RemoveAll(d => d.Id == id) > 0, "missing").Tap(() => Desktops.Add(new DesktopInfo(id, name)));
    public Result Switch(Guid id) { Switches.Add(id); return Result.Success(); }
    public Result Remove(Guid id) { Desktops.RemoveAll(d => d.Id == id); return Result.Success(); }
    public Result MoveWindow(WindowHandle w, Guid id)
    {
        if (RejectMoveFor.Contains(w)) return Result.Failure("move rejected (test)");
        WindowPlacements[w] = id;
        return Result.Success();
    }
    public Result<Guid> DesktopOf(WindowHandle w) =>
        WindowPlacements.TryGetValue(w, out var id) ? id : Result.Failure<Guid>("not placed");
    public IObservable<Guid> CurrentChanged => CurrentChangedSubject.AsObservable();
}

public sealed class FakeMonitor : IWindowMonitor
{
    public Subject<WindowEvent> Subject { get; } = new();
    public List<WindowInfo> InitialWindows { get; } = [];
    public Result Start() => Result.Success();
    public IObservable<WindowEvent> Events => Subject.AsObservable();
    public IReadOnlyList<WindowInfo> Snapshot() => InitialWindows.ToList();
}

public sealed class FakeTitles : IWindowTitles
{
    public Dictionary<WindowHandle, string> Titles { get; } = [];

    // Fix round 1: lets tests force Set to fail for a specific handle (e.g. simulating
    // a hung/closed window WM_SETTEXT failure) without touching the recorded title.
    public HashSet<WindowHandle> RejectSetFor { get; } = [];

    public Result Set(WindowHandle w, string title)
    {
        if (RejectSetFor.Contains(w)) return Result.Failure("set rejected (test)");
        Titles[w] = title;
        return Result.Success();
    }
    public Result<string> Get(WindowHandle w) => Titles.TryGetValue(w, out var t) ? t : "";
}

public sealed class FakeStore : IPersistenceStore
{
    public AppState Stored { get; set; } = AppState.Empty;
    public int SaveCount { get; private set; }

    // Finding 1: lets tests simulate a corrupt state.json (JsonPersistenceStore.Load()
    // returning failure) without touching the filesystem.
    public bool FailLoad { get; set; }

    public Result<AppState> Load() => FailLoad ? Result.Failure<AppState>("corrupt state.json (test)") : Stored;
    public Result Save(AppState state) { Stored = state; SaveCount++; return Result.Success(); }
}
