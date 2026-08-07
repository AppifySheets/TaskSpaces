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
            FloatingBar = new FloatingBarState(120.5, 40.25, true),
            PinnedApps = [new InventoryEntry(@"C:\Programs\Beeper.exe", @"""C:\Programs\Beeper.exe"" ", "Beeper")],
            DetachedApps = [new InventoryEntry(@"C:\Programs\obs.exe", null, "OBS")],
            SwitcherShortcut = "Win+Tab",
        };
    }

    // The switcher shortcut is an init property with a NON-empty default, unlike every other
    // optional field here, so both halves are worth pinning: a value written by the editor
    // must come back, and a file that predates the setting must load with the default rather
    // than with null.
    [Fact]
    public void Roundtrips_a_configured_switcher_shortcut()
    {
        var store = new JsonPersistenceStore(dir);
        Assert.True(store.Save(SampleState()).IsSuccess);

        Assert.Equal("Win+Tab", store.Load().Value.SwitcherShortcut);
    }

    [Fact]
    public void A_file_written_before_the_switcher_shortcut_existed_loads_with_the_default()
    {
        Directory.CreateDirectory(dir);
        // Exactly the shape an older build wrote: the four required members and nothing else.
        File.WriteAllText(Path.Combine(dir, "state.json"),
            """{"Workspaces":[],"WorkspaceRules":[],"RenameRules":[],"Inventory":{}}""");

        Assert.Equal(AppState.DefaultSwitcherShortcut, new JsonPersistenceStore(dir).Load().Value.SwitcherShortcut);
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
        Assert.Equal(state.FloatingBar, loaded.FloatingBar);
        // A pin that does not survive a save/load is the whole reported defect, so both new
        // placement lists are asserted explicitly rather than trusted to the record's shape.
        Assert.Equal(state.PinnedApps, loaded.PinnedApps);
        Assert.Equal(state.DetachedApps, loaded.DetachedApps);
    }

    [Fact]
    public void Old_state_file_without_placement_lists_loads_them_empty()
    {
        // Same back-compat contract as PersistedRenames and FloatingBar: every state.json
        // written before this change has no such keys, and must load as empty rather than
        // throwing or inventing a pinned app.
        Directory.CreateDirectory(dir);
        var oldState = new
        {
            Workspaces = Array.Empty<object>(),
            WorkspaceRules = Array.Empty<object>(),
            RenameRules = Array.Empty<object>(),
            Inventory = new Dictionary<string, object[]>(),
        };
        File.WriteAllText(Path.Combine(dir, "state.json"), System.Text.Json.JsonSerializer.Serialize(oldState));

        var loaded = new JsonPersistenceStore(dir).Load();

        Assert.True(loaded.IsSuccess);
        Assert.Empty(loaded.Value.PinnedApps);
        Assert.Empty(loaded.Value.DetachedApps);
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
    public void Old_state_file_without_FloatingBar_loads_as_null_ie_hidden_at_default_position()
    {
        // Same back-compat contract as PersistedRenames above: the floating bar is a
        // brand-new (Task 11) feature, so every state.json written before this task has
        // no such key at all. It must deserialize to null (hidden, default position),
        // never throw and never fabricate a visible bar out of nowhere.
        Directory.CreateDirectory(dir);
        var oldState = new
        {
            workspaces = new object[0],
            workspaceRules = new object[0],
            renameRules = new object[0],
            inventory = new object[0],
            // Note: no FloatingBar key
        };
        var json = System.Text.Json.JsonSerializer.Serialize(oldState);
        File.WriteAllText(Path.Combine(dir, "state.json"), json);

        var loaded = new JsonPersistenceStore(dir).Load();

        Assert.True(loaded.IsSuccess);
        Assert.Null(loaded.Value.FloatingBar);
    }

    [Fact]
    public void Save_leaves_no_temp_file_behind()
    {
        var store = new JsonPersistenceStore(dir);
        store.Save(SampleState());
        Assert.Single(Directory.GetFiles(dir));
    }

    // Finding 5 (reviewer, Important): RuleMatchKind (and any future enum) must serialize
    // as its name, not a bare int -- unreadable on disk and a silent renumbering hazard.
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
