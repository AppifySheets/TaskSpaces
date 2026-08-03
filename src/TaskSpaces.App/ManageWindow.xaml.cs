using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using CSharpFunctionalExtensions;
using TaskSpaces.Core;
using TaskSpaces.Core.Domain;
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

    // The floating bar lives in the App composition root, so Manage reaches it through a
    // callback rather than holding the window. The state is a FUNC, not a bool: the bar can
    // also be hidden from its own right-click menu, which would leave a by-value copy stale.
    readonly Action toggleFloatingBar;
    // Guards the checkbox's own event from acting on a programmatic update (setting IsChecked
    // in the constructor would otherwise fire OnFloatingBarToggled and toggle the bar off).
    bool suppressFloatingBarEvent;

    public ManageWindow(WorkspaceManager manager, bool compatibilityMode, Action toggleFloatingBar, Func<bool> floatingBarVisible)
    {
        this.manager = manager;
        this.toggleFloatingBar = toggleFloatingBar;
        InitializeComponent();
        if (compatibilityMode) CompatBanner.Visibility = Visibility.Visible;
        StartWithWindows.IsChecked = StartupRegistration.IsEnabled;
        suppressFloatingBarEvent = true;
        ShowFloatingBar.IsChecked = floatingBarVisible();
        // Same gate the tray item had: every bar icon click calls JumpTo, which needs a real
        // desktop to switch to. Disabled rather than hidden, so it stays discoverable.
        ShowFloatingBar.IsEnabled = !compatibilityMode;
        suppressFloatingBarEvent = false;
        WorkspaceRulesGrid.ItemsSource = workspaceRules;
        RenameRulesGrid.ItemsSource = renameRules;
        // Task 10: the Windows tab is now the shared WindowGroupsView (same control the
        // switcher panel uses). No runChildDialog/afterAction override needed — this
        // window doesn't hide-on-deactivate like the panel does, so the default
        // pass-through dialog runner and a no-op afterAction are exactly right; live
        // refresh on manager.StateChanged is the view's own responsibility now.
        WindowGroups.Bind(manager);
        Reload();
    }

    // Reviewer (fix round 1, Important): reassigning ItemsSource wholesale drops the
    // current selection, so every button click ("Rename", "Remove", ...) silently
    // deselects the very row the user just acted on — annoying for the common case of
    // doing several operations on the same workspace in a row. Capture the selected
    // *identity* (Id — never the object reference, since Reload() always rebuilds fresh
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
    // Explicit manual refresh, kept per spec alongside the view's own live StateChanged
    // refresh — useful right after an error (e.g. a transient WindowsByWorkspace()
    // failure) without waiting for the next state mutation.
    void OnRefreshWindows(object s, RoutedEventArgs e) => WindowGroups.Refresh();
    void OnStartupToggled(object s, RoutedEventArgs e)
    {
        if (StartWithWindows.IsChecked == true) StartupRegistration.Enable(); else StartupRegistration.Disable();
    }

    void OnFloatingBarToggled(object s, RoutedEventArgs e)
    {
        if (suppressFloatingBarEvent) return;
        toggleFloatingBar();
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
        // throws ArgumentException on any duplicate key and — with no unhandled-exception
        // handler at the time this bug was found — took the whole process down mid-save.
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
