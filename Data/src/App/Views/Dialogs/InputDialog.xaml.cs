using System.Windows;
using Wpf.Ui.Controls;

namespace MultiServerSync.Views.Dialogs;

public partial class InputDialog : FluentWindow
{
    public InputDialog(string title, string prompt, string initialValue = "")
    {
        InitializeComponent();

        Title = title;
        PromptText.Text = prompt;
        ValueBox.Text = initialValue;

        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    public string Value => ValueBox.Text.Trim();

    private void OnOk(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
