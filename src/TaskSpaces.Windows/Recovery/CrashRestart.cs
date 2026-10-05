using System.Runtime.InteropServices;
using CSharpFunctionalExtensions;

namespace TaskSpaces.Windows.Recovery;

// "Start it again when it dies, but not when I close it."
//
// Petre lives in this app all day and has had to ask for a manual start after every death: "start
// again", "start", "maybe you could add a simple monitor job which will start the taskspaces when it
// dies". The deaths are real and they are not ours -- two dumps, 22 Sep 09:31 and 26 Sep 01:01, both
// System.OutOfMemoryException out of WPF's composition channel while it built a window surface, on a
// machine sitting at 175.9 GB of a 180 GB commit limit. Nothing in the app can prevent that, and a
// fresh process genuinely recovers from it.
//
// WHY NOT A WATCHDOG, which is the obvious shape and was the request's own wording. A loop asking "is
// TaskSpaces running?" cannot tell a crash from the tray's Exit, so it would either resurrect an app
// he has just deliberately closed or need a "he meant it" flag of its own -- a second piece of state
// about the app's life, kept outside the app, going stale the moment either side changes. It is also
// a second process to install, keep alive and explain.
//
// Windows already draws exactly the line that matters. RegisterApplicationRestart asks Windows Error
// Reporting to relaunch this process after a CRASH or a HANG, and after nothing else: a clean exit,
// the tray's Exit included, is respected and stays final. Two further properties make it the right
// tool rather than merely a convenient one:
//
//   * WER refuses to restart a process that lived less than 60 seconds, so an app that fails at
//     startup cannot become a relaunch loop. A watchdog of ours would have had to invent that rule.
//   * The restart happens after the crash report is written, so the dump that names the next death is
//     still collected. Recovering quietly must not cost the evidence; the two dumps above are the only
//     reason this file can state its cause.
//
// RESTART_NO_REBOOT is set because the app already manages its own run-at-startup registration (see
// StartupRegistration). Without it, a reboot while the app was running would bring back both copies
// and one of them would meet the single-instance guard and put up a message box.
//
// NO LONGER THE FIRST LINE OF DEFENCE. All of the above holds only where WER is switched on, and on
// Petre's machine it is not (HKLM ...\Windows Error Reporting\Disabled = 1): measured with a probe,
// no crash of any kind came back, and the app's 5 Oct death stayed dead. The crash handler now starts
// its own successor (CrashRelaunch) and withdraws this registration when it does. What this still
// covers is a death the handler never sees, a native fault or a hang, on machines where WER is on.
public static class CrashRestart
{
    // 0x8 = RESTART_NO_REBOOT. Crash, hang and patch restarts are all left ON.
    const uint RestartNoReboot = 0x8;

    // Unicode, and not optional: the command line is a string parameter, and an ANSI marshalling of a
    // path with non-ASCII characters is how a probe "proves" the call worked while Windows stored
    // something else. Null asks Windows to reuse the process's own command line, which is what we want
    // -- the exe may be launched from a build output path that nothing else knows.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern int RegisterApplicationRestart(string? commandLine, uint flags);

    // HRESULT, not a bool: S_OK is 0 and anything else is a failure to report rather than to throw
    // over. Recovery being unavailable is a degraded app, never a dead one -- the same contract the
    // virtual-desktop service keeps when its COM interfaces are missing.
    public static Result Register() =>
        RegisterApplicationRestart(null, RestartNoReboot) is var hr && hr == 0
            ? Result.Success()
            : Result.Failure($"Automatic restart after a crash is unavailable (HRESULT 0x{hr:X8}).");

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern int UnregisterApplicationRestart();

    // Called once the crash handler has started a successor of its own (CrashRelaunch). On a machine
    // where WER is switched on, leaving the registration in place would have WER start a SECOND copy
    // after the dump, and that one would meet the single-instance guard and put up "already running".
    // Only after a successful relaunch: when ours failed, WER is still the backstop worth keeping.
    public static Result Unregister() =>
        UnregisterApplicationRestart() is var hr && hr == 0
            ? Result.Success()
            : Result.Failure($"Could not withdraw the WER restart registration (HRESULT 0x{hr:X8}).");
}
