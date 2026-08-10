using System.Runtime.InteropServices;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Windows.Monitoring;

// Who started whom (#94), read out of the process itself rather than asked of WMI.
//
// Same trade as ProcessCommandLine next door, and for the same measured reason: this runs on the
// DISPATCHER THREAD inside a WinEvent callback every time a window appears, and Win32_Process cost
// 656ms per process on Petre's machine (#59). NtQueryInformationProcess answers in about 0.1ms, and
// the parent pid is a field of the same ProcessBasicInformation block that file already reads for the
// PEB address, so this is the cheap half of a call the codebase already trusts.
//
// TWO THINGS A BARE PARENT PID CANNOT BE TRUSTED ABOUT, both guarded here rather than in the rule
// above it, because both are questions about the OS:
//
//   * A pid outlives its process, and Windows reuses pids aggressively. So a parent pid can name a
//     process that has nothing to do with the child -- measured on this machine, several windowed
//     apps already have parents reported as gone. Reporting one of those as "the app that started
//     this" would move a window to a workspace chosen by coincidence.
//   * The parent must be OLDER than the child. That is the check that catches pid reuse, since a
//     recycled pid belongs to a process that started later. GetProcessTimes answers it exactly, and a
//     tie is accepted: two processes created in the same 100ns tick is not something to rule out.
//
// Anything that cannot be read is None: a protected process, an elevated one, or one that exited
// while we were asking. The caller treats None as "no launcher", which is the same answer as "started
// by the shell", and both mean the window stays where Windows put it.
public sealed class ProcessTree : IProcessTree
{
    const int ProcessBasicInformation = 0;

    // LIMITED rather than PROCESS_QUERY_INFORMATION: it is granted for processes running as other
    // users and at higher integrity levels, which plain QUERY_INFORMATION is not, and it is enough
    // for both the basic information block and the process times. Reading a parent pid needs no
    // access to the process's memory at all, unlike the command line.
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessBasicInfo
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint ParentProcessId;
    }

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(nint process, int infoClass, ref ProcessBasicInfo info, int length, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern nint OpenProcess(uint access, bool inheritHandle, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    // The image name, which OpenProcess is not needed for at all -- but the module path is, so this
    // uses the same handle as everything else and asks for the base name.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint QueryFullProcessImageNameW(nint process, uint flags, char[] name, ref uint size);

    public Maybe<ProcessFacts> Of(int processId)
    {
        if (processId <= 0) return Maybe<ProcessFacts>.None;

        var child = Read(processId);
        if (child is not { } facts) return Maybe<ProcessFacts>.None;

        // The parent is only reported when it is still there AND older, which is what rules out a
        // recycled pid. Otherwise the process is reported with no parent, which ends the walk rather
        // than sending it somewhere arbitrary.
        var parent = Read(facts.ParentProcessId);
        var plausible = parent is { } up && up.Created <= facts.Created;

        return new ProcessFacts(processId, facts.Name, plausible ? facts.ParentProcessId : 0);
    }

    readonly record struct Snapshot(string Name, int ParentProcessId, long Created);

    static Snapshot? Read(int processId)
    {
        if (processId <= 0) return null;

        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (process == 0) return null;
        try
        {
            var info = new ProcessBasicInfo();
            if (NtQueryInformationProcess(process, ProcessBasicInformation, ref info, Marshal.SizeOf<ProcessBasicInfo>(), out _) != 0)
                return null;
            if (!GetProcessTimes(process, out var created, out _, out _, out _)) return null;

            // A process with no readable NAME is treated as unreadable altogether, and that is the
            // check that rules out a zombie. Measured while writing the tests: a process that has
            // EXITED can still be opened for as long as somebody holds a handle to it -- the kernel
            // object outlives the process -- and its recorded parent pid is still there, but its image
            // is gone, so this comes back empty.
            //
            // Ending the walk there is the conservative direction, and it matters for one case in
            // particular: a name that cannot be read cannot be recognised as the shell, so a zombie
            // explorer would otherwise be walked straight through as if it were an ordinary app's
            // helper process.
            var name = NameOf(process);
            return string.IsNullOrEmpty(name) ? null : new Snapshot(name, (int)info.ParentProcessId, created);
        }
        catch (Exception)
        {
            // Belt and braces around P/Invokes documented not to throw, for the reason
            // ProcessCommandLine gives: this runs inside a WinEvent callback on the dispatcher
            // thread, where an escaping exception ends the process rather than the operation.
            return null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    // "C:\Windows\explorer.exe" -> "explorer", matching WindowInfo.ProcessName so the rule can compare
    // the two without either side trimming. Empty when the path cannot be read, which simply means the
    // shell test below cannot match it.
    static string NameOf(nint process)
    {
        var buffer = new char[260];
        var size = (uint)buffer.Length;
        return QueryFullProcessImageNameW(process, 0, buffer, ref size) != 0
            ? Path.GetFileNameWithoutExtension(new string(buffer, 0, (int)size))
            : "";
    }
}
