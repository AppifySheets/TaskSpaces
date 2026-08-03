using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rules;

namespace TaskSpaces.Core.Tests;

public class WorkspaceManagerTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    WorkspaceManager Manager() => new(desktops, monitor, titles, store);

    static WindowInfo Chrome(nint hwnd = 0x10, string title = "Some Page - Chrome") =>
        new(new WindowHandle(hwnd), 100, "chrome", @"C:\chrome.exe", title, "chrome.exe --profile-directory=Default");

    (WorkspaceManager manager, Workspace work) StartedWithWorkWorkspace(params object[] rules)
    {
        var work = new Workspace(Guid.NewGuid(), "Work", null);
        store.Stored = AppState.Empty with
        {
            Workspaces = [work],
            WorkspaceRules = rules.OfType<WorkspaceRule>().ToList(),
            RenameRules = rules.OfType<RenameRule>().ToList(),
        };
        var manager = Manager();
        Assert.True(manager.Start().IsSuccess);
        return (manager, manager.State.Workspaces.Single());
    }

    [Fact]
    public void Start_creates_a_desktop_for_a_workspace_that_has_none()
    {
        var (manager, work) = StartedWithWorkWorkspace();
        Assert.NotNull(work.DesktopId);
        Assert.Contains(desktops.Desktops, d => d.Id == work.DesktopId && d.Name == "Work");
        Assert.Equal(work.DesktopId, store.Stored.Workspaces.Single().DesktopId); // persisted
    }

    [Fact]
    public void Start_rebinds_to_existing_desktop_by_name_instead_of_duplicating()
    {
        var existing = desktops.Create("Work").Value;
        var (_, work) = StartedWithWorkWorkspace();
        Assert.Equal(existing.Id, work.DesktopId);
        Assert.Single(desktops.Desktops);
    }

    [Fact]
    public void Appeared_window_matching_rule_is_moved_and_inventoried()
    {
        var (manager, work) = StartedWithWorkWorkspace();
        manager.SetRules([new WorkspaceRule(work.Id, RuleMatchKind.ProcessName, "chrome")], []);

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));

        Assert.Equal(work.DesktopId, desktops.WindowPlacements[new WindowHandle(0x10)]);
        Assert.Contains(store.Stored.Inventory[work.Id], e => e.ProcessPath == @"C:\chrome.exe");
    }

    [Fact]
    public void Appeared_window_without_matching_rule_is_left_alone()
    {
        var (manager, _) = StartedWithWorkWorkspace();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));
        Assert.Empty(desktops.WindowPlacements);
    }

    [Fact]
    public void Rename_rule_applies_short_name_on_appearance()
    {
        var (manager, _) = StartedWithWorkWorkspace(new RenameRule(RuleMatchKind.ProcessName, "chrome", "Amy related"));
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));
        Assert.Equal("Amy related", titles.Titles[new WindowHandle(0x10)]);
    }

    [Fact]
    public void Short_name_is_reapplied_when_app_rewrites_its_title()
    {
        var (manager, _) = StartedWithWorkWorkspace(new RenameRule(RuleMatchKind.ProcessName, "chrome", "Amy related"));
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));
        titles.Titles.Clear(); // forget the first application so we can observe the re-apply

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.TitleChanged, Chrome(title: "Other Page - Chrome")));

        Assert.Equal("Amy related", titles.Titles[new WindowHandle(0x10)]);
    }

    [Fact]
    public void Own_echo_titlechange_is_not_reapplied()
    {
        var (manager, _) = StartedWithWorkWorkspace(new RenameRule(RuleMatchKind.ProcessName, "chrome", "Amy related"));
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));
        titles.Titles.Clear();

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.TitleChanged, Chrome(title: "Amy related")));

        Assert.Empty(titles.Titles); // no write happened — loop is broken
    }

    [Fact]
    public void Manual_rename_and_restore_roundtrip()
    {
        var (manager, _) = StartedWithWorkWorkspace();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));

        Assert.True(manager.RenameWindow(new WindowHandle(0x10), "RDP").IsSuccess);
        Assert.Equal("RDP", titles.Titles[new WindowHandle(0x10)]);

        Assert.True(manager.RestoreTitle(new WindowHandle(0x10)).IsSuccess);
        Assert.Equal("Some Page - Chrome", titles.Titles[new WindowHandle(0x10)]);
    }

    [Fact]
    public void Manual_assignment_moves_window_and_wins_over_rules()
    {
        var (manager, work) = StartedWithWorkWorkspace();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));

        Assert.True(manager.AssignWindow(new WindowHandle(0x10), work.Id).IsSuccess);
        Assert.Equal(work.DesktopId, desktops.WindowPlacements[new WindowHandle(0x10)]);
    }

    [Fact]
    public void Switch_delegates_to_desktop_service()
    {
        var (manager, work) = StartedWithWorkWorkspace();
        Assert.True(manager.Switch(work.Id).IsSuccess);
        Assert.Equal(new[] { work.DesktopId!.Value }, desktops.Switches);
    }

    [Fact]
    public void SwitchToDesktop_delegates_raw_desktop_id()
    {
        // Floating-bar unbound-desktop rows switch by DESKTOP id, not workspace id —
        // no workspace lookup involved, any guid goes straight to the service.
        var (manager, _) = StartedWithWorkWorkspace();
        var unboundDesktop = Guid.NewGuid();
        Assert.True(manager.SwitchToDesktop(unboundDesktop).IsSuccess);
        Assert.Equal(new[] { unboundDesktop }, desktops.Switches);
    }

    [Fact]
    public void Disappeared_window_keeps_its_roster_entry()
    {
        // Superseded v1 behavior: inventory used to be "currently running members" and
        // emptied on close. The roster spec inverts this on purpose — a workspace lists
        // what BELONGS to it even when it isn't running (that's what ▶ Start launches).
        var (manager, work) = StartedWithWorkWorkspace();
        manager.SetRules([new WorkspaceRule(work.Id, RuleMatchKind.ProcessName, "chrome")], []);
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Disappeared, Chrome()));
        Assert.Contains(store.Stored.Inventory[work.Id], e => e.ProcessPath == @"C:\chrome.exe");
    }

    [Fact]
    public void Workspace_crud_persists()
    {
        var manager = Manager();
        Assert.True(manager.Start().IsSuccess);

        var added = manager.AddWorkspace("YouTube");
        Assert.True(added.IsSuccess);
        Assert.Contains(store.Stored.Workspaces, w => w.Name == "YouTube");

        Assert.True(manager.RenameWorkspace(added.Value.Id, "Video").IsSuccess);
        Assert.Contains(store.Stored.Workspaces, w => w.Name == "Video");
        Assert.Contains(desktops.Desktops, d => d.Name == "Video"); // desktop renamed too

        Assert.True(manager.RemoveWorkspace(added.Value.Id).IsSuccess);
        Assert.Empty(store.Stored.Workspaces);
    }

    [Fact]
    public void RestoreAllTitles_restores_every_renamed_window()
    {
        var (manager, _) = StartedWithWorkWorkspace(new RenameRule(RuleMatchKind.ProcessName, "chrome", "Amy related"));
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));

        manager.RestoreAllTitles();

        Assert.Equal("Some Page - Chrome", titles.Titles[new WindowHandle(0x10)]);
    }

    // --- Fix round 1: Result-based error propagation on the UI-facing surface ------

    [Fact]
    public void Assign_to_missing_workspace_returns_failure_and_moves_nothing()
    {
        var (manager, _) = StartedWithWorkWorkspace();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));

        var result = manager.AssignWindow(new WindowHandle(0x10), Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Empty(desktops.WindowPlacements);
    }

    [Fact]
    public void Rename_window_failure_from_titles_does_not_create_ledger_entry()
    {
        var (manager, _) = StartedWithWorkWorkspace();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));
        titles.RejectSetFor.Add(new WindowHandle(0x10));

        var result = manager.RenameWindow(new WindowHandle(0x10), "RDP");

        Assert.True(result.IsFailure);
        // No ledger entry was created for the failed rename, so restore has nothing to undo.
        Assert.True(manager.RestoreTitle(new WindowHandle(0x10)).IsFailure);
    }

    [Fact]
    public void Assign_window_move_failure_returns_failure()
    {
        var (manager, work) = StartedWithWorkWorkspace();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));
        desktops.RejectMoveFor.Add(new WindowHandle(0x10));

        var result = manager.AssignWindow(new WindowHandle(0x10), work.Id);

        Assert.True(result.IsFailure);
        Assert.False(desktops.WindowPlacements.ContainsKey(new WindowHandle(0x10)));
        Assert.False(store.Stored.Inventory.ContainsKey(work.Id));
    }

    // --- Fix round 1: duplicate workspace-name guard (reviewer: Critical) ----------
    // Without this guard, two workspaces can share a name; ManageWindow.OnSaveRules'
    // `ToDictionary(w => w.Name, ...)` then throws ArgumentException on save, taking the
    // whole process down (no unhandled-exception handler existed before this round either).

    [Fact]
    public void Add_duplicate_workspace_name_fails()
    {
        var (manager, work) = StartedWithWorkWorkspace();

        var result = manager.AddWorkspace("Work"); // exact case match against existing "Work"

        Assert.True(result.IsFailure);
        Assert.Single(store.Stored.Workspaces); // nothing new persisted
    }

    [Fact]
    public void Add_duplicate_workspace_name_fails_case_insensitively_and_trimmed()
    {
        var (manager, work) = StartedWithWorkWorkspace();

        var result = manager.AddWorkspace("  WORK  ");

        Assert.True(result.IsFailure);
        Assert.Single(store.Stored.Workspaces);
    }

    [Fact]
    public void Rename_workspace_to_existing_name_fails()
    {
        var manager = Manager();
        Assert.True(manager.Start().IsSuccess);
        var work = manager.AddWorkspace("Work").Value;
        var personal = manager.AddWorkspace("Personal").Value;

        var result = manager.RenameWorkspace(personal.Id, "Work");

        Assert.True(result.IsFailure);
        Assert.Contains(store.Stored.Workspaces, w => w.Id == personal.Id && w.Name == "Personal"); // unchanged
    }

    [Fact]
    public void Rename_workspace_to_its_own_name_succeeds()
    {
        var (manager, work) = StartedWithWorkWorkspace();

        // Same name, different case/whitespace — renaming a workspace to (a variant of)
        // its own current name must not be rejected as a "duplicate" of itself.
        var result = manager.RenameWorkspace(work.Id, "Work");

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Rename_workspace_to_blank_name_fails()
    {
        var (manager, work) = StartedWithWorkWorkspace();

        var result = manager.RenameWorkspace(work.Id, "   ");

        Assert.True(result.IsFailure);
        Assert.Contains(store.Stored.Workspaces, w => w.Id == work.Id && w.Name == "Work"); // unchanged
    }

    // --- Task 9: rehydration — pending launch placement beats rule matching --------

    [Fact]
    public void Pending_launch_placement_beats_rules()
    {
        var (manager, work) = StartedWithWorkWorkspace();
        var other = manager.AddWorkspace("Other").Value;
        manager.SetRules([new WorkspaceRule(other.Id, RuleMatchKind.ProcessName, "chrome")], []);

        manager.RegisterPendingLaunch(100, @"C:\chrome.exe", work.Id);
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));

        Assert.Equal(work.DesktopId, desktops.WindowPlacements[new WindowHandle(0x10)]);
    }

    // --- Finding 1 (reviewer, Critical): corrupt state.json must never be silently
    // overwritten. Start()/LoadState() must FAIL (not quietly fall back to empty state)
    // when the store can't be read, so the composition root can detect it, back up the
    // corrupt file, and only THEN retry with a guaranteed-empty state. -----------------

    [Fact]
    public void Start_fails_when_store_load_fails_and_does_not_touch_state()
    {
        store.FailLoad = true;
        var manager = Manager();

        var result = manager.Start();

        Assert.True(result.IsFailure);
        Assert.Empty(manager.State.Workspaces); // untouched default, not overwritten from a failed load
        Assert.Equal(0, store.SaveCount);       // never persisted anything over the corrupt file
    }

    [Fact]
    public void LoadState_on_missing_or_empty_store_succeeds_with_empty_state()
    {
        var manager = Manager();

        var result = manager.LoadState();

        Assert.True(result.IsSuccess);
        Assert.Empty(manager.State.Workspaces);
    }

    [Fact]
    public void LoadState_failure_leaves_previous_state_untouched()
    {
        store.Stored = AppState.Empty with { Workspaces = [new Workspace(Guid.NewGuid(), "Work", null)] };
        var manager = Manager();
        Assert.True(manager.Start().IsSuccess); // seed State with the good load first

        store.FailLoad = true;
        var result = manager.LoadState();

        Assert.True(result.IsFailure);
        Assert.Single(manager.State.Workspaces); // still the good state from before
    }

    [Fact]
    public void Start_retried_after_a_failed_load_succeeds_once_the_store_recovers()
    {
        // Mirrors the composition root's actual recovery path: Start() fails on a corrupt
        // file, the caller backs it up (out of scope for Core), then retries Start() —
        // which must now succeed because the store no longer reports a failure.
        store.FailLoad = true;
        var manager = Manager();
        Assert.True(manager.Start().IsFailure);

        store.FailLoad = false;
        var retried = manager.Start();

        Assert.True(retried.IsSuccess);
        Assert.Empty(manager.State.Workspaces);
    }

    // --- Finding 3 (reviewer, Important): Hidden vs Disappeared ---------------------
    // Hide-to-tray apps (Discord, Outlook, ...) still exist when they leave the taskbar.
    // Hidden must drop live-window bookkeeping like Disappeared does, but KEEP the ledger
    // entry, so a later re-show + RestoreTitle still returns the window's TRUE original
    // title rather than the short name we applied ourselves.

    [Fact]
    public void Hide_then_reshow_then_restore_returns_the_true_original_title()
    {
        var (manager, _) = StartedWithWorkWorkspace(new RenameRule(RuleMatchKind.ProcessName, "chrome", "Amy related"));
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome())); // renamed "Some Page - Chrome" -> "Amy related"
        Assert.Equal("Amy related", titles.Titles[new WindowHandle(0x10)]);

        // Hide-to-tray: the window still exists and still wears our short name (nothing
        // restored it in between) — the monitor reports Hidden, not Disappeared.
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Hidden, Chrome(title: "Amy related")));
        // Re-shown later (e.g. user opens it from the tray again).
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome(title: "Amy related")));

        Assert.True(manager.RestoreTitle(new WindowHandle(0x10)).IsSuccess);
        Assert.Equal("Some Page - Chrome", titles.Titles[new WindowHandle(0x10)]); // TRUE original, not "Amy related"
    }

    [Fact]
    public void Hide_then_destroy_still_drops_the_ledger_entry()
    {
        var (manager, _) = StartedWithWorkWorkspace(new RenameRule(RuleMatchKind.ProcessName, "chrome", "Amy related"));
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Hidden, Chrome(title: "Amy related")));
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Disappeared, Chrome(title: "Amy related")));

        // Ledger entry is gone — nothing left to restore (this is the pre-existing,
        // still-correct behavior for a window that's truly gone).
        Assert.True(manager.RestoreTitle(new WindowHandle(0x10)).IsFailure);
    }

    [Fact]
    public void Hidden_window_leaves_known_windows_but_keeps_its_roster_entry()
    {
        // Same roster-spec inversion as Disappeared (see
        // Disappeared_window_keeps_its_roster_entry above): Hidden must still drop live
        // bookkeeping (knownWindows/memberships), but the roster entry belongs to the
        // workspace regardless of whether the window is currently showing anywhere.
        var (manager, work) = StartedWithWorkWorkspace();
        manager.SetRules([new WorkspaceRule(work.Id, RuleMatchKind.ProcessName, "chrome")], []);
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));
        Assert.Contains(store.Stored.Inventory[work.Id], e => e.ProcessPath == @"C:\chrome.exe");

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Hidden, Chrome()));

        Assert.Contains(store.Stored.Inventory[work.Id], e => e.ProcessPath == @"C:\chrome.exe"); // roster entry stays
        Assert.DoesNotContain(manager.KnownWindows, w => w.Handle == new WindowHandle(0x10)); // dropped from known windows
    }

    // --- Fix wave (reviewer, Important): open/close must pulse StateChanged --------
    // v1's inventory-persisting Appeared/Disappeared path used to pulse StateChanged as
    // a side effect of Persist(). The roster rewrite added paths that don't necessarily
    // call Persist (e.g. Appeared with no rule match at all, Hidden, Disappeared with no
    // matching workspace rule) — an open panel/Windows tab then goes stale: dead rows for
    // windows that closed, missing rows for windows that opened, stale running-counts in
    // workspace headers. Every lifecycle event must pulse regardless of whether it also
    // happened to mutate persisted state.

    [Fact]
    public void Appeared_pulses_state_changed_even_with_no_rule_match()
    {
        // No rules registered: Chrome() is neither placed nor renamed, so nothing calls
        // Persist() for this event. The live UI (panel/Windows tab) still needs to learn
        // a new window showed up so it can add a row for it.
        var (manager, _) = StartedWithWorkWorkspace();
        var pulses = 0;
        using var subscription = manager.StateChanged.Subscribe(_ => pulses++);

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));

        Assert.True(pulses > 0);
    }

    [Fact]
    public void Hidden_and_disappeared_pulse_state_changed()
    {
        var (manager, _) = StartedWithWorkWorkspace();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));
        var pulses = 0;
        using var subscription = manager.StateChanged.Subscribe(_ => pulses++);

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Hidden, Chrome()));
        Assert.True(pulses > 0);

        var afterHidden = pulses;
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Disappeared, Chrome()));
        Assert.True(pulses > afterHidden);
    }

    // --- Task 11: floating icon bar — position/visibility persistence --------------

    [Fact]
    public void SaveFloatingBar_persists_state_and_pulses_StateChanged()
    {
        var manager = Manager();
        Assert.True(manager.Start().IsSuccess);
        var pulses = 0;
        using var subscription = manager.StateChanged.Subscribe(_ => pulses++);

        var result = manager.SaveFloatingBar(new FloatingBarState(50, 75, true));

        Assert.True(result.IsSuccess);
        Assert.Equal(new FloatingBarState(50, 75, true), store.Stored.FloatingBar);
        Assert.True(pulses > 0);
    }
}
