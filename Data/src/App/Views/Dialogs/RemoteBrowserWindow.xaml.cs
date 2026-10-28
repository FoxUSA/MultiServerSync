using System.Windows;
using MultiServerSync.Models;
using MultiServerSync.Services;
using MultiServerSync.Views.Controls;
using Wpf.Ui.Controls;

namespace MultiServerSync.Views.Dialogs;

/// <summary>
/// Обзор сервера для «Копирования» (выбор папки назначения) и «Скачивания» (что скачать).
/// Внутри — та же панель <see cref="FilePane"/>, что у сервера в «Проводнике»: кнопки, адрес,
/// маски, дерево и список одни на оба места, правится одно. Здесь сверху добавлен только
/// выбор: что будет взято и кнопки «Выбрать» / «Отмена».
/// </summary>
public partial class RemoteBrowserWindow : FluentWindow
{
    /// <summary>
    /// Вид панели — столбцы, сортировка, дерево — живёт между открытиями окна, пока работает
    /// программа. Дерево по умолчанию открыто: прежний обзор всегда показывал его.
    /// </summary>
    private static readonly ExplorerPaneState Remembered = new() { ShowTree = true };

    /// <summary>Выбор нескольких файлов и папок — для скачивания; иначе выбирается одна папка.</summary>
    private readonly bool _pickMany;

    private RemoteSource _remote;

    /// <summary>
    /// Путь вкладки, которого ещё нет на сервере: при копировании он создастся сам. Открыта
    /// ближайшая существующая папка, а выбором остаётся этот путь, пока человек не ушёл.
    /// </summary>
    private string? _missingChoice;

    /// <summary>Куда панель встала сама при открытии — уход оттуда снимает <see cref="_missingChoice"/>.</summary>
    private string _landed = string.Empty;

    public RemoteBrowserWindow(ServerEntry server, string initialPath, bool pickMany = false)
    {
        InitializeComponent();

        _pickMany = pickMany;

        if (pickMany)
        {
            Title = "Что скачать с сервера";
            ChooseButton.ToolTip = "Отмеченные галочками файлы и папки, а если ничего не отмечено — открытая папка";
        }

        Server = server;
        _remote = new RemoteSource(RemoteSession.Acquire(server, AppState.Current.Settings));

        Pane.EnableTitlePicker("Сменить сервер");
        Pane.TitleClicked += (pane, anchor) => ServerMenu.Show(anchor, Server, async picked => await SwitchAsync(picked));
        Pane.CommandRequested += OnPaneCommand;
        Pane.StateChanged += _ => UpdateChoice();
        Pane.List.SelectionChanged += (_, _) => UpdateChoice();

        // подключение и прочитанные папки остаются в запасе: следующее открытие — без входа
        Closed += (_, _) => _remote.Session.Release();

        var start = string.IsNullOrWhiteSpace(initialPath) ? "/" : SshService.NormalizeRemote(initialPath);
        Loaded += async (_, _) => await OpenAsync(start);
    }

    public ServerEntry Server { get; private set; }

    public string? SelectedPath { get; private set; }

    /// <summary>Выбранное в режиме скачивания: выделенные файлы и папки, а если ничего — открытая папка.</summary>
    public IReadOnlyList<string> SelectedPaths { get; private set; } = [];

    /// <summary>То же со строками обзора: тип, размер и дата — для таблицы скачивания.</summary>
    public IReadOnlyList<FsEntry> SelectedItems { get; private set; } = [];

    /// <summary>
    /// Открыть путь на сервере окна. Папки может ещё не быть — панель сама откроет ближайшую
    /// существующую, а выбором остаётся нужный путь.
    /// </summary>
    private async Task OpenAsync(string path)
    {
        var state = Remembered;
        state.Path = path;
        state.Focus = string.Empty;
        state.Selected.Clear();

        var title = string.IsNullOrWhiteSpace(Server.User) ? Server.PaneLabel : $"{Server.PaneLabel} ({Server.User})";
        Pane.ShowServerCaption(Server.PaneLabel, Server.Host);
        await Pane.AttachAsync(_remote, state, title, ShellIcons.Server);

        _landed = Pane.CurrentPath;
        _missingChoice = Pane.CurrentPath.Length > 0 && Pane.CurrentPath != path ? path : null;

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

        _remote.Session.Release();
        Server = server;
        _remote = new RemoteSource(RemoteSession.Acquire(server, AppState.Current.Settings));

        await OpenAsync(path);
    }

    /// <summary>
    /// Действия панели, которым нужна страница. Здесь — открыть файл и свойства; копирование
    /// между сторонами и вкладки команд есть только в «Проводнике».
    /// </summary>
    private async void OnPaneCommand(FilePane pane, PaneCommand command)
    {
        try
        {
            var focused = pane.FocusedEntry;

            switch (command)
            {
                case PaneCommand.Open:
                case PaneCommand.OpenWith:
                    if (focused is { IsDirectory: false })
                    {
                        await RemoteEdit.OpenAsync(_remote, focused, command == PaneCommand.OpenWith);
                    }

                    break;

                case PaneCommand.Properties:
                    var targets = pane.TargetEntries;

                    if (targets.Count > 0 && PropertiesDialog.Show(this, _remote, targets))
                    {
                        await pane.RefreshAsync();
                    }

                    break;

                case PaneCommand.Copy:
                case PaneCommand.Cut:
                case PaneCommand.Paste:
                case PaneCommand.Compare:
                case PaneCommand.Run:
                case PaneCommand.CommandTabHere:
                case PaneCommand.SendToCopy:
                case PaneCommand.SendToDownload:
                case PaneCommand.TransferToOther:
                    AppLog.Hint("Это действие есть в разделе «Проводник».");
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(ex.Message, Server.Display);
        }
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

            return Picked is [{ IsDirectory: true } folder] ? folder.FullPath : _missingChoice ?? current;
        }
    }

    private void UpdateChoice() => ChoiceText.Text = Choice;

    private void OnChoose(object sender, RoutedEventArgs e)
    {
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
