using System.Runtime.InteropServices;

namespace TaskSpaces.Windows.Dialogs;

// Windows' own colour picker (#97). Petre asked for more colours than the palette offers, and measuring
// said the palette had no room left: at the lane's 22% alpha over the bar's dark background, every dark
// colour tried came out closer to one of the shipped nine than that palette's own closest pair, and the
// only genuinely distinguishable additions were bright ones -- the register he rejected four times over
// in #68. So instead of nine more colours chosen by guesswork, the whole space, chosen by him.
//
// ChooseColorW rather than WinForms' ColorDialog, which is the same dialog with a framework attached:
// referencing WinForms from a WPF app to reach one native dialog would add its assemblies to every
// single-file publish for nothing this file does not already do in forty lines. The rest of this
// assembly is P/Invoke anyway.
//
// Two traps, both of them the reason this is worth its own file rather than being inlined:
//
//   * COLORREF is 0x00BBGGRR, not RGB. Writing a hex straight into it silently swaps red and blue,
//     which is the kind of bug that looks like the dialog misbehaving.
//   * The custom-colour swatches at the bottom are the CALLER's array: the dialog reads and writes it,
//     and forgetting it means the colours someone mixed are gone the next time they open it.
public static class ColourDialog
{
    const int CC_RGBINIT = 0x00000001;
    const int CC_FULLOPEN = 0x00000002;   // opens with the mixer showing, rather than needing a click
    const int CC_ANYCOLOR = 0x00000100;

    [StructLayout(LayoutKind.Sequential)]
    struct CHOOSECOLOR
    {
        public int lStructSize;
        public nint hwndOwner;
        public nint hInstance;
        public int rgbResult;
        public nint lpCustColors;
        public int Flags;
        public nint lCustData;
        public nint lpfnHook;
        public nint lpTemplateName;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ChooseColorW(ref CHOOSECOLOR cc);

    // The sixteen mixed-colour slots, kept for the life of the process so a colour someone blended is
    // still there the next time they open the dialog. Not persisted: they are scratch space, and a
    // colour worth keeping is one that is already on a workspace.
    static readonly int[] Custom = new int[16];

    // The chosen colour as "#RRGGBB", or null if the dialog was cancelled.
    //
    // `current` seeds the dialog so it opens on the colour the workspace already has rather than on
    // black. Anything unparseable is treated as no colour, which is also what the rest of the app does
    // with a hex it cannot read.
    public static string? Pick(nint owner, string? current)
    {
        var dialog = new CHOOSECOLOR
        {
            lStructSize = Marshal.SizeOf<CHOOSECOLOR>(),
            hwndOwner = owner,
            rgbResult = ToColorRef(current),
            Flags = CC_RGBINIT | CC_FULLOPEN | CC_ANYCOLOR,
        };

        // Pinned for the duration of the call: the dialog writes the mixed colours back through this
        // pointer, so the array must not move under it.
        var handle = GCHandle.Alloc(Custom, GCHandleType.Pinned);
        try
        {
            dialog.lpCustColors = handle.AddrOfPinnedObject();
            return ChooseColorW(ref dialog) ? FromColorRef(dialog.rgbResult) : null;
        }
        finally
        {
            handle.Free();
        }
    }

    // "#RRGGBB" -> 0x00BBGGRR. Zero (black) for anything unreadable, which is simply where the dialog
    // opens; it is not written anywhere.
    static int ToColorRef(string? hex)
    {
        var text = (hex ?? "").Trim().TrimStart('#');
        return text.Length == 6 && int.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var rgb)
            ? ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF)
            : 0;
    }

    static string FromColorRef(int colorRef) =>
        $"#{colorRef & 0xFF:X2}{(colorRef >> 8) & 0xFF:X2}{(colorRef >> 16) & 0xFF:X2}";
}
