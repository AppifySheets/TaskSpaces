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
            })
        {
            PersistedRenames = [new PersistedRename("chrome", "Home - Google Chrome", "Home")],
        };
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
        Assert.Equal(state.PersistedRenames, loaded.PersistedRenames);
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
    public void Old_state_file_without_PersistedRenames_loads_as_empty_list()
    {
        // Backward compatibility: old state.json files have no PersistedRenames key
        // and should deserialize to an empty list (not null, not error).
        Directory.CreateDirectory(dir);
        var oldState = new
        {
            workspaces = new object[0],
            workspaceRules = new object[0],
            renameRules = new object[0],
            inventory = new object[0],
            // Note: no PersistedRenames key
        };
        var json = System.Text.Json.JsonSerializer.Serialize(oldState);
        File.WriteAllText(Path.Combine(dir, "state.json"), json);

        var loaded = new JsonPersistenceStore(dir).Load();

        Assert.True(loaded.IsSuccess);
        Assert.Empty(loaded.Value.PersistedRenames);
    }

    [Fact]
    public void Save_leaves_no_temp_file_behind()
    {
        var store = new JsonPersistenceStore(dir);
        store.Save(SampleState());
        Assert.Single(Directory.GetFiles(dir));
    }

    // Finding 5 (reviewer, Important): RuleMatchKind (and any future enum) must serialize
    // as its name, not a bare int — unreadable on disk and a silent renumbering hazard.
    [Fact]
    public void Enum_fields_serialize_as_names_not_bare_ints()
    {
        var store = new JsonPersistenceStore(dir);
        store.Save(SampleState());

        var raw = File.ReadAllText(Path.Combine(dir, "state.json"));

        Assert.Contains("\"TitleRegex\"", raw);   // RenameRule.Kind
        Assert.Contains("\"ProcessName\"", raw);  // WorkspaceRule.Kind
        Assert.DoesNotContain("\"Kind\": 0", raw);
        Assert.DoesNotContain("\"Kind\": 1", raw);
    }

    [Fact]
    public void Enum_fields_still_roundtrip_correctly_when_written_as_names()
    {
        var store = new JsonPersistenceStore(dir);
        var state = SampleState();
        store.Save(state);

        var loaded = store.Load().Value;

        Assert.Equal(RuleMatchKind.ProcessName, loaded.WorkspaceRules.Single().Kind);
        Assert.Equal(RuleMatchKind.TitleRegex, loaded.RenameRules.Single().Kind);
    }
}
