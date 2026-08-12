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

        var picture = WindowPreview.Of(HandleOf(window), 1.0, 520, 340);

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

        var picture = WindowPreview.Of(HandleOf(window), 1.0, 200, 200);

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

        var natural = WindowPreview.Of(HandleOf(window), 1.0, 5000, 5000);
        var roomier = WindowPreview.Of(HandleOf(window), 1.0, 9000, 9000);
        var boxed = WindowPreview.Of(HandleOf(window), 1.0, natural.Value.PixelWidth / 2, 5000);

        Assert.True(natural.HasValue && roomier.HasValue && boxed.HasValue);
        // A bigger box than the window needs: same picture, not a stretched one.
        Assert.Equal(natural.Value.PixelWidth, roomier.Value.PixelWidth);
        // ...and a box smaller than the window does constrain it.
        Assert.True(boxed.Value.PixelWidth <= natural.Value.PixelWidth / 2,
            $"boxed was {boxed.Value.PixelWidth}, natural {natural.Value.PixelWidth}");

        window.Close();
    });

    // Petre: "make it 1/4 the actual size, make it large." A quarter of the width and a quarter of the
    // height, which is also what keeps the proportions without any arithmetic that could get them wrong.
    [Fact]
    public void A_quarter_scale_capture_is_a_quarter_of_the_window_on_both_axes() => StaThread.Run(() =>
    {
        var window = Striped(800, 600);

        var full = WindowPreview.Of(HandleOf(window), 1.0, 9000, 9000);
        var quarter = WindowPreview.Of(HandleOf(window), 0.25, 9000, 9000);

        Assert.True(full.HasValue && quarter.HasValue);
        // Within a pixel, since the scaled size is truncated to whole pixels.
        Assert.InRange(quarter.Value.PixelWidth, full.Value.PixelWidth / 4 - 1, full.Value.PixelWidth / 4 + 1);
        Assert.InRange(quarter.Value.PixelHeight, full.Value.PixelHeight / 4 - 1, full.Value.PixelHeight / 4 + 1);

        window.Close();
    });

    // "of the same proportions as the window itself." One factor is applied to both axes, so this holds by
    // construction -- which is the reason to assert it, since a future change to the fitting arithmetic
    // could quietly break it.
    [Fact]
    public void A_capture_keeps_the_windows_proportions() => StaThread.Run(() =>
    {
        var window = Striped(800, 400); // deliberately 2:1, so a squashed capture is obvious

        var picture = WindowPreview.Of(HandleOf(window), 0.25, 9000, 9000);

        Assert.True(picture.HasValue);
        var ratio = picture.Value.PixelWidth / (double)picture.Value.PixelHeight;
        Assert.InRange(ratio, 1.9, 2.1);

        window.Close();
    });

    // A window that has gone has no picture, and asking must not throw: the hover that asks arrives
    // milliseconds after a rebuild, and a window can close in between.
    [Fact]
    public void A_window_that_does_not_exist_yields_nothing() => StaThread.Run(() =>
        Assert.False(WindowPreview.Of(new WindowHandle(0x1), 1.0, 520, 340).HasValue));

    // MEASURED, not assumed: a minimised window's PrintWindow returns true and gives back one flat
    // colour, and its rectangle is the iconic nonsense at -32000 (#107). Declining is the honest answer.
    [Fact]
    public void A_minimised_window_yields_nothing() => StaThread.Run(() =>
    {
        var window = Striped(300, 240);
        window.WindowState = WindowState.Minimized;
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);

        Assert.False(WindowPreview.Of(HandleOf(window), 1.0, 520, 340).HasValue);

        window.Close();
    });

    // THE TEST THAT WAS MISSING, and the reason this file's other assertions were not enough. Petre, on
    // the first build: "previews don't work" -- with a screenshot showing the card at full preview size and
    // nothing but the card's own background inside it. The capture had been working all along; the image
    // was being drawn fully TRANSPARENT, because PrintWindow leaves alpha at zero over the opaque parts of
    // a window and the bitmap was being handed to WPF in a format that has an alpha channel.
    //
    // Size and frozen-ness were both correct throughout, which is exactly why neither caught it. So this
    // asserts the PIXELS: the three stripes have to be in there, and every pixel has to be opaque.
    [Fact]
    public void A_capture_carries_the_windows_own_colours_and_is_opaque() => StaThread.Run(() =>
    {
        var window = Striped(300, 240);

        var picture = WindowPreview.Of(HandleOf(window), 1.0, 520, 340);

        Assert.True(picture.HasValue);
        var pixels = new byte[picture.Value.PixelWidth * picture.Value.PixelHeight * 4];
        picture.Value.CopyPixels(pixels, picture.Value.PixelWidth * 4, 0);

        var colours = new HashSet<int>();
        for (var i = 0; i < pixels.Length; i += 4)
            colours.Add(pixels[i] | (pixels[i + 1] << 8) | (pixels[i + 2] << 16));

        // Red, green and blue bands, so a real capture cannot come back as one flat colour.
        Assert.True(colours.Count >= 3, $"only {colours.Count} distinct colours: the capture is blank");

        // Bgr32 has no alpha channel to misread, which is the whole fix: the format itself is the
        // guarantee, so the assertion is on the format rather than on a sample of bytes.
        Assert.Equal(System.Windows.Media.PixelFormats.Bgr32, picture.Value.Format);

        window.Close();
    });

    // The frozen requirement is not cosmetic: an unfrozen WPF bitmap takes thread affinity, and this one
    // is built on whichever thread the hover happened on. The same rule the bar's static brushes follow.
    [Fact]
    public void A_capture_is_frozen() => StaThread.Run(() =>
    {
        var window = Striped(300, 240);

        var picture = WindowPreview.Of(HandleOf(window), 1.0, 520, 340);

        Assert.True(picture.HasValue);
        Assert.True(picture.Value.IsFrozen);

        window.Close();
    });
}
