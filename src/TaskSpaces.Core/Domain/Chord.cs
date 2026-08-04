using CSharpFunctionalExtensions;

namespace TaskSpaces.Core.Domain;

// Petre: "assign shortcuts to workspaces, ability to do so from within the workspaces window",
// and "configurable, along with shortcuts".
//
// Parses a human-typed chord ("Ctrl+Alt+1") into the two numbers RegisterHotKey wants. Pure and
// in Core, with no Win32 reference, so it is unit-testable and so the editor UI can validate
// what someone typed BEFORE anything tries to register it.
//
// WHY this exists at all: hotkeys used to be bound to Ctrl+Alt+1..9 by a workspace's LIST
// POSITION, so reordering workspaces silently changed what each chord did. Naming a chord makes
// the binding survive both reordering and renaming. Those positional chords have since been
// removed outright, and this is what the switcher's own configurable chord is parsed with --
// plus the groundwork for per-workspace named chords (Workspace.Shortcut) if direct jumps are
// wanted back on the keyboard.
public readonly record struct Chord(uint Modifiers, uint VirtualKey)
{
    // Win32 MOD_* values, written out rather than referenced so Core stays free of Win32.
    public const uint Alt = 0x0001, Control = 0x0002, Shift = 0x0004, Win = 0x0008;

    // Ordered LISTS rather than dictionaries, because these tables now serve two directions:
    // parsing (every spelling accepted) and DISPLAY (one canonical spelling per key). The
    // FIRST name given for a value is the canonical one, so "Ctrl" beats "Control" and "`"
    // beats "Backtick" when a chord is rendered back to text.
    static readonly IReadOnlyList<(string Name, uint Value)> ModifierTable =
    [
        ("Ctrl", Control), ("Control", Control),
        ("Alt", Alt),
        ("Shift", Shift),
        ("Win", Win), ("Windows", Win), ("Meta", Win),
    ];

    // Only the keys worth binding to. Deliberately NOT every VK: an allowlist means a typo is
    // rejected with a message rather than registering some surprising key.
    static readonly IReadOnlyList<(string Name, uint Value)> KeyTable = BuildKeys();

    static readonly IReadOnlyDictionary<string, uint> Modifiers_ = Spellings(ModifierTable);
    static readonly IReadOnlyDictionary<string, uint> Keys = Spellings(KeyTable);
    static readonly IReadOnlyDictionary<uint, string> ModifierNames = Canonical(ModifierTable);
    static readonly IReadOnlyDictionary<uint, string> KeyNames = Canonical(KeyTable);

    static IReadOnlyDictionary<string, uint> Spellings(IReadOnlyList<(string Name, uint Value)> table) =>
        table.ToDictionary(entry => entry.Name, entry => entry.Value, StringComparer.OrdinalIgnoreCase);

    static IReadOnlyDictionary<uint, string> Canonical(IReadOnlyList<(string Name, uint Value)> table) =>
        table.GroupBy(entry => entry.Value).ToDictionary(group => group.Key, group => group.First().Name);

    static IReadOnlyList<(string, uint)> BuildKeys()
    {
        var keys = new List<(string, uint)>
        {
            ("Left", 0x25), ("Up", 0x26), ("Right", 0x27), ("Down", 0x28),
            ("Space", 0x20), ("Tab", 0x09), ("Home", 0x24), ("End", 0x23),
            // Punctuation, added when the Alt+Tab-style switcher chord became configurable
            // (Petre: "i want it configurable"). That gesture wants a key reachable by the
            // same hand that is holding the modifiers, and ` is the classic choice -- it was
            // the app's own default before any of this was editable, so it had to be
            // spellable. The names ARE the literal characters, because that is what someone
            // types into a shortcut box; the word forms are aliases for the unprintable-
            // looking ones.
            ("`", 0xC0), ("Grave", 0xC0), ("Backtick", 0xC0),
            ("-", 0xBD), ("=", 0xBB),
            ("[", 0xDB), ("]", 0xDD), ("\\", 0xDC),
            (";", 0xBA), ("'", 0xDE),
            (",", 0xBC), (".", 0xBE), ("/", 0xBF),
        };
        // '0'..'9' and 'A'..'Z' virtual-key codes equal their ASCII values.
        Enumerable.Range(0, 10).ToList().ForEach(d => keys.Add((d.ToString(), (uint)('0' + d))));
        Enumerable.Range(0, 26).ToList().ForEach(i => keys.Add((((char)('A' + i)).ToString(), (uint)('A' + i))));
        Enumerable.Range(1, 12).ToList().ForEach(f => keys.Add(("F" + f, (uint)(0x70 + f - 1)))); // VK_F1 = 0x70
        return keys;
    }

    // --- display ---------------------------------------------------------------------
    // Canonical text, so what someone typed comes back normalised: "control + alt+1" reads
    // as "Ctrl+Alt+1" wherever it is shown. The switcher's own on-screen hint is built from
    // the two halves separately ("hold Ctrl+Alt · tap `"), which is why they are exposed
    // apart as well as joined.

    // Microsoft's own ordering -- Win, then Ctrl, then Alt, then Shift -- rather than the order
    // they were typed in, so two spellings of one chord never look like two different chords.
    // That is the order Windows' documentation uses throughout: "Win+Ctrl+D", "Win+Ctrl+Left",
    // "Ctrl+Shift+Esc", "Ctrl+Alt+Del". Win leading matters now that the default switcher chord
    // is Win+Ctrl+Tab, which the previous order rendered "Ctrl+Win+Tab" -- a spelling nobody
    // writes and which would have shown up in the UI and in state.json.
    //
    // `held` is a local copy on purpose: a lambda inside a struct cannot touch `this`, so
    // reading Modifiers directly in the Where below does not compile (CS1673).
    public string ModifiersText
    {
        get
        {
            var held = Modifiers;
            return string.Join("+", new[] { Win, Control, Alt, Shift }
                .Where(bit => (held & bit) != 0)
                .Select(bit => ModifierNames[bit]));
        }
    }

    // A raw VK can only appear here if something bypassed Parse; showing the number beats
    // showing nothing.
    public string KeyText => KeyNames.TryGetValue(VirtualKey, out var name) ? name : $"0x{VirtualKey:X2}";

    public override string ToString() => ModifiersText.Length == 0 ? KeyText : $"{ModifiersText}+{KeyText}";

    // Result rather than an exception: this validates user input, and the caller (the editor, or
    // startup reading a hand-edited state.json) needs to say WHY a chord was rejected.
    public static Result<Chord> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Result.Failure<Chord>("No shortcut given.");

        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return Result.Failure<Chord>($"'{text}' is not a shortcut.");

        var modifiers = 0u;
        var keyName = (string?)null;
        foreach (var part in parts)
            if (Modifiers_.TryGetValue(part, out var modifier)) modifiers |= modifier;
            // The LAST non-modifier wins rather than erroring, so "Ctrl+Alt+1" and a stray
            // duplicate both resolve; two different keys is caught below by the count check.
            else if (keyName is null) keyName = part;
            else return Result.Failure<Chord>($"'{text}' names more than one key.");

        if (keyName is null) return Result.Failure<Chord>($"'{text}' is only modifiers, with no key.");
        if (!Keys.TryGetValue(keyName, out var vk)) return Result.Failure<Chord>($"'{keyName}' is not a key TaskSpaces can bind.");
        // A bare key would steal that key from every app on the machine.
        if (modifiers == 0) return Result.Failure<Chord>($"'{text}' needs at least one of Ctrl, Alt, Shift or Win.");

        return new Chord(modifiers, vk);
    }
}
