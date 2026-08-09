using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using TaskSpaces.App;
using TaskSpaces.Core;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Windows.Tests;

// Petre: "it crashed" -- the app died outright, and the Windows event log named the spot:
//
//   System.InvalidOperationException: 'System.Windows.Documents.Run' is not a Visual or Visual3D.
//      at System.Windows.Media.VisualTreeHelper.GetParent(DependencyObject reference)
//      at TaskSpaces.App.FloatingBar.StartedOnIcon(Object source)
//      at TaskSpaces.App.FloatingBar.OnPreviewMouseLeftButtonDown(...)
//
// The bar's press handler walks UP from whatever the press actually hit, looking for one of its
// tagged icon buttons. The info line is a TextBlock whose contents are Run inlines, and a Run is a
// ContentElement -- not a Visual. VisualTreeHelper.GetParent does not return null for one of those,
// it THROWS, and an exception from a mouse handler on the dispatcher takes the process with it.
//
// So the bar could be killed by a left-click on its own hint text. It had been possible for as
// long as the info line has had Runs in it; what made it show up now is that the line's text is
// the one part of the bar nobody had reason to press until the rows around it started changing.
//
// Reproduced by raising the real tunnelling event with a Run as its source, which is exactly what
// WPF does when that text is pressed -- no mouse, no message pump, and it fails on the unfixed
// code with the identical exception.
public class FloatingBarPressTests
{
    [Fact]
    public void Pressing_the_info_line_text_does_not_kill_the_bar() => StaThread.Run(() =>
    {
        var desktopId = Guid.NewGuid();
        var workspace = new Workspace(Guid.NewGuid(), "GEPHA", desktopId);

        var desktops = new PulsingDesktops { CurrentId = desktopId };
        desktops.Desktops.Add(new DesktopInfo(desktopId, "GEPHA"));

        var window = new WindowInfo(new WindowHandle(101), 11, "rdm", @"C:\rdm.exe", "Remote Desktop Manager", null);
        desktops.Placements[window.Handle] = desktopId;

        var monitor = new StubMonitor();
        monitor.Initial.Add(window);

        var store = new StubStore { Stored = AppState.Empty with { Workspaces = [workspace] } };
        var manager = new WorkspaceManager(desktops, monitor, new StubTitles(), store);
        Assert.True(manager.Start().IsSuccess);

        var bar = new FloatingBar(manager);
        // Parked off the virtual screen for the same reason FloatingBarRebuildTests does it: the
        // suite must never flash a real translucent topmost bar at whoever is running it.
        bar.Left = -32000;
        bar.Top = -32000;
        bar.Show();

        // The hint the bar shows when nothing is hovered ("hover an icon · drag icons between
        // rows · ctrl+drag to move"), which ClearInfo builds out of Runs. A REAL one from the
        // running bar rather than a Run made up here, so the test cannot drift from what the info
        // line is actually built out of.
        var info = (TextBlock)bar.FindName("Info")!;
        var run = info.Inlines.OfType<Run>().First();

        // Against the walkers themselves rather than through a routed event, and that is a
        // deliberate retreat: the first version of this test raised a real tunnelling
        // PreviewMouseLeftButtonDown on the Run, and it PASSED against the unfixed code -- WPF
        // does not route a tunnelling event raised on a ContentElement up to the window at all, so
        // the handler never ran and the test proved nothing. It only showed that once an assertion
        // was added that the press had arrived. What can be tested honestly is the rule that
        // actually broke: walking up from something that is not a Visual must not throw.
        //
        // The assertion IS that these return. Before the fix each threw
        // InvalidOperationException, which in the running app was an unhandled exception on the
        // dispatcher and took the process down.
        Assert.False(FloatingBar.StartedOnIcon(run));
        Assert.False(bar.StartedOnClickTarget(run));

        // ...and the walks still WORK for the case they exist to answer, so the fix cannot have
        // bought its safety by giving up and returning false everywhere. The ↩ button is a click
        // target; the info line's TextBlock sits beside it and is not.
        Assert.True(bar.StartedOnClickTarget((Button)bar.FindName("BackButton")!));
        Assert.False(bar.StartedOnClickTarget(info));

        // The whole point of the logical hop, and the half a "return false on text" fix would get
        // wrong: from a Run the walk must CROSS INTO the visual tree and carry on, not stop at the
        // text. This Run's TextBlock is inside Rows, so the answer has to be yes.
        var rows = (StackPanel)bar.FindName("Rows")!;
        var planted = new Run("planted");
        rows.Children.Add(new TextBlock { Inlines = { planted } });
        Assert.True(bar.StartedOnClickTarget(planted));

        bar.Close();
    });
}
