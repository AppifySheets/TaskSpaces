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
        // A plain nullable field written by the STA thread and read by the test thread would
        // race: WindowMonitor.Start()'s Snapshot() enumerates every top-level window and calls
        // GetWindowText on each, which has no timeout — if any window on the box is slow to
        // answer WM_GETTEXT, Start() (and therefore the Dispatcher assignment below) can take
        // several seconds, well past the point the test thread is ready to shut it down. Signal
        // dispatcher readiness through a TaskCompletionSource so cleanup genuinely waits for it
        // instead of racing a field that might still be null.
        var dispatcherReady = new TaskCompletionSource<Dispatcher>();

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
            dispatcherReady.TrySetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Process? winver = null;
        try
        {
            winver = Process.Start("winver.exe");
            // xUnit1031 (avoid blocking Task.Wait in test methods) doesn't apply cleanly here:
            // the monitor lives on a dedicated STA/dispatcher thread, not the test thread, so
            // there is no async continuation to deadlock — this is a synchronous cross-thread wait.
#pragma warning disable xUnit1031
            Assert.True(appeared.Task.Wait(TimeSpan.FromSeconds(10)), "winver window never appeared");
            winver.Kill();
            Assert.True(disappeared.Task.Wait(TimeSpan.FromSeconds(10)), "winver window never disappeared");
#pragma warning restore xUnit1031
        }
        finally
        {
            // Both cleanups MUST run even when an assertion above throws — otherwise a
            // timed-out first Wait leaves winver.exe running and strands the STA/dispatcher
            // thread in Dispatcher.Run() forever (leaked process + leaked thread per failed run).
            if (winver is { HasExited: false }) winver.Kill();

            // Wait for the dispatcher to actually exist (see comment above) before asking it to
            // shut down — bounded generously since Start()'s enumeration is the slow part, not
            // anything unbounded. If it never shows up, there is nothing left to shut down safely.
#pragma warning disable xUnit1031
            if (dispatcherReady.Task.Wait(TimeSpan.FromSeconds(15)))
                dispatcherReady.Task.Result.InvokeShutdown();
            else
                output.WriteLine("WARNING: WindowMonitor's dispatcher never became ready; STA thread may be stranded");
#pragma warning restore xUnit1031

            // Bound the shutdown wait too: if the dispatcher thread is somehow wedged, don't
            // hang the whole test run — report it and let the process move on.
            if (!thread.Join(TimeSpan.FromSeconds(5)))
                output.WriteLine("WARNING: WindowMonitor dispatcher thread did not shut down within 5s");
        }
    }
}
