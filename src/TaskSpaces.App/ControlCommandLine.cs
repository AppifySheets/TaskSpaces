using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using TaskSpaces.Core.Control;
using TaskSpaces.Windows.Control;

namespace TaskSpaces.App;

// `TaskSpaces.exe ctl <command>`: the same exe acting as a client of the copy that is already running.
//
// Petre: "i want you to add ability to create workspaces and move windows into them programatically, so
// that when i have a claude session start working in a new worktree, i can tell it to move the windows
// to a new workspace which it creates."
//
// The same exe rather than a separate command-line tool, because the release is ONE self-contained file
// (release.yml publishes PublishSingleFile). A second exe would be a second download of the whole
// runtime, and a second thing to keep at the same version as the first. Whoever wants to drive the app
// finds the exe the way anything finds a running program:
//
//   & (Get-Process TaskSpaces* | Select-Object -First 1).Path ctl windows
//
// Exit codes and output are RemoteControl's; see ControlReply for what each code means.
static class ControlCommandLine
{
    const string Verb = "ctl";

    public static bool Wants(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals(Verb, StringComparison.OrdinalIgnoreCase);

    // The pipe this login session's TaskSpaces listens on. One definition shared by both ends.
    public static string PipeName => ControlWire.PipeName(Process.GetCurrentProcess().SessionId);

    // Runs the command and returns the process exit code. Called before anything else in OnStartup:
    // before the single-instance guard, which this process would otherwise trip (it IS a second
    // instance, by definition), and before any window, tray icon or hook exists.
    public static int Run(IReadOnlyList<string> args)
    {
        // Off the UI thread and waited for synchronously. Nothing in the client needs the dispatcher,
        // and blocking the UI thread on async work that captured it would deadlock.
        var reply = Task.Run(() => ControlClient.RunAsync(PipeName, args.Skip(1).ToList())).GetAwaiter().GetResult();
        Print(reply);
        return reply.ExitCode;
    }

    // This is a WinExe, so Windows gives it no console of its own. Two cases matter:
    //
    //   * The caller REDIRECTED our output, which is what an agent's shell tool does to capture it. The
    //     pipe handles are inherited even by a GUI process, so the standard streams already lead
    //     somewhere and are used as they are.
    //   * A person typed the command in a terminal. Nothing is redirected, so the standard handles are
    //     empty and the text would vanish. Attaching to the parent's console makes it appear there.
    //
    // UTF-8 without a BOM either way. The JSON itself is ASCII (System.Text.Json escapes the rest), but
    // a failure message can carry a workspace name in any script.
    static void Print(ControlReply reply)
    {
        const int stdOutput = -11, stdError = -12;
        var handle = reply.ExitCode == ControlReply.Ok ? stdOutput : stdError;
        if (GetStdHandle(handle) == IntPtr.Zero)
            AttachConsole(AttachParentProcess);

        using var stream = reply.ExitCode == ControlReply.Ok ? Console.OpenStandardOutput() : Console.OpenStandardError();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.WriteLine(reply.Output);
    }

    const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GetStdHandle(int standardHandle);
}
