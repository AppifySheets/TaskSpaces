using CSharpFunctionalExtensions;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Core.Abstractions;

// Launch a roster entry (Process.Start lives in the App layer — Core stays pure).
// Maybe: launching is best-effort; None = "didn't happen" (moved exe, denied, ...).
public interface IAppLauncher
{
    Maybe<int> Launch(InventoryEntry entry);
}
