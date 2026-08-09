namespace TaskSpaces.Core.Persistence;

// What we remember about a window so a later one of the same app can be recognised: enough to relaunch
// the app (path + original command line) and to show the user what would come back.
public sealed record InventoryEntry(string ProcessPath, string? CommandLine, string Title);

// One window we pinned on a nested workspace's behalf, and the desktop it lives on when it is not
// being borrowed (#42). See AppState.InheritedPins for why raw hwnds are the right key here.
public sealed record InheritedPin(long Window, Guid HomeDesktop);
