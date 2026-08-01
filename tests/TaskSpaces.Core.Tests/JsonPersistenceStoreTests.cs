using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rules;

namespace TaskSpaces.Core.Tests;

public sealed class JsonPersistenceStoreTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"taskspaces-tests-{Guid.NewGuid():N}");

    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }

    static AppState SampleState()
    {
        var work = new Workspace(Guid.NewGuid(), "Work", Guid.NewGuid());
        return new AppState(
            [work, new Workspace(Guid.NewGuid(), "Personal", null)],
            [new WorkspaceRule(work.Id, RuleMatchKind.ProcessName, "devenv")],
            [new RenameRule(RuleMatchKind.TitleRegex, "Remote Desktop", "RDP")],
            new Dictionary<Guid, IReadOnlyList<InventoryEntry>>
            {
                [work.Id] = [new InventoryEntry(@"C:\Windows\System32\mstsc.exe", null, "RDP")],
            });
    }

    [Fact]
    public void Roundtrips_full_state()
    {
        var store = new JsonPersistenceStore(dir);
        var state = SampleState();

        Assert.True(store.Save(state).IsSuccess);
        var loaded = store.Load().Value;

        Assert.Equal(state.Workspaces, loaded.Workspaces);
        Assert.Equal(state.WorkspaceRules, loaded.WorkspaceRules);
        Assert.Equal(state.RenameRules, loaded.RenameRules);
        Assert.Equal(state.Inventory.Keys, loaded.Inventory.Keys);
        Assert.Equal(state.Inventory.Values.Single(), loaded.Inventory.Values.Single());
    }

    [Fact]
    public void Missing_file_loads_empty_state_not_failure()
    {
        var loaded = new JsonPersistenceStore(dir).Load();
        Assert.True(loaded.IsSuccess);
        Assert.Empty(loaded.Value.Workspaces);
    }

    [Fact]
    public void Corrupt_file_is_a_failure_not_an_exception()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "state.json"), "{ not json !!!");
        Assert.True(new JsonPersistenceStore(dir).Load().IsFailure);
    }

    [Fact]
    public void Save_leaves_no_temp_file_behind()
    {
        var store = new JsonPersistenceStore(dir);
        store.Save(SampleState());
        Assert.Single(Directory.GetFiles(dir));
    }
}
