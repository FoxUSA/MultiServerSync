using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
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
        SwitchBottomHeight();

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
        InputBindings.Add(new KeyBinding(
            new Core.RelayCommand(_ => TabBoardWindow.Open(this)), Key.D, ModifierKeys.Control));

        // ярлык вкладки ведут от состояния, а не наоборот: двусторонняя привязка SelectedItem
        // умеет записать в ActiveWorkspace свой null при перестроении списка, и тогда текущей
        // становится первая вкладка — переход по разделам слева сбивал открытую вкладку
        AppState.Current.ActiveWorkspaceChanged += workspace =>
        {
            TabStrip.SelectedItem = workspace;

            // вкладку могли открыть с доски вкладок — лента наверху должна до неё доехать
            ScrollTabIntoView(workspace);
        };

        // проводник просит открыть вкладку другого раздела — например, команду «здесь»
        AppState.Current.TabShowRequested += tab => ShowTab(tab, State.SectionOfTab(tab) switch
        {
            AppSection.Command => typeof(CommandPage),
            AppSection.Download => typeof(DownloadPage),
            AppSection.Explorer => typeof(ExplorerPage),
            _ => typeof(FilesPage),
        });

        AppState.Current.ActiveServerSetChanged += set =>
        {
            SetStrip.SelectedItem = set;
            ScrollIntoStrip(SetStrip, SetScroller, set, set.Title);
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
            RootNavigation.Navigate(typeof(ServersPage));

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
            AppLog.Debug($"Выбрана вкладка «{workspace.Title}», место {State.Tabs.IndexOf(workspace) + 1}, "
                + $"лента сдвинута на {TabScroller.HorizontalOffset:0} из {TabScroller.ScrollableWidth:0}.");
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

    public static void ApplyTheme(string theme)
    {
        switch (theme)
        {
            case "Light":
                ApplicationThemeManager.Apply(ApplicationTheme.Light);
                break;
            case "Dark":
                ApplicationThemeManager.Apply(ApplicationTheme.Dark);
                break;
            default:
                ApplicationThemeManager.ApplySystemTheme();
                break;
        }
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
        var workspace = State.AddWorkspace();

        State.ActiveWorkspace = workspace;
        ScrollTabIntoView(workspace);
    }

    private void ScrollTabIntoView(Workspace workspace) => ScrollIntoStrip(TabStrip, TabScroller, workspace, workspace.Title);

    /// <summary>
    /// Подвинуть ленту так, чтобы ярлык был виден целиком. Своя прокрутка ListBox тут
    /// не работает: список лежит внутри общего ScrollViewer и получает всю нужную ширину,
    /// так что ездит именно внешний ScrollViewer — <see cref="System.Windows.Controls.ListBox.ScrollIntoView"/>
    /// на нём ничего не делает. Это единственное место, которое двигает ленту само, —
    /// см. <see cref="OnTabStripBringIntoView"/>.
    /// </summary>
    private void ScrollIntoStrip(System.Windows.Controls.ListBox strip, System.Windows.Controls.ScrollViewer scroller, object item, string title)
    {
        // при запуске ярлыки ещё не созданы — ждём, пока разложится лента
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (strip.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement tab
                || !scroller.IsAncestorOf(tab))
            {
                return;
            }

            var left = tab.TransformToAncestor(scroller).Transform(new Point(0, 0)).X + scroller.HorizontalOffset;
            var right = left + tab.ActualWidth;
            var before = scroller.HorizontalOffset;

            if (left < scroller.HorizontalOffset)
            {
                scroller.ScrollToHorizontalOffset(Math.Max(0, left - 8));
            }
            else if (right > scroller.HorizontalOffset + scroller.ViewportWidth)
            {
                scroller.ScrollToHorizontalOffset(right - scroller.ViewportWidth + 8);
            }
            else
            {
                return;
            }

            AppLog.Debug($"Лента подкручена к «{title}»: была сдвинута на {before:0}, ярлык {left:0}–{right:0}, видно {scroller.ViewportWidth:0}.");
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// WPF сам просит ленту «показать» то, что получило фокус. Когда фокус встаёт на весь список
    /// ярлыков, а не на один, просьба — показать список целиком, и лента отскакивала в начало:
    /// под мышью оказывались другие вкладки, будто они поменялись местами. Ленту двигает только
    /// выбор вкладки (<see cref="ScrollIntoStrip"/>), такие просьбы пропускаем.
    /// </summary>
    private void OnTabStripBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        e.Handled = true;

        var target = e.TargetObject is FrameworkElement { DataContext: Workspace or ServerSet } element
            && element is System.Windows.Controls.ListBoxItem
            ? $"ярлык «{(element.DataContext as Workspace)?.Title ?? (element.DataContext as ServerSet)?.Title}»"
            : e.TargetObject?.GetType().Name ?? "?";

        AppLog.Debug($"Лента: просьба WPF показать {target} пропущена.");
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

    /// <summary>Доска вкладок: те же ярлыки кирпичами, но в отдельном растягиваемом окне.</summary>
    private void OnOpenBoard(object sender, RoutedEventArgs e) => TabBoardWindow.Open(this);

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
        AppLog.Info(on
            ? "Умный режим включён: спросит, если сервер непривычный для команды или вкладки."
            : "Умный режим выключен.");
    }

    /// <summary>«Все» в переключателе наборов: в панели открытой вкладки серверы всех наборов.</summary>
    private void OnPickAllSets(object sender, RoutedEventArgs e)
    {
        if (!State.PaneAllSets)
        {
            State.PaneAllSets = true;
            AppLog.Info($"Целевые сервера — из всех наборов: {State.PaneServers.Count}.", State.ActiveWorkspace.Title);
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
    /// Ярлыки вкладок и наборов переставляются мышью, как в браузере. Первый ярлык
    /// в каждой ленте закреплён.
    /// </summary>
    private void SetupTabReorder()
    {
        _ = new Controls.TabReorder(
            TabStrip,
            TabScroller,
            item => item is Workspace { IsMain: false },
            fixedCount: 1,
            (item, to) =>
            {
                if (item is Workspace workspace && State.MoveWorkspace(workspace, to))
                {
                    // перестановка сбивает выделение списка — возвращаем его на текущую вкладку
                    TabStrip.SelectedItem = State.ActiveWorkspace;
                }
            });

        _ = new Controls.TabReorder(
            SetStrip,
            SetScroller,
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

    // ── вид ленты: полные ярлыки или краткие в два ряда ──────────────────────────

    private System.Windows.Controls.ItemsPanelTemplate? _wideTabsPanel;
    private DataTemplate? _wideTabTemplate;
    private DataTemplate? _wideSetTemplate;

    /// <summary>
    /// Полные ярлыки описаны прямо в разметке лент — их запоминаем, краткие берём из ресурсов.
    /// Подменяются раскладка и шаблон ярлыка; рамка и отступы ярлыка следят за настройкой сами.
    /// </summary>
    private void SetupTabView()
    {
        _wideTabsPanel = TabStrip.ItemsPanel;
        _wideTabTemplate = TabStrip.ItemTemplate;
        _wideSetTemplate = SetStrip.ItemTemplate;

        // лента не уже окна — иначе личным вкладкам не к чему прижаться справа
        TabScroller.SizeChanged += (_, _) => TabStrip.MinWidth = TabScroller.ActualWidth;

        ApplyTabView();

        State.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppState.CompactTabs))
            {
                ApplyTabView();
            }
            else if (e.PropertyName == nameof(AppState.Section))
            {
                SwitchBottomHeight();
            }
        };
    }

    private void ApplyTabView()
    {
        var compact = State.CompactTabs;
        var panel = compact ? (System.Windows.Controls.ItemsPanelTemplate)FindResource("CompactTabsPanel") : _wideTabsPanel;

        TabStrip.ItemsPanel = panel;
        TabStrip.ItemTemplate = compact ? (DataTemplate)FindResource("CompactTabTemplate") : _wideTabTemplate;
        SetStrip.ItemsPanel = panel;
        SetStrip.ItemTemplate = compact ? (DataTemplate)FindResource("CompactSetTemplate") : _wideSetTemplate;

        CompactButton.Appearance = compact ? ControlAppearance.Primary : ControlAppearance.Secondary;
        CompactButton.ToolTip = compact
            ? "Краткие ярлыки включены. Нажмите, чтобы вернуть полные — со счётчиками серверов и отметок"
            : "Краткие ярлыки: только название и статус, мельче и в два ряда";

        // ярлыки переложены заново — открытая вкладка должна остаться на виду
        ScrollTabIntoView(State.ActiveWorkspace);
    }

    private void OnToggleCompactTabs(object sender, RoutedEventArgs e) => State.CompactTabs = !State.CompactTabs;

    // ── своя полоса прокрутки под ярлыками ───────────────────────────────────────

    private bool _syncingTabBar;

    /// <summary>Лента открытого раздела: на «Серверах» — наборы, на «Копировании» и «Команде» — вкладки.</summary>
    private System.Windows.Controls.ScrollViewer ActiveScroller => State.ShowsServerSetTabs ? SetScroller : TabScroller;

    private void OnTabStripScrolled(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        // сдвиг самой ленты — в отладку: по нему видно, кто и когда её двигает
        if (ReferenceEquals(e.OriginalSource, sender) && Math.Abs(e.HorizontalChange) > 0.5)
        {
            AppLog.Debug($"Лента сдвинулась: {e.HorizontalOffset - e.HorizontalChange:0} → {e.HorizontalOffset:0}.");
        }

        SyncTabScrollBar();
    }

    private void OnTabScrollerVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => SyncTabScrollBar();

    /// <summary>Ползунок повторяет ленту: видна она вся — полосы нет, иначе ползунок там, где лента.</summary>
    private void SyncTabScrollBar()
    {
        var scroller = ActiveScroller;
        var overflow = scroller.IsVisible && scroller.ScrollableWidth > 0.5;

        TabScrollBar.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;

        if (!overflow)
        {
            return;
        }

        _syncingTabBar = true;
        TabScrollBar.Maximum = scroller.ScrollableWidth;
        TabScrollBar.LargeChange = Math.Max(1, scroller.ViewportWidth);
        TabScrollBar.Value = scroller.HorizontalOffset;
        _syncingTabBar = false;
    }

    /// <summary>Ползунок тащат — лента едет следом.</summary>
    private void OnTabScrollBarChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_syncingTabBar && TabScrollBar.IsVisible)
        {
            AppLog.Debug($"Ползунок ленты: {e.OldValue:0} → {e.NewValue:0}.");
            ActiveScroller.ScrollToHorizontalOffset(e.NewValue);
        }
    }

    /// <summary>
    /// Колесо над полосой прокрутки листает ленту вбок. Над самими ярлыками это делает общий
    /// <see cref="WheelScroll"/>, но полоса лежит рядом с лентой, а не внутри неё — сама он её не найдёт.
    /// </summary>
    private void OnTabStripWheel(object sender, MouseWheelEventArgs e)
    {
        var scroller = ActiveScroller;
        scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>Правый щелчок по ярлыку только открывает меню ленты, вкладку не переключает.</summary>
    private void OnTabStripRightDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    /// <summary>Карандаш в нижнем углу ярлыка.</summary>
    private void OnRenameTabClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is FrameworkElement { DataContext: Workspace workspace })
        {
            Rename(workspace);
        }
        else if (sender is FrameworkElement { DataContext: ServerSet set })
        {
            Rename(set);
        }
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
            AppLog.Info($"Команды после копирования выключены: «{copy.Title}».");
            return;
        }

        if (copy.Personal is { } personal)
        {
            AppLog.Info($"Команды после копирования включены: «{copy.Title}».");
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

    private void OnOpenSettings(object sender, RoutedEventArgs e) => RootNavigation.Navigate(typeof(SettingsPage));

    /// <summary>Открыть вкладку в её разделе: сначала выбрать её, потом перейти в раздел.</summary>
    private void ShowTab(Workspace tab, Type page)
    {
        State.ActiveWorkspace = tab;
        RootNavigation.Navigate(page);
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
            AppLog.Info("Личная вкладка называется как её вкладка копирования — переименуйте ту.");
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
        BottomPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        BottomSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        BottomRow.Height = new GridLength(visible ? StoredBottom() : 0);
    }

    /// <summary>Нижняя панель сейчас стоит с высотой проводника.</summary>
    private bool _explorerBottom;

    /// <summary>
    /// Проводнику окно нужно под две панели, а не под вывод: у него своя высота терминала,
    /// по умолчанию пятая часть. Остальные разделы делят одну общую.
    /// </summary>
    private void SwitchBottomHeight()
    {
        var explorer = State.Section == AppSection.Explorer;

        if (explorer == _explorerBottom)
        {
            return;
        }

        RememberBottom();
        _explorerBottom = explorer;

        if (BottomPanel.Visibility == Visibility.Visible)
        {
            BottomRow.Height = new GridLength(StoredBottom());
        }
    }

    /// <summary>Что натянул разделителем — запоминается для своего вида: проводника или остальных.</summary>
    private void RememberBottom()
    {
        if (BottomPanel.Visibility != Visibility.Visible || BottomRow.ActualHeight <= 40)
        {
            return;
        }

        if (_explorerBottom)
        {
            AppState.Current.Settings.ExplorerLogPanelHeight = BottomRow.ActualHeight;
        }
        else
        {
            AppState.Current.Settings.LogPanelHeight = BottomRow.ActualHeight;
        }
    }

    private double StoredBottom()
    {
        var settings = AppState.Current.Settings;

        if (_explorerBottom)
        {
            if (settings.ExplorerLogPanelHeight >= 120)
            {
                return settings.ExplorerLogPanelHeight;
            }

            // пятая часть того, что делят страница и панель; до первой раскладки — от высоты окна
            var page = ((System.Windows.Controls.Grid)BottomPanel.Parent).RowDefinitions[2].ActualHeight;
            var shared = page > 0 ? page + BottomRow.ActualHeight : Height - 120;

            return Math.Max(120, Math.Round(shared / 5));
        }

        // панель не должна съедать кнопки запуска на странице копирования:
        // при окне по умолчанию 220 точек — предел, который она может занять
        return settings.LogPanelHeight < 120 ? 220 : settings.LogPanelHeight;
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
