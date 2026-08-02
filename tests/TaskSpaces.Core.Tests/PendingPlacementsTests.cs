using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Rehydration;

namespace TaskSpaces.Core.Tests;

public class PendingPlacementsTests
{
    static readonly Guid Work = Guid.NewGuid();
    static readonly DateTimeOffset T0 = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    static WindowInfo Window(int pid = 500, string? path = @"C:\app.exe") =>
        new(new WindowHandle(0x10), pid, "app", path, "App", null);

    [Fact]
    public void Matches_by_exact_pid_and_consumes_the_entry()
    {
        var pending = PendingPlacements.Empty.Add(500, @"C:\app.exe", Work, T0);
        var (remaining, hit) = pending.Match(Window(pid: 500), T0.AddSeconds(5));
        Assert.Equal(Work, hit.Value);
        Assert.True(remaining.Match(Window(pid: 500), T0.AddSeconds(6)).WorkspaceId.HasNoValue); // consumed
    }

    [Fact]
    public void Falls_back_to_process_path_when_pid_differs()
    {
        // Browsers hand the window to an already-running process — launched pid never appears.
        var pending = PendingPlacements.Empty.Add(500, @"C:\app.exe", Work, T0);
        Assert.Equal(Work, pending.Match(Window(pid: 999), T0.AddSeconds(5)).WorkspaceId.Value);
    }

    [Fact]
    public void Expired_entries_never_match()
    {
        var pending = PendingPlacements.Empty.Add(500, @"C:\app.exe", Work, T0);
        Assert.True(pending.Match(Window(pid: 500), T0.Add(PendingPlacements.Ttl).AddSeconds(1)).WorkspaceId.HasNoValue);
    }

    [Fact]
    public void Unrelated_window_matches_nothing()
    {
        var pending = PendingPlacements.Empty.Add(500, @"C:\app.exe", Work, T0);
        Assert.True(pending.Match(Window(pid: 999, path: @"C:\other.exe"), T0.AddSeconds(5)).WorkspaceId.HasNoValue);
    }

    [Fact]
    public void Two_pendings_same_exe_different_args_route_by_args()
    {
        var personal = Guid.NewGuid();
        var pending = PendingPlacements.Empty
            .Add(500, @"C:\rider\rider64.exe", Work, T0, "\"C:\\rider\\rider64.exe\" X.sln")
            .Add(501, @"C:\rider\rider64.exe", personal, T0, "\"C:\\rider\\rider64.exe\" Y.sln");

        // Window arrives with a pid we never launched (IDE splash handed off), but its
        // command line identifies which pending launch it belongs to.
        var window = new WindowInfo(new WindowHandle(0x10), 999, "rider64", @"C:\rider\rider64.exe", "Y", "\"C:\\rider\\rider64.exe\" Y.sln");
        Assert.Equal(personal, pending.Match(window, T0.AddSeconds(5)).WorkspaceId.Value);
    }

    [Fact]
    public void Falls_back_to_bare_path_when_identity_tier_genuinely_fails()
    {
        // Some browsers hand the window to an existing process AND rewrite their args
        // (e.g. --restore-session gets replaced) — neither pid NOR content identity
        // survive, so the only tier left that can still match is the bare exe path.
        var pending = PendingPlacements.Empty.Add(500, @"C:\rider\rider64.exe", Work, T0, "\"C:\\rider\\rider64.exe\" X.sln");
        var window = new WindowInfo(new WindowHandle(0x10), 999, "rider64", @"C:\rider\rider64.exe", "X", "\"C:\\rider\\rider64.exe\" X-rewritten-args.sln");
        Assert.Equal(Work, pending.Match(window, T0.AddSeconds(5)).WorkspaceId.Value);
    }
}
