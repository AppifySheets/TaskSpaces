using System.Windows;
using CSharpFunctionalExtensions;

namespace TaskSpaces.App;

// The one text-input dialog the app needs (rename a window; arguments for Add app…).
public partial class PromptDialog : Window
{
    public PromptDialog() => InitializeComponent();

    // owner (Task 12): needed by the floating bar, which is Topmost -- an unowned dialog can
    // open BEHIND it, leaving the rename prompt invisible under the very surface that
    // launched it. An owned window always renders above its owner, topmost or not. Optional
    // so the existing WindowGroupsView call sites (hosted in ordinary windows, and already
    // wrapped in the host's runChildDialog guard) are unchanged.
    public static Maybe<string> Ask(string title, string prompt, string initial = "", Window? owner = null)
    {
        // Timed for #51 -- "the dialog shows up but it takes a few seconds for the textbox to
        // appear". MEASURED first, in isolation, before any of this was written: constructing and
        // rendering this dialog on a bare STA thread costs 362ms the first time (WPF's one-off
        // text-stack initialisation) and 76ms every time after. So the seconds are not in the
        // dialog -- they are in whatever else owns the UI thread while it is trying to render.
        //
        // Hence three marks rather than one total: the gap between `shown` and `rendered` is the
        // symptom exactly as reported (frame up, contents missing), and if that gap is where the
        // seconds are, the next question is what ran on the dispatcher during it -- which the
        // rebuild timings in the same log answer.
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var dialog = new PromptDialog { Title = title };
        if (owner is not null) dialog.Owner = owner;
        dialog.PromptText.Text = prompt;
        dialog.Input.Text = initial;
        dialog.Input.SelectAll();
        dialog.Input.Focus();

        if (ClickTrace.On)
        {
            var built = clock.Elapsed.TotalMilliseconds;
            dialog.ContentRendered += (_, _) =>
                ClickTrace.Write($"prompt \"{title}\" built={built:F0}ms rendered={clock.Elapsed.TotalMilliseconds:F0}ms");
        }

        return dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Input.Text)
            ? dialog.Input.Text.Trim()
            : Maybe<string>.None;
    }

    // Two fields, for a rename that matches on one thing and names another (#136). Petre: "i want two
    // separate boxes - one for the title wildcard, another for the new name."
    //
    // Both must be filled: this form exists to say two things, and a half-filled one has no sensible
    // reading. The first box takes focus, since it is the one that usually needs editing (the second is
    // pre-filled with a sensible name and is often right as it stands).
    public static Maybe<(string First, string Second)> AskTwo(
        string title, string prompt, string initial, string secondPrompt, string secondInitial, Window? owner = null)
    {
        var dialog = new PromptDialog { Title = title };
        if (owner is not null) dialog.Owner = owner;
        dialog.PromptText.Text = prompt;
        dialog.Input.Text = initial;
        dialog.SecondPromptText.Text = secondPrompt;
        dialog.SecondInput.Text = secondInitial;
        dialog.SecondPromptText.Visibility = Visibility.Visible;
        dialog.SecondInput.Visibility = Visibility.Visible;
        dialog.Input.SelectAll();
        dialog.Input.Focus();

        return dialog.ShowDialog() == true
               && !string.IsNullOrWhiteSpace(dialog.Input.Text)
               && !string.IsNullOrWhiteSpace(dialog.SecondInput.Text)
            ? (dialog.Input.Text.Trim(), dialog.SecondInput.Text.Trim())
            : Maybe<(string, string)>.None;
    }

    void OnOk(object s, RoutedEventArgs e) => DialogResult = true;
}
