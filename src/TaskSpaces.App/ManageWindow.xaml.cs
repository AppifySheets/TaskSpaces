using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
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

    // Windows-tab row: the window + which workspace it is ACTUALLY on (ground truth
    // via the overview — "Pinned" for pinned, the desktop's own name for desktops no
    // workspace owns) + both titles when renamed.
    public sealed record WindowTabRow(WindowInfo Window, string Workspace, Maybe<string> OriginalTitle)
    {
        public ImageSource? Icon => IconCache.For(Window.ProcessPath);
        public string DisplayTitle => OriginalTitle.HasValue ? $"{Window.Title}  ·  was: {OriginalTitle.Value}" : Window.Title;
    }

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
        Reload();
    }

    // Reviewer (fix round 1, Important): reassigning ItemsSource wholesale drops the
    // current selection, so every button click ("Rename", "Send to workspace", ...)
    // silently deselects the very row the user just acted on — annoying for the common
    // case of doing several operations on the same workspace/window in a row. Capture the
    // selected *identity* (Id / Handle — never the object reference, since Reload() always
    // rebuilds fresh instances) before rebinding, and re-select the matching item after.
    void Reload()
    {
        var selectedWorkspaceId = (WorkspaceList.SelectedItem as Workspace)?.Id;
        var selectedAssignTargetId = (AssignTarget.SelectedItem as Workspace)?.Id;
        var selectedWindowHandle = (WindowList.SelectedItem as WindowTabRow)?.Window.Handle;

        WorkspaceList.ItemsSource = manager.State.Workspaces;
        AssignTarget.ItemsSource = manager.State.Workspaces;
        var windowRows = WindowRows();
        WindowList.ItemsSource = windowRows;
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
        if (selectedAssignTargetId is { } atId)
            AssignTarget.SelectedItem = manager.State.Workspaces.FirstOrDefault(w => w.Id == atId);
        if (selectedWindowHandle is { } handle)
            WindowList.SelectedItem = windowRows.FirstOrDefault(r => r.Window.Handle == handle);
    }

    // Windows-tab rows, keyed to actual ground truth: WindowsByWorkspace() (Task 5) tells us
    // which workspace/desktop each window is REALLY on right now, not just its roster
    // assignment. Falls back to KnownWindows (no workspace grouping) in compatibility mode,
    // where WindowsByWorkspace() always fails — the tab must still list windows.
    IReadOnlyList<WindowTabRow> WindowRows() =>
        manager.WindowsByWorkspace()
            .Map(o => (IReadOnlyList<WindowTabRow>)
                [.. o.Pinned.Select(r => new WindowTabRow(r.Window, "Pinned", r.OriginalTitle)),
                 .. o.Workspaces.SelectMany(g => g.Running.Select(r => new WindowTabRow(r.Window, g.Workspace.Name, r.OriginalTitle))),
                 .. o.OtherDesktops.SelectMany(g => g.Windows.Select(r => new WindowTabRow(r.Window, g.Name, r.OriginalTitle)))])
            .GetValueOrDefault([.. manager.KnownWindows.Select(w => new WindowTabRow(w, "—", Maybe<string>.None))]);

    void OnAddWorkspace(object s, RoutedEventArgs e) => Report(manager.AddWorkspace(NewWorkspaceName.Text).Map(_ => true)).Tap(Reload);
    void OnRenameWorkspace(object s, RoutedEventArgs e) => WithSelectedWorkspace(w => manager.RenameWorkspace(w.Id, NewWorkspaceName.Text));
    void OnRemoveWorkspace(object s, RoutedEventArgs e) => WithSelectedWorkspace(w => manager.RemoveWorkspace(w.Id));
    void OnAssignWindow(object s, RoutedEventArgs e) =>
        WithSelectedWindow(w => AssignTarget.SelectedItem is Workspace target
            ? manager.AssignWindow(w.Handle, target.Id)
            : Result.Failure("Pick a target workspace first."));
    void OnRenameWindow(object s, RoutedEventArgs e) => WithSelectedWindow(w => manager.RenameWindow(w.Handle, ShortName.Text));
    void OnRestoreTitle(object s, RoutedEventArgs e) => WithSelectedWindow(w => manager.RestoreTitle(w.Handle));
    void OnRefreshWindows(object s, RoutedEventArgs e) => Reload();
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

    Result<bool> WithSelectedWindow(Func<WindowInfo, Result> action) =>
        (WindowList.SelectedItem is WindowTabRow row ? action(row.Window) : Result.Failure("Select a window first."))
            .Map(() => true)
            .Tap(Reload)
            .TapError(err => MessageBox.Show(err));

    static Result<bool> Report(Result<bool> result) => result.TapError(err => MessageBox.Show(err));
}
