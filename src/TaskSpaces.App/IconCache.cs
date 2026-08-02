using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TaskSpaces.App;

// exe path -> small frozen ImageSource, cached forever (exe icons don't change while
// an app runs, and the cache is tiny). Frozen so rows on any thread can share it.
public static class IconCache
{
    static readonly Dictionary<string, ImageSource?> cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? For(string? processPath)
    {
        if (processPath is null) return null;
        if (cache.TryGetValue(processPath, out var hit)) return hit;
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
        cache[processPath] = source;
        return source;
    }
}
