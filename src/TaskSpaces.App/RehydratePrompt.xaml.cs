using System.Windows;
using System.Windows.Controls;
using TaskSpaces.Core;

namespace TaskSpaces.App;

// Per-workspace opt-in "restore session?" at startup — now just a veneer over the
// roster: "these workspaces have apps that aren't running; start them?" The same
// NotRunningRoster filter powers the switcher's ▶ Start, so behavior can't drift.
public partial class RehydratePrompt : Window
{
    readonly WorkspaceManager manager;
    readonly AppLauncher launcher = new();
    readonly List<(CheckBox Box, Guid WorkspaceId)> checks = [];

    public RehydratePrompt(WorkspaceManager manager)
    {
        this.manager = manager;
        InitializeComponent();
        manager.State.Workspaces
            .Select(w => (Workspace: w, Missing: manager.NotRunningRoster(w.Id)))
            .Where(x => x.Missing.Count > 0)
            .ToList()
            .ForEach(x =>
            {
                var box = new CheckBox { Content = $"{x.Workspace.Name} ({x.Missing.Count} app(s))", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
                checks.Add((box, x.Workspace.Id));
                WorkspaceChecklist.Items.Add(box);
            });
    }

    public static bool HasAnythingToRestore(WorkspaceManager manager) =>
        manager.State.Workspaces.Any(w => manager.NotRunningRoster(w.Id).Count > 0);

    void OnRestore(object s, RoutedEventArgs e)
    {
        // StartRosterEntry per entry, NOT StartWorkspace: restoring three workspaces
        // must not desktop-switch three times. Entries are re-read at click time via
        // NotRunningRoster, which also makes a workspace removed while this modeless
        // prompt was open a harmless no-op (empty list).
        checks.Where(c => c.Box.IsChecked == true)
            .ToList()
            .ForEach(c => manager.NotRunningRoster(c.WorkspaceId).ToList()
                .ForEach(entry => manager.StartRosterEntry(c.WorkspaceId, entry, launcher)));
        Close();
    }

    void OnSkip(object s, RoutedEventArgs e) => Close();
}
