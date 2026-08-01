using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Abstractions;

// The ONLY doorway to the undocumented virtual-desktop COM API (spec: isolate the risk).
// Every method returns Result: desktops vanish, windows close mid-move, and the COM
// layer can be entirely unsupported after an OS update — all expected, none fatal.
public interface IVirtualDesktopService
{
    // Probes COM support once at startup. Failure => app runs in "compatibility mode":
    // UI still lists workspaces but shows a banner and attempts no desktop operations.
    Result Initialize();

    Result<IReadOnlyList<DesktopInfo>> GetDesktops();
    Result<DesktopInfo> Create(string name);
    Result Rename(Guid desktopId, string name);
    Result Switch(Guid desktopId);
    Result Remove(Guid desktopId);
    Result MoveWindow(WindowHandle window, Guid desktopId);
    Result<Guid> DesktopOf(WindowHandle window);

    // Fires with the new desktop's id whenever the user switches by ANY means
    // (our UI, Win+Ctrl+arrows, Task View) — keeps the tray menu checkmark honest.
    IObservable<Guid> CurrentChanged { get; }
}
