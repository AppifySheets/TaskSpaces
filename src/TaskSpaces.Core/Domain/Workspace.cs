namespace TaskSpaces.Core.Domain;

// A named group of windows, backed 1:1 by a Windows virtual desktop.
// DesktopId is the *live* desktop's GUID — persisted so we can re-bind to the same
// desktop after an app restart, but desktops don't survive reboots, so reconcile
// logic (WorkspaceManager) may re-create the desktop and update this.
public sealed record Workspace(Guid Id, string Name, Guid? DesktopId);
