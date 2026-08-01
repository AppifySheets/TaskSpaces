using TaskSpaces.Windows.Desktops;
using Xunit.Abstractions;

namespace TaskSpaces.Windows.Tests;

// MUTATES REAL VIRTUAL DESKTOPS — excluded from normal runs. Execute manually with:
//   dotnet test tests/TaskSpaces.Windows.Tests --filter "Category=Integration"
//
// THREADING NOTE (spike finding): every body below runs via StaThread.Run(...) — xunit's
// test-runner thread is MTA by default, and VirtualDesktop.Configure() requires STA (it
// builds a WPF HwndSource internally). Without this wrapper, Initialize() would return a
// Result failure ("The calling thread must be STA...") instead of exercising the real API.
[Trait("Category", "Integration")]
public class VirtualDesktopServiceTests(ITestOutputHelper output)
{
    [Fact]
    public void Full_lifecycle_create_rename_switch_remove() =>
        StaThread.Run(() =>
        {
            var service = new VirtualDesktopService();
            Assert.True(service.Initialize().IsSuccess);

            var created = service.Create("TaskSpaces IT");
            output.WriteLine($"created: {created.Value.Id}");
            Assert.True(created.IsSuccess);

            // FIX (code review, round 1): everything after a successful Create() now runs
            // inside try/finally — same cleanup discipline the Task 1 spike used — so a
            // failed Assert partway through the lifecycle can't leave a stray desktop
            // behind on the real machine. `removed` tracks whether the happy-path Remove()
            // already ran so the finally block doesn't attempt a redundant (and noisy)
            // second removal.
            var removed = false;
            try
            {
                Assert.True(service.Rename(created.Value.Id, "TaskSpaces IT2").IsSuccess);
                Assert.Contains(service.GetDesktops().Value, d => d.Name == "TaskSpaces IT2");

                Assert.True(service.Switch(created.Value.Id).IsSuccess);
                Thread.Sleep(1000);                                     // let the shell animate
                Assert.True(service.Remove(created.Value.Id).IsSuccess); // removing current hops back
                removed = true;
            }
            finally
            {
                if (!removed) service.Remove(created.Value.Id);
            }
        });

    [Fact]
    public void Operations_on_missing_desktop_fail_gracefully() =>
        StaThread.Run(() =>
        {
            var service = new VirtualDesktopService();
            Assert.True(service.Initialize().IsSuccess);
            Assert.True(service.Switch(Guid.NewGuid()).IsFailure);
            Assert.True(service.Remove(Guid.NewGuid()).IsFailure);
        });
}
