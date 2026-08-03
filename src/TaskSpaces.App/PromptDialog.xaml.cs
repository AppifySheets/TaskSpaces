using System.Windows;
using CSharpFunctionalExtensions;

namespace TaskSpaces.App;

// The one text-input dialog the app needs (rename a window; arguments for Add app…).
public partial class PromptDialog : Window
{
    public PromptDialog() => InitializeComponent();

    // owner (Task 12): needed by the floating bar, which is Topmost — an unowned dialog can
    // open BEHIND it, leaving the rename prompt invisible under the very surface that
    // launched it. An owned window always renders above its owner, topmost or not. Optional
    // so the existing WindowGroupsView call sites (hosted in ordinary windows, and already
    // wrapped in the host's runChildDialog guard) are unchanged.
    public static Maybe<string> Ask(string title, string prompt, string initial = "", Window? owner = null)
    {
        var dialog = new PromptDialog { Title = title };
        if (owner is not null) dialog.Owner = owner;
        dialog.PromptText.Text = prompt;
        dialog.Input.Text = initial;
        dialog.Input.SelectAll();
        dialog.Input.Focus();
        return dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Input.Text)
            ? dialog.Input.Text.Trim()
            : Maybe<string>.None;
    }

    void OnOk(object s, RoutedEventArgs e) => DialogResult = true;
}
