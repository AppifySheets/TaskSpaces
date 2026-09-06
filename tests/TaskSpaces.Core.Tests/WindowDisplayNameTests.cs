using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Tests;

// The other half of letting a titleless window into the bar (Petre: "i can't see Buzz in HRIS
// workspace"). Buzz's window carries no title at all, so every place that shows one -- the hover
// card's heading, an icon's accessible name, the info line -- had nothing to show for it.
//
// A window always has a process name, so that is the fallback. Not a placeholder like "(untitled)",
// which would be the same word on every such window and tell the reader nothing: "buzz-desktop" is
// what he would call it anyway.
public class WindowDisplayNameTests
{
    static WindowInfo Window(string title, string process = "buzz-desktop") =>
        new(new WindowHandle(1), 42, process, $@"C:\{process}.exe", title, null);

    [Fact]
    public void A_titled_window_shows_its_title() =>
        Assert.Equal("Inbox - Outlook", Window("Inbox - Outlook", "OUTLOOK").DisplayName);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_window_with_no_title_shows_its_app(string title) =>
        Assert.Equal("buzz-desktop", Window(title).DisplayName);

    // Whitespace is trimmed rather than shown, because a heading indented by whatever an app happened
    // to pad its title with reads as a rendering fault.
    [Fact]
    public void A_padded_title_is_trimmed() =>
        Assert.Equal("Buzz", Window("  Buzz  ").DisplayName);
}
