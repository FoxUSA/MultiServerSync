using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using MultiServerSync.Core;
using MultiServerSync.Services;
using MultiServerSync.Views.Dialogs;
using MultiServerSync.Views.Pages;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MultiServerSync.Views;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();

        DataContext = this;

        var settings = AppState.Current.Settings;

        // версия берётся из Data\manifest.json и нигде в коде не дублируется
        Title = AppVersion.Caption;
        Bar.Title = AppVersion.Caption;

        Width = settings.WindowWidth;
        Height = settings.WindowHeight;

        ApplyTheme(settings.Theme);

        BottomRow.Height = new GridLength(Math.Max(0, settings.LogPanelHeight));
        SetBottomVisible(settings.LogPanelVisible);

        // умный режим спрашивает посреди окна; фокус на «Отменить», так что случайный Enter не запустит
        SmartSwitch.IsChecked = settings.SmartMode;
        RunHistory.Choose = (headline, detail, confirm, extra) =>
            ConfirmDialog.Choose(this, headline, detail, confirm, extra, "Отменить");

        // операция запущена — сразу показываем терминал, чтобы вывод был перед глазами,
        // а если она не начата — журнал, где написано почему
        AppState.Current.TerminalRequested += ShowTerminal;
        AppState.Current.LogRequested += ShowLog;

        InputBindings.Add(new KeyBinding(
            new Core.RelayCommand(_ => ToggleBottom()), Key.F12, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(
            new Core.RelayCommand(_ => AddTab()), Key.T, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(
            new Core.RelayCommand(_ => CloseCurrentTab()), Key.W, ModifierKeys.Control));

        // ярлык вкладки ведут от состояния, а не наоборот: двусторонняя привязка SelectedItem
        // умеет записать в ActiveWorkspace свой null при перестроении списка, и тогда текущей
        // становится первая вкладка — переход по разделам слева сбивал открытую вкладку
        AppState.Current.ActiveWorkspaceChanged += workspace =>
        {
            // открыли вкладку из архива (или из основной ленты) — лента показывает её сторону
            (workspace.IsArchived ? _lastArchived : _lastMain)[State.TabsOf(workspace)] = workspace;
            FollowActive();
            TabStrip.SelectedItem = workspace;
        };
        AppState.Current.ArchiveChanged += _ => FollowActive();

        // проводник просит открыть вкладку другого раздела — например, команду «здесь»
        AppState.Current.TabShowRequested += tab => ShowTab(tab, State.SectionOfTab(tab) switch
        {
            AppSection.Command => typeof(CommandPage),
            AppSection.Download => typeof(DownloadPage),
            AppSection.Explorer => typeof(ExplorerPage),
            _ => typeof(FilesPage),
        });

        AppState.Current.ActiveServerSetChanged += set => SetStrip.SelectedItem = set;

        // ряд разделов следит за открытым разделом: в раздел попадают и не кнопкой (из вкладки, из проводника)
        State.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppState.Section))
            {
                ShowSection();
            }
        };
        ShowSection();

        // рамка не копит историю: страницы и так живут всё время работы программы
        PageFrame.Navigated += (_, _) =>
        {
            while (PageFrame.CanGoBack)
            {
                PageFrame.RemoveBackEntry();
            }
        };

        SetupTabReorder();
        SetupTabView();
        SetupTray();

        AppState.Current.OperationFinished += OnOperationFinished;
        AppState.Current.OperationFinished += FinishSound.OnFinished;

        Loaded += (_, _) =>
        {
            TabStrip.SelectedItem = State.ActiveWorkspace;
            SetStrip.SelectedItem = State.ActiveServerSet;
            Navigate(typeof(ServersPage));

            // записи загрузки плашкой не показываем — только то, что сделано после открытия окна
            AppLog.Written += Toast.Show;
        };

        Closing += OnClosing;

        // проверка при запуске закончится позже открытия окна — кнопка всплывёт сама
        UpdateService.AvailableChanged += ShowUpdateButton;
        ShowUpdateButton();
    }

    private void ShowUpdateButton()
    {
        if (UpdateService.Available is { } release)
        {
            UpdateAvailableButton.Content = $"Обновление {release.Version}";
            UpdateAvailableButton.ToolTip = $"Доступна версия {release.Caption}, установлена {AppVersion.Version}";
            UpdateAvailableButton.Visibility = Visibility.Visible;
        }
        else
        {
            UpdateAvailableButton.Visibility = Visibility.Collapsed;
        }
    }

    private void OnUpdateAvailableClick(object sender, RoutedEventArgs e) => UpdateWindow.Open(this);

    private void OnOpenProductPage(object sender, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(UpdateService.PageUrl) { UseShellExecute = true });

    /// <summary>Страницы разделов — по одной на раздел: вкладки, выделение и прокрутка не теряются при переходах.</summary>
    private readonly Dictionary<Type, System.Windows.Controls.Page> _pages = new();

    /// <summary>Открыть раздел. Назад-вперёд у рамки нет: разделы переключает ряд над страницей.</summary>
    private void Navigate(Type type)
    {
        if (!_pages.TryGetValue(type, out var page))
        {
            page = (System.Windows.Controls.Page)Activator.CreateInstance(type)!;
            _pages[type] = page;
        }

        if (ReferenceEquals(PageFrame.Content, page))
        {
            return;
        }

        PageFrame.Navigate(page);
    }

    /// <summary>Кнопка раздела в ряду над рабочей областью.</summary>
    private void OnSectionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Type page })
        {
            Navigate(page);
        }
    }

    /// <summary>Отмечена кнопка открытого раздела; в «Настройках» — ни одна.</summary>
    private void ShowSection()
    {
        var section = State.Section;
        ServersSection.IsChecked = section == AppSection.Servers;
        ExplorerSection.IsChecked = section == AppSection.Explorer;
        CommandSection.IsChecked = section == AppSection.Command;
        FilesSection.IsChecked = section == AppSection.Files;
        DownloadSection.IsChecked = section == AppSection.Download;
    }

    /// <summary>
    /// Двойной щелчок по пункту меню слева возвращает на первую вкладку: с любой вкладки
    /// быстро попасть на основную, не целясь мышью в ленту ярлыков.
    /// Событие не помечается обработанным — переход по разделу должен произойти как обычно.
    /// </summary>
    private void OnNavItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            State.ActiveWorkspace = State.MainWorkspace;
        }
    }

    /// <summary>Щелчок по ярлыку. Пустой выбор не принимаем — возвращаем ярлык на текущую вкладку.</summary>
    private void OnTabSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TabStrip.SelectedItem is Workspace workspace)
        {
            AppLog.Debug($"Выбрана вкладка «{workspace.Title}», место {State.Tabs.IndexOf(workspace) + 1}.");
            State.ActiveWorkspace = workspace;
        }
        else if (State.Tabs.Count > 0)
        {
            TabStrip.SelectedItem = State.ActiveWorkspace;
        }
    }

    /// <summary>Щелчок по ярлыку набора серверов. Пустой выбор так же не принимаем.</summary>
    private void OnSetSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SetStrip.SelectedItem is ServerSet set)
        {
            State.ActiveServerSet = set;
        }
        else if (State.ServerSets.Count > 0)
        {
            SetStrip.SelectedItem = State.ActiveServerSet;
        }
    }

    /// <summary>Всё общее состояние: серверы, вкладки, настройки.</summary>
    public AppState State => AppState.Current;

    /// <summary>
    /// Тема программы. Подложки нет (WindowBackdropType.None): сплошной фон держит ступени
    /// яркости окна, карточек и таблиц одинаковыми при любых обоях и в неактивном окне.
    /// WPF-UI без явного указания снова включает Mica, поэтому «как в системе» считаем сами.
    /// </summary>
    public static void ApplyTheme(string theme)
    {
        var target = theme switch
        {
            "Light" => ApplicationTheme.Light,
            "Dark" => ApplicationTheme.Dark,
            _ => ApplicationThemeManager.GetSystemTheme() switch
            {
                SystemTheme.Dark or SystemTheme.Glow or SystemTheme.CapturedMotion => ApplicationTheme.Dark,
                SystemTheme.HC1 or SystemTheme.HC2 or SystemTheme.HCBlack or SystemTheme.HCWhite => ApplicationTheme.HighContrast,
                _ => ApplicationTheme.Light,
            },
        };
        ApplicationThemeManager.Apply(target, WindowBackdropType.None);
    }

    /// <summary>Повторный запуск программы: вместо второго окна — ещё одна вкладка в этом.</summary>
    public void OpenWorkspaceFromNewInstance()
    {
        // окно могло быть спрятано в трей
        ShowFromTray();
        AddWorkspace();
    }

    private void AddWorkspace()
    {
        State.ActiveWorkspace = State.AddWorkspace();
    }

    private void OnAddTab(object sender, RoutedEventArgs e) => AddTab();

    /// <summary>Плюс и Ctrl+T добавляют вкладку того раздела, который открыт.</summary>
    private void AddTab()
    {
        if (State.ShowsServerSetTabs)
        {
            State.ActiveServerSet = State.AddServerSet();
        }
        else if (State.ShowsWorkspaceTabs)
        {
            AddWorkspace();
        }
    }

    /// <summary>Ctrl+W закрывает текущую вкладку открытого раздела.</summary>
    private void CloseCurrentTab()
    {
        if (State.ShowsServerSetTabs)
        {
            CloseServerSet(State.ActiveServerSet);
        }
        else if (State.ShowsWorkspaceTabs)
        {
            CloseWorkspace(this, State.ActiveWorkspace);
        }
    }

    /// <summary>
    /// Вкладка закрывается только с подтверждением: крестик рядом с переключателями, промахнуться легко.
    /// Первую и занятую не спрашиваем — их закрыть и так нельзя, об этом скажет журнал.
    /// </summary>
    internal static bool CloseWorkspace(Window owner, Workspace workspace)
    {
        var state = AppState.Current;

        if (!workspace.IsMain && !workspace.IsBusy && workspace.Personal is not { IsBusy: true })
        {
            var detail = workspace.Personal is { } personal
                ? $"Вместе с ней закроется её личная вкладка команд «{personal.Title}»."
                : workspace.Owner is { } parent
                    ? $"Команды после копирования у «{parent.Title}» выключатся."
                    : "Всё, что на ней настроено, пропадёт.";

            if (!ConfirmDialog.Ask(owner, $"Закрыть вкладку «{workspace.Title}»?", detail, "Закрыть"))
            {
                return false;
            }
        }

        return state.RemoveWorkspace(workspace);
    }

    /// <summary>
    /// Набор закрывается только с подтверждением: вместе с ним насовсем уходят его серверы.
    /// </summary>
    private void CloseServerSet(ServerSet set)
    {
        if (!set.CanClose)
        {
            return;
        }

        var count = set.Rows.Count;
        var what = count == 0
            ? "В нём нет серверов."
            : $"Вместе с ним будет удалён список серверов ({count}). Восстановить его будет нельзя.";

        if (ConfirmDialog.Ask(this, $"Закрыть набор «{set.Title}»?", what, "Закрыть набор"))
        {
            State.RemoveServerSet(set);
        }
    }

    /// <summary>
    /// Галочки «после команды» / «после копирования» в терминале: вывод сам уходит в буфер.
    /// Срабатывает выбор того вида операции, что закончилась, а не того раздела, что открыт.
    /// </summary>
    private void OnOperationFinished(OperationBatch batch)
    {
        // буфер обмена доступен только из потока окна — операция могла закончиться в фоновом
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnOperationFinished(batch)));
            return;
        }

        var settings = State.Settings;
        var copying = State.IsCopyBatch(batch);
        var finished = copying ? "Копирование закончилось" : "Команда закончилась";

        if (copying ? settings.AutoCopyAllAfterCopy : settings.AutoCopyAll)
        {
            Terminal.CopyAll(batch, finished);
        }
        else if (copying ? settings.AutoCopyCurrentAfterCopy : settings.AutoCopyCurrent)
        {
            Terminal.CopyCurrentAfter(batch, finished);
        }
    }

    /// <summary>
    /// Щелчок по набору в боковой панели: он становится целевым у открытой вкладки. Другие вкладки
    /// и набор, который правится на «Серверах», это не трогает.
    /// </summary>
    private void OnPickServerSet(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ServerSet set })
        {
            State.PickPaneSet(set);
        }
    }

    private void OnSmartModeChanged(object sender, RoutedEventArgs e)
    {
        var on = SmartSwitch.IsChecked == true;

        AppState.Current.Settings.SmartMode = on;
        AppState.Current.SaveSettings();
        AppLog.Hint(on
            ? "Умный режим включён: спросит, если сервер непривычный для команды или вкладки."
            : "Умный режим выключен.");
    }

    /// <summary>«Все» в переключателе наборов: в панели открытой вкладки серверы всех наборов.</summary>
    private void OnPickAllSets(object sender, RoutedEventArgs e)
    {
        if (!State.PaneAllSets)
        {
            State.PaneAllSets = true;
            AppLog.Hint($"Целевые сервера — из всех наборов: {State.PaneServers.Count}.");
        }
    }

    private void OnCloseTab(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        switch (sender)
        {
            case FrameworkElement { DataContext: Workspace workspace }:
                CloseWorkspace(this, workspace);
                break;
            case FrameworkElement { DataContext: ServerSet set }:
                CloseServerSet(set);
                break;
        }
    }

    // ── перетаскивание ярлыков мышью ───────────────────────────────────────────

    /// <summary>
    /// Мышью, как в браузере, переставляются ярлыки вкладок и наборов (первый в каждой ленте
    /// закреплён), кнопки разделов и серверы в панели слева.
    /// </summary>
    private void SetupTabReorder()
    {
        RestoreSectionOrder();

        _ = new Controls.TabReorder(
            SectionBar,
            item => item is System.Windows.Controls.RadioButton,
            (item, to) =>
            {
                if (item is not UIElement button)
                {
                    return;
                }

                var from = SectionBar.Children.IndexOf(button);

                if (from < 0 || from == to)
                {
                    return;
                }

                SectionBar.Children.RemoveAt(from);
                SectionBar.Children.Insert(to, button);

                State.Settings.SectionOrder = SectionBar.Children.OfType<FrameworkElement>().Select(b => b.Name).ToList();
                State.SaveSettings();
            });

        // место в панели — среди серверов, которые она показывает: набор вкладки или «Все»
        _ = new Controls.TabReorder(
            PaneServerList,
            PaneScroller,
            item => item is Models.ServerCheck,
            fixedCount: 0,
            (item, to) =>
            {
                var shown = State.ActiveWorkspace.Servers;

                if (item is Models.ServerCheck dragged && to < shown.Count)
                {
                    State.MoveServer(dragged.Server, shown[to].Server);
                }
            },
            grabButtons: true);

        _tabReorder = new Controls.TabReorder(
            TabStrip,
            null,
            item => item is Workspace { IsMain: false },
            fixedCount: 1,
            (item, to) =>
            {
                // место в ленте → место в списке раздела: архивных в ленте нет, а в списке они есть
                if (item is Workspace workspace
                    && TabStrip.Items.Count > 0
                    && TabStrip.Items[Math.Min(to, TabStrip.Items.Count - 1)] is Workspace anchor
                    && State.MoveWorkspace(workspace, State.Tabs.IndexOf(anchor)))
                {
                    // перестановка сбивает выделение списка — возвращаем его на текущую вкладку
                    TabStrip.SelectedItem = State.ActiveWorkspace;
                }
            })
        {
            // ярлык, брошенный на значок, уходит в другую ленту: из основной — в архив, из архива — обратно
            OutTarget = ArchiveButton,
            DroppedOut = item =>
            {
                if (item is Workspace workspace && State.SetArchived(workspace, !workspace.IsArchived))
                {
                    PulseArchive();
                }
            },
            OverOutTarget = over =>
            {
                _dropOver = over;
                UpdateArchiveButton();
            },
        };

        _ = new Controls.TabReorder(
            SetStrip,
            null,
            item => item is ServerSet { IsMain: false },
            fixedCount: 1,
            (item, to) =>
            {
                if (item is ServerSet set && State.MoveServerSet(set, to))
                {
                    SetStrip.SelectedItem = State.ActiveServerSet;
                }
            });
    }

    /// <summary>Кнопки разделов — в том порядке, в каком их расставили; незнакомые имена пропускаются.</summary>
    private void RestoreSectionOrder()
    {
        var buttons = SectionBar.Children.OfType<FrameworkElement>().ToList();
        var order = State.Settings.SectionOrder;

        // кнопки, которых в сохранённом порядке нет (новый раздел), остаются в конце
        var sorted = buttons
            .OrderBy(b => order.IndexOf(b.Name) is var i && i >= 0 ? i : int.MaxValue)
            .ToList();

        if (sorted.SequenceEqual(buttons))
        {
            return;
        }

        SectionBar.Children.Clear();

        foreach (var button in sorted)
        {
            SectionBar.Children.Add(button);
        }
    }

    // ── лента ярлыков и место под ней ──────────────────────────────────────────

    private void SetupTabView()
    {
        State.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppState.Section))
            {
                ApplyServerPane();
            }
            else if (e.PropertyName == nameof(AppState.Tabs))
            {
                ShowTabs();
            }
        };

        ApplyServerPane();
        ShowTabs();
    }

    /// <summary>У «Проводника» карточки «Целевые сервера» нет: сервер выбирается в самой правой панели.</summary>
    private void ApplyServerPane()
    {
        var explorer = State.Section == AppSection.Explorer;
        PaneCard.Visibility = explorer ? Visibility.Collapsed : Visibility.Visible;
        PaneColumn.Width = new GridLength(explorer ? 0 : 220);
    }

    /// <summary>
    /// Ярлыки встали вторым рядом (или ряд ушёл) — лента выросла вниз. Страница сама ужимается
    /// на всю разницу, а нижняя панель стоит своей высотой; чтобы обе ужались поровну, нижней
    /// отдаём её долю разницы.
    /// </summary>
    private void OnTabStripAreaSized(object sender, SizeChangedEventArgs e)
    {
        var delta = e.NewSize.Height - e.PreviousSize.Height;

        if (!e.HeightChanged || e.PreviousSize.Height <= 0 || Math.Abs(delta) < 1
            || BottomPanel.Visibility != Visibility.Visible || !BottomRow.Height.IsAbsolute)
        {
            return;
        }

        var page = PageRow.ActualHeight;
        var bottom = BottomRow.ActualHeight;

        if (page + bottom <= 0)
        {
            return;
        }

        BottomRow.Height = new GridLength(Math.Max(0, bottom - delta * bottom / (page + bottom)));
    }

    /// <summary>
    /// Правый щелчок по ярлыку вкладку не переключает, а переносит её в другую ленту: из основной —
    /// в архив, из архива — обратно. Ярлык улетает к значку. У наборов серверов архива нет.
    /// </summary>
    private void OnTabStripRightDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        if (!ReferenceEquals(sender, TabStrip)
            || System.Windows.Controls.ItemsControl.ContainerFromElement(TabStrip, e.OriginalSource as DependencyObject)
                is not System.Windows.Controls.ListBoxItem { DataContext: Workspace workspace } container)
        {
            return;
        }

        if (workspace.IsMain)
        {
            AppLog.Hint("Основная вкладка в архив не убирается.", LogLevel.Warning);
            return;
        }

        // снимок ярлыка — до того, как он уйдёт из ленты
        var flight = FlyToArchive(container);

        if (!State.SetArchived(workspace, !workspace.IsArchived))
        {
            flight?.Invoke();
        }
    }

    // ── архив вкладок ─────────────────────────────────────────────────────────

    private readonly Dictionary<ObservableCollection<Workspace>, ListCollectionView> _tabViews = [];

    /// <summary>Последняя открытая вкладка основной ленты и архива — в каждом разделе своя.</summary>
    private readonly Dictionary<ObservableCollection<Workspace>, Workspace> _lastMain = [];
    private readonly Dictionary<ObservableCollection<Workspace>, Workspace> _lastArchived = [];

    private Controls.TabReorder? _tabReorder;

    /// <summary>Лента показывает архив, а не основные вкладки.</summary>
    private bool _archiveMode;

    /// <summary>Над значком держат перетаскиваемый ярлык.</summary>
    private bool _dropOver;

    /// <summary>
    /// Лента — основные вкладки раздела или архивные. Выглядят и работают одинаково: вкладки
    /// остаются в списке раздела как были, меняется только, в какой ленте виден ярлык.
    /// </summary>
    private void ShowTabs()
    {
        var tabs = State.Tabs;

        if (!_tabViews.TryGetValue(tabs, out var view))
        {
            view = new ListCollectionView(tabs)
            {
                Filter = item => item is Workspace workspace && workspace.IsArchived == _archiveMode,
                IsLiveFiltering = true,
            };
            view.LiveFilteringProperties.Add(nameof(Workspace.IsArchived));
            ((System.Collections.Specialized.INotifyCollectionChanged)view).CollectionChanged += (_, _) => UpdateArchiveButton();
            _tabViews[tabs] = view;
        }

        _archiveMode = State.ShowsWorkspaceTabs && State.ActiveWorkspace.IsArchived;
        view.Refresh();

        if (!ReferenceEquals(TabStrip.ItemsSource, view))
        {
            // выбор ведёт состояние, а не «текущий элемент» представления — иначе выбиралась бы первая
            TabStrip.IsSynchronizedWithCurrentItem = false;
            TabStrip.ItemsSource = view;
        }

        ApplyArchiveMode();
    }

    /// <summary>Лента идёт за открытой вкладкой: открыта архивная — показан архив, и наоборот.</summary>
    private void FollowActive()
    {
        var archive = State.ShowsWorkspaceTabs && State.ActiveWorkspace.IsArchived;

        if (archive != _archiveMode)
        {
            _archiveMode = archive;

            if (_tabViews.TryGetValue(State.Tabs, out var view))
            {
                view.Refresh();
            }

            ApplyArchiveMode();
        }
        else
        {
            UpdateArchiveButton();
        }
    }

    private void ApplyArchiveMode()
    {
        // в основной ленте первая (основная) вкладка закреплена, в архиве закреплённых нет
        if (_tabReorder is not null)
        {
            _tabReorder.FixedCount = _archiveMode ? 0 : 1;
        }

        TabStrip.SelectedItem = State.ActiveWorkspace;
        UpdateArchiveButton();
    }

    /// <summary>
    /// Значок под «+» — только у разделов с вкладками. В основной ленте — архив и число вкладок
    /// в нём, в архиве — «назад к основной ленте».
    /// </summary>
    private void UpdateArchiveButton()
    {
        var shown = State.ShowsWorkspaceTabs;
        var archived = shown ? State.Tabs.Count(t => t.IsArchived) : 0;

        ArchiveButton.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        ArchiveRow.Height = shown ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ArchiveIcon.Symbol = _archiveMode ? SymbolRegular.TabArrowLeft24 : SymbolRegular.Archive24;
        ArchiveCount.Text = archived.ToString();
        ArchiveCount.Visibility = archived > 0 && !_archiveMode ? Visibility.Visible : Visibility.Collapsed;
        ArchiveButton.Opacity = archived > 0 || _archiveMode ? 1 : 0.6;
        ArchiveButton.Appearance = _dropOver ? ControlAppearance.Success
            : _archiveMode ? ControlAppearance.Primary
            : ControlAppearance.Secondary;
        ArchiveButton.ToolTip = _archiveMode
            ? "Открыт архив. Щелчок — назад к основной ленте. Правый щелчок по ярлыку или перетаскивание сюда — вернуть вкладку в основную ленту"
            : "Архив вкладок: щелчок — открыть. Убрать сюда — правый щелчок по ярлыку или перетащить ярлык на этот значок";
    }

    /// <summary>Значок переключает ленту: основная ↔ архив. Открывается вкладка, что была открыта там последней.</summary>
    private void OnArchiveClick(object sender, RoutedEventArgs e)
    {
        var tabs = State.Tabs;

        if (_archiveMode)
        {
            State.ActiveWorkspace = _lastMain.GetValueOrDefault(tabs) is { IsArchived: false } back && tabs.Contains(back)
                ? back
                : State.MainWorkspace;
            return;
        }

        var archived = tabs.Where(t => t.IsArchived).ToList();

        if (archived.Count == 0)
        {
            AppLog.Hint("Архив пуст. Сюда вкладку убирает правый щелчок по ярлыку или перетаскивание на этот значок.");
            return;
        }

        State.ActiveWorkspace = _lastArchived.GetValueOrDefault(tabs) is { IsArchived: true } last && tabs.Contains(last)
            ? last
            : archived[0];
    }

    /// <summary>
    /// Ярлык улетает к значку архива: летит его снимок, уменьшаясь и бледнея, значок в конце
    /// вздрагивает. Возвращает, как убрать снимок, если в архив вкладку не пустили.
    /// </summary>
    private Action? FlyToArchive(FrameworkElement from)
    {
        if (from.ActualWidth < 1 || ArchiveButton.Visibility != Visibility.Visible)
        {
            return null;
        }

        var dpi = VisualTreeHelper.GetDpi(from);
        var shot = new RenderTargetBitmap(
            (int)Math.Ceiling(from.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(from.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        shot.Render(from);

        var scale = new ScaleTransform(1, 1);
        var image = new System.Windows.Controls.Image
        {
            Source = shot,
            Width = from.ActualWidth,
            Height = from.ActualHeight,
            RenderTransform = scale,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };

        var start = from.TranslatePoint(default, FxLayer);
        var end = ArchiveButton.TranslatePoint(
            new Point((ArchiveButton.ActualWidth - from.ActualWidth) / 2, (ArchiveButton.ActualHeight - from.ActualHeight) / 2),
            FxLayer);

        System.Windows.Controls.Canvas.SetLeft(image, start.X);
        System.Windows.Controls.Canvas.SetTop(image, start.Y);
        FxLayer.Children.Add(image);

        var time = new Duration(TimeSpan.FromMilliseconds(420));
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var left = new DoubleAnimation(end.X, time) { EasingFunction = ease };
        left.Completed += (_, _) =>
        {
            FxLayer.Children.Remove(image);
            PulseArchive();
        };

        image.BeginAnimation(System.Windows.Controls.Canvas.LeftProperty, left);
        image.BeginAnimation(System.Windows.Controls.Canvas.TopProperty, new DoubleAnimation(end.Y, time) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.15, time) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.15, time) { EasingFunction = ease });
        image.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.3, time));

        return () => FxLayer.Children.Remove(image);
    }

    /// <summary>Значок архива вздрагивает: вкладка долетела.</summary>
    private void PulseArchive()
    {
        if (ArchiveButton.RenderTransform is not ScaleTransform { IsFrozen: false } scale)
        {
            return;
        }

        var pulse = new DoubleAnimation(1, 1.3, new Duration(TimeSpan.FromMilliseconds(130))) { AutoReverse = true };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
    }

    /// <summary>
    /// Свитч на ярлыке копирования. Первое включение создаёт пустую личную вкладку команд
    /// и сразу ведёт на неё; дальше свитч только включает и выключает запуск после копирования.
    /// </summary>
    private void OnAfterCopySwitch(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is not FrameworkElement { DataContext: Workspace copy })
        {
            return;
        }

        if (!copy.AfterCopyEnabled)
        {
            AppLog.Hint($"Команды после копирования выключены: «{copy.Title}».");
            return;
        }

        if (copy.Personal is { } personal)
        {
            AppLog.Hint($"Команды после копирования включены: «{copy.Title}».");
            return;
        }

        personal = State.CreatePersonalTab(copy);
        copy.RaiseChain();
        State.JumpToPersonal(personal);
        ShowTab(personal, typeof(CommandPage));
    }

    /// <summary>Стрелка на ярлыке копирования — в её личные команды.</summary>
    private void OnGoPersonal(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is FrameworkElement { DataContext: Workspace { Personal: { } personal } })
        {
            State.JumpToPersonal(personal);
            ShowTab(personal, typeof(CommandPage));
        }
    }

    /// <summary>Стрелка на личной вкладке — назад, к её вкладке копирования.</summary>
    private void OnGoOwner(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is FrameworkElement { DataContext: Workspace { Owner: { } owner } })
        {
            ShowTab(owner, typeof(FilesPage));
        }
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e) => Navigate(typeof(SettingsPage));

    /// <summary>Открыть вкладку в её разделе: сначала выбрать её, потом перейти в раздел.</summary>
    private void ShowTab(Workspace tab, Type page)
    {
        State.ActiveWorkspace = tab;
        Navigate(page);
    }

    /// <summary>Двойной щелчок по ярлыку — то же самое.</summary>
    private void OnRenameTab(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;

        // по кнопкам в углах ярлыка второй раз переименование не открываем
        if (FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(source) is not null)
        {
            return;
        }

        switch (FindAncestor<System.Windows.Controls.ListBoxItem>(source)?.DataContext)
        {
            case Workspace workspace:
                Rename(workspace);
                break;
            case ServerSet set:
                Rename(set);
                break;
        }
    }

    private void Rename(ServerSet set)
    {
        var dialog = new InputDialog("Название набора", "Как назвать этот набор серверов:", set.Title)
        {
            Owner = this,
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Value))
        {
            set.Title = dialog.Value;
        }
    }

    private void Rename(Workspace workspace)
    {
        if (workspace.IsPersonal)
        {
            AppLog.Hint("Личная вкладка называется как её вкладка копирования — переименуйте ту.");
            return;
        }

        var dialog = new InputDialog("Название вкладки", "Как назвать эту вкладку:", workspace.Title)
        {
            Owner = this,
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Value))
        {
            workspace.Title = dialog.Value;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? node)
        where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
            {
                return match;
            }

            node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(node)
                : System.Windows.LogicalTreeHelper.GetParent(node);
        }

        return null;
    }

    /// <summary>Нижняя панель прячется и разворачивается по F12; кнопки в шапке для этого нет.</summary>
    private void ToggleBottom() => SetBottomVisible(BottomPanel.Visibility != Visibility.Visible);

    private void SetBottomVisible(bool visible)
    {
        // спрятали — натянутая высота не теряется, панель вернётся такой же
        if (!visible)
        {
            RememberBottom();
        }

        BottomPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        BottomSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        BottomRow.Height = new GridLength(visible ? StoredBottom() : 0);
    }

    /// <summary>
    /// Высота нижней панели одна на все разделы, как у панели в VS Code: при переходе между
    /// разделами она не прыгает. Что натянул разделителем — то и запоминается.
    /// </summary>
    private void RememberBottom()
    {
        if (BottomPanel.Visibility == Visibility.Visible && BottomRow.ActualHeight > 40)
        {
            AppState.Current.Settings.LogPanelHeight = BottomRow.ActualHeight;
        }
    }

    private double StoredBottom()
    {
        // панель не должна съедать кнопки запуска на странице копирования:
        // при окне по умолчанию 220 точек — предел, который она может занять
        var stored = AppState.Current.Settings.LogPanelHeight;
        return stored < 120 ? 220 : stored;
    }

    /// <summary>Разворачивает нижнюю панель на вкладке «Терминал».</summary>
    private void ShowTerminal()
    {
        if (BottomPanel.Visibility != Visibility.Visible)
        {
            SetBottomVisible(true);
        }

        TerminalTab.IsChecked = true;
    }

    /// <summary>Разворачивает нижнюю панель на вкладке «Журнал»: операция не пошла, причина там.</summary>
    private void ShowLog()
    {
        if (BottomPanel.Visibility != Visibility.Visible)
        {
            SetBottomVisible(true);
        }

        JournalTab.IsChecked = true;
    }

    private void OnBottomTabChanged(object sender, RoutedEventArgs e)
    {
        if (Terminal is null || Log is null)
        {
            return;
        }

        var terminal = TerminalTab.IsChecked == true;

        Terminal.Visibility = terminal ? Visibility.Visible : Visibility.Collapsed;
        Log.Visibility = terminal ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Ярлыки «Терминал / Журнал» разложились — кнопки обоих встают в их строку, правее них.</summary>
    private void OnBottomTabsSized(object sender, SizeChangedEventArgs e)
    {
        Terminal.AlignToolbar(e.NewSize.Width, e.NewSize.Height);
        Log.AlignToolbar(e.NewSize.Width, e.NewSize.Height);
    }

    // ── трей и выход ───────────────────────────────────────────────────────────

    private TrayIcon? _tray;

    private void SetupTray()
    {
        _tray = new TrayIcon(ShowFromTray, ExitApp);
        Closed += (_, _) => _tray?.Dispose();
    }

    /// <summary>Окно обратно из трея — туда же, где было, и поверх остальных.</summary>
    public void ShowFromTray()
    {
        Show();

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => ExitApp();

    /// <summary>
    /// Выход из программы. Если идёт копирование или команда, сначала спрашиваем:
    /// выход их оборвёт. Правленые в проводнике файлы сервера, ещё не залитые, — тоже.
    /// </summary>
    private async void ExitApp()
    {
        if (RemoteEdit.Pending > 0)
        {
            ShowFromTray();
        }

        if (!await RemoteEdit.BeforeExitAsync(this))
        {
            return;
        }

        var busy = State.AllWorkspaces.Where(w => w.IsBusy).ToList();

        if (busy.Count > 0)
        {
            ShowFromTray();

            var names = string.Join(", ", busy.Select(w => $"«{w.Title}»"));

            if (!ConfirmDialog.Ask(
                this,
                "Выйти из программы?",
                $"Идёт операция на вкладках {names} — выход её оборвёт.",
                "Выйти"))
            {
                return;
            }
        }

        App.Quit();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var settings = AppState.Current.Settings;

        settings.WindowWidth = Width;
        settings.WindowHeight = Height;
        settings.LogPanelVisible = BottomPanel.Visibility == Visibility.Visible;
        RememberBottom();

        AppState.Current.SaveAll();

        // крестик прячет окно в трей; закрывается оно только при выходе из программы
        if (!App.Quitting)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
