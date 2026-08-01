using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Rules;

namespace TaskSpaces.Core.Persistence;

// The single unit of persistence — everything under %APPDATA%\TaskSpaces\state.json.
// Inventory maps workspace id -> windows last seen in it (for rehydration prompts).
public sealed record AppState(
    IReadOnlyList<Workspace> Workspaces,
    IReadOnlyList<WorkspaceRule> WorkspaceRules,
    IReadOnlyList<RenameRule> RenameRules,
    IReadOnlyDictionary<Guid, IReadOnlyList<InventoryEntry>> Inventory)
{
    public static AppState Empty { get; } = new([], [], [], new Dictionary<Guid, IReadOnlyList<InventoryEntry>>());
}
