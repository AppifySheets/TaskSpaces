using System.Reactive.Linq;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Tests;

// Petre: "can you also identify if an app has something to say, a notification, and say it on
// the icon?... let's say somebody has messaged me, or vscode is asking for my attention
// somewhere."
//
// Both are one Windows mechanism -- a flashing taskbar button -- reported through the shell
// hook. Two measured facts about that signal shape everything here, and neither is guessable:
// it REPEATS for as long as the window flashes, and there is NO notification when it stops.
public class AttentionTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();
    readonly FakeAttention attention = new();

    static WindowInfo Window(nint handle, string process) =>
        new(new WindowHandle(handle), (int)handle, process, $@"C:\{process}.exe", $"{process} window", $@"""C:\{process}.exe""");

    readonly WindowInfo chat = Window(0x1, "Chat");
    readonly WindowInfo editor = Window(0x2, "Editor");

    WorkspaceManager Started()
    {
        var desktop = desktops.Create("Main").Value;
        desktops.CurrentDesktopId = desktop.Id;
        monitor.InitialWindows.AddRange([chat, editor]);
        desktops.WindowPlacements[chat.Handle] = desktop.Id;
        desktops.WindowPlacements[editor.Handle] = desktop.Id;

        var manager = new WorkspaceManager(desktops, monitor, titles, store, attention: attention);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    static IReadOnlyList<Overview.WindowRow> RowsOf(WorkspaceManager manager) =>
        manager.WindowsByWorkspace().Value.OtherDesktops.Single().Windows;

    static Overview.WindowRow RowFor(WorkspaceManager manager, WindowInfo window) =>
        RowsOf(manager).Single(r => r.Window.Handle == window.Handle);

    [Fact]
    public void A_flashing_window_is_marked_as_wanting_attention()
    {
        var manager = Started();

        attention.Subject.OnNext(chat.Handle);

        Assert.True(RowFor(manager, chat).WantsAttention);
        Assert.False(RowFor(manager, editor).WantsAttention);
    }

    // Windows never says a flash has stopped, so the end of attention is OUR rule: you looked at
    // it, so it is dealt with. Same thing the taskbar does.
    [Fact]
    public void Looking_at_the_window_clears_it()
    {
        var manager = Started();
        attention.Subject.OnNext(chat.Handle);

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, chat));

        Assert.False(RowFor(manager, chat).WantsAttention);
    }

    // A flash arriving for the window you are ALREADY in must not mark it. Some apps flash
    // regardless of focus, and a dot on the icon you are looking at would be pointing you
    // somewhere you already are.
    [Fact]
    public void The_window_you_are_already_in_is_never_marked()
    {
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, chat));

        attention.Subject.OnNext(chat.Handle);

        Assert.False(RowFor(manager, chat).WantsAttention);
    }

    // The subtle one. A flash that lands while you are sitting IN the window still has to be
    // cleared when you next activate it -- and re-activating the window you are already in is a
    // no-op for the highlight, so the clearing cannot be tucked behind the "did the active
    // window change" guard.
    [Fact]
    public void Re_activating_the_window_you_are_in_still_clears_a_flash()
    {
        var manager = Started();
        attention.Subject.OnNext(chat.Handle);          // flashed while we were elsewhere
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, chat));
        attention.Subject.OnNext(chat.Handle);          // ...and again, ignored: we are in it now

        Assert.False(RowFor(manager, chat).WantsAttention);
    }

    // HSHELL_FLASH repeats for as long as the window flashes -- several times a second. Each
    // pulse rebuilds every open surface, and each rebuild costs a DesktopOf COM call per known
    // window, so only the first may pulse.
    [Fact]
    public void A_repeating_flash_pulses_only_once()
    {
        var manager = Started();
        var pulses = 0;
        using var subscription = manager.StateChanged.Subscribe(_ => pulses++);

        attention.Subject.OnNext(chat.Handle);
        attention.Subject.OnNext(chat.Handle);
        attention.Subject.OnNext(chat.Handle);

        Assert.Equal(1, pulses);
    }

    // A flash naming something we have no row for -- our own chrome, a shell helper window --
    // must not pulse every surface for nothing.
    [Fact]
    public void A_flash_from_a_window_we_do_not_track_is_ignored()
    {
        var manager = Started();
        var pulses = 0;
        using var subscription = manager.StateChanged.Subscribe(_ => pulses++);

        attention.Subject.OnNext(new WindowHandle(0xDEAD));

        Assert.Equal(0, pulses);
    }
}
