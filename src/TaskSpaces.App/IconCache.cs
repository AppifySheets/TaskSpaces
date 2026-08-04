using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TaskSpaces.Core.Domain;
using TaskSpaces.Windows.Monitoring;

namespace TaskSpaces.App;

// Small frozen ImageSources for the icon surfaces (floating bar, Manage rows), cached
// forever — icons don't change while an app runs, and the cache is tiny. Frozen so rows
// on any thread can share them.
//
// Petre: "i also don't see an icon for whatsapp app". Root cause, and the reason this
// class grew a second lookup path: WhatsApp is a Store app whose WhatsApp.Root.exe is a
// launcher stub with NO embedded icon. Icon.ExtractAssociatedIcon does not FAIL on it —
// it quietly returns the generic Windows default — so "extract from the exe, and fall
// back if that fails" could never have fixed it. There was nothing to fall back from.
//
// So the order is inverted: ASK THE WINDOW FIRST, and only ask the file if the window has
// nothing to say. A window's icon is what the taskbar itself draws, which is exactly the
// icon Petre expects to recognise.
public static class IconCache
{
    // WM_GETICON is answered by the owning process's UI thread, so this is capped rather
    // than left to block. 100ms is generous for a responsive app (these normally answer in
    // microseconds) and is only ever paid ONCE per window, because the result is cached
    // below whether it succeeded or not.
    const uint IconTimeoutMs = 100;

    // Keyed by (hwnd, exe path), not by hwnd alone: Windows recycles hwnds, and a recycled
    // handle belonging to a DIFFERENT app would otherwise inherit the previous window's
    // icon. Same app on a recycled handle shares an icon anyway, so the pair is exact
    // enough. This is the cache that matters for cost — one entry means zero P/Invoke on
    // every subsequent rebuild, and the bar rebuilds on every window event.
    static readonly Dictionary<(nint Hwnd, string Path), ImageSource?> byWindow = [];
    // HICON -> bitmap. Every window of an app usually reports the SAME icon handle, so this
    // keeps one bitmap per icon rather than one per window.
    static readonly Dictionary<nint, ImageSource?> byIconHandle = [];
    // The exe fallback, unchanged from the original implementation.
    static readonly Dictionary<string, ImageSource?> byExePath = new(StringComparer.OrdinalIgnoreCase);

    // For roster ("not running") rows, which have an app but no window to ask.
    public static ImageSource? For(string? processPath) => FromExe(processPath);

    // How many times an icon-less window is re-probed before we accept that it has no icon.
    // The bar rebuilds on every window event and at least every 5s, so a few dozen attempts
    // comfortably spans the second or two a freshly launched app needs.
    const int MaxProbes = 40;

    // Attempts so far for windows that have not produced an icon yet. Only failures appear
    // here; a success moves the entry into byWindow and stops all further probing.
    static readonly Dictionary<(nint Hwnd, string Path), int> attempts = [];

    // For live windows. Prefer this everywhere a WindowHandle is in hand.
    public static ImageSource? For(WindowHandle window, string? processPath)
    {
        var key = (window.Value, processPath ?? "");
        if (byWindow.TryGetValue(key, out var hit)) return hit;

        var resolved = FromWindow(window.Value) ?? FromExe(processPath);
        if (resolved is not null)
        {
            byWindow[key] = resolved;
            attempts.Remove(key);
            return resolved;
        }

        // Petre: "youtube music can't be seen in personal" -- it rendered as a blank
        // placeholder, on a window that answers WM_GETICON perfectly well. A probe taken while
        // it was showing that placeholder found IconBig = 0x17321537 and a class icon too, so
        // there was nothing wrong with the lookup. The bug was CACHING THE FAILURE.
        //
        // A window's icon arrives ASYNCHRONOUSLY: a freshly launched PWA answers WM_GETICON
        // with nothing until it has loaded. The first probe therefore lost, and storing that
        // null the same way a success is stored meant the window kept a placeholder for its
        // entire life. (A file's icon is static, which is why the exe cache below still caches
        // its misses -- the two cases are genuinely different.)
        //
        // Bounded rather than unbounded, because re-probing forever would cost up to five
        // P/Invokes per icon-less window on every rebuild, and a HUNG app makes three of those
        // wait out the timeout. After MaxProbes the answer really is "no icon".
        var tried = attempts.GetValueOrDefault(key) + 1;
        attempts[key] = tried;
        if (tried >= MaxProbes) byWindow[key] = null; // give up, and stop paying for it
        return null;
    }

    // In preference order. Lazily evaluated by the Select below, so a window that answers
    // ICON_BIG never pays for the other four probes.
    static readonly IReadOnlyList<Func<nint, nint>> IconSources =
    [
        hwnd => Ask(hwnd, NativeMethods.ICON_BIG),     // usually 32px: scales DOWN to the bar's 20px, which is the sharp direction
        hwnd => Ask(hwnd, NativeMethods.ICON_SMALL2),  // the one Windows synthesises for the window when the app set none
        hwnd => Ask(hwnd, NativeMethods.ICON_SMALL),
        hwnd => NativeMethods.GetClassLongPtr(hwnd, NativeMethods.GCLP_HICON),   // registered on the window CLASS rather than the window
        hwnd => NativeMethods.GetClassLongPtr(hwnd, NativeMethods.GCLP_HICONSM),
    ];

    static ImageSource? FromWindow(nint hwnd) =>
        IconSources
            .Select(ask => ask(hwnd))
            .Where(icon => icon != nint.Zero)
            .Select(Convert)
            .FirstOrDefault(source => source is not null);

    // Returns 0 both when the window has no such icon AND when the call timed out. The two
    // are deliberately not distinguished: either way we have no icon and should try the
    // next source.
    static nint Ask(nint hwnd, nint kind) =>
        NativeMethods.SendMessageTimeout(hwnd, NativeMethods.WM_GETICON, kind, nint.Zero,
            NativeMethods.SMTO_ABORTIFHUNG, IconTimeoutMs, out var icon) == nint.Zero
            ? nint.Zero
            : icon;

    // The HICON belongs to the WINDOW, not to us: no DestroyIcon here. CreateBitmapSource-
    // FromHIcon copies the pixels, so the bitmap outliving the window is fine.
    static ImageSource? Convert(nint hicon)
    {
        if (byIconHandle.TryGetValue(hicon, out var hit)) return hit;
        ImageSource? source = null;
        try
        {
            // FromEmptyOptions, unlike the exe path's fixed 16x16: window icons are usually
            // 32px and the bar draws at 20px, so keeping the native resolution and letting
            // WPF scale it down is sharper than forcing a 16px source back up to 20.
            source = Imaging.CreateBitmapSourceFromHIcon(hicon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
        }
        catch (Exception) { /* an odd/stale HICON: fall through to the next source */ }
        byIconHandle[hicon] = source;
        return source;
    }

    static ImageSource? FromExe(string? processPath)
    {
        if (processPath is null) return null;
        if (byExePath.TryGetValue(processPath, out var hit)) return hit;
        ImageSource? source = null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(processPath);
            if (icon is not null)
            {
                source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(16, 16));
                source.Freeze();
            }
        }
        catch (Exception) { /* missing/odd exe: a row without an icon beats no row */ }
        byExePath[processPath] = source;
        return source;
    }
}
