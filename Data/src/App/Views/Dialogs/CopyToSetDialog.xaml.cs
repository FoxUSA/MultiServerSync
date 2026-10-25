using System.Windows;
using MultiServerSync.Services;
using Wpf.Ui.Controls;

namespace MultiServerSync.Views.Dialogs;

/// <summary>
/// Выбор наборов серверов, куда скопировать отмеченные серверы. Кнопка «Скопировать»
/// оживает, только когда отмечен хотя бы один набор. Esc — отмена.
/// </summary>
public partial class CopyToSetDialog : FluentWindow
{
    private readonly List<Choice> _choices;

    private CopyToSetDialog(int count, IEnumerable<ServerSet> sets)
    {
        InitializeComponent();

        HeadlineText.Text = $"Скопировать серверы ({count}) в наборы";
        DetailText.Text = "Отметьте вкладки, куда добавить копии. Сервер, который в наборе уже есть, второй раз не добавляется.";

        _choices = sets.Select(s => new Choice(s)).ToList();
        SetList.ItemsSource = _choices;
    }

    /// <summary>Спрашивает наборы-получатели. Пустой список — передумали.</summary>
    public static IReadOnlyList<ServerSet> Ask(Window? owner, int count, IEnumerable<ServerSet> sets)
    {
        var dialog = new CopyToSetDialog(count, sets);

        if (owner is { IsLoaded: true })
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return dialog.ShowDialog() == true
            ? dialog._choices.Where(c => c.IsPicked).Select(c => c.Set).ToList()
            : [];
    }

    private void OnPickChanged(object sender, RoutedEventArgs e) =>
        ConfirmButton.IsEnabled = _choices.Any(c => c.IsPicked);

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private sealed class Choice(ServerSet set)
    {
        public ServerSet Set { get; } = set;

        public string Label => $"{Set.Title} — {Set.TabCounts}";

        public bool IsPicked { get; set; }
    }
}
