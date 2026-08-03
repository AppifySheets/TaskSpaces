using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.App;

// Makes a Button that represents a window draggable, carrying a DraggedWindow payload.
// Extracted verbatim from WindowGroupsView.SetupDragSource when the floating bar gained
// icon drag (Petre: "i also want to be able to drag them around across tabs") — every
// comment below is a root cause someone already paid for, so both surfaces get the same
// hardening instead of the bar re-learning it.
static class WindowDragSource
{
    // Drag-and-drop (spec §Drag-and-drop window management): press-drag beyond the
    // system's minimum drag distance starts an OLE drag carrying this row's handle + its
    // current group. Hooked on the PREVIEW (tunneling) events rather than the bubbling
    // MouseMove/MouseLeftButtonDown: ButtonBase does its own internal press-tracking on
    // the bubbling route, so tunneling guarantees this handler still sees every move.
    // (Confirms pitfall #3 from the debugging brief: this reliance on tunneling — not on
    // ButtonBase's own capture — is exactly why the threshold check below keeps
    // receiving moves right up to the point DoDragDrop takes over, even for a fast
    // flick that would otherwise carry the cursor clean off a ~24px-tall row before any
    // MouseMove fired on the bubbling route.)
    //
    // A drag never also fires this row's Click: once DoDragDrop hands the mouse to its
    // own modal OLE drag loop, the Button never observes the MouseLeftButtonUp it needs
    // in order to raise Click for the same press — documented WPF behavior, verified by
    // reasoning about ButtonBase's capture model (no UI test exists to click-and-drag in
    // this codebase; per the task brief, none was added — "no UI unit tests"). The
    // ReleaseMouseCapture() call below does NOT change this: DoDragDrop's OLE loop
    // intercepts the terminating mouse-up as a native message before WPF's input system
    // ever turns it into a routed MouseLeftButtonUp, with or without capture already
    // released — releasing only prevents capture from OUTLIVING the drag (see below), it
    // doesn't resurrect the Click.
    //
    // onDragStarting fires once, immediately before the modal OLE loop takes the mouse —
    // the floating bar uses it to dismiss its hover info panel, which would otherwise sit
    // there frozen for the whole drag (the icon under the cursor never raises MouseLeave
    // during a drag, so nothing else would clear it).
    internal static void Attach(Button button, WindowHandle handle, string groupKey, string title, Action? onDragStarting = null)
    {
        Point? dragStart = null;

        button.PreviewMouseLeftButtonDown += (_, e) =>
        {
            dragStart = e.GetPosition(null);
            DnDTrace.Log($"press '{title}' in '{groupKey}'");
        };

        // Root-cause hardening, pitfall #2 (debugging brief): a plain click used to
        // leave dragStart set FOREVER — nothing ever cleared it. Any later
        // press-and-move that reaches this same button's handlers (a legitimate later
        // click on this row, or a move mis-routed here by leftover capture — see the
        // ReleaseMouseCapture note below) would then measure distance from that STALE
        // point instead of the new press, which can misfire a drag using the wrong
        // start position. Clearing on release and on a leave-while-not-pressed keeps a
        // finished interaction from bleeding into whatever happens next on this row.
        button.PreviewMouseLeftButtonUp += (_, _) => dragStart = null;
        button.MouseLeave += (_, e) => { if (e.LeftButton != MouseButtonState.Pressed) dragStart = null; };

        button.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || dragStart is not { } start) return;
            var pos = e.GetPosition(null);
            if (Math.Abs(pos.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(pos.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            dragStart = null;

            // Root-cause hardening, pitfall #1 (debugging brief): ButtonBase.
            // OnMouseLeftButtonDown calls CaptureMouse() on press. Because DoDragDrop's
            // modal OLE loop swallows the matching mouse-up itself (see the Click note
            // above), ButtonBase's OWN OnMouseLeftButtonUp override — the one that would
            // normally call ReleaseMouseCapture() — never runs for a press that turns
            // into a drag. Without this explicit release, THIS row's button keeps WPF
            // mouse capture for the rest of the app's life: capture redirects ALL
            // subsequent mouse input in the window to the captured element regardless
            // of where the cursor physically is, so the very NEXT press-and-move
            // anywhere in the roster would tunnel through THIS row's handlers with
            // THIS row's stale dragStart/handle/groupKey closed over — a "drag fires
            // from the wrong row" failure mode that has nothing to do with direction
            // and everything to do with which row happened to start a drag first.
            button.ReleaseMouseCapture();

            onDragStarting?.Invoke();
            DnDTrace.Log($"drag-start '{title}' from '{groupKey}'");
            DnDTrace.ResetTarget();
            var effect = DragDrop.DoDragDrop(button, new DataObject(DraggedWindow.DragFormat, new DraggedWindow(handle, groupKey)), DragDropEffects.Move);
            DnDTrace.Log($"DoDragDrop returned {effect} for '{title}'");
        };
    }
}
