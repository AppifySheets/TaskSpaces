namespace TaskSpaces.Core.Domain;

// Immutable snapshot of a top-level window at the moment an event fired.
// CommandLine is captured for EVERY window now (roster identity is path+args, not just
// browser profiles) -- the startup snapshot batches one WMI query for all processes, and
// each subsequent live event does a single per-pid WMI lookup. Still nullable: a window
// can arrive before its command line is resolvable, or the WMI lookup can fail (best-effort).
public sealed record WindowInfo(
    WindowHandle Handle,
    int ProcessId,
    string ProcessName,     // e.g. "chrome" (no extension)
    string? ProcessPath,    // null when inaccessible (elevated process)
    string Title,
    string? CommandLine)
{
    // What to call this window on screen. Its title, or its app when it has no title.
    //
    // Petre: "i can't see Buzz in HRIS workspace." Buzz turned out to be missing because its window
    // carries no title at all (see TopLevelWindows.IsTaskbarCandidate, which used to require one), and
    // letting such a window in means every place that shows a title now has to have an answer: the
    // hover card's heading, an icon's accessible name, the info line under the bar.
    //
    // The app name rather than a placeholder like "(untitled)", which would read the same on every one
    // of them and say nothing. "buzz-desktop" is what he would call it anyway.
    //
    // Trimmed, because a heading indented by whatever padding an app left in its own title reads as a
    // rendering fault rather than as the window's name.
    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? ProcessName : Title.Trim();
}
