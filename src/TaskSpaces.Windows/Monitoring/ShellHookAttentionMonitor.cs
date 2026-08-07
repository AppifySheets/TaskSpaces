using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Windows.Monitoring;

using static NativeMethods;

// Flashing taskbar buttons, via the shell hook -- the same notification stream the real taskbar
// listens to.
//
// WHY NOT A WINEVENT, given that every other signal in this app is one. Because it does not
// work, and that was measured rather than reasoned about: a probe hooked EVERY event in both the
// SYSTEM (0x0001-0x00FF) and OBJECT (0x8000-0x80FF) ranges, then called FlashWindowEx on a
// window, and captured ZERO events. Flashing is simply not an accessibility event. The shell
// hook reports it as HSHELL_FLASH, and the same probe confirmed that arriving correctly --
// including for an app that happened to be flashing on the machine at the time.
//
// The listener is a window of our own, because RegisterShellHookWindow needs an hwnd to post to.
// A dedicated one rather than the floating bar's: the bar is created lazily, is destroyed and
// rebuilt by the tray toggle, and does not exist at all in compatibility mode, none of which
// should be able to take notifications down with it.
public sealed class ShellHookAttentionMonitor : IAttentionMonitor, IDisposable
{
    readonly Subject<WindowHandle> flashed = new();
    HwndSource? listener;
    uint shellMessage;

    public IObservable<WindowHandle> Flashed => flashed.AsObservable();

    public Result Start() =>
        Result.Try(() =>
        {
            // The message id is registered per-name and shared system-wide; RegisterWindowMessage
            // returns the same value to everyone asking for "SHELLHOOK".
            shellMessage = RegisterWindowMessageW("SHELLHOOK");
            if (shellMessage == 0) throw new InvalidOperationException("RegisterWindowMessage(\"SHELLHOOK\") failed");

            // A 0x0 window that is never shown. NOT HWND_MESSAGE (a message-only window):
            // RegisterShellHookWindow wants a real top-level window, and a message-only one is
            // not in the window manager's tree at all.
            listener = new HwndSource(new HwndSourceParameters("TaskSpaces shell hook listener")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0, // not WS_VISIBLE: created, never shown
            });
            listener.AddHook(OnMessage);

            if (!RegisterShellHookWindow(listener.Handle))
                throw new InvalidOperationException("RegisterShellHookWindow failed");
        }, e => $"Notification monitoring unavailable: {e.Message}");

    nint OnMessage(nint hwnd, int msg, nint wparam, nint lparam, ref bool handled)
    {
        // HSHELL_FLASH only. The shell hook also carries window created/destroyed/activated,
        // which this app already learns from WinEvents -- consuming them here as well would give
        // it two sources of truth for one fact, and they would disagree under load.
        if ((uint)msg == shellMessage && wparam.ToInt32() == HSHELL_FLASH && lparam != 0)
            flashed.OnNext(new WindowHandle(lparam));
        return 0;
    }

    public void Dispose()
    {
        if (listener is not null)
        {
            DeregisterShellHookWindow(listener.Handle);
            listener.RemoveHook(OnMessage);
            listener.Dispose();
        }
        flashed.OnCompleted();
    }
}
