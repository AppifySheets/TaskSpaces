namespace TaskSpaces.Core.Persistence;

// Where one CONTAINER lives: the folder, project or session a window has loaded, and the workspace
// Petre put it in (#132).
//
// The gap this fills, in one sentence: the roster keys on exe path plus arguments, so seven VS Code
// windows started from the Start menu are one identity and placement memory cannot tell them apart.
// TitleToken reads the container out of the title, and this is where the answer is kept.
//
// ProcessName as well as Token, rather than the token alone. A folder called "dice" open in VS Code
// and a Remote Desktop session called "dice" are not the same thing, and nothing about a bare token
// says which app it came from. It is the process NAME rather than the path because that is what
// TitleToken keys its allowlist on, and it is what survives an app being reinstalled elsewhere.
//
// Not an InventoryEntry, deliberately, which is what PinnedApps and DetachedApps reuse. Those are
// statements about an APP and RosterIdentity.Of derives their identity; this is a statement about one
// container within an app, and giving it the same shape would invite code that treats the two as
// interchangeable. They are not: the roster answers for the app, this answers for the folder, and
// #132 exists because the second question had no answer at all.
public sealed record ContainerHome(string ProcessName, string Token, Guid WorkspaceId);
