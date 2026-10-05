using TaskSpaces.Windows.Recovery;

namespace TaskSpaces.Windows.Tests;

// Petre: "taskspaces terminated and didn't restart".
//
// The crash itself is the one CrashRestart already describes (OutOfMemoryException out of WPF's
// composition channel, this time opening the tray menu, on a machine at 89.5 of 95.7 GB commit). The
// restart was supposed to be WER's job, and WER never did it: on this machine
// HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\Disabled is 1. A probe registered for
// restart, lived 70 seconds and then died three ways (an unhandled exception in a timer tick, one
// inside a wndproc SetWindowPos had called, and FailFast from the dispatcher handler); none of the
// three came back. So the app now starts its own successor from its crash handler, and these tests
// pin the two rules that keep that from becoming a problem of its own.
public class CrashRelaunchTests
{
    // WER's own rule, copied rather than reinvented: a copy that died inside its first minute may be
    // dying of something in startup, and relaunching it would turn one crash into a loop.
    [Theory]
    [InlineData(0, false)]
    [InlineData(59, false)]
    [InlineData(60, true)]
    [InlineData(2 * 24 * 3600, true)]
    public void Only_a_copy_that_lived_a_minute_is_relaunched(int seconds, bool expected) =>
        Assert.Equal(expected, CrashRelaunch.IsWorthwhile(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void A_copy_that_died_young_starts_nothing() =>
        Assert.True(CrashRelaunch.Start(Environment.ProcessPath, TimeSpan.FromSeconds(5)).IsFailure);

    // The handler runs while the process is already failing, often out of memory, so a launch that
    // cannot happen has to come back as a failure for the handler to fall back on, never as a second
    // exception thrown from inside the first one's handler.
    [Fact]
    public void A_launch_that_cannot_happen_is_a_failure_rather_than_a_throw() =>
        Assert.True(CrashRelaunch.Start(@"C:\nowhere\TaskSpaces.App.exe", TimeSpan.FromHours(1)).IsFailure);

    [Fact]
    public void An_unknown_exe_path_is_a_failure() =>
        Assert.True(CrashRelaunch.Start(null, TimeSpan.FromHours(1)).IsFailure);
}
