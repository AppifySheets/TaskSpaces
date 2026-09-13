using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace TaskSpaces.App;

// The taskbar button a minimized bar leaves behind (#173). Petre: "i want ability to minimize the
// floating bar, which gets minimized in every workspace as an item in the taskbar".
//
// WHY A SECOND WINDOW, rather than minimizing the bar itself, which is the obvious reading of the
// request. The bar cannot be the thing that holds the button:
//
//   * It carries WS_EX_TOOLWINDOW deliberately, and that flag is precisely what keeps a window off
//     the taskbar. FloatingBar.OnSourceInitialized records what it cost to learn that
//     ShowInTaskbar="False" alone was not enough to get out of Alt+Tab; taking the flag back off
//     would undo that fix to gain this one.
//   * Flipping ShowInTaskbar on a live WPF window DESTROYS AND RECREATES ITS HWND. The bar's handle
//     is handed to WindowMonitor.Ignore once at startup and is what ReclaimTopmost pokes every
//     second, so a recreated handle would silently un-ignore the bar -- it would start listing
//     ITSELF as a window in its own rows, which is the exact defect Ignore was added to fix -- while
//     the topmost timer went on addressing a handle that no longer existed.
//
// So the bar hides, untouched and with its handle intact, and this stands in its place: an ordinary
// window whose only job is to own a taskbar button and hand back one event when it is clicked.
//
// PINNED BY THE CALLER, and that is what makes it appear "in every workspace". Windows gives a
// taskbar button only to windows on the current desktop, so without the pin the button would exist
// on the desktop it was minimized from and nowhere else. App.PinAcrossDesktops does it, the same
// call the bar itself already uses.
//
// Minimized from the start and never restored. Every way back -- clicking the button, the X on its
// thumbnail, Alt+Tab -- is funnelled into RestoreRequested, and App answers it by showing the bar
// and closing this. The window itself must therefore never actually appear: a window that both held
// the button AND opened a panel would be a second surface to explain.
public sealed class BarStandIn : Window
{
    // Raised for every gesture that means "give me the bar back": the taskbar button, the X on the
    // taskbar thumbnail, Alt+Tab. One event rather than three handlers in App, so the restore path
    // cannot drift between them.
    public event Action? RestoreRequested;

    bool restoring; // one restore per stand-in: Closing and Activated can both fire for one click

    // Set the moment Windows starts closing this, so App does not call Close() on a window that is
    // already on its way out. WPF has no IsClosing of its own, and re-entering Close() from inside
    // the Closing handler is the one sequence the X produces every time.
    public bool IsClosing { get; private set; }

    public BarStandIn()
    {
        Title = "TaskSpaces";
        ShowInTaskbar = true;
        // NOT minimized yet: a window that has never been shown is on no virtual desktop, so it
        // cannot be pinned to all of them, and pinning is what puts this button on every workspace.
        // The caller shows it, pins it, then calls MinimizeToButton. Being parked off the virtual
        // screen with ShowActivated false is what makes that invisible.
        WindowState = WindowState.Normal;
        // Off the virtual screen as well as minimized. Restoring is never ALLOWED to happen (see
        // OnStateChanged), but if some shell gesture ever forces a frame out anyway, it paints
        // where nobody is looking rather than in the middle of Petre's screen.
        Left = -32000;
        Top = -32000;
        Width = 320;
        Height = 120;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;

        // Never topmost, unlike the bar: this window is a taskbar button and nothing else, and a
        // topmost window that cannot be seen is a window that can steal a click.
        Topmost = false;

        // Says what it is, for the one place it is visible: the taskbar thumbnail preview, which
        // the shell renders from the window whether or not a human ever sees the frame itself.
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)),
            Child = new TextBlock
            {
                Text = "TaskSpaces bar is minimized.\nClick to bring it back.",
                Foreground = Brushes.White,
                FontSize = 13,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            },
        };
    }

    // The handle, created before the window is shown so the caller can register it with
    // WindowMonitor.Ignore and pin it while there is still nothing to see. Same ordering the bar
    // uses, and for the same reason: an EVENT_OBJECT_SHOW that arrives before Ignore gives this
    // window a permanent row in the bar it is standing in for.
    public nint EnsureHandle() => new WindowInteropHelper(this).EnsureHandle();

    // Clicking a minimized window's taskbar button asks Windows to restore it. We want the BAR
    // back, not this, so the restore is refused -- the state is put straight back to Minimized --
    // and turned into the event instead. Without this the stand-in would flash a 320x120 panel on
    // its way to being closed.
    // Minimize, and only from here on does a restore mean anything. Arming matters because this
    // window is shown NORMAL for as long as it takes to pin it, and a state change during that
    // setup would otherwise read as the user asking for the bar back before it had even gone.
    public void MinimizeToButton()
    {
        WindowState = WindowState.Minimized;
        armed = true;
    }

    bool armed;

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (!armed || WindowState == WindowState.Minimized) return;
        WindowState = WindowState.Minimized;
        Restore();
    }

    // Alt+Tab reaches a minimized window without changing its state, so activation is its own way in.
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (armed) Restore();
    }

    // The X on the taskbar thumbnail. Petre chose "restore the bar" over "exit the app" for it, and
    // the reason holds on its own: Exit lives in the tray, and a taskbar X that killed TaskSpaces
    // would put quitting one stray click away from a button whose whole purpose is to be clicked.
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        IsClosing = true;
        Restore();
    }

    void Restore()
    {
        if (restoring) return;
        restoring = true;
        RestoreRequested?.Invoke();
    }
}
