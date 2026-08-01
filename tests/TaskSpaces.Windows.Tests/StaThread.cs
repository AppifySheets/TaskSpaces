using System.Runtime.ExceptionServices;

namespace TaskSpaces.Windows.Tests;

// The spike (docs/superpowers/notes/2026-08-01-virtualdesktop-spike.md) found that
// VirtualDesktop.Configure() constructs a WPF HwndSource internally and throws unless
// the calling thread is STA. xunit's default test-runner threads are MTA (the same
// "async Main is silently ignored" family of gotcha the spike documented for console
// apps — xunit has no built-in STA test-thread support on .NET, only on old desktop-CLR
// runners). The task's dispatch instructions (not the brief, which says nothing about
// threading) offered two ways to cope: marshal the COM calls onto a dedicated STA thread
// inside the test, or use an assembly-level fixture. This class implements the former —
// a tiny, self-contained helper that runs a test body on a freshly spun-up STA thread and
// re-throws any exception (including Assert failures, which xunit implements as thrown
// exceptions) back on the calling test thread so xunit reports them normally.
static class StaThread
{
    public static void Run(Action body)
    {
        ExceptionDispatchInfo? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        failure?.Throw();
    }
}
