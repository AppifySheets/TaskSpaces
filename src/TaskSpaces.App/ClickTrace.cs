using System.IO;

namespace TaskSpaces.App;

// Instrumentation for #48 -- "first click on a workspace sometimes does nothing; second click
// works" -- and deliberately not a fix.
//
// That symptom has been declared fixed twice already: once by deferring rebuilds while a mouse
// button is down (a rebuild between press and release destroys the pressed Button, and a Button
// that no longer exists raises no Click), and once by listening on the bubbling MouseUp with
// handledEventsToo (ButtonBase marks a release handled even when it raises no Click, and
// MouseLeftButtonUp is a Direct event that then never travels). Both fixes are still in place, so
// either one has a hole or there is a third mechanism -- and the honest way to tell which is to
// make ONE failed click say where it was lost, rather than to reason about it a third time.
//
// Every stage of a click writes a line, so a failure reads as an absence: a press with no
// matching switch names the gap between them.
//
// OFF unless TASKSPACES_TRACE=1 is in the environment, and the check is a static readonly bool, so
// a normal run pays one branch per call and touches no disk. Left in the tree afterwards rather
// than stripped: this is the third visit to this bug, and the next one should start with the log
// rather than with a fresh set of guesses.
//
// Nothing here may ever throw. It is called from mouse handlers on the dispatcher, and an
// exception on that path takes the process down -- which is exactly how the Run/ContentElement
// crash killed the app, in the very handler this traces.
//
// ---------------------------------------------------------------------------------------------
// WHEN THE BUG HAPPENS: WHAT TO DO
// ---------------------------------------------------------------------------------------------
//
// It is intermittent and nobody has made it happen on demand, so the trace has to be ON before it
// occurs. That is the whole reason this is an environment variable and not a menu item.
//
//   1. Turn it on permanently, once, by creating a marker file:
//
//          type nul > "%APPDATA%\TaskSpaces\trace.on"
//
//      Then restart TaskSpaces. To turn it off, delete that file and restart.
//
//      TASKSPACES_TRACE=1 in the environment also works, but PREFER THE FILE. `setx` writes the
//      user-scope value, and a process only ever inherits its parent's environment block as
//      captured when that parent started -- so anything launched from a shell that predates the
//      setx runs with tracing OFF while every check of the setting insists it is on. That cost a
//      reproduction of #51 already: the dialog was slow, the trace was not recording, and nothing
//      said so.
//
//      Whichever switch is used, the log's first line names it. A log that does not begin with a
//      `--- trace on ...` line was not recording, and an empty log means the same.
//
//   2. Use the bar normally. When a click on a workspace does nothing, note ROUGHLY what time it
//      was and which workspace -- the log is timestamped and each line names the group, so
//      "Sparrow, about ten past two" is enough to find it.
//
//   3. The log is at  %TEMP%\taskspaces-trace.log  and is appended to across restarts. It is
//      small: a handful of lines per click, nothing while idle.
//
// READING IT. One click should produce this, in order:
//
//   press source=Border icon=False clickTarget=True rebuilding=False pending=False
//   row-up group=Sparrow consumedByChild=False ourPress=True
//   switch group=Sparrow ok=True
//
// A lost click is an ABSENCE, and which line is missing names the mechanism:
//
//   * No `press` at all
//         The bar never saw the button go down. Not a bar bug: look at what else had the mouse
//         (a drag loop, a menu, another topmost window).
//
//   * `press` but no `row-up`
//         The release never reached the row's handler. This is the family both previous fixes
//         belong to. Check for a `REBUILD WHILE PRESSED` line in between: if it is there, the
//         deferral in Rebuild() has a hole and something is rebuilding despite a held button --
//         find its trigger and defer that too. If it is NOT there, the row survived and the
//         release went somewhere else, which points at the routing (ButtonBase handling the
//         release, or a child element that did not exist when the press landed).
//
//   * `row-up` with `ourPress=False`
//         The press was disowned: it started somewhere other than this row, so pressedRow does
//         not match. Look at the `press` line's source -- a press that begins on one row and
//         releases on another is correctly refused, but if BOTH lines name the same row then the
//         bookkeeping was reset mid-click, and the reset in OnPreviewMouseLeftButtonDown is the
//         place to look.
//
//   * `row-up` with `consumedByChild=True` but no `label-click`
//         A child Button swallowed the press and raised no Click of its own -- the exact
//         asymmetry the second fix was written for, recurring somewhere new.
//
//   * `switch ... ok=False`
//         The click WORKED and the switch was refused. Petre's "it happens when the app has been
//         idle" lead fits here: a stale virtual-desktop COM object, or the OS declining. The
//         error text is on the line. This one is invisible without the trace, because the failure
//         only raises a dialog behind a topmost bar.
//
//   * An `enter-row rebuild` immediately before a missing press
//         Moving between rows re-sorts the row just left, and that is newer than both previous
//         fixes. If this line keeps company with the failures, the pointer-driven rebuild needs
//         the same deferral a window-driven one gets.
//
// THEN: fix the stage the log names, and add the failing sequence to this comment as a worked
// example, so the fourth visit starts where the third finished.
//
// ---------------------------------------------------------------------------------------------
// WORKED EXAMPLES: the fourth and fifth mechanisms, both caught 2026-08-10
// ---------------------------------------------------------------------------------------------
//
// Petre could finally reproduce it -- "i clicked two edge icons in the taskspace left monitor, I had
// 2 misses" -- and the log named two causes that no amount of reasoning had reached. Neither was a
// hole in the two earlier fixes: both of those still hold.
//
// FOURTH: a click that became a DRAG.
//
//   press source=Image icon=True ...          <- eleven clicks like this, all fine
//   up source=Button ...
//   icon-click app=msedge hwnd=DB0FE2 ok=True
//   row-up group=TaskSpace consumedByChild=True ourPress=True
//   row-up group=TaskSpace consumedByChild=True ourPress=True   <- the twelfth: no press, no up
//
// The drag threshold is the system's four DIPs, which is six physical pixels at 150% scale, so a
// quick click on a 20px icon exceeds it. DoDragDrop then swallows the press, the icon never raises
// Click, and the drop lands back on the row it started from -- where the same-row branch used to
// return without doing anything. Fixed by treating a drag that ends where it began as the click it
// was (`drop-as-click`), and by refusing the stray release the drag leaves behind (`afterDrag=True`).
//
// FIFTH: a press Windows never delivered.
//
//   label-click TaskSpace                                          <- switch happens here
//   up source=TextBlock ... pressedRow=...6929(Personal)           <- no press line at all
//   row-up group=Personal consumedByChild=True ourPress=True       <- stood down, click lost
//
// Every instance landed 0.35-0.46s after a workspace switch. A switch animates for a few hundred ms
// and the app activates a window on the arriving desktop as it lands, so the bar is not the
// foreground window; the press is eaten before it arrives. Nothing here can prevent that, so the
// release is honoured alone (`orphan=True`), skipping the two press-derived guards -- which for an
// orphan describe the PREVIOUS click and can only mislead. Icons need their own handler for this,
// since ButtonBase raises Click only from a matched pair (`icon-orphan-click`).
//
// Measured after the fix, in one session: twelve orphan releases, nine rescued switches, three
// rescued icon clicks, no refusals. Presses 81, releases 82, orphans 12, presses with no release 11
// -- those last being drags, whose release the OLE loop eats. Every event accounted for.
//
// A SIXTH visit therefore starts by asking: is `orphan` False on the failing release? If it is, this
// is new. If it is True and nothing followed, the rescue itself is broken, which is a much smaller
// search.
static class ClickTrace
{
    // A MARKER FILE beside state.json, or the environment variable. Two switches because the
    // variable alone cost a reproduction: `setx TASKSPACES_TRACE 1` writes the user-scope value,
    // but a process only ever inherits its PARENT's environment block, captured when that parent
    // started -- so anything launched from a shell that predates the setx runs with tracing off
    // while every check of the setting says it is on. Petre reproduced the slow dialog against a
    // build that was silently not recording, and nothing said so.
    //
    // The file has no such failure mode: it is either there or it is not, and the same check
    // answers for every process on the machine whatever launched it.
    //
    //     to enable:   type nul > "%APPDATA%\TaskSpaces\trace.on"     (then restart TaskSpaces)
    //     to disable:  del "%APPDATA%\TaskSpaces\trace.on"
    static readonly string MarkerPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskSpaces", "trace.on");

    static readonly bool Enabled =
        Environment.GetEnvironmentVariable("TASKSPACES_TRACE") == "1" || Exists(MarkerPath);

    static readonly string LogPath = Path.Combine(Path.GetTempPath(), "taskspaces-trace.log");

    static readonly Lock Gate = new();

    public static bool On => Enabled;

    static bool Exists(string path)
    {
        try { return File.Exists(path); }
        catch { return false; }
    }

    // Called once at startup, and it earns its place: an empty log is otherwise ambiguous between
    // "tracing is off" and "nothing happened yet", and telling those apart after the fact is
    // exactly what was missing when the first reproduction was lost. A log that starts with this
    // line is a log that was recording.
    public static void Announce()
    {
        if (!Enabled) return;
        Write($"--- trace on (marker={Exists(MarkerPath)}, env={Environment.GetEnvironmentVariable("TASKSPACES_TRACE") == "1"}) pid={Environment.ProcessId} ---");
    }

    public static void Write(string message)
    {
        if (!Enabled) return;
        try
        {
            // Locked because WinEvent callbacks and the dispatcher can both reach this, and a
            // torn line is worse than no line when the whole point is reading a sequence.
            lock (Gate) File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // A trace that cannot write is not a reason to lose the app.
        }
    }
}
