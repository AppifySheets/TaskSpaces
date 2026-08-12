using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.App;

// A picture of a window, for the hover card (#134). Petre: "show a large preview of the window in
// another workspace when i hover over its icon in another workspace."
//
// MEASURED before it was written, because the whole feature hangs on one question -- can a window that
// is CLOAKED because it lives on another virtual desktop be rendered at all -- and that is a question
// about the compositor's rules rather than something to reason out. Probe results, against his own
// windows:
//
//   this desktop, visible   PrintWindow ok=True  315,268 distinct colours  27ms, 26ms, 33ms
//   OTHER desktop, cloaked  PrintWindow ok=True   10,276 distinct colours  49ms, 40ms, 33ms
//   minimised               PrintWindow ok=True        1 colour            (blank)
//
// The saved PNG of the middle row is the full content of a VS Code window sitting on another desktop,
// title bar and all. So: yes, and PW_RENDERFULLCONTENT is the flag that makes it so -- without it, DWM
// composited content (which is all content, for anything modern) comes back blank.
//
// DWM THUMBNAILS were the preferred candidate in the issue and are not used, which is worth recording
// rather than leaving as an apparent oversight. They would be live rather than a snapshot and cost no
// bitmap copying. The probe could not answer whether DWM renders a cloaked source: verifying it means
// reading the SCREEN, since a thumbnail is composited by DWM and never appears in the destination
// window's own paint, and the destination kept being covered by a topmost Teams call window -- the probe
// reported RENDERED three times while its own preflight said the pixels it measured were not ours. That
// is a measurement to redo on a quiet machine, not a reason to hold up the feature.
//
// MINIMISED windows get no preview, and that is measured rather than assumed: PrintWindow returns true
// and gives back one flat colour, because a minimised window has no redirection surface to render. Its
// rectangle is nonsense too (#107). The card falls back to text alone.
public static class WindowPreview
{
    [DllImport("user32.dll")] static extern bool PrintWindow(nint window, nint dc, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint window, out RECT rect);
    [DllImport("user32.dll")] static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] static extern nint GetDC(nint window);
    [DllImport("user32.dll")] static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(
        nint dst, int x, int y, int w, int h, nint src, int sx, int sy, int sw, int sh, uint rop);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(nint dc, int mode);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    const uint PW_RENDERFULLCONTENT = 0x2;
    const uint SRCCOPY = 0x00CC0020;
    const int HALFTONE = 4; // smooth downscale; COLORONCOLOR drops rows and turns text to noise

    /// <summary>
    /// A snapshot of <paramref name="window"/>, scaled to fit inside the given box, or None when there
    /// is nothing to show (a minimised window, a window that has gone, a refused capture).
    /// </summary>
    public static Maybe<BitmapSource> Of(WindowHandle window, int boxWidth, int boxHeight)
    {
        // Minimised: measured blank, so declining is honest rather than cautious.
        if (IsIconic(window.Value) || !GetWindowRect(window.Value, out var rect)) return Maybe<BitmapSource>.None;

        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        if (width <= 1 || height <= 1) return Maybe<BitmapSource>.None;

        // Fit inside the box, never enlarging: a small window is shown at its own size rather than
        // stretched into a blur.
        var scale = Math.Min(1.0, Math.Min(boxWidth / (double)width, boxHeight / (double)height));
        int shrunkWidth = Math.Max(1, (int)(width * scale)), shrunkHeight = Math.Max(1, (int)(height * scale));

        var screen = GetDC(0);
        var fullDc = CreateCompatibleDC(screen);
        var full = CreateCompatibleBitmap(screen, width, height);
        var smallDc = CreateCompatibleDC(screen);
        var small = CreateCompatibleBitmap(screen, shrunkWidth, shrunkHeight);
        var oldFull = SelectObject(fullDc, full);
        var oldSmall = SelectObject(smallDc, small);

        try
        {
            // The capture has to be full size -- PrintWindow renders the window, not a scaled version --
            // and is then SHRUNK IN GDI before anything crosses into WPF. That ordering is the point: a
            // full-size 3747x2182 BitmapSource is a 32MB allocation per hover, where the shrunk one is
            // under a megabyte, and the shrink costs nothing next to the 40ms the capture already costs.
            if (!PrintWindow(window.Value, fullDc, PW_RENDERFULLCONTENT)) return Maybe<BitmapSource>.None;

            SetStretchBltMode(smallDc, HALFTONE);
            if (!StretchBlt(smallDc, 0, 0, shrunkWidth, shrunkHeight, fullDc, 0, 0, width, height, SRCCOPY))
                return Maybe<BitmapSource>.None;

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                small, 0, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            // Frozen for the reason every static brush in this app is frozen: an unfrozen WPF object takes
            // thread affinity, and this one is built on whichever thread the hover happened on.
            source.Freeze();
            return Maybe<BitmapSource>.From(source);
        }
        catch (Exception)
        {
            // A window can go away mid-capture, and a hover is never worth a crash.
            return Maybe<BitmapSource>.None;
        }
        finally
        {
            SelectObject(fullDc, oldFull);
            SelectObject(smallDc, oldSmall);
            DeleteObject(full);
            DeleteObject(small);
            DeleteDC(fullDc);
            DeleteDC(smallDc);
            ReleaseDC(0, screen);
        }
    }
}
