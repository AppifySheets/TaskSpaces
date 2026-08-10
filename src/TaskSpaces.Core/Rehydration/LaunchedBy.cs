using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Rehydration;

// Which app started this one (#94), decided by walking up the process chain.
//
// Pure, like PlacementMemory next door: every fact arrives through two delegates, so the whole rule
// is unit-testable without a process on the machine. What the OS is asked for lives behind
// IProcessTree.
//
// THE SHAPE OF THE PROBLEM, measured on Petre's machine rather than assumed, because a plain
// "read the parent pid" implementation answers nothing useful:
//
//   chrome ← node ← cmd ← node ← cmd ← claude ← Code ← Code[window] ← explorer
//   Code[window] ← explorer
//   Beeper ← explorer
//   msedgewebview2 ← WhatsApp.Root[window]
//
// Two facts fall out of that. A browser window opened from inside an editor sits SEVEN hops below
// the editor's window, every one of them a windowless helper, so the walk has to keep going. And
// almost everything a person launches by hand is a child of explorer, so the shell must never count
// as the app that started something: without that, launching anything from the taskbar would send it
// to wherever a File Explorer window happened to be sitting.
public static class LaunchedBy
{
    // Long enough for the chain measured above, which needed seven. Short enough that the walk cannot
    // become a cost: each hop is one process open and one query, so a miss is bounded rather than
    // proportional to how deep the machine's process tree happens to go.
    public const int MaxHops = 12;

    // Processes that are the shell or a system host, and therefore never the app that started
    // anything. Hitting one ends the walk.
    //
    // explorer is the one that matters and the reason this list exists: it is the parent of every app
    // started from the taskbar, the Start menu or the desktop, and it owns File Explorer windows, so
    // treating it as a launcher would place a newly started app wherever a folder window happened to
    // be. Petre picking an app off his taskbar is not "an app starting another app".
    //
    // The rest are the ancestors every user process eventually has (a session's chain runs up through
    // sihost, svchost, services, wininit). None of them owns a window that means anything here, and
    // stopping at them saves hops.
    static readonly IReadOnlySet<string> NotLaunchers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "sihost", "svchost", "services", "wininit", "winlogon", "csrss", "smss",
        "taskhostw", "dllhost", "runtimebroker", "startmenuexperiencehost", "searchhost",
        "shellexperiencehost", "applicationframehost",
    };

    // The nearest ancestor process that owns a window this app is tracking, or None.
    //
    // "Owns a window we track" is what makes an ancestor a LAUNCHER rather than plumbing: a windowless
    // helper (node, cmd, an editor's own background process) is something the app you launched from
    // used to do the launching, and the question being answered is which app that was.
    //
    // `lookup` is IProcessTree.Of and is expected to drop a parent that cannot be this process's
    // parent -- see the implementation for why a bare parent pid is not trustworthy.
    public static Maybe<int> Launcher(int processId, Func<int, Maybe<ProcessFacts>> lookup, Func<int, bool> ownsTrackedWindow)
    {
        var seen = new HashSet<int> { processId };
        var at = lookup(processId);

        for (var hop = 0; hop < MaxHops; hop++)
        {
            if (at.GetValueOrDefault() is not { } facts) return Maybe<int>.None;

            var parent = facts.ParentProcessId;
            // A chain that loops cannot happen from real parentage, but it arrives here from pid
            // arithmetic this code does not own, and an infinite walk on the dispatcher thread would
            // freeze the bar rather than fail.
            if (parent <= 0 || !seen.Add(parent)) return Maybe<int>.None;

            at = lookup(parent);
            if (at.GetValueOrDefault() is not { } up) return Maybe<int>.None;
            if (NotLaunchers.Contains(up.Name)) return Maybe<int>.None;
            if (ownsTrackedWindow(parent)) return parent;
        }

        return Maybe<int>.None;
    }
}
