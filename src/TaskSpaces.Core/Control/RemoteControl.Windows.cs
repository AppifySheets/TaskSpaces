using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Control;

// Windows: listing, moving, and the window icon menu. See RemoteControl.cs for the shared parts.
public sealed partial class RemoteControl
{
    ControlReply Windows() =>
        Success(manager.KnownWindows.Select(DescribeWindow));

    object DescribeWindow(WindowInfo w) => new
    {
        hwnd = Hex(w.Handle),
        title = w.Title,
        originalTitle = manager.OriginalTitle(w.Handle).GetValueOrDefault(),
        process = w.ProcessName,
        pid = w.ProcessId,
        workspace = manager.WorkspaceOf(w.Handle).Map(ws => ws.Name).GetValueOrDefault(),
    };

    // All-or-nothing on the SELECTORS: if any --title or --hwnd finds no window, nothing moves. A partial
    // move would leave the caller to work out what happened from the JSON, and the common cause of a
    // miss is a window that is still opening, where the right answer is "not yet" (exit 3, which --wait
    // retries) rather than half the job.
    //
    // The workspace is created only once there is something to put in it, so a wrong title passed with
    // --create does not leave an empty workspace on the bar.
    //
    // Each window goes through AssignWindow, the door a drag on the bar uses, so a remote move unpins,
    // teaches placement memory, and takes you along when it is the window you are in.
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

        return Ensure(request.Workspace, request.Create, request.Placement).Match(
            found =>
            {
                var outcomes = chosen
                    .Select(w => (Window: w, Result: manager.AssignWindow(w.Handle, found.Workspace.Id)))
                    .ToList();
                var failed = outcomes.Where(o => o.Result.IsFailure).ToList();
                return new ControlReply(failed.Count == 0 ? ControlReply.Ok : ControlReply.Failed, Serialize(new
                {
                    created = found.Created,
                    workspace = Describe(found.Workspace.Id),
                    moved = outcomes.Where(o => o.Result.IsSuccess).Select(o => new { hwnd = Hex(o.Window.Handle), title = o.Window.Title }),
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

    // --- the window icon menu -------------------------------------------------------------------

    // Every one of these replies with the window as it reads afterwards, which for a rename is the
    // quickest way to see whether the new name took.
    ControlReply OnWindow(string hwnd, Func<WindowInfo, Result> act) =>
        WindowAt(hwnd).Match(
            w => Reply(act(w), () => manager.KnownWindows.TryFirst(k => k.Handle == w.Handle)
                .Map(DescribeWindow).GetValueOrDefault(new { hwnd = Hex(w.Handle), closed = true })),
            ControlReply.Fail);

    ControlReply RenameWindow(string hwnd, string name) => OnWindow(hwnd, w => manager.RenameWindow(w.Handle, name));

    ControlReply RenameApp(string hwnd, string name) => OnWindow(hwnd, w => manager.RenameApp(w.Handle, name));

    ControlReply RestoreTitle(string hwnd) => OnWindow(hwnd, w => manager.RestoreTitle(w.Handle));

    ControlReply Pin(string hwnd, bool pin) =>
        OnWindow(hwnd, w => pin ? manager.PinWindow(w.Handle) : manager.UnpinWindow(w.Handle));

    ControlReply NameByFolder(string hwnd, string onOff) =>
        onOff.ToLowerInvariant() switch
        {
            "on" or "true" or "yes" => OnWindow(hwnd, w => manager.NameWindowsByFolder(w.Handle, true)),
            "off" or "false" or "no" => OnWindow(hwnd, w => manager.NameWindowsByFolder(w.Handle, false)),
            _ => ControlReply.Fail(ControlUsage.LineFor("name-by-folder")),
        };

    // Names no window, so there is no window to describe: the reply echoes the rule that was made.
    ControlReply RenamePattern(string pattern, string name) =>
        Reply(manager.RenameByPattern(pattern, name), () => new { pattern, name });
}
