namespace TaskSpaces.Core.Domain;

public enum WindowEventKind { Appeared, TitleChanged, Disappeared }

// What WindowMonitor emits. TitleChanged matters twice: rename rules may now match,
// and apps that rewrite their own titles must have our short name re-applied.
public sealed record WindowEvent(WindowEventKind Kind, WindowInfo Window);
