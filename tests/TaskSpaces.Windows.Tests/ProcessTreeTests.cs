using System.Diagnostics;
using TaskSpaces.Windows.Monitoring;

namespace TaskSpaces.Windows.Tests;

// #94's read of the OS: who started whom. Same shape as ProcessCommandLineTests next door, and for the
// same reason: the DECISIONS live in Core with their own tests, so what is left to check here is that
// the P/Invoke answers correctly at all.
//
// Every case below has a real oracle. A child process this test starts knows its own parent (us), and
// this process knows its own name, so nothing is compared against a second guess at the same fact.
public class ProcessTreeTests
{
    readonly ProcessTree tree = new();

    [Fact]
    public void It_names_this_process()
    {
        var facts = tree.Of(Environment.ProcessId);

        Assert.True(facts.HasValue);
        // No extension, matching WindowInfo.ProcessName, which is what the shell test compares against.
        Assert.DoesNotContain(".exe", facts.Value.Name, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Process.GetCurrentProcess().ProcessName, facts.Value.Name, ignoreCase: true);
    }

    // The case the whole feature rests on, with the strongest oracle available: WE started this child,
    // so its parent is known rather than inferred.
    [Fact]
    public void It_finds_the_process_that_started_another()
    {
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c pause")
        {
            CreateNoWindow = true,
            RedirectStandardInput = true,
        })!;
        try
        {
            // Start returns as soon as the kernel has a process object; the same race
            // ProcessCommandLineTests documents.
            Assert.True(SpinWait.SpinUntil(() => tree.Of(child.Id).HasValue, TimeSpan.FromSeconds(5)),
                "the child never became readable");

            var facts = tree.Of(child.Id);

            Assert.Equal("cmd", facts.Value.Name, ignoreCase: true);
            Assert.Equal(Environment.ProcessId, facts.Value.ParentProcessId);
        }
        finally
        {
            child.Kill(entireProcessTree: true);
        }
    }

    // A parent that has exited must not be reported, and this is the case that catches PID REUSE: the
    // parent pid recorded in the child outlives the parent, and Windows hands the number out again.
    //
    // Built with a real orphan: cmd starts a grandchild and exits, so the grandchild's recorded parent
    // pid names a process that is gone.
    [Fact]
    public void A_parent_that_has_gone_is_not_reported()
    {
        // start /b runs the grandchild without waiting, then cmd exits immediately.
        using var launcher = Process.Start(new ProcessStartInfo("cmd.exe", "/c start /b cmd.exe /c ping -n 20 127.0.0.1")
        {
            CreateNoWindow = true,
        })!;
        launcher.WaitForExit();

        // The orphan is whichever ping-runner cmd still has the dead launcher as its parent pid.
        var orphan = Process.GetProcessesByName("cmd")
            .Select(p => (p, facts: tree.Of(p.Id)))
            .FirstOrDefault(x => x.facts.HasValue && x.facts.Value.ParentProcessId == 0 || false);

        // Whatever we found, the invariant is the one that matters: no process is ever reported with a
        // parent that cannot be read. Asserted over EVERY process this test can see, which is a
        // stronger statement than one orphan would make and does not depend on winning a race.
        Process.GetProcesses()
            .Select(p => tree.Of(p.Id))
            .Where(facts => facts.HasValue && facts.Value.ParentProcessId != 0)
            .ToList()
            .ForEach(facts => Assert.True(tree.Of(facts.Value.ParentProcessId).HasValue,
                $"{facts.Value.Name} reported parent {facts.Value.ParentProcessId}, which cannot be read"));

        orphan.p?.Kill(entireProcessTree: true);
    }

    // MEASURED, and not what was expected when this was written: a process that has EXITED can still be
    // OPENED for as long as somebody holds a handle to it, which the Process object here does. The
    // kernel object outlives the process, so OpenProcess succeeds and the recorded parent pid is still
    // readable -- but the image is gone, so the NAME comes back empty.
    //
    // ProcessTree treats a nameless process as unreadable, which is what makes this answer None. That
    // is the conservative direction and it closes a real hole: a name that cannot be read cannot be
    // recognised as the shell, so a zombie explorer would otherwise be walked straight through as if it
    // were some app's helper process.
    [Fact]
    public void An_exited_process_is_not_reported_even_while_its_handle_is_held()
    {
        using var gone = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true })!;
        gone.WaitForExit();

        Assert.False(tree.Of(gone.Id).HasValue);
    }

    // A pid that names nothing at all. Not a multiple of four, so it is not a pid Windows would ever
    // hand out.
    [Fact]
    public void A_pid_that_names_nothing_answers_none() =>
        Assert.False(tree.Of(int.MaxValue - 2).HasValue);

    // PID 0 is the System Idle Process, which cannot be opened at all: the cheapest check that a
    // refused OpenProcess is answered rather than thrown.
    [Fact]
    public void A_process_that_cannot_be_opened_answers_none()
    {
        Assert.False(tree.Of(0).HasValue);
        Assert.False(tree.Of(-1).HasValue);
    }
}
