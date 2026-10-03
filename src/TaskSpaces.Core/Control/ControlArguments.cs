using System.Globalization;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Control;

// Where a NEW workspace goes. Petre: "when adding a new workspace, we should say in which group and, if
// neither, at what position". At most one of these per request; none means the end of the bar, which is
// where the bar's own "add" puts one.
public abstract record Placement
{
    public sealed record AtEnd : Placement;

    // The bottom of that group (WorkspaceManager.AddWorkspaceToGroup).
    public sealed record InGroup(string Group) : Placement;

    // A 1-based top-level row of the bar, a whole group counting as one row (AddWorkspaceAtRow).
    public sealed record AtRow(int Row) : Placement;

    // Right before or after an existing workspace, at THAT workspace's depth: beside a group member it
    // joins the group. The same thing as the row menu's "Insert before…" and "Insert after…".
    public sealed record Beside(string Workspace, bool After) : Placement;

    public static Placement End { get; } = new AtEnd();
}

// The `create` command, parsed.
public sealed record CreateRequest(string Name, Placement Placement);

// The `move` command, parsed. Handles and titles are the two ways to name a window, and a request may
// mix them; every one of them has to find a window or nothing moves (see RemoteControl.Move).
public sealed record MoveRequest(string Workspace, bool Create, Placement Placement, IReadOnlyList<WindowHandle> Handles, IReadOnlyList<string> Titles, bool NoFollow = false)
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

    // Everything `create` and `move` accept after their first argument, gathered before it is judged,
    // so each refusal can name the actual conflict rather than the first option that happened to be odd.
    sealed record Options(bool Create, IReadOnlyList<Placement> Placements, IReadOnlyList<WindowHandle> Handles, IReadOnlyList<string> Titles, bool NoFollow = false)
    {
        public static Options None { get; } = new(false, [], [], []);
    }

    // `create <name> [--group <g> | --position <n> | --before <ws> | --after <ws>]`
    public static Result<CreateRequest> ParseCreate(IReadOnlyList<string> args) =>
        FirstArgument(args, "create needs a workspace name, e.g. create wt-login --group EC")
            .Bind(name => Gather(Options.None, args.Skip(1).ToArray())
                .Ensure(o => !o.Create && !o.NoFollow && o.Handles.Count == 0 && o.Titles.Count == 0,
                    "create only takes a placement (--group, --position, --before or --after); use move to move windows.")
                .Bind(OnePlacement)
                .Map(placement => new CreateRequest(name, placement)));

    // `move <workspace> [--create [placement]] [--hwnd <handle>]... [--title <text>]...`
    public static Result<MoveRequest> ParseMove(IReadOnlyList<string> args) =>
        FirstArgument(args, "move needs a workspace name first, e.g. move wt-login --create --title wt-login")
            .Bind(name => Gather(Options.None, args.Skip(1).ToArray())
                .Ensure(o => o.Handles.Count > 0 || o.Titles.Count > 0, "move needs at least one --hwnd or --title to say which windows")
                // Placement only means something for a workspace being made. Accepting it for one that
                // exists would leave the caller believing the row had moved; `reorder` does that.
                .Ensure(o => o.Create || o.Placements.Count == 0,
                    "--group, --position, --before and --after only apply with --create. To move an existing workspace use reorder or group-join.")
                .Bind(o => OnePlacement(o).Map(placement => new MoveRequest(name, o.Create, placement, o.Handles, o.Titles, o.NoFollow))));

    static Result<string> FirstArgument(IReadOnlyList<string> args, string usage) =>
        args.Count == 0 || string.IsNullOrWhiteSpace(args[0]) || args[0].StartsWith("--", StringComparison.Ordinal)
            ? Result.Failure<string>(usage)
            : args[0].Trim();

    static Result<Placement> OnePlacement(Options options) =>
        options.Placements.Count switch
        {
            0 => Placement.End,
            1 => options.Placements[0],
            // Petre's rule is a group OR a position. Two at once would mean a position inside a group,
            // which none of these options says.
            _ => Result.Failure<Placement>("Give at most one of --group, --position, --before and --after."),
        };

    // A list pattern per option, recursing on what is left. An option missing its value falls through
    // to the last arm and is reported by name.
    static Result<Options> Gather(Options so, string[] rest) => rest switch
    {
        [] => so,
        ["--create", .. var tail] => Gather(so with { Create = true }, tail),
        // Leave the caller where they are even when the window being moved is the one they are in. See
        // WorkspaceManager.AssignWindow for the rule this switches off.
        ["--no-follow", .. var tail] => Gather(so with { NoFollow = true }, tail),
        ["--title", var text, .. var tail] when Named(text) => Gather(so with { Titles = [.. so.Titles, text] }, tail),
        ["--hwnd", var raw, .. var tail] => Handle(raw).Bind(handle => Gather(so with { Handles = [.. so.Handles, handle] }, tail)),
        ["--group", var group, .. var tail] when Named(group) => Gather(With(so, new Placement.InGroup(group.Trim())), tail),
        ["--before", var target, .. var tail] when Named(target) => Gather(With(so, new Placement.Beside(target.Trim(), After: false)), tail),
        ["--after", var target, .. var tail] when Named(target) => Gather(With(so, new Placement.Beside(target.Trim(), After: true)), tail),
        ["--position", var raw, .. var tail] => Position(raw).Bind(row => Gather(With(so, new Placement.AtRow(row)), tail)),
        [var unknown, ..] => Result.Failure<Options>($"'{unknown}' is not an option here, or it is missing its value."),
    };

    static Options With(Options so, Placement placement) => so with { Placements = [.. so.Placements, placement] };

    static bool Named(string value) => !string.IsNullOrWhiteSpace(value) && !value.StartsWith("--", StringComparison.Ordinal);

    // A 1-based row or position. Zero and negatives are refused rather than clamped, because they are
    // almost always an off-by-one in the caller, and clamping would hide it by doing something plausible.
    public static Result<int> Position(string raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var row) && row >= 1
            ? row
            : Result.Failure<int>($"'{raw}' is not a position. Positions count from 1, the top of the bar.");

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
