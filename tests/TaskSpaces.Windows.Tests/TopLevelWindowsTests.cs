using System.Runtime.InteropServices;
using TaskSpaces.Windows.Monitoring;

namespace TaskSpaces.Windows.Tests;

// Petre: "i can't see Buzz in HRIS workspace."
//
// Buzz was running, its window visible on that desktop, and the bar had never heard of it. Measured
// with the app's own predicate:
//
//   0x1F12EA buzz-desktop vis=1 cloaked=0 owner=0x0 ex=0x00040110 class='Tauri Window' title=''
//            candidate=False
//
// The window carries WS_EX_APPWINDOW -- the explicit "put me on the taskbar" opt-in -- and has no
// title at all. IsTaskbarCandidate required text, on the reasoning that "taskbar buttons always have
// text", and that turns out to be false for a Tauri app that never sets one. Windows gives it a
// button anyway.
//
// The relaxation is narrow because it was measured before it was written. On his machine, every
// window that the old rule rejected ONLY for having no text:
//
//   with WS_EX_APPWINDOW:    1  (Buzz)
//   without it:              0
//
// So text OR an explicit opt-in, and nothing else changes.
//
// These tests make their own windows with CreateWindowEx rather than hunting for a real app's, which
// is the only way to hold the styles still: one titleless window that opts in, one that does not, and
// one with a title. Created off screen and destroyed at the end, on an STA thread like every other
// window test here.
public class TopLevelWindowsTests
{
    [Fact]
    public void A_titleless_window_that_asks_for_a_taskbar_button_is_a_candidate() => StaThread.Run(() =>
    {
        using var window = TestWindow.Create(title: "", exStyle: WS_EX_APPWINDOW);

        Assert.True(TopLevelWindows.IsTaskbarCandidate(window.Handle));
        Assert.Contains(window.Handle, TopLevelWindows.Enumerate());
    });

    // The other half of the rule, and the reason it is not simply "drop the text test": a titleless
    // window that never asked for a button is the shape of every helper window an app leaves lying
    // around, and the bar has no row to spend on those.
    [Fact]
    public void A_titleless_window_without_the_opt_in_is_not() => StaThread.Run(() =>
    {
        using var window = TestWindow.Create(title: "", exStyle: 0);

        Assert.False(TopLevelWindows.IsTaskbarCandidate(window.Handle));
    });

    [Fact]
    public void A_titled_window_is_a_candidate_as_it_always_was() => StaThread.Run(() =>
    {
        using var window = TestWindow.Create(title: "Ordinary window", exStyle: 0);

        Assert.True(TopLevelWindows.IsTaskbarCandidate(window.Handle));
    });

    // A tool window is still refused, opt-in or not... except with the opt-in, which is the existing
    // rule and is left exactly as it was: WS_EX_TOOLWINDOW keeps a window off the taskbar unless it
    // asks back in.
    [Fact]
    public void A_tool_window_is_still_refused_unless_it_opts_back_in() => StaThread.Run(() =>
    {
        using var tool = TestWindow.Create(title: "Palette", exStyle: WS_EX_TOOLWINDOW);
        using var optedIn = TestWindow.Create(title: "Palette", exStyle: WS_EX_TOOLWINDOW | WS_EX_APPWINDOW);

        Assert.False(TopLevelWindows.IsTaskbarCandidate(tool.Handle));
        Assert.True(TopLevelWindows.IsTaskbarCandidate(optedIn.Handle));
    });

    const uint WS_EX_TOOLWINDOW = 0x00000080;
    const uint WS_EX_APPWINDOW = 0x00040000;

    // A real top-level window of our own, with the styles a test asks for. "static" as a class name is
    // a stock control class that always exists, so nothing has to be registered.
    sealed class TestWindow : IDisposable
    {
        public required nint Handle { get; init; }

        public static TestWindow Create(string title, uint exStyle)
        {
            // WS_POPUP with no parent: top-level, so GetAncestor(GA_ROOT) answers itself, which the
            // predicate checks first. Parked far off the virtual screen so the suite never flashes
            // anything at whoever is running it.
            var handle = CreateWindowExW(exStyle, "static", title, WS_POPUP | WS_VISIBLE,
                -32000, -32000, 40, 40, 0, 0, 0, 0);
            Assert.NotEqual(0, handle);
            return new TestWindow { Handle = handle };
        }

        public void Dispose() => DestroyWindow(Handle);

        const uint WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern nint CreateWindowExW(uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyWindow(nint hwnd);
    }
}
