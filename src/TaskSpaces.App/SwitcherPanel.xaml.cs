using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using Microsoft.Win32;
using TaskSpaces.Core;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Overview;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Windows.Activation;

namespace TaskSpaces.App;

// The switcher: every window across every workspace in one place (spec) — the answer
// to "I need to see all windows, similar to taskbar, without changing desktop first".
// One instance lives for the app's lifetime; each summon rebuilds content fresh.
public partial class SwitcherPanel : Window
{
    readonly WorkspaceManager manager;
    readonly WindowActivator activator = new();
    readonly AppLauncher launcher = new();

    public SwitcherPanel(WorkspaceManager manager)
    {
        this.manager = manager;
        InitializeComponent();
        // Live refresh while open: windows appear/close and renames land as Petre watches.
        manager.StateChanged.Subscribe(_ => Dispatcher.Invoke(() => { if (IsVisible) Rebuild(); }));
    }

    public void Summon(double screenX, double screenY)
    {
        Rebuild();
        Left = Math.Max(0, screenX - 320);   // hug the tray corner, stay on-screen
        Top = Math.Max(0, screenY - 24 - 660);
        Show();
        Activate();
    }

    void OnDeactivated(object? s, EventArgs e) => Hide();
    void OnKeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Escape) Hide(); }

    void Rebuild()
    {
        GroupsHost.Children.Clear();
        manager.WindowsByWorkspace()
            .Tap(overview =>
            {
                if (overview.Pinned.Count > 0)
                    AddGroup("📌 Pinned", isCurrent: false, header: null, overview.Pinned.Select(r => RunningRow(r, pinned: true)));
                overview.Workspaces.ToList().ForEach(g => AddGroup(
                    $"{g.Workspace.Name} ({g.Running.Count})", g.IsCurrent, WorkspaceHeader(g),
                    g.Running.Select(r => RunningRow(r, pinned: false)).Concat(g.NotRunning.Select(e => RosterRow(g.Workspace.Id, e)))));
                overview.OtherDesktops.ToList().ForEach(g => AddGroup(
                    $"{g.Name} ({g.Windows.Count})", g.IsCurrent, header: null, g.Windows.Select(r => RunningRow(r, pinned: false))));
            })
            .TapError(err => GroupsHost.Children.Add(new TextBlock { Text = err, Margin = new Thickness(4) }));
    }

    // --- group scaffolding -------------------------------------------------------

    void AddGroup(string title, bool isCurrent, UIElement? header, IEnumerable<UIElement> rows)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        panel.Children.Add(header ?? new TextBlock { Text = title, FontWeight = isCurrent ? FontWeights.Bold : FontWeights.SemiBold, Margin = new Thickness(4, 2, 4, 2) });
        rows.ToList().ForEach(r => panel.Children.Add(r));
        GroupsHost.Children.Add(panel);
    }

    // Workspace headers are interactive: click = switch there; ▶ = start missing apps;
    // ＋ = manually roster an exe. Bold marks the workspace Petre is on right now.
    UIElement WorkspaceHeader(WorkspaceGroup group)
    {
        var header = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };

        var start = new Button { Content = "▶", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(4, 0, 0, 0), ToolTip = $"Start {group.Workspace.Name}: launch its {group.NotRunning.Count} not-running app(s) and switch there", Visibility = group.NotRunning.Count > 0 ? Visibility.Visible : Visibility.Collapsed };
        start.Click += (_, _) => Report(manager.StartWorkspace(group.Workspace.Id, launcher)).Tap(Hide);
        DockPanel.SetDock(start, Dock.Right);

        var add = new Button { Content = "＋", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Add app… (roster an exe in this workspace)" };
        add.Click += (_, _) => OnAddApp(group.Workspace.Id);
        DockPanel.SetDock(add, Dock.Right);

        var name = new Button { Content = $"{group.Workspace.Name} ({group.Running.Count})", FontWeight = group.IsCurrent ? FontWeights.Bold : FontWeights.SemiBold, HorizontalContentAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(0), Background = Brushes.Transparent, ToolTip = "Switch to this workspace" };
        name.Click += (_, _) => Report(manager.Switch(group.Workspace.Id)).Tap(Hide);

        header.Children.Add(start);
        header.Children.Add(add);
        header.Children.Add(name);
        return header;
    }

    // --- rows ----------------------------------------------------------------------

    UIElement RunningRow(WindowRow row, bool pinned)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = IconCache.For(row.Window.ProcessPath);
        if (icon is not null) content.Children.Add(new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) });
        content.Children.Add(new TextBlock { Text = row.Window.Title, FontWeight = row.OriginalTitle.HasValue ? FontWeights.SemiBold : FontWeights.Normal });
        // Renamed window: short name prominent, original title dimmed beside it (spec).
        row.OriginalTitle.Tap(original => content.Children.Add(new TextBlock { Text = $"  ·  was: {original}", Opacity = 0.55, TextTrimming = TextTrimming.CharacterEllipsis }));

        var button = new Button { Content = content, HorizontalContentAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(16, 2, 4, 2), ToolTip = row.Window.Title };
        button.Click += (_, _) => Report(manager.JumpTo(row.Window.Handle, activator)).Tap(Hide);
        button.ContextMenu = RunningMenu(row, pinned);
        return button;
    }

    ContextMenu RunningMenu(WindowRow row, bool pinned)
    {
        var menu = new ContextMenu();

        var pin = new MenuItem { Header = pinned ? "Unpin from all workspaces" : "Pin to all workspaces" };
        pin.Click += (_, _) => Report(pinned ? manager.UnpinWindow(row.Window.Handle) : manager.PinWindow(row.Window.Handle));
        menu.Items.Add(pin);

        var sendTo = new MenuItem { Header = "Send to" };
        manager.State.Workspaces.ToList().ForEach(w =>
        {
            var item = new MenuItem { Header = w.Name };
            item.Click += (_, _) => Report(manager.AssignWindow(row.Window.Handle, w.Id));
            sendTo.Items.Add(item);
        });
        menu.Items.Add(sendTo);
        menu.Items.Add(new Separator());

        var rename = new MenuItem { Header = "Rename…" };
        rename.Click += (_, _) => PromptDialog.Ask("Rename window", "Short name to show on the taskbar:", row.Window.Title)
            .Tap(shortName => Report(manager.RenameWindow(row.Window.Handle, shortName)));
        menu.Items.Add(rename);

        var restore = new MenuItem { Header = "Restore title", IsEnabled = row.OriginalTitle.HasValue };
        restore.Click += (_, _) => Report(manager.RestoreTitle(row.Window.Handle));
        menu.Items.Add(restore);
        return menu;
    }

    // Roster-only entry: the app BELONGS here but isn't running — dimmed, click to launch.
    // The panel stays open on purpose: the row flips to running as the window arrives,
    // and Petre can start several apps in a row (spec).
    UIElement RosterRow(Guid workspaceId, InventoryEntry entry)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Opacity = 0.55 };
        var icon = IconCache.For(entry.ProcessPath);
        if (icon is not null) content.Children.Add(new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) });
        content.Children.Add(new TextBlock { Text = $"{entry.Title}  (not running)", FontStyle = FontStyles.Italic });

        var button = new Button { Content = content, HorizontalContentAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(16, 2, 4, 2), ToolTip = entry.CommandLine ?? entry.ProcessPath };
        button.Click += (_, _) => Report(manager.StartRosterEntry(workspaceId, entry, launcher));

        var menu = new ContextMenu();
        var startOne = new MenuItem { Header = "Start" };
        startOne.Click += (_, _) => Report(manager.StartRosterEntry(workspaceId, entry, launcher));
        menu.Items.Add(startOne);
        var remove = new MenuItem { Header = "Remove from workspace" };
        remove.Click += (_, _) => Report(manager.RemoveRosterEntry(workspaceId, entry));
        menu.Items.Add(remove);
        button.ContextMenu = menu;
        return button;
    }

    void OnAddApp(Guid workspaceId)
    {
        var picker = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe", Title = "Add app to workspace" };
        if (picker.ShowDialog() != true) return;
        var arguments = PromptDialog.Ask("Arguments", "Optional command-line arguments (path+args identify WHAT the app shows):").GetValueOrDefault("");
        Report(manager.AddRosterEntry(workspaceId, picker.FileName, arguments).Map(_ => true));
    }

    static Result Report(Result result) => result.TapError(err => MessageBox.Show(err, "TaskSpaces"));
    static Result<T> Report<T>(Result<T> result) => result.TapError(err => MessageBox.Show(err, "TaskSpaces"));
}
