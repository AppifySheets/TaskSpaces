using System.Diagnostics;
using System.Management;
using System.Text;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Windows.Monitoring;

using static NativeMethods;

// hwnd -> immutable WindowInfo snapshot. Anything can vanish between calls
// (window closed, process exited), so the whole thing is Maybe, not exceptions.
public static class WindowInfoFactory
{
    public static Maybe<WindowInfo> FromHwnd(nint hwnd, IReadOnlyDictionary<uint, string>? commandLines = null)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return Maybe<WindowInfo>.None;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var path = TryPath(process);
            // Command line for EVERY window now (roster identity is path+args, not just browser
            // profiles); the startup snapshot passes a prefetched batch instead of querying here.
            //
            // The line above this one used to read "Per-event single WMI lookup ~10ms -- fine at
            // human window-opening rates". MEASURED on Petre's machine while chasing #51: 680ms,
            // and not merely on first use -- four consecutive per-pid queries cost 680/656/628/675.
            // A whole-table query for all 720 processes costs 720ms, which is to say the per-pid
            // WHERE clause buys nothing at all.
            //
            // That number is spent on the DISPATCHER THREAD, inside the WinEvent callback, every
            // time any window appears anywhere on the machine. See TryCommandLine.
            var commandLine = commandLines is not null
                ? commandLines.GetValueOrDefault(pid)
                : TryCommandLine(pid);
            return new WindowInfo(new WindowHandle(hwnd), (int)pid, process.ProcessName, path, TitleOf(hwnd), commandLine);
        }
        catch (ArgumentException) { return Maybe<WindowInfo>.None; } // process already gone
    }

    // One WMI round-trip for ALL processes -- the startup snapshot enumerates dozens of
    // windows; per-window queries there would cost seconds on the dispatcher thread.
    public static IReadOnlyDictionary<uint, string> AllCommandLines()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process");
            return searcher.Get().Cast<ManagementBaseObject>()
                .Where(o => o["CommandLine"] is string { Length: > 0 })
                .ToDictionary(o => (uint)o["ProcessId"], o => (string)o["CommandLine"]);
        }
        catch (Exception) { return new Dictionary<uint, string>(); } // best-effort, like TryCommandLine
    }

    public static string TitleOf(nint hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length == 0) return string.Empty;
        var buffer = new StringBuilder(length + 1);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    // Elevated processes deny module access to non-elevated callers -- expected, not an error.
    static string? TryPath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    static string? TryCommandLine(uint pid)
    {
        // Petre: "the new workspace / rename dialog takes a few seconds before the textbox
        // appears." Traced to here, and the chain is short once the numbers are real:
        //
        //   * PromptDialog is a top-level, visible, titled window, so IsTaskbarCandidate says yes
        //     -- ownership is not parentage, and ShowInTaskbar="False" does not set
        //     WS_EX_TOOLWINDOW (see FloatingBar.OnSourceInitialized, which had to learn the same
        //     thing about Alt+Tab).
        //   * Showing it therefore fires our own EVENT_OBJECT_SHOW, and the hook runs TryAppear on
        //     the DISPATCHER THREAD -- the same thread that has to render the dialog.
        //   * Which lands here, and costs 680ms. Twice, as the title arrives after the show.
        //
        // From the trace: prompt "Rename workspace" built=6ms rendered=1550ms. The dialog was
        // never slow; it was waiting for its own process to be asked about itself over WMI.
        //
        // Our own command line needs no service at all. This is the whole fix for that report, and
        // it is exact rather than approximate: Environment.CommandLine IS this process's command
        // line, which is more than WMI can promise for anyone else's.
        if (pid == (uint)Environment.ProcessId) return Environment.CommandLine;

        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            return searcher.Get().Cast<ManagementBaseObject>().FirstOrDefault()?["CommandLine"] as string;
        }
        // Command line is best-effort metadata for BrowserProfile rules only -- never worth
        // crashing over. WMI can throw more than ManagementException (UnauthorizedAccessException,
        // raw COM exceptions from the WMI service hiccuping, etc.), so swallow broadly.
        catch (Exception) { return null; }
    }
}
