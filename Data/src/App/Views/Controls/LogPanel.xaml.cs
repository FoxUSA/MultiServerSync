using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using MultiServerSync.Core;
using MultiServerSync.Models;
using MultiServerSync.Services;

namespace MultiServerSync.Views.Controls;

public partial class LogPanel : UserControl
{
    private readonly ListCollectionView _view = new(AppLog.Entries);

    public LogPanel()
    {
        // прочитать до разметки: её «Все действия» при создании записывается в настройку
        var settings = AppState.Current.Settings;
        var savedFilter = settings.JournalFilter;
        var savedShown = settings.JournalShown;

        InitializeComponent();

        LogList.ItemsSource = _view;
        AppLog.Written += OnWritten;
        Unloaded += (_, _) => AppLog.Written -= OnWritten;

        // фильтр и автопрокрутка — как были при выходе
        if (ActionFilter.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Content == savedFilter) is { } filter)
        {
            ActionFilter.SelectedItem = filter;
        }

        AutoScrollBox.IsChecked = settings.JournalAutoScroll;
        AutoScrollBox.Checked += (_, _) => settings.JournalAutoScroll = true;
        AutoScrollBox.Unchecked += (_, _) => settings.JournalAutoScroll = false;
        SearchBox.Text = settings.JournalSearch;

        // галочка «Весь журнал» — своя у каждой вкладки: сменили вкладку или раздел — читается заново
        var state = AppState.Current;
        state.ActiveWorkspaceChanged += _ => ShowTab();
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppState.Section))
            {
                ShowTab();
            }
        };
        Loaded += (_, _) => ShowTab();
        ShowTab();

        // лог, открытый при выходе, — снова открыт
        LogList.SelectedItem = _view.OfType<LogEntry>().FirstOrDefault(entry => entry.Time == savedShown);

        // панель показали — открытая строка в поле зрения: пока панель скрыта, прокрутки нет
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && LogList.SelectedItem is { } picked)
            {
                Dispatcher.BeginInvoke(() => LogList.ScrollIntoView(picked), DispatcherPriority.Loaded);
            }
        };
    }

    /// <summary>Текст открытого лога — для его кнопки копирования.</summary>
    private string _shownText = string.Empty;

    /// <summary>Строку выбрали — справа её полный лог; выбор сняли — таблица снова на всю ширину.</summary>
    private void OnEntryPicked(object sender, SelectionChangedEventArgs e)
    {
        var entry = LogList.SelectedItem as LogEntry;

        AppState.Current.Settings.JournalShown = entry?.Time;
        OutputPane.Visibility = entry is null ? Visibility.Collapsed : Visibility.Visible;
        SplitColumn.Width = new GridLength(entry is null ? 0 : 4);
        OutputColumn.MinWidth = entry is null ? 0 : 260;
        OutputColumn.Width = entry is null ? new GridLength(0) : new GridLength(1, GridUnitType.Star);

        if (entry is null)
        {
            _shownText = string.Empty;
            OutputFeed.SetResult(OutputBox, null);
            return;
        }

        OutputTitle.Text = $"{entry.Time:dd.MM.yyyy HH:mm:ss} · {entry.SectionText}" + (entry.Scope is null ? string.Empty : " · " + entry.Scope);
        _shownText = Compose(entry);

        // то же поле, что в терминале: подсветка строк, лупа, Ctrl + колесо
        var row = new OperationResult(ActionLog.Program) { IsBare = true };
        row.Append(_shownText);
        OutputFeed.SetResult(OutputBox, row);
    }

    /// <summary>
    /// Шапка — сервер, раздел, вкладка, как в выводе терминала; сама запись целиком — в таблице она
    /// обрезана — и под ней полный лог, если он есть. В буфер идёт этот же текст.
    /// </summary>
    private static string Compose(LogEntry entry)
    {
        var head = entry.PlaceText + Environment.NewLine + entry.Text;

        if (entry.Output is null)
        {
            return head;
        }

        var log = JournalOutput.Read(entry.Output) ?? "— полный лог уже удалён: вышел за предел «Логи на диске» в настройках —";
        return head + Environment.NewLine + Environment.NewLine + log;
    }

    private void OnCloseOutput(object sender, RoutedEventArgs e) => LogList.SelectedItem = null;

    private void OnCopyOutput(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_shownText);
            AppLog.Echo("Лог записи скопирован в буфер обмена.");
        }
        catch (Exception ex)
        {
            AppLog.Hint("Не удалось скопировать лог: " + ex.Message, LogLevel.Error);
        }
    }

    /// <summary>То же, что у терминала: строка кнопок — в одной строке с ярлыками «Терминал / Журнал».</summary>
    public void AlignToolbar(double indent, double height)
    {
        ToolBar.Margin = new Thickness(indent, 0, 0, 4);
        ToolBar.MinHeight = height;
    }

    private void OnWritten(LogEntry entry)
    {
        // пока открыт лог строки, таблица не уезжает от неё
        if (AutoScrollBox.IsChecked != true || entry.IsHint || LogList.SelectedItem is not null)
        {
            return;
        }

        // свежая запись всегда первая — держим начало списка в поле зрения
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_view.Count > 0)
            {
                LogList.ScrollIntoView(_view.GetItemAt(0));
            }
        }));
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        var box = (ComboBox)sender;
        var action = (box.SelectedItem as ComboBoxItem)?.Content as string;

        _action = box.SelectedIndex <= 0 ? null : action;
        ApplyFilter();

        AppState.Current.Settings.JournalFilter = _action ?? string.Empty;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text;
        ApplyFilter();

        AppState.Current.Settings.JournalSearch = _search;
    }

    /// <summary>Действие из списка; null — все. «Удаление» берёт и «Удаление файла», и «Удаление папки».</summary>
    private string? _action;

    private string _search = string.Empty;

    /// <summary>Открытая вкладка, чью галочку показывает журнал; null — раздел без вкладок.</summary>
    private Workspace? _tab;

    /// <summary>Журнал сейчас оставляет записи одной вкладки.</summary>
    private bool _tabFiltered;

    private void ShowTab()
    {
        var state = AppState.Current;
        var tab = state is { TabsLoaded: true, ShowsWorkspaceTabs: true } ? state.ActiveWorkspace : null;

        if (!ReferenceEquals(tab, _tab))
        {
            if (_tab is not null)
            {
                _tab.PropertyChanged -= OnTabChanged;
            }

            _tab = tab;

            if (tab is not null)
            {
                tab.PropertyChanged += OnTabChanged;
            }
        }

        // в разделе без вкладок журнал и так весь: галочка стоит и не нажимается
        WholeBox.IsChecked = tab is null || tab.JournalWhole;
        WholeBox.IsEnabled = tab is not null;

        // весь журнал и остаётся весь — незачем перестраивать его 5000 строк
        if (_tabFiltered || tab is { JournalWhole: false })
        {
            ApplyFilter();
        }
    }

    /// <summary>Вкладку переименовали — записи ищутся под новым названием.</summary>
    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Workspace.Title) && _tabFiltered)
        {
            ApplyFilter();
        }
    }

    private void OnWholeClick(object sender, RoutedEventArgs e)
    {
        _tab?.SetJournalWhole(WholeBox.IsChecked == true);
        ApplyFilter();
    }

    /// <summary>Вкладка, действие и поиск действуют вместе. Запись от вкладки узнаётся по разделу и названию.</summary>
    private void ApplyFilter()
    {
        var action = _action;
        var search = _search;
        var tab = _tab is { JournalWhole: false } ? _tab.Title : null;
        var section = _tab is null ? (AppSection?)null : AppState.Current.SectionOfTab(_tab);

        _tabFiltered = tab is not null;

        _view.Filter = action is null && NameMask.IsEmpty(search) && tab is null
            ? null
            : item => item is LogEntry entry
                && (tab is null || (entry.Section == section && entry.TabText == tab))
                && (action is null || entry.ActionText.StartsWith(action, StringComparison.Ordinal))
                && NameMask.FoundIn(entry.SearchText, search);
    }

    /// <summary>Записи и их логи не вернуть, а уходит весь журнал, что бы ни оставляли фильтры, — поэтому вопрос.</summary>
    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (AppLog.Entries.Count == 0)
        {
            AppLog.Hint("Журнал уже пуст.");
            return;
        }

        if (Dialogs.ConfirmDialog.Ask(
                Window.GetWindow(this),
                $"Очистить журнал ({AppLog.Entries.Count})?",
                "Удалятся записи всех вкладок и их полные логи, вернуть их нельзя. Файлы дней в папке логов останутся.",
                "Очистить"))
        {
            AppLog.Clear();
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(AppLog.ExportText());
            AppLog.Echo("Журнал скопирован в буфер обмена.");
        }
        catch (Exception ex)
        {
            AppLog.Hint("Не удалось скопировать журнал: " + ex.Message, LogLevel.Error);
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(AppPaths.LogsDir) { UseShellExecute = true });
    }
}
