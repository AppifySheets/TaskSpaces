namespace TaskSpaces.Core.Domain;

// Typed HWND. A raw nint invites passing the wrong integer; the struct costs nothing
// and makes signatures self-documenting.
public readonly record struct WindowHandle(nint Value);
