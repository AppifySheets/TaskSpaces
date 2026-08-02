using System.Windows;
using CSharpFunctionalExtensions;

namespace TaskSpaces.App;

// The one text-input dialog the app needs (rename a window; arguments for Add app…).
public partial class PromptDialog : Window
{
    public PromptDialog() => InitializeComponent();

    public static Maybe<string> Ask(string title, string prompt, string initial = "")
    {
        var dialog = new PromptDialog { Title = title };
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
