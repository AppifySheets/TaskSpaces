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
    string? CommandLine);
