using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using TaskSpaces.Core;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Time;
using TaskSpaces.Core.Rules;

namespace TaskSpaces.App;

// Code-behind, not MVVM: three windows, no view-state worth abstracting. Every handler
// is a thin adapter onto WorkspaceManager, which owns all behavior (and all the tests).
public partial class ManageWindow : Window
{
    // DataGrid needs mutable rows; the domain records are immutable. These DTOs are the bridge.
    public sealed class WorkspaceRuleRow { public string Workspace { get; set; } = ""; public RuleMatchKind Kind { get; set; } public string Pattern { get; set; } = ""; }
    public sealed class RenameRuleRow { public RuleMatchKind Kind { get; set; } public string Pattern { get; set; } = ""; public string ShortName { get; set; } = ""; }

    // One roster entry, as a row. Display is precomputed rather than templated because the part
    // worth reading is a JOIN of two fields -- the exe you would recognise it by, and the title it
    // last had -- and a DisplayMemberPath cannot compose those. The Entry itself rides along
    // because RemoveRosterEntry matches on identity, not on the text shown.
    public sealed class RosterRow
    {
        public required InventoryEntry Entry { get; init; }
        public required string Display { get; init; }
    }

    readonly WorkspaceManager manager;
    readonly ObservableCollection<WorkspaceRuleRow> workspaceRules = [];
    readonly ObservableCollection<RenameRuleRow> renameRules = [];

    // #53: read-only here. Manage shows what has been tracked; the tracker itself is owned by App,
    // which is where the timer that feeds it lives. Optional so the two tests that construct this
    // window without a tracker keep working, and so a compatibility-mode start has one less thing
    // that must exist.
    readonly TimeTracker? timeTracker;

    public ManageWindow(WorkspaceManager manager, bool compatibilityMode, TimeTracker? timeTracker = null)
    {
        this.manager = manager;
        this.timeTracker = timeTracker;
        InitializeComponent();
        if (compatibilityMode) CompatBanner.Visibility = Visibility.Visible;
        StartWithWindows.IsChecked = StartupRegistration.IsEnabled;
        WorkspaceRulesGrid.ItemsSource = workspaceRules;
        RenameRulesGrid.ItemsSource = renameRules;
        // Task 10: the Windows tab is now the shared WindowGroupsView (same control the
        // switcher panel uses). No runChildDialog/afterAction override needed -- this
        // window doesn't hide-on-deactivate like the panel does, so the default
        // pass-through dialog runner and a no-op afterAction are exactly right; live
        // refresh on manager.StateChanged is the view's own responsibility now.
        Reload();
    }

    // Reviewer (fix round 1, Important): reassigning ItemsSource wholesale drops the
    // current selection, so every button click ("Rename", "Remove", ...) silently
    // deselects the very row the user just acted on -- annoying for the common case of
    // doing several operations on the same workspace in a row. Capture the selected
    // *identity* (Id -- never the object reference, since Reload() always rebuilds fresh
    // instances) before rebinding, and re-select the matching item after.
    void Reload()
    {
        var selectedWorkspaceId = (WorkspaceList.SelectedItem as Workspace)?.Id;

        WorkspaceList.ItemsSource = manager.State.Workspaces;
        workspaceRules.Clear();
        manager.State.WorkspaceRules.ToList().ForEach(r => workspaceRules.Add(new WorkspaceRuleRow
        {
            Workspace = manager.State.Workspaces.FirstOrDefault(w => w.Id == r.WorkspaceId)?.Name ?? "?",
            Kind = r.Kind,
            Pattern = r.Pattern,
        }));
        renameRules.Clear();
        manager.State.RenameRules.ToList().ForEach(r => renameRules.Add(new RenameRuleRow { Kind = r.Kind, Pattern = r.Pattern, ShortName = r.ShortName }));

        if (selectedWorkspaceId is { } wsId)
            WorkspaceList.SelectedItem = manager.State.Workspaces.FirstOrDefault(w => w.Id == wsId);

        // After the selection is restored, so it fills for the workspace that ends up selected
        // rather than for the one that happened to be selected before the rebind.
        ReloadRoster();
        ReloadTime();

        // Reads through WorkspaceManager, not AppState, so the box shows what is actually
        // BOUND -- including the fallback to the default when state.json holds something
        // unusable. Assigning Text raises TextChanged, which validates it for free.
        SwitcherShortcutBox.Text = manager.SwitcherShortcut;
    }

    // --- Shortcuts tab ------------------------------------------------------------------
    // Petre: "i want it configurable".

    // Validated as it is typed, which is precisely what Chord.Parse's Result was built for:
    // "so the editor UI can validate what someone typed BEFORE anything tries to register
    // it". Nothing is bound here -- this only says whether Apply would work.
    void OnSwitcherShortcutTyped(object s, RoutedEventArgs e) =>
        Chord.Parse(SwitcherShortcutBox.Text).Match(
            chord => Status(Describe(chord), ok: true),
            error => Status(error, ok: false));

    // Says what the chord will DO, not just that it parsed: a chord that already contains
    // Shift has no free modifier left to mean "backwards", so the walk becomes forward-only.
    // Better to say so here than to let it be discovered as a missing feature.
    static string Describe(Chord chord) =>
        (chord.Modifiers & Chord.Shift) == 0
            ? $"Hold {chord.ModifiersText}, tap {chord.KeyText} to walk forwards, add Shift to walk backwards."
            : $"Hold {chord.ModifiersText}, tap {chord.KeyText} to walk. Forwards only: Shift is already part of the chord, so it cannot also mean \"backwards\".";

    void Status(string message, bool ok)
    {
        SwitcherShortcutStatus.Text = message;
        SwitcherShortcutStatus.Foreground = ok ? SystemColors.GrayTextBrush : Brushes.Firebrick;
    }

    // Persisting is all this does. App re-registers off StateChanged, so the new chord is
    // live immediately and a chord another app already owns reports itself from there.
    void OnApplySwitcherShortcut(object s, RoutedEventArgs e) =>
        Report(manager.SetSwitcherShortcut(SwitcherShortcutBox.Text).Map(() => true)).Tap(Reload);

    void OnResetSwitcherShortcut(object s, RoutedEventArgs e) =>
        Report(manager.SetSwitcherShortcut(AppState.DefaultSwitcherShortcut).Map(() => true)).Tap(Reload);

    // --- tracked time (#53) -----------------------------------------------------------------

    public sealed class TimeRow
    {
        public required string Workspace { get; init; }
        public required string Today { get; init; }
        public required string ThisWeek { get; init; }
        public required string LastMonth { get; init; }
    }

    readonly ObservableCollection<TimeRow> times = [];

    void ReloadTime()
    {
        TimeGrid.ItemsSource ??= times;
        times.Clear();

        if (timeTracker is null)
        {
            TimeStatus.Text = "Time tracking is not running.";
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        // The week Petre is IN, not the last seven days: "this week" means since Monday to
        // everyone who says it, and a rolling window would make Monday morning read as a full week
        // of work carried over from the last one.
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));

        manager.State.Workspaces.ToList().ForEach(w => times.Add(new TimeRow
        {
            Workspace = w.Name,
            Today = Format(timeTracker.Time.On(w.Id, today)),
            ThisWeek = Format(timeTracker.Time.Between(w.Id, monday, today)),
            LastMonth = Format(timeTracker.Time.Between(w.Id, today.AddDays(-29), today)),
        }));

        // Says what the numbers cannot: a fresh install shows zeroes everywhere, and without this
        // line that is indistinguishable from tracking being broken.
        TimeStatus.Text = times.All(t => t.LastMonth == Format(TimeSpan.Zero))
            ? "Nothing tracked yet. Time accrues while you work in a workspace, in 15-second steps, and is written to time.json beside state.json."
            : $"Counted in 15-second steps while you are active; idle after {ActivityAccrual.IdleAfter.TotalMinutes:0} minutes without input.";
    }

    // Hours and minutes, never seconds: the numbers here are answers to "where did my day go", and
    // a seconds column would invite a precision the 15-second granularity cannot support.
    static string Format(TimeSpan time) =>
        time < TimeSpan.FromMinutes(1) ? "—"
        : time < TimeSpan.FromHours(1) ? $"{time.TotalMinutes:0}m"
        : $"{(int)time.TotalHours}h {time.Minutes:00}m";

    // --- roster (#55) ---------------------------------------------------------------------
    //
    // Petre's open thread: "No UI removes a roster entry (that lived on the deleted Windows tab),
    // so a wrong one needs hand-editing %APPDATA%\TaskSpaces\state.json."
    //
    // The roster is the workspace half of placement memory -- identity (exe path + args) ->
    // workspace, written on every placement and read to put an app back where it was last had --
    // so a wrong entry keeps sending an app somewhere unwanted for as long as it stands.
    // WorkspaceManager.RemoveRosterEntry has existed and been tested all along; nothing called it.
    readonly ObservableCollection<RosterRow> roster = [];

    void ReloadRoster()
    {
        // ItemsSource is assigned once, here, rather than on every reload: the collection is
        // observable, so clearing and refilling it updates the ListBox without dropping its
        // scroll position -- the same reason the rules grids are bound this way.
        RosterList.ItemsSource ??= roster;
        roster.Clear();

        // EVERY entry, not just the ones whose app is closed. The deleted Windows tab dimmed the
        // not-running ones, which answered a different question -- "what is open right now", which
        // the floating bar already answers far better. What this list is for is "what will be sent
        // here in future", and a running app's entry governs that just as much as a closed one's.
        if (WorkspaceList.SelectedItem is Workspace workspace)
            manager.State.Inventory.GetValueOrDefault(workspace.Id, [])
                .ToList()
                .ForEach(entry => roster.Add(new RosterRow { Entry = entry, Display = Describe(entry) }));

        OnRosterSelected(this, null!);
    }

    // The exe you would recognise it by, plus the title it last had when the two differ. Both are
    // needed: five Chrome profiles share an exe and are told apart only by the title, while a
    // title like "Inbox" says nothing about which app would open.
    static string Describe(InventoryEntry entry)
    {
        var exe = Path.GetFileNameWithoutExtension(entry.ProcessPath);
        return string.IsNullOrWhiteSpace(entry.Title) || entry.Title.Equals(exe, StringComparison.OrdinalIgnoreCase)
            ? exe
            : $"{exe} — {entry.Title}";
    }

    void OnWorkspaceSelected(object s, SelectionChangedEventArgs e) => ReloadRoster();

    // The button is disabled with nothing selected rather than reporting "nothing selected" after
    // the fact: there is no useful action to attempt, so there is nothing to explain.
    void OnRosterSelected(object s, SelectionChangedEventArgs e) =>
        RemoveRosterButton.IsEnabled = RosterList.SelectedItem is RosterRow;

    void OnRemoveRosterEntry(object s, RoutedEventArgs e)
    {
        if (WorkspaceList.SelectedItem is not Workspace workspace || RosterList.SelectedItem is not RosterRow row) return;
        Report(manager.RemoveRosterEntry(workspace.Id, row.Entry).Map(() => true)).Tap(Reload);
    }

    void OnAddWorkspace(object s, RoutedEventArgs e) => Report(manager.AddWorkspace(NewWorkspaceName.Text).Map(_ => true)).Tap(Reload);
    void OnRenameWorkspace(object s, RoutedEventArgs e) => WithSelectedWorkspace(w => manager.RenameWorkspace(w.Id, NewWorkspaceName.Text));
    void OnRemoveWorkspace(object s, RoutedEventArgs e) => WithSelectedWorkspace(w => manager.RemoveWorkspace(w.Id));

    // Petre: "i need to be able to move workspaces up or down in the manage window".
    // WithSelectedWorkspace already reloads and re-selects by Id, so the selection follows
    // the workspace as it moves and ↑ can simply be clicked repeatedly. MoveWorkspace
    // returns success for an out-of-range move, so hitting ↑ on the top row does nothing
    // rather than popping an error box.
    void OnMoveWorkspaceUp(object s, RoutedEventArgs e) => WithSelectedWorkspace(w => manager.MoveWorkspace(w.Id, -1));
    void OnMoveWorkspaceDown(object s, RoutedEventArgs e) => WithSelectedWorkspace(w => manager.MoveWorkspace(w.Id, +1));
    void OnStartupToggled(object s, RoutedEventArgs e)
    {
        if (StartWithWindows.IsChecked == true) StartupRegistration.Enable(); else StartupRegistration.Disable();
    }


    void OnSaveRules(object s, RoutedEventArgs e)
    {
        var invalid = workspaceRules.Where(r => r.Kind == RuleMatchKind.TitleRegex && !IsValidRegex(r.Pattern))
            .Select(r => r.Pattern)
            .Concat(renameRules.Where(r => r.Kind == RuleMatchKind.TitleRegex && !IsValidRegex(r.Pattern)).Select(r => r.Pattern))
            .ToList();
        if (invalid.Count > 0) { MessageBox.Show($"Invalid regex pattern(s):\n{string.Join("\n", invalid)}"); return; }

        // Reviewer (fix round 1, Critical, defense in depth): WorkspaceManager now rejects
        // duplicate names going forward, but state.json written before that guard existed
        // may still contain duplicates on disk. A plain `ToDictionary(w => w.Name, ...)`
        // throws ArgumentException on any duplicate key and -- with no unhandled-exception
        // handler at the time this bug was found -- took the whole process down mid-save.
        // GroupBy never throws on duplicates; first-match-wins here is an accepted, silent
        // tie-break (the root-cause fix means new duplicates can no longer be created).
        var byName = manager.State.Workspaces
            .GroupBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        var unknown = workspaceRules.Where(r => !byName.ContainsKey(r.Workspace)).Select(r => r.Workspace).ToList();
        if (unknown.Count > 0) { MessageBox.Show($"Unknown workspace(s):\n{string.Join("\n", unknown)}"); return; }

        Report(manager.SetRules(
            workspaceRules.Select(r => new WorkspaceRule(byName[r.Workspace], r.Kind, r.Pattern)).ToList(),
            renameRules.Select(r => new RenameRule(r.Kind, r.Pattern, r.ShortName)).ToList()).Map(() => true)).Tap(Reload);
    }

    static bool IsValidRegex(string pattern)
    {
        try { _ = Regex.IsMatch("", pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)); return true; }
        catch (ArgumentException) { return false; }
    }

    Result<bool> WithSelectedWorkspace(Func<Workspace, Result> action) =>
        (WorkspaceList.SelectedItem is Workspace w ? action(w) : Result.Failure("Select a workspace first."))
            .Map(() => true)
            .Tap(Reload)
            .TapError(err => MessageBox.Show(err));

    static Result<bool> Report(Result<bool> result) => result.TapError(err => MessageBox.Show(err));
}
