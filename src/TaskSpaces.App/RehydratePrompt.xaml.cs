using System.Windows;
using System.Windows.Controls;
using TaskSpaces.Core;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rehydration;

namespace TaskSpaces.App;

// Per-workspace opt-in, spec's "restore session?" model. Shown once at startup and
// only when at least one workspace has remembered apps that are not already running.
public partial class RehydratePrompt : Window
{
    readonly WorkspaceManager manager;
    // Finding 4 (reviewer, Important): a plain app restart with the original apps still
    // open must not re-offer them as "restore?" — snapshot each workspace's inventory
    // filtered down to entries whose app isn't already live (RehydrationFilter, Core,
    // unit-tested) ONCE at construction, and use that snapshot everywhere below instead
    // of re-reading manager.State.Inventory directly.
    readonly Dictionary<Guid, IReadOnlyList<InventoryEntry>> surviving;
    readonly List<(CheckBox Box, Guid WorkspaceId)> checks = [];

    public RehydratePrompt(WorkspaceManager manager)
    {
        this.manager = manager;
        surviving = Surviving(manager);
        InitializeComponent();
        manager.State.Workspaces
            .Select(w => (Workspace: w, Entries: surviving.GetValueOrDefault(w.Id)))
            .Where(x => x.Entries is { Count: > 0 })
            .ToList()
            .ForEach(x =>
            {
                var box = new CheckBox { Content = $"{x.Workspace.Name} ({x.Entries!.Count} app(s))", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
                checks.Add((box, x.Workspace.Id));
                WorkspaceChecklist.Items.Add(box);
            });
    }

    static Dictionary<Guid, IReadOnlyList<InventoryEntry>> Surviving(WorkspaceManager manager) =>
        manager.State.Inventory.ToDictionary(kv => kv.Key, kv => RehydrationFilter.Surviving(kv.Value, manager.KnownWindows));

    public static bool HasAnythingToRestore(WorkspaceManager manager) =>
        Surviving(manager).Values.Any(entries => entries.Count > 0);

    void OnRestore(object s, RoutedEventArgs e)
    {
        // Finding 6 (reviewer, Important): the prompt is modeless — a workspace can be
        // removed via the Manage window while this is still open. GetValueOrDefault (not
        // the indexer) means a removed workspace's checkbox is simply a no-op instead of
        // throwing KeyNotFoundException.
        checks.Where(c => c.Box.IsChecked == true)
            .ToList()
            .ForEach(c => Rehydrator.Launch(manager, c.WorkspaceId, surviving.GetValueOrDefault(c.WorkspaceId, [])));
        Close();
    }

    void OnSkip(object s, RoutedEventArgs e) => Close();
}
