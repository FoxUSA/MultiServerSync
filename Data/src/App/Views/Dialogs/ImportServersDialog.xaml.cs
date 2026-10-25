using System.Windows;
using MultiServerSync.Services;
using Wpf.Ui.Controls;

namespace MultiServerSync.Views.Dialogs;

/// <summary>
/// Импорт серверов из ~/.ssh/config, PuTTY и WinSCP: список найденного с галочками.
/// То, что уже есть в программе, по умолчанию не отмечено.
/// </summary>
public partial class ImportServersDialog : FluentWindow
{
    private readonly List<ImportCandidate> _found;

    private ImportServersDialog(List<ImportCandidate> found, string setTitle)
    {
        InitializeComponent();

        _found = found;
        Grid.ItemsSource = found;

        var sources = string.Join(", ", found.Select(c => c.Source).Distinct());
        InfoText.Text = $"Найдено серверов: {found.Count} ({sources}). Отмеченные добавятся в набор «{setTitle}» "
            + "без галочек на вкладках. Пароли не переносятся — их нужно будет ввести, если вход не по ключу.";

        UpdateButton();
    }

    /// <summary>Отмеченное к добавлению, или null — передумали.</summary>
    public static List<ImportCandidate>? Ask(Window? owner, List<ImportCandidate> found, string setTitle)
    {
        var dialog = new ImportServersDialog(found, setTitle) { Owner = owner };
        return dialog.ShowDialog() == true ? found.Where(c => c.IsSelected).ToList() : null;
    }

    private void UpdateButton()
    {
        var picked = _found.Count(c => c.IsSelected);
        AddButton.Content = $"Добавить отмеченные ({picked})";
        AddButton.IsEnabled = picked > 0;
    }

    private void OnRowChecked(object sender, RoutedEventArgs e) => UpdateButton();

    private void OnAdd(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
