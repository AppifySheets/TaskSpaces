using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Renaming;

namespace TaskSpaces.Core.Tests;

public class RenameLedgerTests
{
    static readonly WindowHandle H = new(0x1234);

    [Fact]
    public void Apply_records_original_title_and_applied_name()
    {
        var ledger = RenameLedger.Empty.Apply(H, "myserver - Remote Desktop Connection", "RDP");
        Assert.Equal("RDP", ledger.AppliedName(H).Value);
        Assert.Equal("myserver - Remote Desktop Connection", ledger.OriginalTitle(H).Value);
    }

    [Fact]
    public void Second_apply_keeps_the_first_original_title()
    {
        // User renames "long title" -> "RDP" -> "Server". Restore must return to
        // "long title", not to the intermediate "RDP".
        var ledger = RenameLedger.Empty.Apply(H, "long title", "RDP").Apply(H, "RDP", "Server");
        Assert.Equal("Server", ledger.AppliedName(H).Value);
        Assert.Equal("long title", ledger.OriginalTitle(H).Value);
    }

    [Fact]
    public void Remove_forgets_the_window()
    {
        var ledger = RenameLedger.Empty.Apply(H, "title", "RDP").Remove(H);
        Assert.True(ledger.AppliedName(H).HasNoValue);
        Assert.Empty(ledger.Handles);
    }

    [Fact]
    public void NeedsReapply_when_app_rewrote_its_own_title()
    {
        // Browser navigated -> Windows fired NAMECHANGE with the browser's new title.
        var ledger = RenameLedger.Empty.Apply(H, "Old Page - Chrome", "Amy related");
        Assert.True(ledger.NeedsReapply(H, "New Page - Chrome"));
    }

    [Fact]
    public void No_reapply_when_observed_title_is_our_own_short_name()
    {
        // Our WM_SETTEXT also fires NAMECHANGE -- this check breaks the infinite loop.
        var ledger = RenameLedger.Empty.Apply(H, "Old Page - Chrome", "Amy related");
        Assert.False(ledger.NeedsReapply(H, "Amy related"));
    }

    [Fact]
    public void Untracked_window_never_needs_reapply() =>
        Assert.False(RenameLedger.Empty.NeedsReapply(H, "anything"));
}
