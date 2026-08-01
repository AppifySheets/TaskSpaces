using CSharpFunctionalExtensions;

namespace TaskSpaces.Core.Persistence;

// Seam for tests: WorkspaceManager persists through this, fakes record the calls.
public interface IPersistenceStore
{
    Result<AppState> Load();
    Result Save(AppState state);
}
