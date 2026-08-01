using System.Collections.Immutable;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Renaming;

/// <summary>
/// Immutable bookkeeping for renamed windows. Tracks, per window, both the title
/// the app had before we touched it (for restore on un-rename/app exit) and the
/// short name we set (to detect when the app rewrote its own title and we must
/// re-apply our chosen name).
/// </summary>
public sealed class RenameLedger
{
    sealed record Entry(string OriginalTitle, string AppliedName);

    readonly ImmutableDictionary<WindowHandle, Entry> entries;

    RenameLedger(ImmutableDictionary<WindowHandle, Entry> entries) => this.entries = entries;

    public static RenameLedger Empty { get; } = new(ImmutableDictionary<WindowHandle, Entry>.Empty);

    /// <summary>
    /// Apply a short name to a window. First Apply captures the true original title;
    /// later Applies on the same window only change the short name (preserving the
    /// original for eventual restore).
    /// </summary>
    public RenameLedger Apply(WindowHandle window, string currentTitle, string shortName) =>
        new(entries.SetItem(window, entries.TryGetValue(window, out var existing)
            ? existing with { AppliedName = shortName }
            : new Entry(currentTitle, shortName)));

    /// <summary>
    /// Remove a window from the ledger (forget about it).
    /// </summary>
    public RenameLedger Remove(WindowHandle window) => new(entries.Remove(window));

    /// <summary>
    /// Get the short name we applied, if any.
    /// </summary>
    public Maybe<string> AppliedName(WindowHandle window) =>
        entries.TryGetValue(window, out var e) ? e.AppliedName : Maybe<string>.None;

    /// <summary>
    /// Get the original title the app had before we renamed it.
    /// </summary>
    public Maybe<string> OriginalTitle(WindowHandle window) =>
        entries.TryGetValue(window, out var e) ? e.OriginalTitle : Maybe<string>.None;

    /// <summary>
    /// True when the app overwrote our short name (observed title differs from
    /// applied name). Signals that we must re-set the title. Observed == applied
    /// means the NAMECHANGE event was our own echo and needs no action.
    /// </summary>
    public bool NeedsReapply(WindowHandle window, string observedTitle) =>
        entries.TryGetValue(window, out var e) && observedTitle != e.AppliedName;

    /// <summary>
    /// All window handles currently tracked in this ledger.
    /// </summary>
    public IReadOnlyCollection<WindowHandle> Handles => entries.Keys.ToList();
}
