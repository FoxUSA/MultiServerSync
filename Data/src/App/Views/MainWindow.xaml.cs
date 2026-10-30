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

        // там же, где было при выходе, — если это место ещё на экране (монитор могли отключить)
        if (settings.WindowLeft is { } left && settings.WindowTop is { } top && OnScreen(left, top))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }

        if (settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }

        _maximized = settings.WindowMaximized;
        StateChanged += (_, _) =>
        {
            // свёрнутое окно помнит, каким было до этого
            if (WindowState != WindowState.Minimized)
            {
                _maximized = WindowState == WindowState.Maximized;
            }
        };

        ApplyTheme(settings.Theme);

        BottomRow.Height = new GridLength(Math.Max(0, settings.LogPanelHeight));
        SetBottomVisible(settings.LogPanelVisible);

        // натянутое разделителем запоминается сразу: от него считается доля при смене рядов ленты
        BottomSplitter.DragCompleted += (_, _) => RememberBottom();

        if (settings.BottomOnJournal)
        {
            JournalTab.IsChecked = true;
        }

        // умный режим спрашивает посреди окна; фокус на «Отменить», так что случайный Enter не запустит
        SmartSwitch.IsChecked = settings.SmartMode;
        RunHistory.Choose = (headline, detail, confirm, extra) =>
            ConfirmDialog.Choose(this, headline, detail, confirm, extra, "Отменить");

        // операция запущена — сразу показываем терминал, чтобы вывод был перед глазами;
        // не начата — причина плашкой, журнал сам не открывается
        AppState.Current.TerminalRequested += ShowTerminal;

        // окно вывода закрыли сами — при следующем запуске вывод в панели; закрылось при выходе — откроется снова
        OutputWindow.OpenChanged += () =>
        {
            if (!App.Quitting)
            {
                settings.OutputDetached = OutputWindow.IsOpen;
            }
        };

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

        // лента наборов — основная или архив — идёт за набором выбранной таблицы, как лента вкладок
        SetupSetView();
        AppState.Current.ActiveServerSetChanged += set =>
        {
            if (set.IsArchived)
            {
                _lastArchivedSet = set;
            }
            else
            {
                _lastMainSet = set;
            }

            FollowActiveSet();
        };
        AppState.Current.SetArchiveChanged += _ => FollowActiveSet();

        // ряд разделов следит за открытым разделом: в раздел попадают и не кнопкой (из вкладки, из проводника)
        State.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppState.Section))
            {
                ShowSection();
            }
        };
        ShowSection();
        SetupSectionMarks();

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

        // срок жизни вывода: окошко отсчитывает, на нуле терминал вкладки очищается
        _ = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromSeconds(1),
            System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => State.TickOutputLife(),
            Dispatcher);
        SetupTray();

        AppState.Current.OperationFinished += OnOperationFinished;
        AppState.Current.OperationFinished += FinishSound.OnFinished;

        Loaded += (_, _) =>
        {
            TabStrip.SelectedItem = State.ActiveWorkspace;
            SetStrip.SelectedItem = State.ActiveServerSet;

            // раздел, где были при выходе; вкладки разделов и всё в них восстанавливает AppState
            Navigate(PageOf(settings.LastSection));

            // вынесенные окна вывода и запусков — снова отдельно, как были
            Terminal.RestoreDetached(settings.OutputDetached, settings.RunsDetached);

            // записи загрузки плашкой не показываем — только то, что сделано после открытия окна
            AppLog.Written += ShowToast;

            // файл длинного лога в буфере живёт, пока буфер не сменится
            ClipboardText.Watch(this);
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

        // сменили раздел — прежняя плашка уже не про то, что на экране
        HideToasts();
        PageFrame.Navigate(page);
    }

    /// <summary>Страница раздела по имени, сохранённому при выходе; незнакомое — «Серверы».</summary>
    private static Type PageOf(string section) =>
        Enum.TryParse<AppSection>(section, out var parsed)
            ? parsed switch
            {
                AppSection.Explorer => typeof(ExplorerPage),
                AppSection.Command => typeof(CommandPage),
                AppSection.Files => typeof(FilesPage),
                AppSection.Download => typeof(DownloadPage),
                AppSection.Settings => typeof(SettingsPage),
                _ => typeof(ServersPage),
            }
            : typeof(ServersPage);

    /// <summary>Окно, развёрнутое на весь экран, — с учётом того, что сейчас оно может быть свёрнуто.</summary>
    private bool _maximized;

    /// <summary>Заголовок окна в этом месте попадает на какой-нибудь из мониторов.</summary>
    private static bool OnScreen(double left, double top) =>
        left + 100 > SystemParameters.VirtualScreenLeft
        && left + 100 < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
        && top >= SystemParameters.VirtualScreenTop - 8
        && top + 30 < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;

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

        // в «Настройках» разовых действий нет — и настраивать нечего
        OutputPrefsButton.Visibility = section == AppSection.Settings ? Visibility.Collapsed : Visibility.Visible;

        // в раздел пришли — его ошибки увидены
        _unseenFailures.RemoveWhere(tab => State.SectionOfTab(tab) == section);
        ShowSectionMarks();
    }

    /// <summary>Вкладки, кончившие ошибкой, пока их раздел был не на экране. Гаснут, когда в раздел заходят.</summary>
    private readonly HashSet<Workspace> _unseenFailures = new();

    /// <summary>Кнопки разделов следят за статусами вкладок всех разделов, и уже открытых, и новых.</summary>
    private void SetupSectionMarks()
    {
        foreach (var tabs in new[] { State.Workspaces, State.CommandTabs, State.DownloadTabs, State.ExplorerTabs })
        {
            foreach (var tab in tabs)
            {
                tab.PropertyChanged += OnTabStatusChanged;
            }

            tabs.CollectionChanged += (_, e) =>
            {
                foreach (Workspace tab in e.OldItems ?? Array.Empty<Workspace>())
                {
                    tab.PropertyChanged -= OnTabStatusChanged;
                    _unseenFailures.Remove(tab);
                }

                foreach (Workspace tab in e.NewItems ?? Array.Empty<Workspace>())
                {
                    tab.PropertyChanged += OnTabStatusChanged;
                }

                ShowSectionMarks();
            };
        }

        ShowSectionMarks();
    }

    private void OnTabStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Workspace.Status) || sender is not Workspace tab)
        {
            return;
        }

        if (tab.Status is WorkspaceStatus.Failed or WorkspaceStatus.Partial && State.SectionOfTab(tab) != State.Section)
        {
            _unseenFailures.Add(tab);
        }
        else
        {
            _unseenFailures.Remove(tab);
        }

        ShowSectionMarks();
    }

    /// <summary>
    /// Рамка на кнопке раздела, которого нет на экране: красная — там что-то кончилось ошибкой
    /// без человека, иначе зелёная, пока там что-то идёт. У открытого раздела рамки нет — всё видно на ярлыках.
    /// </summary>
    private void ShowSectionMarks()
    {
        Mark(ExplorerSection, AppSection.Explorer, State.ExplorerTabs);
        Mark(CommandSection, AppSection.Command, State.CommandTabs);
        Mark(FilesSection, AppSection.Files, State.Workspaces);
        Mark(DownloadSection, AppSection.Download, State.DownloadTabs);

        void Mark(DependencyObject button, AppSection section, IEnumerable<Workspace> tabs)
        {
            var status = WorkspaceStatus.None;

            if (State.Section != section)
            {
                if (tabs.Any(_unseenFailures.Contains))
                {
                    status = WorkspaceStatus.Failed;
                }
                else if (tabs.Any(t => t.Status == WorkspaceStatus.Running))
                {
                    status = WorkspaceStatus.Running;
                }
            }

            Controls.SectionMark.SetStatus(button, status);
        }
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

        if (!_tuneHooked)
        {
            // тема сменилась сама (как в системе) — поправки следом
            _tuneHooked = true;
            ApplicationThemeManager.Changed += (_, _) => TuneTheme();
        }

        TuneTheme();
    }

    private static bool _tuneHooked;

    /// <summary>Кисти, которые программа подменяет в светлой теме; в тёмной их нет — всё из темы.</summary>
    private static readonly string[] LightTuned =
        ["TextFillColorSecondaryBrush", "TextFillColorTertiaryBrush", "TextFillColorDisabledBrush"];

    /// <summary>
    /// Светлая тема WPF UI рисует вторичный и третичный текст полупрозрачным чёрным — на белом
    /// он выходит бледнее, чем такой же белый на тёмном: подписи сливаются с фоном. В светлой
    /// теме эти кисти темнее, а полоса строк таблиц — лёгкое затемнение вместо белого на сером.
    /// Тёмная тема не меняется: подмены убираются, и кисти снова берутся из темы.
    /// </summary>
    private static void TuneTheme()
    {
        var resources = Application.Current.Resources;
        var light = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light;

        foreach (var key in LightTuned)
        {
            resources.Remove(key);
        }

        resources.Remove("AppRowAltBrush");

        if (light)
        {
            resources["TextFillColorSecondaryBrush"] = Frozen(0xC4, 0x00);
            resources["TextFillColorTertiaryBrush"] = Frozen(0x9E, 0x00);
            resources["TextFillColorDisabledBrush"] = Frozen(0x80, 0x00);
            resources["AppRowAltBrush"] = Frozen(0x09, 0x00);
        }
        else
        {
            // тёмная — как была: полоса строк та же кисть темы, что и раньше
            resources["AppRowAltBrush"] = Application.Current.TryFindResource("ControlFillColorDefaultBrush");
        }

        static SolidColorBrush Frozen(byte alpha, byte gray)
        {
            var brush = new SolidColorBrush(Color.FromArgb(alpha, gray, gray, gray));
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>Повторный запуск программы: вместо второго окна — ещё одна вкладка в этом.</summary>
    public void OpenWorkspaceFromNewInstance()
    {
        // окно могло быть спрятано в трей
        ShowFromTray();
        AddWorkspace();
    }

    private void AddWorkspace(bool archived = false)
    {
        State.ActiveWorkspace = State.AddWorkspace(archived: archived);
    }

    private void OnAddTab(object sender, RoutedEventArgs e) => AddTab();

    /// <summary>Плюс и Ctrl+T добавляют вкладку того раздела, который открыт, — в ту ленту, что на экране.</summary>
    private void AddTab()
    {
        if (State.ShowsServerSetTabs)
        {
            State.ActiveServerSet = State.AddServerSet(archived: _setArchiveMode);
        }
        else if (State.ShowsWorkspaceTabs)
        {
            AddWorkspace(_archiveMode);
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

    /// <summary>Шестерёнка у «Терминал / Журнал»: галочки открытого раздела, как сохранены.</summary>
    private void OnOutputPrefs(object sender, RoutedEventArgs e)
    {
        var section = State.Section;
        var prefs = State.ActionOutputOf(section);

        OutputPrefsTitle.Text = $"ЧТО ПИСАТЬ · {LogEntry.NameOf(section).ToUpperInvariant()}";
        ServerTerminalBox.IsChecked = prefs.ServerTerminal;
        ServerJournalBox.IsChecked = prefs.ServerJournal;
        ComputerTerminalBox.IsChecked = prefs.ComputerTerminal;
        ComputerJournalBox.IsChecked = prefs.ComputerJournal;
        ProgramTerminalBox.IsChecked = prefs.ProgramTerminal;
        ProgramJournalBox.IsChecked = prefs.ProgramJournal;

        // только те строки, что в разделе бывают
        var (server, computer) = OutputKinds(section);
        ShowRow(server, ServerRowLabel, ServerTerminalBox, ServerJournalBox);
        ShowRow(computer, ComputerRowLabel, ComputerTerminalBox, ComputerJournalBox);

        OutputPrefsPopup.IsOpen = true;

        static void ShowRow(bool on, params UIElement[] cells)
        {
            foreach (var cell in cells)
            {
                cell.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    /// <summary>
    /// Чьи разовые действия бывают в разделе. «Программа» есть везде, кроме «Настроек»: там
    /// действий нет вовсе, и шестерёнки тоже нет. Сервер — где действуют на сервере
    /// (правая панель «Проводника», строки скачивания, сами серверы), Компьютер — где на этом ПК.
    /// </summary>
    private static (bool Server, bool Computer) OutputKinds(AppSection section) => section switch
    {
        AppSection.Explorer => (true, true),
        AppSection.Files => (false, true),
        AppSection.Download => (true, false),
        AppSection.Servers => (true, false),
        _ => (false, false),
    };

    /// <summary>Галочку переключили — раздел запоминает сразу, без кнопки «Сохранить».</summary>
    private void OnOutputPrefChanged(object sender, RoutedEventArgs e)
    {
        State.SetActionOutput(State.Section, prefs =>
        {
            prefs.ServerTerminal = ServerTerminalBox.IsChecked == true;
            prefs.ServerJournal = ServerJournalBox.IsChecked == true;
            prefs.ComputerTerminal = ComputerTerminalBox.IsChecked == true;
            prefs.ComputerJournal = ComputerJournalBox.IsChecked == true;
            prefs.ProgramTerminal = ProgramTerminalBox.IsChecked == true;
            prefs.ProgramJournal = ProgramJournalBox.IsChecked == true;
        });
    }

    /// <summary>
    /// Галочки «авто» в терминале: вывод сам уходит в буфер.
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

        var copying = State.IsCopyBatch(batch);
        var finished = batch.OperationName.StartsWith(Services.Workspace.CompareName, StringComparison.OrdinalIgnoreCase) ? "Сравнение закончилось"
            : copying ? "Копирование закончилось"
            : "Команда закончилась";

        // «авто» у каждой вкладки своя — смотрим вкладку операции, а не ту, что открыта.
        // «Авто» одна на обе кнопки: несколько серверов — вывод всех, один — его же.
        // Считаются серверы запуска: в таблице могут лежать и строки разовых действий
        if (State.TabOfBatch(batch).AutoCopy)
        {
            if (batch.LastRun.Count > 1)
            {
                Terminal.CopyAll(batch, finished);
            }
            else
            {
                Terminal.CopyCurrentAfter(batch, finished);
            }
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

    /// <summary>Замок в строке целевого сервера: закрепить вкладку за ним или снять замок.</summary>
    private void OnPaneLockClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is FrameworkElement { DataContext: Models.ServerCheck check })
        {
            State.ActiveWorkspace.TogglePaneLock(check);
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

        // наборы переставляются все, закреплённого нет; место в ленте → место в списке, как у вкладок
        _ = new Controls.TabReorder(
            SetStrip,
            null,
            item => item is ServerSet,
            fixedCount: 0,
            (item, to) =>
            {
                if (item is ServerSet set
                    && SetStrip.Items.Count > 0
                    && SetStrip.Items[Math.Min(to, SetStrip.Items.Count - 1)] is ServerSet anchor
                    && State.MoveServerSet(set, State.ServerSets.IndexOf(anchor)))
                {
                    SetStrip.SelectedItem = State.ActiveServerSet;
                }
            })
        {
            OutTarget = ArchiveButton,
            DroppedOut = item =>
            {
                if (item is ServerSet set && State.SetArchived(set, !set.IsArchived))
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

            if (e.PropertyName is nameof(AppState.Section) or nameof(AppState.ActiveWorkspace))
            {
                ShowExplorerServer();
            }
        };

        ApplyServerPane();
        ShowTabs();
        ShowExplorerServer();
    }

    /// <summary>
    /// Карточки «Целевые сервера» нет у «Проводника» (сервер выбирается в самой правой панели)
    /// и у «Серверов»: там правят сами наборы, а целевые серверы — дело вкладок других разделов.
    /// </summary>
    private void ApplyServerPane()
    {
        var hidden = State.Section is AppSection.Explorer or AppSection.Servers;
        PaneCard.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        PaneColumn.Width = new GridLength(hidden ? 0 : 220);
    }

    /// <summary>Вкладка, за сменой сервера которой следит подпись в ряду разделов.</summary>
    private Workspace? _explorerTagTab;

    /// <summary>
    /// Подпись сервера у правого края ряда разделов. «Проводник» — сервер правой панели;
    /// «Копирование», «Команда», «Скачивание» — отмеченный целевой сервер, а отмечено несколько —
    /// их число. Щелчок — тот же список серверов.
    /// </summary>
    private void ShowExplorerServer()
    {
        var tab = State.Section is AppSection.Explorer or AppSection.Files or AppSection.Command or AppSection.Download
            && State.Tabs.Count > 0
            ? State.ActiveWorkspace
            : null;

        if (!ReferenceEquals(tab, _explorerTagTab))
        {
            if (_explorerTagTab is not null)
            {
                _explorerTagTab.PropertyChanged -= OnExplorerTagTabChanged;
            }

            _explorerTagTab = tab;

            if (tab is not null)
            {
                tab.PropertyChanged += OnExplorerTagTabChanged;
            }
        }

        if (tab is null)
        {
            ExplorerServerTag.Visibility = Visibility.Collapsed;
            return;
        }

        var explorer = State.Section == AppSection.Explorer;
        var picked = explorer
            ? (tab.ExplorerServer is { } one ? [one] : [])
            : tab.SelectedServers;
        var server = picked.Count == 1 ? picked[0] : null;

        ExplorerServerIcon.Source ??= ShellIcons.Server;
        ExplorerServerName.Text = server?.PaneLabel ?? (picked.Count > 1 ? $"Серверов: {picked.Count}" : "Сервер не выбран");
        ExplorerServerUser.Text = server is null || string.IsNullOrWhiteSpace(server.User) ? string.Empty : $"({server.User})";
        ExplorerServerAddress.Text = server is null ? string.Empty : $"{server.Host}:{server.Port}";
        ExplorerServerTag.ToolTip = explorer
            ? "Сервер правой панели — щелчок открывает список серверов"
            : picked.Count > 1
                ? "Отмечены: " + string.Join(", ", picked.Select(s => s.PaneLabel)) + ". Щелчок — список серверов"
                : "Целевой сервер вкладки — щелчок открывает список серверов";
        ExplorerServerTag.Visibility = Visibility.Visible;
    }

    private void OnExplorerTagTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        // отметки серверов поднимают и то и другое — подпись перечитывается по любому
        if (e.PropertyName is nameof(Workspace.ExplorerServer) or nameof(Workspace.SelectionInfo))
        {
            ShowExplorerServer();
        }
    }

    private void OnExplorerServerTag(object sender, RoutedEventArgs e)
    {
        if (_explorerTagTab is not { } tab)
        {
            return;
        }

        if (State.Section == AppSection.Explorer)
        {
            Controls.ServerMenu.Show(ExplorerServerTag, tab.ExplorerServer, tab.PickExplorerServer);
            return;
        }

        var picked = tab.SelectedServers;
        Controls.ServerMenu.Show(ExplorerServerTag, s => picked.Contains(s), tab.PickTargetServer);
    }

    /// <summary>Высота ленты ярлыков сейчас и самая низкая за сеанс — в один ряд.</summary>
    private double _stripHeight;
    private double _stripMin = double.MaxValue;

    /// <summary>Насколько лента сейчас выше одного ряда.</summary>
    private double StripExtra => Math.Max(0, _stripHeight - _stripMin);

    /// <summary>
    /// Ярлыки встали вторым рядом (или ряд ушёл) — лента выросла вниз. Страница сама ужимается
    /// на всю разницу, а нижняя панель стоит своей высотой; чтобы обе ужались поровну, нижней
    /// отдаём её долю разницы. Считается каждый раз от высоты, натянутой разделителем, а не от
    /// прошлой: прибавками панель от запуска к запуску сама росла.
    /// </summary>
    private void OnTabStripAreaSized(object sender, SizeChangedEventArgs e)
    {
        if (!e.HeightChanged || e.NewSize.Height <= 0)
        {
            return;
        }

        _stripHeight = e.NewSize.Height;
        _stripMin = Math.Min(_stripMin, _stripHeight);

        if (BottomPanel.Visibility == Visibility.Visible && BottomRow.Height.IsAbsolute)
        {
            BottomRow.Height = new GridLength(BottomFor(StoredBottom()));
        }
    }

    /// <summary>Высота нижней панели при нынешней ленте: натянутая минус её доля лишних рядов ленты.</summary>
    private double BottomFor(double stored)
    {
        var extra = StripExtra;
        var total = PageRow.ActualHeight + BottomRow.ActualHeight;

        return extra <= 0 || total <= 0 ? stored : Math.Max(0, stored - extra * stored / (total + extra));
    }

    /// <summary>
    /// Правый щелчок по ярлыку вкладку не переключает, а переносит её в другую ленту: из основной —
    /// в архив, из архива — обратно. Ярлык улетает к значку. У наборов серверов архива нет.
    /// </summary>
    private void OnTabStripRightDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        if (ReferenceEquals(sender, SetStrip))
        {
            if (System.Windows.Controls.ItemsControl.ContainerFromElement(SetStrip, e.OriginalSource as DependencyObject)
                is System.Windows.Controls.ListBoxItem { DataContext: ServerSet set } box)
            {
                var flightSet = FlyToArchive(box);

                if (!State.SetArchived(set, !set.IsArchived))
                {
                    flightSet?.Invoke();
                }
            }

            return;
        }

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

    // ── архив наборов серверов: та же вторая лента под «+», что у вкладок ─────────

    private ListCollectionView? _setView;

    /// <summary>Лента наборов показывает архив, а не основные наборы.</summary>
    private bool _setArchiveMode;

    /// <summary>Последний открытый набор основной ленты и архива.</summary>
    private ServerSet? _lastMainSet;
    private ServerSet? _lastArchivedSet;

    private void SetupSetView()
    {
        _setView = new ListCollectionView(State.ServerSets)
        {
            Filter = item => item is ServerSet set && set.IsArchived == _setArchiveMode,
            IsLiveFiltering = true,
        };
        _setView.LiveFilteringProperties.Add(nameof(ServerSet.IsArchived));
        ((System.Collections.Specialized.INotifyCollectionChanged)_setView).CollectionChanged += (_, _) => UpdateArchiveButton();

        SetStrip.IsSynchronizedWithCurrentItem = false;
        _setArchiveMode = State.ServerSets.Count > 0 && State.ActiveServerSet.IsArchived;
        _setView.Refresh();
        SetStrip.ItemsSource = _setView;
    }

    /// <summary>Лента наборов идёт за набором выбранной таблицы: он в архиве — показан архив.</summary>
    private void FollowActiveSet()
    {
        var archive = State.ActiveServerSet.IsArchived;

        if (archive != _setArchiveMode)
        {
            _setArchiveMode = archive;
            _setView?.Refresh();
        }

        SetStrip.SelectedItem = State.ActiveServerSet;
        UpdateArchiveButton();
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
        var sets = State.ShowsServerSetTabs;
        var shown = State.ShowsWorkspaceTabs || sets;
        var mode = sets ? _setArchiveMode : _archiveMode;
        var archived = sets ? State.ServerSets.Count(s => s.IsArchived)
            : shown ? State.Tabs.Count(t => t.IsArchived) : 0;
        var what = sets ? "набор" : "вкладку";

        ArchiveButton.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        ArchiveRow.Height = shown ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ArchiveIcon.Symbol = mode ? SymbolRegular.TabArrowLeft24 : SymbolRegular.Archive24;
        ArchiveCount.Text = archived.ToString();
        ArchiveCount.Visibility = archived > 0 && !mode ? Visibility.Visible : Visibility.Collapsed;
        ArchiveButton.Opacity = archived > 0 || mode ? 1 : 0.6;
        ArchiveButton.Appearance = _dropOver ? ControlAppearance.Success
            : mode ? ControlAppearance.Primary
            : ControlAppearance.Secondary;
        ArchiveButton.ToolTip = mode
            ? $"Открыт архив. Щелчок — назад к основной ленте. Правый щелчок по ярлыку или перетаскивание сюда — вернуть {what} в основную ленту"
            : $"Архив: щелчок — открыть. Убрать сюда {what} — правый щелчок по ярлыку или перетащить ярлык на этот значок";
    }

    /// <summary>Значок переключает ленту: основная ↔ архив. Открывается вкладка, что была открыта там последней.</summary>
    private void OnArchiveClick(object sender, RoutedEventArgs e)
    {
        if (State.ShowsServerSetTabs)
        {
            ToggleSetArchive();
            return;
        }

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

    /// <summary>Значок на «Серверах»: основная лента наборов ↔ архив, в выбранной таблице — последний открытый там набор.</summary>
    private void ToggleSetArchive()
    {
        var sets = State.ServerSets;

        if (_setArchiveMode)
        {
            State.ActiveServerSet = _lastMainSet is { IsArchived: false } back && sets.Contains(back)
                ? back
                : sets.First(s => !s.IsArchived);
            return;
        }

        var archived = sets.Where(s => s.IsArchived).ToList();

        if (archived.Count == 0)
        {
            AppLog.Hint("Архив пуст. Сюда набор убирает правый щелчок по ярлыку или перетаскивание на этот значок.");
            return;
        }

        State.ActiveServerSet = _lastArchivedSet is { IsArchived: true } last && sets.Contains(last)
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

    /// <summary>Плашка о нажатии: у главной кнопки открытой страницы, а где своей нет — в правом нижнем углу.</summary>
    private void ShowToast(LogEntry entry)
    {
        var target = PageFrame.Content is Controls.IToastHost host and FrameworkElement { IsVisible: true }
            ? host.PageToast
            : Toast;

        // плашка одна на окно: новая гасит прежнюю, где бы та ни стояла
        if (target.Show(entry))
        {
            HideToasts(except: target);
        }
    }

    /// <summary>Погасить плашки — угловую и плашки страниц, кроме указанной.</summary>
    private void HideToasts(Controls.ActionToast? except = null)
    {
        foreach (var toast in _pages.Values.OfType<Controls.IToastHost>().Select(h => h.PageToast).Append(Toast))
        {
            if (!ReferenceEquals(toast, except))
            {
                toast.HideNow();
            }
        }
    }

    /// <summary>Запуск без отмеченных серверов: строки целевых серверов мигают тусклым оранжевым.</summary>
    public void FlashTargets()
    {
        foreach (var item in PaneServerList.Items)
        {
            if (PaneServerList.ItemContainerGenerator.ContainerFromItem(item) is not System.Windows.Controls.ContentPresenter presenter
                || presenter.ContentTemplate?.FindName("Item", presenter) is not System.Windows.Controls.Border row)
            {
                continue;
            }

            var brush = new SolidColorBrush(Color.FromArgb(0, 0xE0, 0x7A, 0x1F));
            var blink = new ColorAnimation(Color.FromArgb(0x50, 0xE0, 0x7A, 0x1F), TimeSpan.FromMilliseconds(180))
            {
                AutoReverse = true,
                RepeatBehavior = new RepeatBehavior(2),
            };

            // после вспышки — снова фон шаблона, с его подсветкой под мышью
            blink.Completed += (_, _) => row.ClearValue(System.Windows.Controls.Border.BackgroundProperty);
            row.Background = brush;
            brush.BeginAnimation(SolidColorBrush.ColorProperty, blink);
        }
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
            ActionLog.Info($"Команды после копирования выключены: «{copy.Title}»", null, AppSection.Files, copy);
            return;
        }

        if (copy.Personal is { } personal)
        {
            ActionLog.Info($"Команды после копирования включены: «{copy.Title}»", null, AppSection.Files, copy);
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

    /// <summary>Карандаш в правом нижнем углу ярлыка — то же, что двойной щелчок.</summary>
    private void OnRenameTabClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        switch (sender)
        {
            case FrameworkElement { DataContext: Workspace workspace }:
                Rename(workspace);
                break;
            case FrameworkElement { DataContext: ServerSet set }:
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

        BottomRow.Height = new GridLength(visible ? BottomFor(StoredBottom()) : 0);
    }

    /// <summary>
    /// Высота нижней панели одна на все разделы, как у панели в VS Code: при переходе между
    /// разделами она не прыгает. Что натянул разделителем — то и запоминается.
    /// </summary>
    private void RememberBottom()
    {
        if (BottomPanel.Visibility != Visibility.Visible || BottomRow.ActualHeight <= 40)
        {
            return;
        }

        // запоминается высота при ленте в один ряд — обратный пересчёт к BottomFor
        var bottom = BottomRow.ActualHeight;
        var extra = StripExtra;
        var total = PageRow.ActualHeight + bottom;

        AppState.Current.Settings.LogPanelHeight = extra > 0 && total > 0 ? bottom * (total + extra) / total : bottom;
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

        // файл длинного лога, которого в буфере уже нет, — удалить; ещё лежащий там уберётся при следующем запуске
        ClipboardText.Release();

        // развёрнутое или свёрнутое окно — запоминается его обычный размер, а не весь экран
        var bounds = WindowState == WindowState.Normal || RestoreBounds.IsEmpty
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        settings.WindowWidth = bounds.Width;
        settings.WindowHeight = bounds.Height;
        settings.WindowLeft = bounds.Left;
        settings.WindowTop = bounds.Top;
        settings.WindowMaximized = _maximized;

        settings.LogPanelVisible = BottomPanel.Visibility == Visibility.Visible;
        settings.BottomOnJournal = JournalTab.IsChecked == true;
        RememberBottom();

        // вынесены ли вывод и запуски, пишется при их открытии и закрытии: при выходе
        // программа закрывает окна в своём порядке, и здесь их могло уже не быть
        settings.LastSection = State.Section.ToString();

        AppState.Current.SaveAll();

        // крестик прячет окно в трей; закрывается оно только при выходе из программы
        if (!App.Quitting)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
