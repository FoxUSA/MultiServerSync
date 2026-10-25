using System.Windows;
using Wpf.Ui.Controls;

namespace MultiServerSync.Views.Dialogs;

/// <summary>
/// Правка записи избранного: название и сам код. Отдельное окно, а не поле ввода на странице,
/// потому что код бывает многострочный и его надо видеть целиком.
/// </summary>
public partial class SnippetEditDialog : FluentWindow
{
    public SnippetEditDialog(string title, string name, string code)
    {
        InitializeComponent();

        Title = title;
        NameBox.Text = name;
        CodeBox.Text = code;

        CodeBox.TextChanged += (_, _) => ShowSize();
        ShowSize();

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    public string SnippetName => NameBox.Text.Trim();

    /// <summary>Код возвращается как есть: отступы и пустые строки внутри скрипта значимы.</summary>
    public string Code => CodeBox.Text.Trim();

    private void ShowSize()
    {
        var text = CodeBox.Text ?? string.Empty;

        SizeText.Text = text.Length == 0
            ? string.Empty
            : $"символов: {text.Length} · строк: {text.Split('\n').Length}";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SnippetName))
        {
            NameBox.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(Code))
        {
            CodeBox.Focus();
            return;
        }

        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
