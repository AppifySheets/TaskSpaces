using CSharpFunctionalExtensions;

namespace TaskSpaces.Core.Time;

// Where tracked time lives. A SEPARATE file from state.json, and separate for a reason that is
// about shape rather than tidiness: everything in state.json is bounded -- a handful of
// workspaces, rules and renames -- while this grows by one row per workspace per day forever.
// Mixing them would mean rewriting the whole of a growing file on every workspace rename.
//
// Same Result-returning shape as IPersistenceStore, so a missing or corrupt file is a value the
// caller handles rather than an exception at startup.
public interface ITimeStore
{
    Result<WorkspaceTime> Load();
    Result Save(WorkspaceTime time);
}
