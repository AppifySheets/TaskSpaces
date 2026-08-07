using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rehydration;

namespace TaskSpaces.Core.Tests;

// One test per ruling in the spec's "Last placement IS the rule" section.
public class PlacementMemoryTests
{
    static readonly Guid Gepha = Guid.NewGuid();
    static readonly Guid Sparrow = Guid.NewGuid();

    const string BeeperPath = @"C:\Programs\BeeperTexts\Beeper.exe";
    static readonly InventoryEntry Beeper = new(BeeperPath, $"\"{BeeperPath}\" ", "Beeper");

    static WindowInfo BeeperWindow(nint handle = 1) =>
        new(new WindowHandle(handle), 42, "Beeper", BeeperPath, "Beeper | HRIS", $"\"{BeeperPath}\" ");

    static AppState StateWith(
        IReadOnlyList<InventoryEntry>? pinned = null,
        IReadOnlyList<InventoryEntry>? detached = null,
        Dictionary<Guid, IReadOnlyList<InventoryEntry>>? inventory = null) =>
        AppState.Empty with
        {
            PinnedApps = pinned ?? [],
            DetachedApps = detached ?? [],
            Inventory = inventory ?? new Dictionary<Guid, IReadOnlyList<InventoryEntry>>(),
        };

    [Fact]
    public void A_workspace_roster_entry_is_remembered_as_that_workspace()
    {
        var placement = PlacementMemory.For(BeeperWindow(), StateWith(inventory: new() { [Sparrow] = [Beeper] }));

        Assert.True(placement.HasValue);
        Assert.Equal(PlacementKind.Workspace, placement.Value.Kind);
        Assert.Equal(Sparrow, placement.Value.WorkspaceId);
    }

    // THE defect: a pinned app must be remembered as pinned, so a recycled HWND gets
    // re-pinned instead of drifting into whichever workspace happens to be current.
    [Fact]
    public void A_pinned_app_is_remembered_as_pinned()
    {
        var placement = PlacementMemory.For(BeeperWindow(), StateWith(pinned: [Beeper]));

        Assert.True(placement.HasValue);
        Assert.Equal(PlacementKind.Pinned, placement.Value.Kind);
    }

    [Fact]
    public void A_detached_app_is_remembered_as_detached()
    {
        var placement = PlacementMemory.For(BeeperWindow(), StateWith(detached: [Beeper]));

        Assert.True(placement.HasValue);
        Assert.Equal(PlacementKind.Detached, placement.Value.Kind);
    }

    // Pinned wins: a stale roster entry from before the pin must not drag the window back
    // into a workspace.
    [Fact]
    public void Pinned_beats_a_leftover_workspace_roster_entry()
    {
        var placement = PlacementMemory.For(
            BeeperWindow(),
            StateWith(pinned: [Beeper], inventory: new() { [Sparrow] = [Beeper] }));

        Assert.Equal(PlacementKind.Pinned, placement.Value.Kind);
    }

    [Fact]
    public void An_app_nobody_claims_is_not_remembered() =>
        Assert.False(PlacementMemory.For(BeeperWindow(), StateWith()).HasValue);

    // Defensive: AddEntry maintains one-workspace-per-identity, so this only arises from a
    // hand-edited state.json. Skip rather than guess -- a wrong guess moves a window
    // somewhere Petre never put it.
    [Fact]
    public void An_identity_claimed_by_two_workspaces_is_skipped_not_guessed()
    {
        var placement = PlacementMemory.For(
            BeeperWindow(),
            StateWith(inventory: new() { [Sparrow] = [Beeper], [Gepha] = [Beeper] }));

        Assert.False(placement.HasValue);
    }

    // Identity is path+args, so the same exe showing different content is remembered
    // separately -- the content-based membership decision, applied to placement memory.
    [Fact]
    public void The_same_exe_with_different_arguments_is_a_different_identity()
    {
        const string code = @"C:\Programs\Code.exe";
        var corne = new InventoryEntry(code, $"\"{code}\" corne-config", "corne");
        var taskspaces = new WindowInfo(new WindowHandle(9), 7, "Code", code, "TaskSpaces", $"\"{code}\" taskspaces");

        var placement = PlacementMemory.For(taskspaces, StateWith(inventory: new() { [Sparrow] = [corne] }));

        Assert.False(placement.HasValue);
    }

    // An elevated window exposes no process path, so there is no identity to remember.
    [Fact]
    public void A_window_with_no_process_path_is_not_remembered()
    {
        var elevated = new WindowInfo(new WindowHandle(5), 3, "admin", null, "Administrator", null);

        Assert.False(PlacementMemory.For(elevated, StateWith(pinned: [Beeper])).HasValue);
    }
}
