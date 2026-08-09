namespace TaskSpaces.Core.Domain;

// Finding 3 (reviewer, Important): Hidden is distinct from Disappeared. A window that
// merely left the taskbar (EVENT_OBJECT_HIDE -- e.g. Discord/Outlook minimizing to tray)
// still EXISTS; only a real DESTROY means it's gone. Collapsing the two used to mean a
// hide/show cycle permanently corrupted the rename ledger's notion of "original title"
// (see WorkspaceManager.OnHidden).
// Activated (Petre: "active window should be highlighted in the floating window") is
// purely informational -- unlike the other four it never places, renames or forgets
// anything. It exists so the surfaces can show WHICH window has focus, which on an
// icon-only bar with three identical VS Code glyphs is otherwise unanswerable.
// Moved is informational in the same way, and exists for one reason. Petre: "when i drag a
// window to another monitor, it doesn't show the hairline separator until i switch to another
// workspace." Dragging a window across monitors changes nothing this app used to hear about --
// no show, no hide, no title change, and no foreground change either, since the window you are
// dragging is the one you are already in. The bar has no periodic rebuild, so nothing ever asked
// again and the monitor grouping stayed stale until some unrelated event happened to pulse.
// MinimizeChanged is informational in exactly the same way, and exists for the same class of
// silence. Petre: "the icon doesn't always dim when it's minimized, or doesn't always brighten up
// when it's un-minimized." Minimizing raises none of the events above -- HIDE does not fire, the
// title does not change, and the window being put down is usually the one you were already in, so
// the foreground never changes either. Un-minimizing normally DOES fire a foreground change, but
// that one is guarded on change (see MarkActive), and after a minimize from the bar the window is
// still recorded as the active one -- so the restore pulsed nothing and the icon stayed dim.
//
// Measured before being written (scratchpad probe, notepad driven by ShowWindowAsync exactly as
// the bar drives a window): EVENT_SYSTEM_MINIMIZESTART arrives ~15ms after the call with IsIconic
// ALREADY true, and MINIMIZEEND likewise with it already false -- despite MINIMIZESTART being
// documented as "about to be minimized". So a rebuild triggered by either one reads the settled
// state, and no delay or retry is needed.
public enum WindowEventKind { Appeared, TitleChanged, Hidden, Disappeared, Activated, Moved, MinimizeChanged }

// What WindowMonitor emits. TitleChanged matters twice: rename rules may now match,
// and apps that rewrite their own titles must have our short name re-applied.
public sealed record WindowEvent(WindowEventKind Kind, WindowInfo Window);
