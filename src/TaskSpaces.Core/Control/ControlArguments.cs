using System.Globalization;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Control;

// The `move` command, parsed. Handles and titles are the two ways to name a window, and a request may
// mix them; every one of them has to find a window or nothing moves (see RemoteControl.Move).
public sealed record MoveRequest(string Workspace, bool Create, IReadOnlyList<WindowHandle> Handles, IReadOnlyList<string> Titles)
{
    public bool NamesNoWindow => Handles.Count == 0 && Titles.Count == 0;
}

// The command line after `ctl`, with the parsing kept apart from the doing so both halves can be tested
// without a pipe or a running app.
public static class ControlArguments
{
    // --wait belongs to the CLIENT: it is how long the calling process keeps re-asking while the answer
    // is "nothing matched yet". So it is taken off here, before the rest is sent, and the running app
    // never sees it. That also keeps the app's message loop from ever being parked on a wait.
    public static Result<(IReadOnlyList<string> Forwarded, TimeSpan Wait)> SplitWait(IReadOnlyList<string> args) =>
        args.ToList().IndexOf("--wait") is var at && at < 0
            ? (args, TimeSpan.Zero)
            : at + 1 < args.Count
              && double.TryParse(args[at + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
              && seconds >= 0
                ? (args.Where((_, i) => i != at && i != at + 1).ToList(), TimeSpan.FromSeconds(seconds))
                : Result.Failure<(IReadOnlyList<string>, TimeSpan)>("--wait takes a number of seconds, e.g. --wait 15");

    // `move <workspace> [--create] [--hwnd <handle>]... [--title <text>]...`
    public static Result<MoveRequest> ParseMove(IReadOnlyList<string> args) =>
        args.Count == 0 || args[0].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[0])
            ? Result.Failure<MoveRequest>("move needs a workspace name first, e.g. move wt-login --create --title wt-login")
            : Options(new MoveRequest(args[0].Trim(), false, [], []), args.Skip(1).ToArray())
                .Ensure(request => !request.NamesNoWindow, "move needs at least one --hwnd or --title to say which windows");

    // A list pattern per option, recursing on what is left. An option missing its value falls through
    // to the last arm and is reported by name.
    static Result<MoveRequest> Options(MoveRequest so, string[] rest) => rest switch
    {
        [] => so,
        ["--create", .. var tail] => Options(so with { Create = true }, tail),
        ["--title", var text, .. var tail] when !string.IsNullOrWhiteSpace(text) =>
            Options(so with { Titles = [.. so.Titles, text] }, tail),
        ["--hwnd", var raw, .. var tail] =>
            Handle(raw).Bind(handle => Options(so with { Handles = [.. so.Handles, handle] }, tail)),
        [var unknown, ..] => Result.Failure<MoveRequest>($"move does not understand '{unknown}' here."),
    };

    // Hex with 0x, which is how `windows` prints them and how Spy++ shows them, or plain decimal, which
    // is what PowerShell's MainWindowHandle gives.
    public static Result<WindowHandle> Handle(string raw) =>
        (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(raw[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            : long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        && value > 0
            ? new WindowHandle((nint)value)
            : Result.Failure<WindowHandle>($"'{raw}' is not a window handle (expected 0x1A2B or a decimal number).");
}
