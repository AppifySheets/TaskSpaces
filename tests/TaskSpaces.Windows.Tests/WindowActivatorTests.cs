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
            var result = new WindowActivator().Activate(new WindowHandle(WaitForMainWindow(winver)));
            output.WriteLine($"activate: {result.IsSuccess}");
            Assert.True(result.IsSuccess);
        }
        finally { if (!winver.HasExited) winver.Kill(); }
    }

    // The close half (Petre: "delete workspace in context menu, close all windows in it"), and it
    // is here against a real window rather than a fake for the reason every other trap in this
    // project got its own probe: a posted message that goes nowhere fails SILENTLY. PostMessage
    // hands back true for a message nobody ever handles, so nothing but a window that actually
    // disappears proves this works.
    //
    // winver is the right subject twice over: it has no unsaved anything, so it closes on the first
    // ask, and it is already the process this file spawns.
    [Fact]
    public void Closes_a_real_window_and_reports_no_survivors()
    {
        var winver = Process.Start("winver.exe");
        try
        {
            var window = new WindowHandle(WaitForMainWindow(winver));

            var survivors = new WindowActivator().CloseAll([window]);

            output.WriteLine($"survivors: {survivors.Count}");
            Assert.Empty(survivors);
            Assert.True(winver.HasExited);
        }
        finally { if (!winver.HasExited) winver.Kill(); }
    }

    // A handle that has ALREADY gone is not a survivor. It is the ordinary case rather than an edge
    // one: a window can close between the overview that listed it and the click that acts on it,
    // and reporting it as refusing to close would cancel a delete over a window that is not there.
    [Fact]
    public void A_window_that_has_already_gone_is_not_a_survivor()
    {
        var winver = Process.Start("winver.exe");
        var window = new WindowHandle(WaitForMainWindow(winver));
        winver.Kill();
        winver.WaitForExit();

        Assert.Empty(new WindowActivator().CloseAll([window]));
    }

    // FIX (code review, round 1): bounded to ~5s (50 * 100ms) instead of spinning forever -- an
    // unbounded wait here would hang past the finally block if winver never surfaces a window (e.g.
    // blocked by a dialog, killed externally), leaving the process running and the test stuck. A
    // clear Assert message pinpoints the timeout as the cause instead of a bare
    // NullReferenceException further down.
    //
    // Shared by all three tests now that the close half spawns winver too; it was inline in the
    // activate test when that was the only one.
    static nint WaitForMainWindow(Process process)
    {
        var waited = 0;
        while (process.MainWindowHandle == 0)
        {
            Assert.True(waited < 50, "winver.exe did not surface a main window within 5s.");
            Thread.Sleep(100);
            process.Refresh();
            waited++;
        }
        return process.MainWindowHandle;
    }
}
