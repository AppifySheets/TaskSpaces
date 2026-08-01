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
}
