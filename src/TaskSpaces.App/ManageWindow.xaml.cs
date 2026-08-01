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

    void Reload()
    {
        WorkspaceList.ItemsSource = manager.State.Workspaces;
        AssignTarget.ItemsSource = manager.State.Workspaces;
        WindowList.ItemsSource = manager.KnownWindows;
        workspaceRules.Clear();
        manager.State.WorkspaceRules.ToList().ForEach(r => workspaceRules.Add(new WorkspaceRuleRow
        {
            Workspace = manager.State.Workspaces.FirstOrDefault(w => w.Id == r.WorkspaceId)?.Name ?? "?",
            Kind = r.Kind,
            Pattern = r.Pattern,
        }));
        renameRules.Clear();
        manager.State.RenameRules.ToList().ForEach(r => renameRules.Add(new RenameRuleRow { Kind = r.Kind, Pattern = r.Pattern, ShortName = r.ShortName }));
    }

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

        var byName = manager.State.Workspaces.ToDictionary(w => w.Name, w => w.Id, StringComparer.OrdinalIgnoreCase);
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
        (WindowList.SelectedItem is WindowInfo w ? action(w) : Result.Failure("Select a window first."))
            .Map(() => true)
            .Tap(Reload)
            .TapError(err => MessageBox.Show(err));

    static Result<bool> Report(Result<bool> result) => result.TapError(err => MessageBox.Show(err));
}
