using System.Windows.Controls;
using System.Windows.Documents;
using TaskSpaces.App;

namespace TaskSpaces.Windows.Tests;

// Petre, minutes after the hover dwell shipped: "TaskSpaces hit an unexpected error and must close:
// 'System.Windows.Documents.Run' is not a Visual or Visual3D", and then where: "it happened when viewing
// a preview of the image in a workspace."
//
// The hover card's two timers both ask what is under the pointer, and Mouse.DirectlyOver answers with
// whatever that is -- including a Run, because the bar's footer line and its row labels are built from
// Inlines rather than plain text. VisualTreeHelper.GetParent THROWS for anything that is not a Visual, the
// throw reached the dispatcher's unhandled handler, and the app turns that into a fatal dialog. So resting
// a pointer on text closed a program holding renamed titles.
//
// Cheap to pin, once the walk is reachable: hand it the exact element type that crashed.
public class HoverHitTestTests
{
    // The crash, exactly. A Run belongs to no icon, so the honest answer is null -- and getting there must
    // not throw on the way.
    [Fact]
    public void A_run_under_the_pointer_is_not_an_icon_and_does_not_throw() => StaThread.Run(() =>
    {
        var run = new Run("hover an icon · drag icons between rows");
        var label = new TextBlock();
        label.Inlines.Add(run);

        Assert.Null(FloatingBar.IconAncestorOf(run));
    });

    // ...and the walk still has to WORK, or the fix would be "never find anything", which no test of the
    // crash alone would catch. A Run inside a TextBlock inside an icon has to lead back to that icon:
    // the route crosses from the content tree into the visual tree, which is the whole subtlety.
    [Fact]
    public void A_run_inside_an_icon_still_finds_it() => StaThread.Run(() =>
    {
        var run = new Run("x");
        var label = new TextBlock();
        label.Inlines.Add(run);
        var icon = new Button { Tag = "icon", Content = label };
        // A layout pass, so the button's visual tree actually contains the TextBlock.
        icon.Measure(new System.Windows.Size(100, 100));
        icon.Arrange(new System.Windows.Rect(0, 0, 100, 100));

        Assert.Same(icon, FloatingBar.IconAncestorOf(run));
    });

    [Fact]
    public void Nothing_under_the_pointer_is_not_an_icon() => StaThread.Run(() =>
        Assert.Null(FloatingBar.IconAncestorOf(null)));

    // A button that is not one of ours is not an icon either: the Tag is what marks an icon, and the bar
    // has other buttons on it.
    [Fact]
    public void A_button_that_is_not_an_icon_is_not_mistaken_for_one() => StaThread.Run(() =>
        Assert.Null(FloatingBar.IconAncestorOf(new Button { Content = "Back" })));
}
