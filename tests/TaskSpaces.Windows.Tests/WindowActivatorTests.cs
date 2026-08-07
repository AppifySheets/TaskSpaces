using System.Diagnostics;
using TaskSpaces.Core.Domain;
using TaskSpaces.Windows.Activation;
using Xunit.Abstractions;

namespace TaskSpaces.Windows.Tests;

[Trait("Category", "Integration")]
public class WindowActivatorTests(ITestOutputHelper output)
{
    [Fact]
    public void Activates_a_real_window()
    {
        var winver = Process.Start("winver.exe");
        try
        {
            // FIX (code review, round 1): bounded to ~5s (50 * 100ms) instead of spinning
            // forever -- an unbounded wait here would hang past the finally block if winver
            // never surfaces a window (e.g. blocked by a dialog, killed externally), leaving
            // the process running and the test stuck. A clear Assert message pinpoints the
            // timeout as the cause instead of a bare NullReferenceException further down.
            var waited = 0;
            while (winver.MainWindowHandle == 0)
            {
                Assert.True(waited < 50, "winver.exe did not surface a main window within 5s.");
                Thread.Sleep(100);
                winver.Refresh();
                waited++;
            }
            var result = new WindowActivator().Activate(new WindowHandle(winver.MainWindowHandle));
            output.WriteLine($"activate: {result.IsSuccess}");
            Assert.True(result.IsSuccess);
        }
        finally { if (!winver.HasExited) winver.Kill(); }
    }
}
