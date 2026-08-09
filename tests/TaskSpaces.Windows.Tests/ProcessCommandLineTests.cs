using System.Diagnostics;
using System.IO;
using TaskSpaces.Windows.Monitoring;

namespace TaskSpaces.Windows.Tests;

// #59: the command line now comes out of the target process's own memory rather than out of WMI,
// because WMI cost 656ms per lookup ON THE DISPATCHER THREAD every time any window appeared.
//
// What can be tested honestly here is that the read is CORRECT -- speed is measured, not asserted,
// since a timing assertion on a shared build machine is a flaky test waiting to happen. Correctness
// is the part that matters anyway: roster identity is exe path + args, so a subtly different answer
// would silently repartition which workspace an app belongs to.
public class ProcessCommandLineTests
{
    // The strongest available oracle: this process knows its own command line without asking
    // anybody, so the PEB walk can be checked against the truth rather than against another
    // implementation of the same guess.
    //
    // Deliberately NOT compared against Environment.CommandLine, which looks like the obvious
    // oracle and is not one. For a framework-dependent app the runtime reports the MANAGED command
    // line while the process was actually launched with the native one:
    //
    //     Environment.CommandLine   ...\testhost.dll --port 10915 --endpoint ...
    //     PEB (this)               "...\testhost.exe"  --runtimeconfig ...
    //
    // Two different strings, both correct, describing the same process. WMI returns the PEB's
    // version -- which is the one that matters here, since roster identity is built from these
    // strings and every entry already in state.json was written from WMI's answer. The probe that
    // justified this change compared the two across 19 real processes and found zero
    // disagreements; this test just checks the read works on ourselves at all.
    [Fact]
    public void It_reads_this_process()
    {
        var read = ProcessCommandLine.TryRead((uint)Environment.ProcessId);

        Assert.False(string.IsNullOrWhiteSpace(read));
        Assert.Contains(Path.GetFileNameWithoutExtension(Environment.ProcessPath)!, read, StringComparison.OrdinalIgnoreCase);
    }

    // A DIFFERENT process, which is the case that actually exercises OpenProcess and
    // ReadProcessMemory across an address-space boundary -- reading your own memory can succeed for
    // reasons that do not generalise.
    //
    // The arguments are the assertion: a command line that comes back without them would mean the
    // ImagePathName field was read instead of CommandLine, which is the neighbouring 16 bytes and
    // the obvious way to get this wrong.
    [Fact]
    public void It_reads_another_process_including_its_arguments()
    {
        // cmd.exe rather than a .NET child: it starts in milliseconds, needs no SDK, and /c waits
        // for a command that never comes, so it sits still until it is killed.
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c pause")
        {
            CreateNoWindow = true,
            RedirectStandardInput = true,
        })!;
        try
        {
            // The command line lives in the PEB before the process runs a single instruction of its
            // own code, but the process still has to BE there -- Start returns as soon as the
            // kernel has an object, so a slow machine can otherwise lose this race.
            Assert.True(SpinWait.SpinUntil(() => ProcessCommandLine.TryRead((uint)child.Id) is not null, TimeSpan.FromSeconds(5)),
                "the child's command line never became readable");

            var read = ProcessCommandLine.TryRead((uint)child.Id);

            Assert.Contains("cmd.exe", read, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("/c pause", read, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            child.Kill(entireProcessTree: true);
        }
    }

    // A process that has gone is the ordinary case, not an edge one: windows close while events
    // about them are still queued, and this runs inside a WinEvent callback where an exception ends
    // the process rather than the operation.
    [Fact]
    public void A_process_that_does_not_exist_answers_null_rather_than_throwing()
    {
        using var gone = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true })!;
        gone.WaitForExit();

        Assert.Null(ProcessCommandLine.TryRead((uint)gone.Id));
    }

    // PID 0 is the System Idle Process, which cannot be opened at all -- the cheapest available
    // check that a refused OpenProcess is answered rather than thrown.
    [Fact]
    public void A_process_that_cannot_be_opened_answers_null() =>
        Assert.Null(ProcessCommandLine.TryRead(0));
}
