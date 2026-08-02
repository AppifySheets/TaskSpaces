using CSharpFunctionalExtensions;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rehydration;

namespace TaskSpaces.Core.Overview;

// Pure: all OS facts (pin states, desktop-of, desktop list, current) arrive as data,
// so every grouping rule is unit-testable without a single COM call.
public static class OverviewBuilder
{
    public static Core.Overview.Overview Build(
        AppState state,
        IReadOnlyList<WindowInfo> windows,
        Func<WindowHandle, Maybe<string>> originalTitleOf,
        ISet<WindowHandle> pinned,
        IReadOnlyDictionary<WindowHandle, Guid> desktopOf,
        IReadOnlyList<DesktopInfo> desktops,
        Guid currentDesktopId)
    {
        WindowRow Row(WindowInfo w) => new(w, originalTitleOf(w.Handle));

        var pinnedRows = windows.Where(w => pinned.Contains(w.Handle)).Select(Row).ToList();

        List<WindowRow> OnDesktop(Guid desktopId) => windows
            .Where(w => !pinned.Contains(w.Handle) && desktopOf.TryGetValue(w.Handle, out var d) && d == desktopId)
            .Select(Row).ToList();

        var workspaceGroups = state.Workspaces
            .Select(ws => new WorkspaceGroup(
                ws,
                ws.DesktopId == currentDesktopId,
                ws.DesktopId is { } id ? OnDesktop(id) : [],
                state.Inventory.GetValueOrDefault(ws.Id, []).Where(e => !RosterIdentity.IsRunning(e, windows)).ToList()))
            .ToList();

        var claimed = state.Workspaces.Where(w => w.DesktopId is not null).Select(w => w.DesktopId!.Value).ToHashSet();
        var otherDesktops = desktops
            .Where(d => !claimed.Contains(d.Id))
            .Select(d => new DesktopGroup(d.Id, d.Name, d.Id == currentDesktopId, OnDesktop(d.Id)))
            .Where(g => g.Windows.Count > 0) // an empty unbound desktop is noise, not information
            .ToList();

        return new(pinnedRows, workspaceGroups, otherDesktops);
    }
}
