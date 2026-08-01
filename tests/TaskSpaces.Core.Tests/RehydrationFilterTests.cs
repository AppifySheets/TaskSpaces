using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rehydration;

namespace TaskSpaces.Core.Tests;

// Finding 4 (reviewer, Important): pins the "don't re-offer apps that are still running"
// rule as a pure, fast unit test independent of WPF/RehydratePrompt.
public class RehydrationFilterTests
{
    [Fact]
    public void Drops_entries_whose_process_path_matches_a_live_window()
    {
        var inventory = new List<InventoryEntry>
        {
            new(@"C:\Program Files\Discord\Discord.exe", null, "Discord"),
            new(@"C:\Program Files\Slack\slack.exe", null, "Slack"),
        };
        var known = new List<WindowInfo>
        {
            new(new WindowHandle(1), 1, "Discord", @"C:\Program Files\Discord\Discord.exe", "Discord", null),
        };

        var result = RehydrationFilter.Surviving(inventory, known);

        Assert.Single(result);
        Assert.Equal(@"C:\Program Files\Slack\slack.exe", result[0].ProcessPath);
    }

    [Fact]
    public void Match_is_case_insensitive_windows_paths_are_not_case_sensitive()
    {
        var inventory = new List<InventoryEntry> { new(@"C:\Program Files\Discord\DISCORD.EXE", null, "Discord") };
        var known = new List<WindowInfo>
        {
            new(new WindowHandle(1), 1, "Discord", @"c:\program files\discord\discord.exe", "Discord", null),
        };

        Assert.Empty(RehydrationFilter.Surviving(inventory, known));
    }

    [Fact]
    public void Entries_with_no_live_match_all_survive()
    {
        var inventory = new List<InventoryEntry> { new(@"C:\Program Files\Discord\Discord.exe", null, "Discord") };

        var result = RehydrationFilter.Surviving(inventory, []);

        Assert.Single(result);
    }

    [Fact]
    public void Windows_with_no_process_path_never_match_and_never_throw()
    {
        var inventory = new List<InventoryEntry> { new(@"C:\Program Files\Discord\Discord.exe", null, "Discord") };
        var known = new List<WindowInfo> { new(new WindowHandle(1), 1, "elevated", null, "Elevated App", null) };

        Assert.Single(RehydrationFilter.Surviving(inventory, known));
    }
}
