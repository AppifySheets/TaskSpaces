using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TaskSpaces.Core.Domain;
using TaskSpaces.Windows.Monitoring;

namespace TaskSpaces.App;

// Small frozen ImageSources for the icon surfaces (floating bar, Manage rows), cached
// forever -- icons don't change while an app runs, and the cache is tiny. Frozen so rows
// on any thread can share them.
//
// Petre: "i also don't see an icon for whatsapp app". Root cause, and the reason this
// class grew a second lookup path: WhatsApp is a Store app whose WhatsApp.Root.exe is a
// launcher stub with NO embedded icon. Icon.ExtractAssociatedIcon does not FAIL on it --
// it quietly returns the generic Windows default -- so "extract from the exe, and fall
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
    // enough. This is the cache that matters for cost -- one entry means zero P/Invoke on
    // every subsequent rebuild, and the bar rebuilds on every window event.
    static readonly Dictionary<(nint Hwnd, string Path), ImageSource?> byWindow = [];
    // HICON -> bitmap. Every window of an app usually reports the SAME icon handle, so this
    // keeps one bitmap per icon rather than one per window.
    static readonly Dictionary<nint, ImageSource?> byIconHandle = [];
    // The exe fallback, unchanged from the original implementation.
    static readonly Dictionary<string, ImageSource?> byExePath = new(StringComparer.OrdinalIgnoreCase);

    // For roster ("not running") rows, which have an app but no window to ask.
    public static ImageSource? For(string? processPath) => FromExe(processPath);

    // How long an icon-less window keeps being re-asked before we accept that it has no icon
    // of its own, and how often it is asked within that period.
    //
    // Bounded by TIME, not by a count of attempts, and that distinction is the entire second
    // round of this bug. The count version gave up after 40 rebuilds on the reasoning that
    // "the bar rebuilds on every window event and at least every 5s, so a few dozen attempts
    // comfortably spans the second or two a freshly launched app needs". A rebuild count is
    // not a clock: the bar rebuilds on EVERY window event from ANY window, so on a busy
    // machine the budget can burn out in seconds, and on a quiet one 40 rebuilds is three
    // minutes. Either way it bore no relation to how long the PWA actually needed, and when
    // it ran out early the browser's placeholder was frozen in as the final answer -- which
    // is exactly what Petre saw: "Now it shows up as a edge icon, not YouTube music icon."
    //
    // The interval is what keeps this cheap. Probing costs up to five P/Invokes, three of
    // which are SendMessageTimeout, so it is rate-limited rather than run on every rebuild;
    // between probes the cached fallback is returned without touching Win32 at all.
    static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(1);
    static readonly TimeSpan ProbeDeadline = TimeSpan.FromMinutes(2);

    // First sighting and last probe, for windows that have not produced an icon of their own
    // yet. A success moves the entry into byWindow and removes it from here.
    static readonly Dictionary<(nint Hwnd, string Path), (DateTime FirstSeen, DateTime LastProbe)> probing = [];

    // True while some window is still being shown a placeholder. The floating bar watches this
    // to know whether to keep re-drawing: it has NO periodic rebuild of its own (it redraws on
    // window events only), so without something asking again, the one probe taken when a PWA
    // first appears -- while its icon is still blank -- would be the only one ever taken.
    public static bool HasPendingIcons => probing.Count > 0;

    // For live windows. Prefer this everywhere a WindowHandle is in hand.
    public static ImageSource? For(WindowHandle window, string? processPath)
    {
        var key = (window.Value, processPath ?? "");
        if (byWindow.TryGetValue(key, out var hit)) return hit;

        var now = DateTime.UtcNow;
        var state = probing.TryGetValue(key, out var seen) ? seen : (FirstSeen: now, LastProbe: DateTime.MinValue);

        // Only the window's OWN icon is a final answer. The class icon and the exe icon are
        // both process-wide, so for any PWA they are the browser's -- correct-looking, and
        // wrong. Rate-limited: between probes we skip the three SendMessageTimeouts entirely.
        if (now - state.LastProbe >= ProbeInterval)
        {
            state = (state.FirstSeen, now);
            var own = FromSources(window.Value, OwnIconSources);
            if (own is not null)
            {
                byWindow[key] = own;
                probing.Remove(key);
                return own;
            }
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
        // ROUND 2, and the reason the round-1 fix above was not enough. Petre, on YouTube
        // Music: "I restarted YouTube music, and the icon is still missing... It doesn't have
        // a browser icon either. It has nothing." Measured against the live window: it answers
        // ICON_BIG with a perfectly valid handle whose bitmap is 32x32 and FULLY TRANSPARENT
        // while the PWA loads. So "answers with nothing" was wrong -- it answers with an EMPTY
        // ICON, not a zero handle. Convert succeeded, a blank bitmap was cached as a success,
        // and this retry path -- which only runs when the result is null -- never executed.
        // Hence: restarting the PWA reproduced it every time, and only restarting TaskSpaces
        // (which empties these dictionaries) cleared it. Convert now rejects blank bitmaps,
        // which is what routes this class of window here at all.
        //
        // The exe icon is therefore PROVISIONAL, never final: it is shown so the row is not
        // empty while a PWA loads, but the window keeps being asked on every rebuild, and its
        // own icon replaces the placeholder the moment it exists. Caching the fallback the way
        // a real answer is cached is the same mistake one level up -- a browser-shaped icon
        // for a window that has its own, for the rest of the session.
        //
        // Bounded rather than unbounded, because re-probing forever would cost five P/Invokes
        // per icon-less window for the life of the session, and a HUNG app makes three of them
        // wait out the timeout. Two minutes of asking is far longer than any app takes to
        // publish an icon, and after it the answer really is "this window has no icon of its
        // own" -- so whatever the exe gave us (possibly null) becomes final.
        probing[key] = state;
        // Class icon before exe icon: both are process-wide, but the class one is what the
        // taskbar itself would draw, and it costs no message send.
        var provisional = FromSources(window.Value, ClassIconSources) ?? FromExe(processPath);
        if (now - state.FirstSeen >= ProbeDeadline)
        {
            byWindow[key] = provisional; // give up on the window, and stop paying for it
            probing.Remove(key);
        }
        return provisional;
    }

    // THIS WINDOW's own icon, asked of the owning process. The only source specific to the
    // window rather than to its whole process, and therefore the only FINAL answer.
    // Lazily evaluated, so a window that answers ICON_BIG never pays for the other two.
    static readonly IReadOnlyList<Func<nint, nint>> OwnIconSources =
    [
        hwnd => Ask(hwnd, NativeMethods.ICON_BIG),     // usually 32px: scales DOWN to the bar's 20px, which is the sharp direction
        hwnd => Ask(hwnd, NativeMethods.ICON_SMALL2),  // the one Windows synthesises for the window when the app set none
        hwnd => Ask(hwnd, NativeMethods.ICON_SMALL),
    ];

    // Registered on the window CLASS, so it is shared by every window of that class in the
    // process -- and that is exactly why it cannot be a final answer.
    //
    // Measured on Petre's machine, on one YouTube Music window, at the same instant:
    //   ICON_BIG   (the window's own)  avg R=251 G=33  B=77   the red YouTube Music mark
    //   GCLP_HICON (the class)         avg R=36  G=154 B=178  Microsoft Edge's blue
    //
    // Every Chromium PWA is a Chrome_WidgetWin_1 window in the browser's process, so the class
    // icon is ALWAYS the browser's. A window that has just been created answers WM_GETICON
    // with zero for a moment before it publishes its own icon, and in that moment this source
    // wins -- returning a perfectly valid, non-blank, WRONG icon. Cached as success, it stuck
    // for the life of the window: "same, edge icon, not changing for youtube music icon".
    static readonly IReadOnlyList<Func<nint, nint>> ClassIconSources =
    [
        hwnd => NativeMethods.GetClassLongPtr(hwnd, NativeMethods.GCLP_HICON),
        hwnd => NativeMethods.GetClassLongPtr(hwnd, NativeMethods.GCLP_HICONSM),
    ];

    static ImageSource? FromSources(nint hwnd, IReadOnlyList<Func<nint, nint>> sources) =>
        sources
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
        try
        {
            // FromEmptyOptions, unlike the exe path's fixed 16x16: window icons are usually
            // 32px and the bar draws at 20px, so keeping the native resolution and letting
            // WPF scale it down is sharper than forcing a 16px source back up to 20.
            var source = Imaging.CreateBitmapSourceFromHIcon(hicon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();

            // A loading PWA hands out a valid handle to a FULLY TRANSPARENT icon (measured on
            // YouTube Music: 32x32, every pixel alpha 0). Rendering it produces an icon-shaped
            // hole, indistinguishable from a missing icon, and -- worse -- it used to be cached
            // as a success, so the real icon arriving a second later was never looked at.
            // Treated as "no icon yet" so the caller's re-probe loop keeps asking.
            if (IsBlank(source)) return null;

            byIconHandle[hicon] = source;
            return source;
        }
        // Neither an exception nor a blank result is cached, for the reason this file learned
        // the hard way: never cache a lookup failure that can succeed later. HICONs are
        // recycled by Windows, so a null stored here would also be inherited by whatever
        // unrelated icon lands on the same handle value next -- the same hazard byWindow
        // guards against by keying on (hwnd, path) rather than the hwnd alone.
        catch (Exception) { return null; } // an odd/stale HICON: fall through to the next source
    }

    // True when every pixel is fully transparent. Public for the tests: it is the one piece of
    // this class that is pure, and it is the piece the YouTube Music bug turned on.
    public static bool IsBlank(BitmapSource source)
    {
        var bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        // Alpha is the 4th byte of each BGRA quad; one visible pixel is enough to be a real icon.
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) return false;
        return true;
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
