using System.Windows;
using System.Windows.Threading;
using MultiServerSync.Models;
using MultiServerSync.Services;
using MultiServerSync.Views.Controls;
using Wpf.Ui.Controls;

namespace MultiServerSync.Views.Dialogs;

/// <summary>
/// Окно проводника поверх: обзор сервера для «Копирования» (выбор папки назначения) и «Скачивания» (что скачать),
/// а также фильтр галочками из исключений копирования. Внутри — та же панель <see cref="FilePane"/>, что
/// в «Проводнике»: кнопки, адрес, маски, дерево и список одни на все места, правится одно.
/// В обзоре из правки только новая папка (кнопкой наверху) и переименование, снизу — что будет выбрано.
/// В фильтре наверху режимы галочек вместо новой папки, снизу — «Готово»: галочки — черновик исключений.
/// </summary>
public partial class RemoteBrowserWindow : FluentWindow
{
    /// <summary>
    /// Вид панели — столбцы, сортировка — живёт между открытиями окна, пока работает программа.
    /// Дерево открыто всегда: по нему и ходят.
    /// </summary>
    private static readonly ExplorerPaneState Remembered = new() { ShowTree = true };

    /// <summary>Дерево в окне шире, чем в половине «Проводника»: окно большое.</summary>
    private const double TreeWidth = 230;

    /// <summary>Выбор нескольких файлов и папок — для скачивания; иначе выбирается одна папка.</summary>
    private readonly bool _pickMany;
    private readonly bool _viewOnly;

    private RemoteSource? _remote;

    /// <summary>Фильтр галочками из исключений; null — окно обзора сервера.</summary>
    private readonly ExplorerFilter? _filter;

    /// <summary>
    /// Путь вкладки, которого ещё нет на сервере: при копировании он создастся сам. Открыта
    /// ближайшая существующая папка, а выбором остаётся этот путь, пока человек не ушёл.
    /// </summary>
    private string? _missingChoice;

    /// <summary>Куда панель встала сама при открытии — уход оттуда снимает <see cref="_missingChoice"/>.</summary>
    private string _landed = string.Empty;

    /// <summary>Сервер и выбор при открытии окна: пока они те же, «Выбрать» тусклая — выбирать нечего.</summary>
    private ServerEntry? _startServer;

    private string _startChoice = string.Empty;

    private string _startPath = string.Empty;

    /// <summary>Папка, по которой щёлкнули в открытой папке, — выбор вместо открытой; ушли из папки — снимается.</summary>
    private FsEntry? _clicked;

    private string _clickedIn = string.Empty;

    /// <param name="viewOnly">Вкладка под замком: смотреть можно, выбрать — нет.</param>
    public RemoteBrowserWindow(ServerEntry server, string initialPath, bool pickMany = false, bool viewOnly = false)
    {
        InitializeComponent();
        Setup();

        _pickMany = pickMany;
        _viewOnly = viewOnly;

        // выбирают путь, а не наводят порядок: из правки — новая папка и переименование
        Pane.ShowBrowse();

        // кнопка без дела — тусклая, но нажимается: щелчок объясняет, почему нельзя
        if (viewOnly)
        {
            ChooseButton.Opacity = 0.45;
            ChooseButton.ToolTip = "Вкладка закреплена за своим сервером — выбрать можно, сняв замок";
            ChoiceLabel.Text = "Только просмотр:";
            CancelButton.Content = "Закрыть";
        }

        if (pickMany)
        {
            Title = "Что скачать с сервера";
            ChooseButton.ToolTip = "Отмеченные галочками файлы и папки, а если ничего не отмечено — открытая папка";
        }
        else
        {
            // одна папка: галочки ни к чему — выбор переходом в папку или щелчком по ней
            Pane.HideChecks();
            ChooseButton.ToolTip = "Папка, по которой щёлкнули, а если не щёлкали — открытая";
            Pane.List.PreviewMouseLeftButtonUp += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                _clicked = Pane.FocusedEntry is { IsDirectory: true } folder ? folder : null;
                _clickedIn = Pane.CurrentPath;
                UpdateChoice();
            }));
        }

        Server = server;
        _remote = new RemoteSource(RemoteSession.Acquire(server, AppState.Current.Settings));

        Pane.EnableTitlePicker("Сменить сервер");
        Pane.TitleClicked += (pane, anchor) => ServerMenu.Show(anchor, Server, async picked => await SwitchAsync(picked));
        Pane.CommandRequested += (_, _) => AppLog.Hint("Это действие есть в разделе «Проводник».");
        Pane.StateChanged += _ => UpdateChoice();
        Pane.List.SelectionChanged += (_, _) => UpdateChoice();

        // подключение и прочитанные папки остаются в запасе: следующее открытие — без входа
        Closed += (_, _) => _remote?.Session.Release();

        var start = string.IsNullOrWhiteSpace(initialPath) ? "/" : SshService.NormalizeRemote(initialPath);
        Loaded += async (_, _) => await OpenAsync(start);
    }

    /// <summary>
    /// Фильтр галочками окна копирования: корень — строки окон вкладки, наверху режимы «что копируется / не копируется»
    /// и «Сбросить». Галочки — черновик: в маски окон они уходят по «Сохранить» в окне исключений.
    /// </summary>
    /// <param name="viewOnly">Вкладка под замком: галочки только посмотреть.</param>
    public RemoteBrowserWindow(ExplorerFilter filter, bool viewOnly)
    {
        InitializeComponent();
        Setup();

        _filter = filter;
        _viewOnly = viewOnly;
        Server = null!;

        Title = $"Фильтр галочками — {filter.Source.Title}";
        ChoiceLabel.Text = "Галочки:";
        ChooseButton.Content = "Готово";
        ChooseButton.ToolTip = "Вернуться в исключения. Галочки вступят в силу после «Сохранить» там";

        // «Отмена» и крестик — галочки и режим как при открытии этого окна
        var opened = filter.Remembered();
        Closed += (_, _) =>
        {
            if (DialogResult != true)
            {
                filter.RestoreTo(opened);
            }
        };

        Pane.ShowFilter(filter.TickedNames);
        Pane.MuteFilter(viewOnly);
        Pane.MarkFilterMode(filter.CopiedMode);
        Pane.TicksEdited += OnFilterTicks;
        Pane.CommandRequested += (_, command) => OnFilterCommand(command);

        Loaded += async (_, _) =>
        {
            await Pane.AttachAsync(filter.Files, new ExplorerPaneState { ShowTree = true }, filter.Source.Title, ShellIcons.Computer);
            ShowFilterState();
            Pane.FocusList();
        };
    }

    /// <summary>Общее у обоих видов: действия и ошибки — раздела, откуда открыли, дерево шире.</summary>
    private void Setup()
    {
        // действия и ошибки окна — раздела, откуда открыли, а не «Проводника»
        Pane.Section = null;
        Pane.TreeWidth = TreeWidth;
    }

    public ServerEntry Server { get; private set; }

    public string? SelectedPath { get; private set; }

    /// <summary>Выбранное в режиме скачивания: выделенные файлы и папки, а если ничего — открытая папка.</summary>
    public IReadOnlyList<string> SelectedPaths { get; private set; } = [];

    /// <summary>То же со строками обзора: тип, размер и дата — для таблицы скачивания.</summary>
    public IReadOnlyList<FsEntry> SelectedItems { get; private set; } = [];

    /// <summary>
    /// Открыть путь на сервере окна. Папки может ещё не быть — панель сама откроет ближайшую
    /// существующую, а выбором остаётся нужный путь. Дерево — всегда открыто и раскрыто до неё.
    /// </summary>
    private async Task OpenAsync(string path)
    {
        if (_remote is null)
        {
            return;
        }

        var state = Remembered;
        state.Path = path;
        state.Focus = string.Empty;
        state.ShowTree = true;
        state.Selected.Clear();

        var title = string.IsNullOrWhiteSpace(Server.User) ? Server.PaneLabel : $"{Server.PaneLabel} ({Server.User})";
        Pane.ShowServerCaption(Server, ShellIcons.Server);
        await Pane.AttachAsync(_remote, state, title, ShellIcons.Server);

        _landed = Pane.CurrentPath;
        _missingChoice = Pane.CurrentPath.Length > 0 && Pane.CurrentPath != path ? path : null;
        _clicked = null;

        // с чем окно открыли — один раз: смена сервера в окне уже выбор
        if (_startServer is null)
        {
            _startServer = Server;
            _startChoice = SshService.NormalizeRemote(Choice);
            _startPath = Pane.CurrentPath;
        }

        if (_missingChoice is not null)
        {
            AppLog.Hint($"Папки {path} на сервере пока нет — она будет создана при копировании. Открыта ближайшая: {Pane.CurrentPath}");
        }

        UpdateChoice();
        Pane.FocusList();
    }

    /// <summary>Другой сервер: тот же путь, если он там есть, иначе ближайшая папка выше.</summary>
    private async Task SwitchAsync(ServerEntry server)
    {
        if (ReferenceEquals(server, Server))
        {
            return;
        }

        var path = _missingChoice ?? (Pane.CurrentPath.Length > 0 ? Pane.CurrentPath : "/");

        _remote?.Session.Release();
        Server = server;
        _remote = new RemoteSource(RemoteSession.Acquire(server, AppState.Current.Settings));

        await OpenAsync(path);
    }

    // ── фильтр галочками ────────────────────────────────────────────────────────

    /// <summary>Галочки поменяли руками — в черновик фильтра. Под замком поставленное сразу возвращается.</summary>
    private void OnFilterTicks(FilePane pane)
    {
        if (_filter is not { } filter)
        {
            return;
        }

        if (_viewOnly)
        {
            // выделение пересобралось само (открыли папку) — галочки те же, отказывать не в чем
            if (filter.TickedNames(pane.VisibleEntries).ToHashSet().SetEquals(pane.TickedNames))
            {
                return;
            }

            RefuseLocked();

            // не изнутри смены выделения, а следом: иначе список правится, пока сам ещё меняется
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Pane.ReapplyTicks()));
            return;
        }

        filter.Edit(pane.VisibleEntries, pane.TickedNames);
        ShowFilterState();
    }

    /// <summary>Режимы и «Сбросить». Под замком режимы — только посмотреть их галочки, «Сбросить» их поменял бы.</summary>
    private void OnFilterCommand(PaneCommand command)
    {
        if (_filter is not { } filter)
        {
            return;
        }

        if (_viewOnly && command == PaneCommand.FilterReset)
        {
            _refusedAt = default;
            RefuseLocked();
            return;
        }

        switch (command)
        {
            case PaneCommand.FilterCopied:
                filter.SetMode(copied: true);
                break;
            case PaneCommand.FilterSkipped:
                filter.SetMode(copied: false);
                break;
            case PaneCommand.FilterReset:
                filter.Reset();
                break;
            default:
                AppLog.Hint("Это действие есть в разделе «Проводник».");
                return;
        }

        Pane.MarkFilterMode(filter.CopiedMode);
        Pane.ReapplyTicks();
        ShowFilterState();
    }

    /// <summary>Когда последний раз отказали из-за замка: протяжка мышью меняет галочки по строке, плашка — одна.</summary>
    private DateTime _refusedAt;

    private void RefuseLocked()
    {
        if (DateTime.Now - _refusedAt < TimeSpan.FromSeconds(2) || _filter is not { } filter)
        {
            return;
        }

        _refusedAt = DateTime.Now;
        Toast.ShowInPlace(AppLog.WarnInPlace(
            $"Вкладка «{filter.Source.Title}» закреплена за своим сервером — фильтр здесь только посмотреть. Изменить можно, сняв замок."));
    }

    /// <summary>Снизу — режим и есть ли несохранённые галочки.</summary>
    private void ShowFilterState()
    {
        if (_filter is not { } filter)
        {
            return;
        }

        ChoiceText.Text = (filter.CopiedMode ? "что копируется" : "что не копируется")
            + (filter.HasChanges ? " · поменяли — вступят в силу после «Сохранить» в исключениях" : string.Empty);

        // «Готово» — как «Сохранить» везде: пока галочки и режим как в масках окон, тусклая
        SaveGate.Show(ChooseButton, filter.HasChanges);
    }

    // ── выбор ──────────────────────────────────────────────────────────────────

    /// <summary>Выделенное, без «..».</summary>
    private IReadOnlyList<FsEntry> Picked => Pane.SelectedEntries;

    /// <summary>Что возьмёт «Выбрать»: выделенная папка (или выделенное при скачивании), иначе открытая.</summary>
    private string Choice
    {
        get
        {
            // ушли из папки, куда встали сами, — несуществующий путь вкладки больше не выбор
            if (_missingChoice is not null && Pane.CurrentPath != _landed)
            {
                _missingChoice = null;
            }

            var current = Pane.CurrentPath.Length > 0 ? Pane.CurrentPath : "/";

            if (_pickMany)
            {
                return Picked.Count switch
                {
                    0 => current,
                    1 => Picked[0].FullPath,
                    var n => $"{n} объектов в {current}",
                };
            }

            return _clicked is { } folder && Pane.CurrentPath == _clickedIn ? folder.FullPath : _missingChoice ?? current;
        }
    }

    /// <summary>
    /// Выбор сделан: другой сервер, а в нём для копирования — другая папка, для скачивания — что-то отмечено
    /// или открыта другая папка. Пока нет — «Выбрать» тусклая.
    /// </summary>
    private bool Chosen =>
        _startServer is not null
        && (!ReferenceEquals(Server, _startServer)
            || (_pickMany
                ? Picked.Count > 0 || Pane.CurrentPath != _startPath
                : SshService.NormalizeRemote(Choice) != _startChoice));

    private void UpdateChoice()
    {
        if (_filter is not null)
        {
            return;
        }

        ChoiceText.Text = Choice;

        // под замком кнопка тусклая всегда — там своя подсказка
        if (!_viewOnly)
        {
            ChooseButton.Opacity = Chosen ? 1 : 0.45;
        }
    }

    private void OnChoose(object sender, RoutedEventArgs e)
    {
        // фильтр: галочки уже в черновике — назад в исключения; не меняли — «Изменений нет», выйти — «Отмена»
        if (_filter is { } filter)
        {
            if (!filter.HasChanges)
            {
                Toast.Show(AppLog.WarnInPlace(SaveGate.Nothing), SaveGate.Anchor(ChooseButton, Toast.Parent as System.Windows.Media.Visual ?? this));
                return;
            }

            DialogResult = true;
            Close();
            return;
        }

        if (_viewOnly)
        {
            var anchor = ChooseButton.TransformToVisual(Toast.Parent as System.Windows.Media.Visual ?? this)
                .TransformBounds(new Rect(0, 0, ChooseButton.ActualWidth, ChooseButton.ActualHeight));
            Toast.Show(AppLog.WarnInPlace("Вкладка закреплена за своим сервером — здесь только посмотреть. Выбрать можно, сняв замок."), anchor);
            return;
        }

        if (!Chosen)
        {
            Toast.Show(AppLog.WarnInPlace(_pickMany
                    ? "Ничего не выбрано — отметьте файлы и папки или откройте другую папку."
                    : "Выбор тот же, что был, — откройте другую папку или щёлкните по ней."),
                SaveGate.Anchor(ChooseButton, Toast.Parent as System.Windows.Media.Visual ?? this));
            return;
        }

        if (_pickMany)
        {
            var picked = Picked;
            var current = Pane.CurrentPath.Length > 0 ? Pane.CurrentPath : "/";

            // ничего не выделено — берётся открытая папка
            SelectedItems = picked.Count > 0
                ? picked
                : [new FsEntry(current, current, true, 0, default)];
            SelectedPaths = SelectedItems.Select(i => SshService.NormalizeRemote(i.FullPath)).ToList();
            DialogResult = true;
            Close();
            return;
        }

        SelectedPath = SshService.NormalizeRemote(Choice);
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
