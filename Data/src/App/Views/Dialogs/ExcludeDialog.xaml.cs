using System.Windows;
using MultiServerSync.Core;
using Wpf.Ui.Controls;

namespace MultiServerSync.Views.Dialogs;

/// <summary>Исключения вкладки копирования: свои маски и готовые шаблоны в один щелчок.</summary>
public partial class ExcludeDialog : FluentWindow
{
    private ExcludeDialog(string masks)
    {
        InitializeComponent();

        MasksBox.Text = masks;
        TemplatesList.ItemsSource = ExcludeRules.Templates;

        Loaded += (_, _) =>
        {
            MasksBox.Focus();
            MasksBox.CaretIndex = MasksBox.Text.Length;
        };
    }

    private string? Result { get; set; }

    /// <summary>Новые маски — или null, если окно закрыли без сохранения.</summary>
    public static string? Ask(Window? owner, string masks)
    {
        var dialog = new ExcludeDialog(masks) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    /// <summary>Шаблон дописывается строкой в конец, если такого ещё нет.</summary>
    private void OnTemplate(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: string mask })
        {
            return;
        }

        var lines = MasksBox.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (lines.Contains(mask, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        lines.Add(mask);
        MasksBox.Text = string.Join(Environment.NewLine, lines);
        MasksBox.CaretIndex = MasksBox.Text.Length;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // пустые строки и пробелы по краям не храним
        Result = string.Join(
            Environment.NewLine,
            MasksBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
