namespace TaskSpaces.Core.Abstractions;

// A virtual desktop as Core sees it — id + name, nothing COM-shaped.
public sealed record DesktopInfo(Guid Id, string Name);
