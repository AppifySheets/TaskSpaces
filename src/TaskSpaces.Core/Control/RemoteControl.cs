using System.Text.Json;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Control;

// Commands from OUTSIDE the app: another process asking the running TaskSpaces to list, create and move.
//
// Petre: "i want you to add ability to create workspaces and move windows into them programatically, so
// that when i have a claude session start working in a new worktree, i can tell it to move the windows
// to a new workspace which it creates."
//
// Everything here goes through the same WorkspaceManager doors a drag on the bar does (AddWorkspace,
// AssignWindow), so a remote move gets the same unpinning, placement memory and "the window you are in
// takes you with it" as a hand move, and the bar redraws from the same StateChanged. Nothing here keeps
// state of its own.
//
// Runs on the app's UI thread, always: the manager is not thread-safe and its COM calls are STA. The
// pipe server is responsible for getting here through the dispatcher (see ControlPipeServer).
//
// Output is JSON for every success, because the expected caller is an agent that reads it back, and
// a plain sentence for every failure, because the expected reader of a failure is whoever is debugging.
public sealed class RemoteControl(WorkspaceManager manager)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public const string Usage =
        """
        usage: TaskSpaces ctl <command>

          workspaces                         list workspaces as JSON
          windows                            list windows as JSON: hwnd, title, process, workspace
          create <name>                      create a workspace (already existing is not an error)
          move <workspace> [options]         move windows to a workspace
              --title <text>                 every window whose title contains <text> (case-insensitive,
                                             also searched in the title from before TaskSpaces renamed it)
              --hwnd <handle>                one window, as 0x1A2B or decimal
              --create                       create the workspace if it does not exist
              --wait <seconds>               keep retrying while a selector matches nothing yet

          Every --title and --hwnd must find a window, or nothing is moved and the exit code is 3.

        exit codes: 0 done, 1 refused, 2 TaskSpaces is not running, 3 no window matched
        """;

    public ControlReply Execute(IReadOnlyList<string> args) =>
        args.Count == 0
            ? ControlReply.Fail(Usage)
            : args[0].ToLowerInvariant() switch
            {
                "workspaces" => Workspaces(),
                "windows" => Windows(),
                "create" when args.Count == 2 && !string.IsNullOrWhiteSpace(args[1]) => Create(args[1].Trim()),
                "create" => ControlReply.Fail("create takes exactly one workspace name; quote it if it has spaces."),
                "move" => ControlArguments.ParseMove(args.Skip(1).ToList()).Match(Move, ControlReply.Fail),
                "help" or "--help" or "-h" or "/?" => new ControlReply(ControlReply.Ok, Usage),
                var unknown => ControlReply.Fail($"unknown command '{unknown}'.\n\n{Usage}"),
            };

    ControlReply Workspaces() =>
        Success(manager.State.Workspaces.Select(w => new
        {
            name = w.Name,
            id = w.Id,
            current = manager.CurrentWorkspaceId == w.Id,
        }));

    ControlReply Windows() =>
        Success(manager.KnownWindows.Select(w => new
        {
            hwnd = Hex(w.Handle),
            title = w.Title,
            originalTitle = manager.OriginalTitle(w.Handle).GetValueOrDefault(),
            process = w.ProcessName,
            pid = w.ProcessId,
            workspace = manager.WorkspaceOf(w.Handle).Map(ws => ws.Name).GetValueOrDefault(),
        }));

    // Idempotent on purpose. An agent that runs its setup twice, or a worktree whose name happens to
    // match a workspace Petre already has, both mean "make sure it exists", and failing them would only
    // teach the caller to ignore errors from this command.
    ControlReply Create(string name) =>
        Ensure(name, create: true).Match(
            found => Success(new { name = found.Workspace.Name, id = found.Workspace.Id, created = found.Created }),
            ControlReply.Fail);

    // All-or-nothing on the SELECTORS: if any --title or --hwnd finds no window, nothing moves. A partial
    // move would leave the caller to work out what happened from the JSON, and the common cause of a
    // miss is a window that is still opening, where the right answer is "not yet" (exit 3, which --wait
    // retries) rather than half the job.
    //
    // The workspace is created only once there is something to put in it, so a wrong title passed with
    // --create does not leave an empty workspace on the bar.
    ControlReply Move(MoveRequest request)
    {
        var windows = manager.KnownWindows;
        var unmatched = request.Handles.Where(h => windows.All(w => w.Handle != h)).Select(Hex)
            .Concat(request.Titles.Where(t => !windows.Any(w => TitleContains(w, t))).Select(t => $"title:{t}"))
            .ToList();
        if (unmatched.Count > 0)
            return new ControlReply(ControlReply.NothingMatched,
                $"No window matched {string.Join(", ", unmatched)}. Nothing was moved. `windows` lists what is open.");

        var chosen = windows
            .Where(w => request.Handles.Contains(w.Handle) || request.Titles.Any(t => TitleContains(w, t)))
            .ToList();

        return Ensure(request.Workspace, request.Create).Match(
            found =>
            {
                var outcomes = chosen
                    .Select(w => (Window: w, Result: manager.AssignWindow(w.Handle, found.Workspace.Id)))
                    .ToList();
                var failed = outcomes.Where(o => o.Result.IsFailure).ToList();
                return new ControlReply(failed.Count == 0 ? ControlReply.Ok : ControlReply.Failed, Serialize(new
                {
                    workspace = found.Workspace.Name,
                    created = found.Created,
                    moved = outcomes.Where(o => o.Result.IsSuccess).Select(o => Summary(o.Window)),
                    failed = failed.Select(o => new { hwnd = Hex(o.Window.Handle), title = o.Window.Title, error = o.Result.Error }),
                }));
            },
            ControlReply.Fail);
    }

    // Matched case-insensitively against what the window says now AND what it said before a rename
    // rule shortened it: the folder name an agent knows is usually only in the latter.
    bool TitleContains(WindowInfo window, string text) =>
        window.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
        || manager.OriginalTitle(window.Handle).Map(t => t.Contains(text, StringComparison.OrdinalIgnoreCase)).GetValueOrDefault(false);

    // Names are compared the way the manager compares them for collisions (trimmed, ignoring case), so
    // "find" and "already exists" can never disagree about whether a workspace is there.
    Result<(Workspace Workspace, bool Created)> Ensure(string name, bool create) =>
        manager.State.Workspaces.TryFirst(w => w.Name.Trim().Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) is { HasValue: true } existing
            ? (existing.Value, false)
            : create
                ? manager.AddWorkspace(name).Map(w => (w, true))
                : Result.Failure<(Workspace, bool)>($"There is no workspace named '{name}'. Pass --create to make it.");

    static object Summary(WindowInfo window) => new { hwnd = Hex(window.Handle), title = window.Title };

    static string Hex(WindowHandle handle) => $"0x{(long)handle.Value:X}";

    static ControlReply Success(object value) => new(ControlReply.Ok, Serialize(value));

    static string Serialize(object value) => JsonSerializer.Serialize(value, Json);
}
