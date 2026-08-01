namespace TaskSpaces.Core.Persistence;

// What we remember about a window for post-reboot rehydration: enough to relaunch
// the app (path + original command line) and to show the user what would come back.
public sealed record InventoryEntry(string ProcessPath, string? CommandLine, string Title);
