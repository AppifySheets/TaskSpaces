using System.Windows.Media;
using System.Windows.Media.Imaging;
using TaskSpaces.App;

namespace TaskSpaces.Windows.Tests;

// Petre, on YouTube Music: "I restarted YouTube music, and the icon is still missing...
// It doesn't have a browser icon either. It has nothing."
//
// Root cause, measured on the live window rather than guessed: a Chromium PWA that is still
// loading answers WM_GETICON with a VALID handle to a 32x32 icon in which every pixel is
// fully transparent. The previous code took "we got an ImageSource" as success, cached the
// blank bitmap forever, and never asked again -- so restarting the PWA reproduced it every
// time and only restarting TaskSpaces cleared it.
//
// These pin the predicate that distinguishes the two, which is the whole hinge of the fix.
public class IconCacheBlankTests
{
    // 32x32 BGRA, matching the size a real window icon comes back as. `alpha` is written to
    // every pixel, so 0 reproduces exactly what a loading PWA hands out.
    static BitmapSource Icon(byte alpha, bool oneVisiblePixel = false)
    {
        const int size = 32;
        var stride = size * 4;
        var pixels = new byte[stride * size];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x4D;     // B
            pixels[i + 1] = 0x21; // G
            pixels[i + 2] = 0xFB; // R  (roughly the YouTube Music red)
            pixels[i + 3] = alpha;
        }
        if (oneVisiblePixel) pixels[3] = 0xFF;
        return BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
    }

    [Fact]
    public void A_fully_transparent_icon_is_blank() => StaThread.Run(() =>
        Assert.True(IconCache.IsBlank(Icon(alpha: 0))));

    [Fact]
    public void A_normal_icon_is_not_blank() => StaThread.Run(() =>
        Assert.False(IconCache.IsBlank(Icon(alpha: 0xFF))));

    // The boundary that matters: "mostly transparent" is what a real icon looks like -- the
    // YouTube Music logo measured 856 opaque pixels out of 1024. Only ENTIRELY empty counts,
    // or every icon with rounded corners would be thrown away.
    [Fact]
    public void One_visible_pixel_is_enough_to_be_a_real_icon() => StaThread.Run(() =>
        Assert.False(IconCache.IsBlank(Icon(alpha: 0, oneVisiblePixel: true))));

    // #105, second visit: the ordinal colour band groups by the PICTURE, because every cheaper proxy
    // for "the same app" was measured wrong on Petre's machine (IconCache.ArtworkKeyOf lists them).
    // These pin the two halves of that claim.
    [Fact]
    public void The_same_picture_fingerprints_the_same() => StaThread.Run(() =>
        Assert.Equal(IconCache.Fingerprint(Icon(alpha: 0xFF)), IconCache.Fingerprint(Icon(alpha: 0xFF))));

    [Fact]
    public void A_different_picture_fingerprints_differently() => StaThread.Run(() =>
        Assert.NotEqual(IconCache.Fingerprint(Icon(alpha: 0xFF)), IconCache.Fingerprint(Icon(alpha: 0x80))));

    // One pixel apart is still a different picture. The band's job is telling apart windows that look
    // identical, so "nearly identical" has to count as different or a badged icon would be swallowed.
    [Fact]
    public void One_pixel_of_difference_is_enough() => StaThread.Run(() =>
        // The blank icon against the same icon with a single pixel switched on: one byte apart out of
        // four thousand, and it has to read as a different picture.
        Assert.NotEqual(
            IconCache.Fingerprint(Icon(alpha: 0)),
            IconCache.Fingerprint(Icon(alpha: 0, oneVisiblePixel: true))));

    // Window icons do not always arrive as Bgra32; the predicate must convert rather than
    // assume, or it would read another format's bytes as alpha and answer nonsense.
    [Fact]
    public void A_non_bgra_icon_is_still_measured_correctly() => StaThread.Run(() =>
    {
        var opaque = new FormatConvertedBitmap(Icon(alpha: 0xFF), PixelFormats.Bgr24, null, 0);

        // Bgr24 has no alpha channel at all, so every pixel is by definition visible.
        Assert.False(IconCache.IsBlank(opaque));
    });
}
