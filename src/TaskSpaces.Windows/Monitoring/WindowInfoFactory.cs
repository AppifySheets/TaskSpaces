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
    // WMI command-line lookup is slow (~10ms) — only browsers get it, and only because
    // BrowserProfile rules need --profile-directory.
    static readonly IReadOnlySet<string> Browsers =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "chrome", "msedge", "firefox", "brave", "vivaldi", "opera" };

    public static Maybe<WindowInfo> FromHwnd(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return Maybe<WindowInfo>.None;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var path = TryPath(process);
            return new WindowInfo(
                new WindowHandle(hwnd), (int)pid, process.ProcessName, path, TitleOf(hwnd),
                Browsers.Contains(process.ProcessName) ? TryCommandLine(pid) : null);
        }
        catch (ArgumentException) { return Maybe<WindowInfo>.None; } // process already gone
    }

    public static string TitleOf(nint hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length == 0) return string.Empty;
        var buffer = new StringBuilder(length + 1);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    // Elevated processes deny module access to non-elevated callers — expected, not an error.
    static string? TryPath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    static string? TryCommandLine(uint pid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            return searcher.Get().Cast<ManagementBaseObject>().FirstOrDefault()?["CommandLine"] as string;
        }
        // Command line is best-effort metadata for BrowserProfile rules only — never worth
        // crashing over. WMI can throw more than ManagementException (UnauthorizedAccessException,
        // raw COM exceptions from the WMI service hiccuping, etc.), so swallow broadly.
        catch (Exception) { return null; }
    }
}
