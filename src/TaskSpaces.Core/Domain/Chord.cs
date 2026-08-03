using CSharpFunctionalExtensions;

namespace TaskSpaces.Core.Domain;

// Petre: "assign shortcuts to workspaces, ability to do so from within the workspaces window",
// and "configurable, along with shortcuts".
//
// Parses a human-typed chord ("Ctrl+Alt+1") into the two numbers RegisterHotKey wants. Pure and
// in Core, with no Win32 reference, so it is unit-testable and so the editor UI can validate
// what someone typed BEFORE anything tries to register it.
//
// WHY this exists at all: hotkeys are currently bound to Ctrl+Alt+1..9 by a workspace's LIST
// POSITION, so reordering workspaces silently changes what each chord does. Naming the chord on
// the workspace makes the binding survive both reordering and renaming.
public readonly record struct Chord(uint Modifiers, uint VirtualKey)
{
    // Win32 MOD_* values, written out rather than referenced so Core stays free of Win32.
    public const uint Alt = 0x0001, Control = 0x0002, Shift = 0x0004, Win = 0x0008;

    static readonly IReadOnlyDictionary<string, uint> Modifiers_ = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = Control, ["control"] = Control,
        ["alt"] = Alt,
        ["shift"] = Shift,
        ["win"] = Win, ["windows"] = Win, ["meta"] = Win,
    };

    // Only the keys worth binding a workspace to. Deliberately NOT every VK: an allowlist means
    // a typo is rejected with a message rather than registering some surprising key.
    static readonly IReadOnlyDictionary<string, uint> Keys = BuildKeys();

    static IReadOnlyDictionary<string, uint> BuildKeys()
    {
        var keys = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
            ["space"] = 0x20, ["tab"] = 0x09, ["home"] = 0x24, ["end"] = 0x23,
        };
        // '0'..'9' and 'A'..'Z' virtual-key codes equal their ASCII values.
        Enumerable.Range(0, 10).ToList().ForEach(d => keys[d.ToString()] = (uint)('0' + d));
        Enumerable.Range(0, 26).ToList().ForEach(i => keys[((char)('A' + i)).ToString()] = (uint)('A' + i));
        Enumerable.Range(1, 12).ToList().ForEach(f => keys["F" + f] = (uint)(0x70 + f - 1)); // VK_F1 = 0x70
        return keys;
    }

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
