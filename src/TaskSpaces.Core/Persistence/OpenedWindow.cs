namespace TaskSpaces.Core.Persistence;

// A window the app watched OPEN, and the desktop it was first seen on.
//
// Petre: "rider jumped to services, why?" Because days earlier Rider had opened the freight solution
// while he was standing in Services, and the snapshot learned that position as the solution's home
// (see WorkspaceManager.SnapshotContainerHomes). Where a window opens is wherever he happened to be,
// so a window recorded here teaches nothing until it has been moved off FirstSeenOn.
//
// Persisted because the app restarts far more often than an IDE window closes. A restarted app sees
// every live window as "already open", and those are learned from where they sit, so a record kept
// only in memory would let the first restart learn the very mistake this exists to prevent.
//
// Raw hwnd plus process id, the same reasoning InheritedPin gives for a raw hwnd: these windows
// outlive our restart, so their handles are still valid on the next start. The process id is there
// because handles are recycled, and after a reboot the same number can belong to an unrelated window
// that really was open before the app started. A record whose handle no longer names a live window of
// the same process is dropped on start.
//
// FirstSeenOn is null until the first sweep has looked: a window can appear before Windows will say
// which desktop it is on.
public sealed record OpenedWindow(long Window, int ProcessId, Guid? FirstSeenOn);
