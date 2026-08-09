namespace TaskSpaces.Core.Time;

// How much ACTIVE time has been spent in each workspace, per day (#53).
//
// Petre's ruling on what counts: "active means keyboard or mouse activity -- pure wall-clock
// presence does not count." So this is not "how long was that desktop on screen"; it is "how long
// was somebody working there", and a workspace left on screen overnight accrues nothing.
//
// Immutable, and keyed by workspace ID rather than by name so a rename does not orphan its own
// history -- the same reason every other persisted reference in this app is an id.
//
// Days are DateOnly in LOCAL time, because the question this answers is "what did I do today",
// and today is a local-calendar fact. The cost is that an hour worked at 00:30 UTC+2 lands on a
// different day than the same instant would in UTC, which is correct for a personal tool and
// wrong for aggregating across timezones -- something this will never do.
public sealed record WorkspaceTime(IReadOnlyDictionary<Guid, IReadOnlyDictionary<DateOnly, TimeSpan>> ByWorkspace)
{
    public static WorkspaceTime Empty { get; } = new(new Dictionary<Guid, IReadOnlyDictionary<DateOnly, TimeSpan>>());

    // Adds one slice of activity. Returns a new instance: this record is handed around and
    // serialised, and a mutable one would let a UI reading it see a half-written day.
    public WorkspaceTime Credit(Guid workspaceId, DateOnly day, TimeSpan amount)
    {
        // Zero and negative are refused rather than added: a negative slice would come from a
        // clock that moved backwards, and silently subtracting somebody's tracked time is a worse
        // answer than ignoring the tick that produced it.
        if (amount <= TimeSpan.Zero) return this;

        var days = ByWorkspace.TryGetValue(workspaceId, out var existing)
            ? new Dictionary<DateOnly, TimeSpan>(existing)
            : [];
        days[day] = days.GetValueOrDefault(day) + amount;

        var byWorkspace = new Dictionary<Guid, IReadOnlyDictionary<DateOnly, TimeSpan>>(ByWorkspace)
        {
            [workspaceId] = days,
        };
        return new WorkspaceTime(byWorkspace);
    }

    public TimeSpan On(Guid workspaceId, DateOnly day) =>
        ByWorkspace.TryGetValue(workspaceId, out var days) ? days.GetValueOrDefault(day) : TimeSpan.Zero;

    // Inclusive at both ends, because every caller asks in human terms ("Monday to today") and an
    // exclusive end is where off-by-one days come from.
    public TimeSpan Between(Guid workspaceId, DateOnly from, DateOnly to) =>
        ByWorkspace.TryGetValue(workspaceId, out var days)
            ? days.Where(d => d.Key >= from && d.Key <= to).Aggregate(TimeSpan.Zero, (total, d) => total + d.Value)
            : TimeSpan.Zero;

    // Days older than the cutoff, dropped. The file grows forever otherwise -- one entry per
    // workspace per day is small, but "small forever" is still forever, and nobody asks what they
    // were doing in a workspace two years ago.
    public WorkspaceTime Forgetting(DateOnly before) =>
        new(ByWorkspace
            .Select(w => (w.Key, Days: (IReadOnlyDictionary<DateOnly, TimeSpan>)w.Value.Where(d => d.Key >= before).ToDictionary(d => d.Key, d => d.Value)))
            .Where(w => w.Days.Count > 0)
            .ToDictionary(w => w.Key, w => w.Days));
}
