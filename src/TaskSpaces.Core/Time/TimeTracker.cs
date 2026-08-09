using CSharpFunctionalExtensions;
using TaskSpaces.Core.Abstractions;

namespace TaskSpaces.Core.Time;

// Turns ticks into tracked time (#53). The policy lives in ActivityAccrual and the numbers live in
// WorkspaceTime; this is the small mutable thing in the middle that holds the ledger, decides when
// to write it, and is the one place the app talks to.
//
// POLLED rather than event-driven, deliberately. There is no "the user became active" event to
// subscribe to -- input events would mean a system-wide hook, which is exactly what
// IInputActivity's comment explains this feature refuses to install. A timer asking a cheap
// question is the honest shape for a question about the passage of time.
public sealed class TimeTracker(ITimeStore store, IInputActivity input, Func<DateTime> now)
{
    public WorkspaceTime Time { get; private set; } = WorkspaceTime.Empty;

    // Dirty-tracking rather than saving on every tick: a tick is every 15 seconds forever, and
    // rewriting a growing file 240 times an hour to add 15 seconds to one row is the kind of thing
    // that quietly wears out an SSD.
    bool unsaved;
    DateTime lastSaved;

    // Often enough that a crash costs minutes rather than a day, rare enough that the file is not
    // being rewritten every quarter minute forever.
    static readonly TimeSpan SaveEvery = TimeSpan.FromMinutes(5);

    public void Start()
    {
        Time = store.Load().GetValueOrDefault(WorkspaceTime.Empty);
        lastSaved = now();
    }

    // One tick. `workspace` is where the user is RIGHT NOW -- None while standing on a desktop that
    // is not a workspace, which is a real state on Petre's machine (most of his windows live on an
    // unnamed desktop) and simply accrues nothing rather than being attributed somewhere wrong.
    public void Tick(Maybe<Guid> workspace, TimeSpan interval)
    {
        if (workspace.HasNoValue) return;
        var id = workspace.Value;

        var slice = ActivityAccrual.Slice(input.SinceLastInput(), interval, now());
        if (slice.HasNoValue) return;

        Time = Time.Credit(id, slice.Value.Day, slice.Value.Amount);
        unsaved = true;
        if (now() - lastSaved >= SaveEvery) Flush();
    }

    // Called on the app's existing save cadence and at shutdown. Silent on failure for the same
    // reason the rest of this feature is quiet: tracked time is a nice-to-have, and a dialog about
    // a file it could not write would be worse than the missing minute.
    public void Flush()
    {
        if (!unsaved) return;
        unsaved = false;
        lastSaved = now();
        store.Save(Time);
    }

    // Drops days older than the cutoff, then flushes if anything went. Called at startup, which is
    // the only moment worth doing it: the file is small, and a background prune is machinery for a
    // problem measured in kilobytes per year.
    public void Forget(DateOnly before)
    {
        var pruned = Time.Forgetting(before);
        if (pruned.ByWorkspace.Count == Time.ByWorkspace.Count
            && pruned.ByWorkspace.Sum(w => w.Value.Count) == Time.ByWorkspace.Sum(w => w.Value.Count)) return;
        Time = pruned;
        unsaved = true;
        Flush();
    }
}
