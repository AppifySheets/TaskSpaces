using System.Windows;
using System.Windows.Controls;
using TaskSpaces.Core;

namespace TaskSpaces.App;

// Per-workspace opt-in, spec's "restore session?" model. Shown once at startup and
// only when at least one workspace has remembered apps.
public partial class RehydratePrompt : Window
{
    readonly WorkspaceManager manager;
    readonly List<(CheckBox Box, Guid WorkspaceId)> checks = [];

    public RehydratePrompt(WorkspaceManager manager)
    {
        this.manager = manager;
        InitializeComponent();
        manager.State.Workspaces
            .Select(w => (Workspace: w, Entries: manager.State.Inventory.GetValueOrDefault(w.Id)))
            .Where(x => x.Entries is { Count: > 0 })
            .ToList()
            .ForEach(x =>
            {
                var box = new CheckBox { Content = $"{x.Workspace.Name} ({x.Entries!.Count} app(s))", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
                checks.Add((box, x.Workspace.Id));
                WorkspaceChecklist.Items.Add(box);
            });
    }

    public static bool HasAnythingToRestore(WorkspaceManager manager) =>
        manager.State.Workspaces.Any(w => manager.State.Inventory.GetValueOrDefault(w.Id) is { Count: > 0 });

    void OnRestore(object s, RoutedEventArgs e)
    {
        checks.Where(c => c.Box.IsChecked == true)
            .ToList()
            .ForEach(c => Rehydrator.Launch(manager, c.WorkspaceId, manager.State.Inventory[c.WorkspaceId]));
        Close();
    }

    void OnSkip(object s, RoutedEventArgs e) => Close();
}
