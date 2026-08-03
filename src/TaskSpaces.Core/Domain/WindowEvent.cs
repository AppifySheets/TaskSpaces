namespace TaskSpaces.Core.Domain;

// Finding 3 (reviewer, Important): Hidden is distinct from Disappeared. A window that
// merely left the taskbar (EVENT_OBJECT_HIDE — e.g. Discord/Outlook minimizing to tray)
// still EXISTS; only a real DESTROY means it's gone. Collapsing the two used to mean a
// hide/show cycle permanently corrupted the rename ledger's notion of "original title"
// (see WorkspaceManager.OnHidden).
// Activated (Petre: "active window should be highlighted in the floating window") is
// purely informational — unlike the other four it never places, renames or forgets
// anything. It exists so the surfaces can show WHICH window has focus, which on an
// icon-only bar with three identical VS Code glyphs is otherwise unanswerable.
public enum WindowEventKind { Appeared, TitleChanged, Hidden, Disappeared, Activated }

// What WindowMonitor emits. TitleChanged matters twice: rename rules may now match,
// and apps that rewrite their own titles must have our short name re-applied.
public sealed record WindowEvent(WindowEventKind Kind, WindowInfo Window);
