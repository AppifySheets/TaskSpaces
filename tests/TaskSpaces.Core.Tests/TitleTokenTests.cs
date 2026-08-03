using TaskSpaces.Core.Rules;

namespace TaskSpaces.Core.Tests;

// Real titles from Petre's machine wherever possible — the heuristic is only worth anything
// if it survives the actual formats, not invented ones.
public class TitleTokenTests
{
    // ---- VS Code family: "file - Container - App Name" -----------------------------

    // THE case that motivated all of this: three argless VS Code windows share one identity,
    // so the folder in the title is the only thing that can tell them apart.
    [Fact]
    public void VS_Code_yields_the_folder_not_the_file_or_the_app_name() =>
        Assert.Equal("Corne-Config", TitleToken.For("Code", "index.ts - Corne-Config - Visual Studio Code").Value);

    // Petre's exact example: "if a vscode window has a title 'filename - TaskSpaces' you
    // should pay attention to the TaskSpaces".
    [Fact]
    public void VS_Code_without_a_trailing_app_name_still_yields_the_container() =>
        Assert.Equal("TaskSpaces", TitleToken.For("Code", "filename - TaskSpaces").Value);

    // A hyphenated folder must survive: splitting on a bare '-' instead of " - " would shred
    // "Corne-Config" into "Corne" and "Config".
    [Fact]
    public void A_hyphenated_container_is_not_split_apart() =>
        Assert.Equal("my-cool-repo", TitleToken.For("Code", "main.ts - my-cool-repo - Visual Studio Code").Value);

    // "so i open vscode, then i load a folder in it, that should take it to the correct
    // workspace" — the FIRST half of that flow must yield nothing, or we would learn garbage
    // from a window that has loaded no folder at all.
    [Fact]
    public void A_freshly_opened_VS_Code_has_no_container() =>
        Assert.False(TitleToken.For("Code", "Visual Studio Code").HasValue);

    // An unsaved buffer is a FILE name, not a project. Learning it would pollute.
    [Fact]
    public void An_unsaved_file_with_no_folder_open_has_no_container() =>
        Assert.False(TitleToken.For("Code", "Untitled-1 - Visual Studio Code").HasValue);

    [Fact]
    public void Visual_Studio_yields_the_solution() =>
        Assert.Equal("TaskSpaces", TitleToken.For("devenv", "Program.cs - TaskSpaces - Microsoft Visual Studio").Value);

    [Theory]
    [InlineData("Cursor")]
    [InlineData("VSCodium")]
    [InlineData("Windsurf")]
    public void VS_Code_forks_use_the_same_shape(string process) =>
        Assert.Equal("TaskSpaces", TitleToken.For(process, "main.cs - TaskSpaces - Visual Studio Code").Value);

    // ---- JetBrains: "Container – file", EN DASH, container FIRST -------------------

    // The container is at the OPPOSITE end from VS Code, which is why one "take the last
    // segment" rule cannot serve both families.
    [Fact]
    public void JetBrains_yields_the_project_from_the_FRONT() =>
        Assert.Equal("TaskSpaces", TitleToken.For("rider64", "TaskSpaces – Program.cs").Value);

    [Fact]
    public void JetBrains_en_dash_is_a_delimiter_just_like_a_hyphen() =>
        Assert.Equal("my-project", TitleToken.For("idea64", "my-project – Main.java – feature/branch").Value);

    [Fact]
    public void A_bare_JetBrains_window_has_no_container() =>
        Assert.False(TitleToken.For("rider64", "Rider").HasValue);

    // ---- Bracketed: Remote Desktop Manager ----------------------------------------

    // Petre's real RDM title. Note the session itself contains " - ", which is exactly why
    // the bracket rule must run BEFORE any splitting.
    [Fact]
    public void Remote_Desktop_Manager_yields_the_bracketed_session_including_its_own_dash() =>
        Assert.Equal("_Richard - fhd", TitleToken.For("RemoteDesktopManager", "Remote Desktop Manager [_Richard - fhd]").Value);

    [Fact]
    public void Remote_Desktop_Manager_with_no_session_has_no_container() =>
        Assert.False(TitleToken.For("RemoteDesktopManager", "Remote Desktop Manager").HasValue);

    // ---- The allowlist ------------------------------------------------------------

    // Petre: "not a good idea to apply to browsers, because browser tabs are a bad way to
    // identify which tab to assign it to."
    [Theory]
    [InlineData("chrome", "Inbox (12) - petre@gepha.com - Gmail - Google Chrome")]
    [InlineData("msedge", "TaskSpaces design - Notion - Microsoft Edge")]
    [InlineData("firefox", "Some Page - Mozilla Firefox")]
    public void Browsers_are_never_tokenised(string process, string title) =>
        Assert.False(TitleToken.For(process, title).HasValue);

    // Petre: "it doesn't have to apply to single instance apps, like beeper, whatsapp, etc."
    // One window means one identity means one workspace, which placement memory handles.
    [Theory]
    [InlineData("Beeper", "Beeper | Maiko Sagharadze")]
    [InlineData("WhatsApp", "WhatsApp")]
    [InlineData("ms-teams", "Meeting in Application Support | Microsoft Teams")]
    public void Single_window_apps_are_never_tokenised(string process, string title) =>
        Assert.False(TitleToken.For(process, title).HasValue);

    // An unknown app keeps today's behaviour rather than being guessed at — the reason this
    // is an allowlist and not a blocklist.
    [Fact]
    public void An_unknown_app_is_never_tokenised() =>
        Assert.False(TitleToken.For("some-random-tool", "thing - other thing - Some App").HasValue);

    [Fact]
    public void An_empty_title_yields_nothing() =>
        Assert.False(TitleToken.For("Code", "").HasValue);

    // Process names arrive from WindowInfo.ProcessName with inconsistent casing.
    [Fact]
    public void Process_names_match_case_insensitively() =>
        Assert.Equal("TaskSpaces", TitleToken.For("CODE", "x.cs - TaskSpaces - Visual Studio Code").Value);

    // ---- Matching a learned token -------------------------------------------------

    [Fact]
    public void A_learned_token_matches_the_window_it_came_from() =>
        Assert.True(TitleToken.Matches("Code", "index.ts - Corne-Config - Visual Studio Code", "Corne-Config"));

    [Fact]
    public void A_learned_token_does_not_match_a_different_container() =>
        Assert.False(TitleToken.Matches("Code", "index.ts - TaskSpaces - Visual Studio Code", "Corne-Config"));

    // Containment, not equality: apps decorate a container name (branch suffixes,
    // "[Administrator]") and a learned token should survive that.
    [Fact]
    public void A_learned_token_matches_a_decorated_container() =>
        Assert.True(TitleToken.Matches("Code", "x.ts - TaskSpaces [WSL] - Visual Studio Code", "TaskSpaces"));

    // A token must never match via the FILE name — that would place a window because of
    // whatever document happened to be open.
    [Fact]
    public void A_learned_token_does_not_match_against_the_file_name() =>
        Assert.False(TitleToken.Matches("Code", "TaskSpaces.cs - Corne-Config - Visual Studio Code", "TaskSpaces"));
}
