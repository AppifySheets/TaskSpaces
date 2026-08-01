namespace TaskSpaces.Core.Domain;

// Immutable snapshot of a top-level window at the moment an event fired.
// CommandLine is only populated for browser processes (WMI is expensive) —
// it exists solely so BrowserProfile rules can inspect --profile-directory.
public sealed record WindowInfo(
    WindowHandle Handle,
    int ProcessId,
    string ProcessName,     // e.g. "chrome" (no extension)
    string? ProcessPath,    // null when inaccessible (elevated process)
    string Title,
    string? CommandLine);
