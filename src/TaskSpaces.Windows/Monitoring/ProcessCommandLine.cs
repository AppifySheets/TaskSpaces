using System.Runtime.InteropServices;
using System.Text;

namespace TaskSpaces.Windows.Monitoring;

// A process's command line, read straight out of its own memory instead of asked of WMI (#59).
//
// WHY THIS EXISTS, in numbers measured on Petre's machine rather than claimed:
//
//                          per process     19 windowed processes
//     WMI (Win32_Process)      656ms              12,468ms
//     PEB (this)                 0.1ms                 2ms
//
//   ...and zero disagreements: both answered for 18 of the 19, byte for byte, and the one neither
//   could read was the same protected process on both sides.
//
// That is not a micro-optimisation, it is the difference between a working app and a stuttering
// one. The lookup runs inside the WinEvent callback on the DISPATCHER THREAD every time any window
// appears anywhere on the machine, so 656ms of WMI was 656ms of frozen bar -- clicks queued, the
// switch gesture stalled, and a dialog of our own could not even render (#51).
//
// HOW IT WORKS. Every process keeps its command line in its own address space, in the
// RTL_USER_PROCESS_PARAMETERS block hanging off its PEB. NtQueryInformationProcess gives the PEB's
// address, then three ReadProcessMemory calls walk PEB -> ProcessParameters -> CommandLine, which
// is a UNICODE_STRING (length, capacity, pointer) rather than a null-terminated one.
//
// WHAT IT CANNOT DO, and why that is acceptable. Reading another process's memory needs
// PROCESS_VM_READ, which is denied for elevated and protected processes -- so those return null
// and the caller falls back to WMI exactly as before. The command line was always best-effort
// metadata (WindowInfoFactory has swallowed WMI's failures from the beginning); this changes which
// answers are cheap, not which are guaranteed.
//
// UNDOCUMENTED OFFSETS, which is the real cost of doing it this way. NtQueryInformationProcess is
// documented but "may be altered or unavailable in future versions", and the PEB layout is
// internal. These offsets have been stable across every 64-bit Windows since Vista, and the code
// treats every step as failable -- a layout change surfaces as null, which means "fall back to
// WMI", not as a crash. That is the same posture the rest of this codebase takes towards the OS.
static class ProcessCommandLine
{
    const int ProcessBasicInformation = 0;
    const uint PROCESS_QUERY_INFORMATION = 0x0400, PROCESS_VM_READ = 0x0010;

    // x64 layout: ProcessParameters sits at 0x20 in the PEB, and CommandLine is the UNICODE_STRING
    // at 0x70 in RTL_USER_PROCESS_PARAMETERS. (ImagePathName is the one at 0x60 -- the pair are
    // adjacent and easy to confuse, which is why the probe compared results against WMI before any
    // of this was written.)
    const int PebProcessParameters = 0x20;
    const int ParametersCommandLine = 0x70;

    // Only the first two fields are read; the rest is here so the struct's SIZE is right, which is
    // what NtQueryInformationProcess validates its length argument against.
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
    static extern bool ReadProcessMemory(nint process, nint address, byte[] buffer, nint size, out nint read);

    // Null for anything that cannot be read -- a process that has exited, one we are not allowed to
    // open, or a PEB that does not look the way this code expects. Never throws: every caller of
    // this treats the command line as best-effort, and a window whose args are unknown is a window
    // that still has to appear.
    public static string? TryRead(uint pid)
    {
        var process = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
        if (process == 0) return null;
        try
        {
            var info = new ProcessBasicInfo();
            if (NtQueryInformationProcess(process, ProcessBasicInformation, ref info, Marshal.SizeOf<ProcessBasicInfo>(), out _) != 0) return null;
            if (info.PebBaseAddress == 0) return null;

            if (ReadPointer(process, info.PebBaseAddress + PebProcessParameters) is not { } parameters || parameters == 0) return null;

            // UNICODE_STRING: Length and MaximumLength as ushorts, four bytes of padding, then the
            // buffer pointer. Length is in BYTES, not characters -- the classic way to read half a
            // command line.
            if (Read(process, parameters + ParametersCommandLine, 16) is not { } unicodeString) return null;
            var bytes = BitConverter.ToUInt16(unicodeString, 0);
            var buffer = (nint)BitConverter.ToInt64(unicodeString, 8);
            if (bytes == 0 || buffer == 0) return null;

            return Read(process, buffer, bytes) is { } text ? Encoding.Unicode.GetString(text) : null;
        }
        catch (Exception)
        {
            // Belt and braces around three P/Invokes that are documented not to throw: this runs on
            // the dispatcher thread inside a WinEvent callback, where an escaping exception ends
            // the process rather than the operation.
            return null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    static nint? ReadPointer(nint process, nint address) =>
        Read(process, address, 8) is { } bytes ? (nint)BitConverter.ToInt64(bytes) : null;

    static byte[]? Read(nint process, nint address, int length)
    {
        var buffer = new byte[length];
        // A PARTIAL read is a failure here, not a partial success: half a pointer is a wild
        // address, and half a command line is a lie about what an app was launched with.
        return ReadProcessMemory(process, address, buffer, length, out var read) && (int)read == length
            ? buffer
            : null;
    }
}
