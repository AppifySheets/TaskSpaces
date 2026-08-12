using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using TaskSpaces.App;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Windows.Tests;

// #134. Petre: "show a large preview of the window in another workspace when i hover over its icon."
//
// Against a REAL window, because every claim here is about what Windows does rather than about our
// arithmetic: PrintWindow either renders a window or it does not, and no fake can tell us which. The
// window these tests build is a WPF one on the STA thread, shown, with content chosen so a successful
// capture is unmistakable.
//
// What was measured on Petre's machine before any of this was written (see WindowPreview's header):
// PrintWindow with PW_RENDERFULLCONTENT captured a window CLOAKED on another virtual desktop in full,
// 10,276 distinct colours, in about 40ms. A minimised window came back as one flat colour. These tests
// pin the parts of that a test can reach; the cross-desktop half needs a second desktop and lives in the
// probe rather than here.
public class WindowPreviewTests
{
    // Content that cannot be mistaken for an empty capture: three saturated bands, so a real capture has
    // at least three colours and a blank one has exactly one.
    static Window Striped(int width, int height)
    {
        var stripes = new StackPanel();
        new[] { Colors.Red, Colors.Lime, Colors.Blue }.ToList().ForEach(colour =>
            stripes.Children.Add(new Border { Background = new SolidColorBrush(colour), Height = height / 3.0 }));

        var window = new Window
        {
            Title = "preview target",
            Width = width, Height = height, Left = 40, Top = 40,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize,
            Content = stripes,
        };
        window.Show();
        // One layout and render pass, or the window has no pixels yet and the capture is honestly blank.
        window.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        return window;
    }

    static WindowHandle HandleOf(Window window) => new(new WindowInteropHelper(window).Handle);

    [Fact]
    public void A_shown_window_can_be_captured() => StaThread.Run(() =>
    {
        var window = Striped(300, 240);

        var picture = WindowPreview.Of(HandleOf(window), 520, 340);

        Assert.True(picture.HasValue);
        Assert.True(picture.Value.PixelWidth > 1);
        Assert.True(picture.Value.PixelHeight > 1);

        window.Close();
    });

    // The capture is scaled in GDI before it ever becomes a WPF bitmap, which is what keeps a
    // 3747x2182 window from costing 32MB per hover. So the result must FIT the box it was given.
    [Fact]
    public void A_capture_is_scaled_to_fit_the_box_it_was_given() => StaThread.Run(() =>
    {
        var window = Striped(900, 700);

        var picture = WindowPreview.Of(HandleOf(window), 200, 200);

        Assert.True(picture.HasValue);
        Assert.True(picture.Value.PixelWidth <= 200, $"width was {picture.Value.PixelWidth}");
        Assert.True(picture.Value.PixelHeight <= 200, $"height was {picture.Value.PixelHeight}");

        window.Close();
    });

    // ...and never ENLARGED past its own size. A small window blown up to fill the box is a blur, and a
    // preview exists to be recognised rather than to fill a rectangle.
    //
    // Asserted WITHOUT naming any pixel count, which is the correction that made this test honest. The
    // first version compared against the box in DIPs and failed at 270 for a 180 DIP window: window
    // rectangles are physical pixels, so on a 150% display a 180 DIP window really is 270 across. What
    // matters is not the number but that a box larger than the window changes nothing.
    [Fact]
    public void A_small_window_is_not_blown_up_to_fill_the_box() => StaThread.Run(() =>
    {
        var window = Striped(180, 120);

        var natural = WindowPreview.Of(HandleOf(window), 5000, 5000);
        var roomier = WindowPreview.Of(HandleOf(window), 9000, 9000);
        var boxed = WindowPreview.Of(HandleOf(window), natural.Value.PixelWidth / 2, 5000);

        Assert.True(natural.HasValue && roomier.HasValue && boxed.HasValue);
        // A bigger box than the window needs: same picture, not a stretched one.
        Assert.Equal(natural.Value.PixelWidth, roomier.Value.PixelWidth);
        // ...and a box smaller than the window does constrain it.
        Assert.True(boxed.Value.PixelWidth <= natural.Value.PixelWidth / 2,
            $"boxed was {boxed.Value.PixelWidth}, natural {natural.Value.PixelWidth}");

        window.Close();
    });

    // A window that has gone has no picture, and asking must not throw: the hover that asks arrives
    // milliseconds after a rebuild, and a window can close in between.
    [Fact]
    public void A_window_that_does_not_exist_yields_nothing() => StaThread.Run(() =>
        Assert.False(WindowPreview.Of(new WindowHandle(0x1), 520, 340).HasValue));

    // MEASURED, not assumed: a minimised window's PrintWindow returns true and gives back one flat
    // colour, and its rectangle is the iconic nonsense at -32000 (#107). Declining is the honest answer.
    [Fact]
    public void A_minimised_window_yields_nothing() => StaThread.Run(() =>
    {
        var window = Striped(300, 240);
        window.WindowState = WindowState.Minimized;
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);

        Assert.False(WindowPreview.Of(HandleOf(window), 520, 340).HasValue);

        window.Close();
    });

    // The frozen requirement is not cosmetic: an unfrozen WPF bitmap takes thread affinity, and this one
    // is built on whichever thread the hover happened on. The same rule the bar's static brushes follow.
    [Fact]
    public void A_capture_is_frozen() => StaThread.Run(() =>
    {
        var window = Striped(300, 240);

        var picture = WindowPreview.Of(HandleOf(window), 520, 340);

        Assert.True(picture.HasValue);
        Assert.True(picture.Value.IsFrozen);

        window.Close();
    });
}
