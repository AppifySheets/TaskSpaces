using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using TaskSpaces.Core;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rules;

namespace TaskSpaces.App;

// Code-behind, not MVVM: three windows, no view-state worth abstracting. Every handler
// is a thin adapter onto WorkspaceManager, which owns all behavior (and all the tests).
public partial class ManageWindow : Window
{
    // DataGrid needs mutable rows; the domain records are immutable. These DTOs are the bridge.
    public sealed class WorkspaceRuleRow { public string Workspace { get; set; } = ""; public RuleMatchKind Kind { get; set; } public string Pattern { get; set; } = ""; }
    public sealed class RenameRuleRow { public RuleMatchKind Kind { get; set; } public string Pattern { get; set; } = ""; public string ShortName { get; set; } = ""; }

    readonly WorkspaceManager manager;
    readonly ObservableCollection<WorkspaceRuleRow> workspaceRules = [];
    readonly ObservableCollection<RenameRuleRow> renameRules = [];

    public ManageWindow(WorkspaceManager manager, bool compatibilityMode)
    {
        this.manager = manager;
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
