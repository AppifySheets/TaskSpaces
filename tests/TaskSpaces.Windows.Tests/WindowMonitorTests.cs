using System.Diagnostics;
using System.Windows.Threading;
using TaskSpaces.Core.Domain;
using TaskSpaces.Windows.Monitoring;
using Xunit.Abstractions;

namespace TaskSpaces.Windows.Tests;

// Spawns a real window (winver) — manual run only:
//   dotnet test tests/TaskSpaces.Windows.Tests --filter "Category=Integration"
[Trait("Category", "Integration")]
public class WindowMonitorTests(ITestOutputHelper output)
{
    [Fact]
    public void Detects_appear_and_disappear_of_a_real_window()
    {
        var appeared = new TaskCompletionSource<WindowInfo>();
        var disappeared = new TaskCompletionSource<WindowInfo>();
        Dispatcher? dispatcher = null;

        // WinEvent hooks need a message pump; give the monitor a dedicated STA thread.
        var thread = new Thread(() =>
        {
            var monitor = new WindowMonitor();
            monitor.Events.Subscribe(e =>
            {
                output.WriteLine($"{e.Kind}: {e.Window.ProcessName} '{e.Window.Title}'");
                if (e.Window.ProcessName == "winver" && e.Kind == WindowEventKind.Appeared) appeared.TrySetResult(e.Window);
                if (e.Window.ProcessName == "winver" && e.Kind == WindowEventKind.Disappeared) disappeared.TrySetResult(e.Window);
            });
            Assert.True(monitor.Start().IsSuccess);
            dispatcher = Dispatcher.CurrentDispatcher;
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var winver = Process.Start("winver.exe");
        // xUnit1031 (avoid blocking Task.Wait in test methods) doesn't apply cleanly here:
        // the monitor lives on a dedicated STA/dispatcher thread, not the test thread, so
        // there is no async continuation to deadlock — this is a synchronous cross-thread wait.
#pragma warning disable xUnit1031
        Assert.True(appeared.Task.Wait(TimeSpan.FromSeconds(10)), "winver window never appeared");
        winver.Kill();
        Assert.True(disappeared.Task.Wait(TimeSpan.FromSeconds(10)), "winver window never disappeared");
#pragma warning restore xUnit1031
        dispatcher?.InvokeShutdown();
    }
}
