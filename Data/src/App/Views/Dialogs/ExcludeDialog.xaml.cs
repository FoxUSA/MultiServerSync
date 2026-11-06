using System.IO;
using System.Windows;
using System.Windows.Threading;
using MultiServerSync.Core;
using MultiServerSync.Models;
using MultiServerSync.Services;
using Wpf.Ui.Controls;
using SaveGate = MultiServerSync.Views.Controls.SaveGate;

namespace MultiServerSync.Views.Dialogs;

/// <summary>
/// Исключения: свои у вкладки и общие на все вкладки «Копирования», «Проводника» и «Скачивания».
/// Готовые шаблоны — в один щелчок, «Стандартный набор» — всё лишнее разом.
/// </summary>
public partial class ExcludeDialog : FluentWindow
{
    private ExcludeDialog(string own, string global, bool useGlobal, IReadOnlyList<TransferItem> files, bool includes, bool explorer, bool viewOnly,
        bool anyRows, ExplorerFilter? pending, IReadOnlyList<string>? pendingLines)
    {
        InitializeComponent();

        _viewOnly = viewOnly;

        // «Включить» хранится в тех же масках вкладки строками с «+»: в окне — своё поле.
        // Где его нет («Проводник»), такие строки не показываются, но и не теряются.
        // Галочки «Проводника» — строками с «@»: правятся только там, здесь — итогом справа
        var ownLines = Lines(own);
        _pickLines = ownLines.Where(ExplorerPicks.IsPickLine).ToList();
        ownLines = ownLines.Where(l => !ExplorerPicks.IsPickLine(l)).ToList();
        var includeLines = ownLines.Where(IsIncludeLine).Select(l => l[1..].Trim()).ToList();
        own = string.Join(Environment.NewLine, ownLines.Where(l => !IsIncludeLine(l)));
        _includes = includes;
        IncludePanel.Visibility = includes ? Visibility.Visible : Visibility.Collapsed;
        IncludeBox.Text = string.Join(Environment.NewLine, includeLines);
        IncludeBox.TextChanged += (_, _) => ScheduleCheck();
        OwnExcludeTitle.Visibility = includes ? Visibility.Visible : Visibility.Collapsed;

        // файлы вкладки читаются один раз, в фоне: по ним считается, сколько отсеет каждая маска
        _tree = files.Count == 0 ? Task.FromResult<List<Node>?>(null) : Task.Run(() => BuildTree(files));
        _checkTimer.Tick += (_, _) =>
        {
            _checkTimer.Stop();
            _ = CheckAsync();
        };

        // у ярлыков окна рамки нет, даже когда в поле есть маски; состояние глобальных — фиолетовый переключатель
        OwnBox.TextChanged += (_, _) => ScheduleCheck();
        GlobalBox.TextChanged += (_, _) => ScheduleCheck();
        GlobalBox.TextChanged += (_, _) => ShowGlobalState();
        UseGlobalSwitch.Checked += (_, _) => ShowGlobalState();
        UseGlobalSwitch.Unchecked += (_, _) => ShowGlobalState();
        UseGlobalSwitch.Checked += (_, _) => ScheduleCheck();
        UseGlobalSwitch.Unchecked += (_, _) => ScheduleCheck();

        OwnBox.Text = own;
        GlobalBox.Text = global;
        UseGlobalSwitch.IsChecked = useGlobal;
        ShowGlobalState();
        TemplatesList.ItemsSource = ExcludeRules.Templates;

        // «Проводник» — у «Копирования»: в самом «Проводнике» фильтра галочками нет, у «Скачивания» — пока нет
        ExplorerTab.Visibility = explorer ? Visibility.Visible : Visibility.Collapsed;
        // фильтр показывает строки всех окон вкладки — открыть есть что, если строки есть хоть в одном
        OpenExplorerButton.IsEnabled = anyRows;
        ShowExplorerState();

        // вкладка под замком: всё видно, ничего не правится. Кнопки без дела тусклые, щелчок объясняет почему
        if (viewOnly)
        {
            OwnBox.IsReadOnly = true;
            IncludeBox.IsReadOnly = true;
            TemplatesBlock.Opacity = 0.45;
            SaveButton.Opacity = 0.45;
            CancelButton.Content = "Закрыть";
            Title = "Исключения — только просмотр";
        }

        // «Сохранить» — только когда что-то поменяли. Галочки, поставленные в «Проводнике», — тоже правка:
        // пришли оттуда — сохраняются здесь
        _initialOwn = ComposeOwn();
        _initialGlobal = string.Join(Environment.NewLine, Lines(GlobalBox.Text));
        _initialUseGlobal = UseGlobal;

        // то, что применит вкладка, с самого начала — нынешние маски, а не пустые строки:
        // путь закрытия без «Сохранить» больше не сотрёт маски вкладки и глобальные
        Own = _initialOwn;
        Global = _initialGlobal;
        _pending = pending;

        if (pendingLines is not null)
        {
            _pickLines = pendingLines.ToList();
            ShowExplorerState();
        }

        if (!viewOnly)
        {
            SaveGate.Watch(this, SaveButton, Changed);
        }

        // открывается там, где закрыли в прошлый раз; вернулись из «Проводника» — на его вкладке
        var settings = AppState.Current.Settings;
        var last = pending is not null && explorer ? 0 : settings.ExcludeDialogTab ?? (settings.ExcludeDialogOnGlobal ? 2 : 1);
        Tabs.SelectedIndex = last == 0 && !explorer ? 1 : Math.Clamp(last, 0, 2);
        ShowTabParts();

        Loaded += (_, _) =>
        {
            EvenTabHeights();
            FocusBox();
            _ = CheckAsync();
        };
        Tabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, Tabs))
            {
                TemplateNote.Visibility = Visibility.Collapsed;
                ShowTabParts();
                FocusBox();
                ScheduleCheck();
            }
        };

        Closing += OnClosingAsk;

        Closed += (_, _) =>
        {
            if (settings.ExcludeDialogTab != Tabs.SelectedIndex)
            {
                settings.ExcludeDialogTab = Tabs.SelectedIndex;
                settings.ExcludeDialogOnGlobal = OnGlobal;
                AppState.Current.SaveSettings();
            }
        };
    }

    private bool OnGlobal => ReferenceEquals(Tabs.SelectedItem, GlobalTab);

    private bool OnExplorer => ReferenceEquals(Tabs.SelectedItem, ExplorerTab);

    /// <summary>
    /// Все вкладки окна — высотой с самую высокую: окно подгоняется по содержимому, и при переключении оно прыгало.
    /// Невидимые вкладки ещё не выложены — меряются на ширину левой колонки.
    /// </summary>
    private void EvenTabHeights()
    {
        var contents = new[] { ExplorerTab, OwnTab, GlobalTab }
            .Where(t => t.Visibility == Visibility.Visible)
            .Select(t => t.Content)
            .OfType<FrameworkElement>()
            .ToList();

        foreach (var content in contents)
        {
            content.Measure(new Size(LeftColumn.ActualWidth, double.PositiveInfinity));
        }

        var tallest = contents.Max(c => c.DesiredSize.Height);

        foreach (var content in contents)
        {
            content.MinHeight = tallest;
        }

        // поле глобальных — на всю высоту вкладки. Именно высотой: без неё поле просит место
        // под все свои строки, и окно вырастало на весь экран
        if (GlobalTab.Content is FrameworkElement global)
        {
            GlobalBox.Height += Math.Max(0, tallest - global.DesiredSize.Height);
        }
    }

    /// <summary>На «Проводнике» полей масок нет — нет и шаблонов (место за ними остаётся), а справа вместо разбора масок итог галочек.</summary>
    private void ShowTabParts()
    {
        TemplatesBlock.Visibility = OnExplorer ? Visibility.Hidden : Visibility.Visible;
        CheckTitle.Text = OnExplorer ? "Фильтр проводника:" : "Как понята каждая маска:";
    }

    /// <summary>Поле «Включить» показано: «Копирование» и «Скачивание».</summary>
    private readonly bool _includes;

    private static bool IsIncludeLine(string line) => line.StartsWith(ExcludeRules.IncludeMark);

    /// <summary>Галочки «Проводника» из масок окна — как были, окно их не правит.</summary>
    private readonly List<string> _pickLines;

    /// <summary>
    /// Маски вкладки целиком, как их хранит вкладка: «Исключить», за ними «Включить» строками с «+»
    /// и галочки «Проводника» строками с «@».
    /// </summary>
    private string ComposeOwn() =>
        string.Join(Environment.NewLine, Lines(OwnBox.Text)
            .Concat(Lines(IncludeBox.Text).Select(l => ExcludeRules.IncludeMark + " " + l))
            .Concat(_pickLines));

    /// <summary>Вкладка, чьи исключения открыты: фильтр галочками ходит по строкам её окон.</summary>
    private Workspace? _workspace;

    /// <summary>
    /// «Открыть в проводнике»: фильтр галочками — окном поверх, как обзор пути. Окно исключений остаётся открытым:
    /// поставленные галочки — черновик, они видны здесь же и сохраняются вместе с масками по «Сохранить».
    /// Под замком галочки там только посмотреть.
    /// </summary>
    private void OnOpenExplorer(object sender, RoutedEventArgs e)
    {
        if (_workspace is not { } workspace)
        {
            return;
        }

        // открывали уже — те же галочки: черновик живёт, пока открыто окно исключений
        _pending ??= new ExplorerFilter(workspace);

        new RemoteBrowserWindow(_pending, _viewOnly) { Owner = this }.ShowDialog();

        // галочки окна в фокусе — в его маски вместе с остальным, их и показывает разбор справа
        if (_pending.LinesOf(workspace.FocusedWindow) is { } lines)
        {
            _pickLines.Clear();
            _pickLines.AddRange(lines);
        }

        ShowExplorerState();

        if (!_viewOnly)
        {
            SaveGate.Show(SaveButton, Changed());
        }

        ScheduleCheck();
    }

    /// <summary>Под кнопкой — в каком режиме галочки и сколько их; пришли из «Проводника» с правками — что их надо сохранить.</summary>
    private void ShowExplorerState()
    {
        var picks = ExplorerPicks.Parse(string.Join(Environment.NewLine, _pickLines));

        ExplorerState.Text = (!OpenExplorerButton.IsEnabled
            ? "В окне нет строк — отмечать нечего."
            : picks.IsEmpty
                ? "Галочек ещё нет — действуют маски «Эта вкладка» и «Глобально»."
                : $"Режим: {(picks.CopiedMode ? "что копируется" : "что не копируется")} · отмечено строк: {picks.Active.Count}")
            + (_pending?.HasChanges == true ? "\nГалочки поменяли в «Проводнике» — они вступят в силу после «Сохранить»." : string.Empty);
    }

    /// <summary>Галочки «Проводника», которые ещё не сохранены; null — пришли не оттуда.</summary>
    private ExplorerFilter? _pending;

    private string _initialOwn = string.Empty;

    private string _initialGlobal = string.Empty;

    private bool _initialUseGlobal;

    /// <summary>Есть что сохранять: маски, «Включить», глобальные, их переключатель или галочки «Проводника».</summary>
    private bool Changed() =>
        _pending?.HasChanges == true
        || ComposeOwn() != _initialOwn
        || string.Join(Environment.NewLine, Lines(GlobalBox.Text)) != _initialGlobal
        || UseGlobal != _initialUseGlobal;

    private string Own { get; set; } = string.Empty;

    private string Global { get; set; } = string.Empty;

    private bool UseGlobal => UseGlobalSwitch.IsChecked == true;

    /// <summary>Поле открытой вкладки окна — шаблоны дописываются в него.</summary>
    private System.Windows.Controls.TextBox Box => OnGlobal ? GlobalBox : OwnBox;

    /// <summary>
    /// Открывает исключения вкладки и глобальные; сохранённое сразу ставит на место, итог — плашкой.
    /// <paramref name="action"/> — «копировании» или «скачивании», для подписи итога.
    /// <paramref name="includes"/> — показывать «Включить»: на «Копировании» и «Скачивании», не в «Проводнике».
    /// </summary>
    /// <param name="viewOnly">Вкладка под замком: посмотреть можно, сохранить нельзя.</param>
    /// <param name="pending">Галочки из вкладки-фильтра «Проводника», ещё не сохранённые: «Сохранить» пишет их в маски окон.</param>
    public static void Edit(Window? owner, Workspace workspace, string action, bool includes = true, bool viewOnly = false,
        ExplorerFilter? pending = null)
    {
        var state = AppState.Current;
        // при скачивании файлы на сервере — считать нечего, остаётся только разбор масок.
        // Маски у каждого окна свои — и считается по строкам окна в фокусе, тем же, что покажет фильтр «Проводника»
        var upload = action != "скачивании";
        List<TransferItem> files = upload ? workspace.FocusedWindow.Items.ToList() : [];
        var dialog = new ExcludeDialog(workspace.ExcludeMasks, state.GlobalExcludeMasks, workspace.UseGlobalExcludes, files, includes,
            explorer: includes && upload, viewOnly, anyRows: workspace.CopyWindows.Any(w => w.Items.Count > 0),
            pending, pending?.LinesOf(workspace.FocusedWindow)) { Owner = owner, _workspace = workspace };

        // «Отмена» — галочки из «Проводника» не сохраняются
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (!viewOnly)
        {
            Apply(dialog, workspace, action);

            // галочки окна в фокусе уже в его масках вместе с остальным, здесь — остальных окон
            if (dialog._pending?.Commit() is > 0 and var windows)
            {
                ActionLog.Info($"Изменён фильтр «Проводника» в других окнах вкладки: {windows}", null, null, workspace);
            }
        }
    }

    private static void Apply(ExcludeDialog dialog, Workspace workspace, string action)
    {
        var state = AppState.Current;

        // глобальных нет — переключатель погашен и ничего не значит: память вкладки остаётся прежней,
        // иначе глобальные, добавленные потом с другой вкладки, сюда бы уже не попали
        var hasGlobal = ExcludeRules.Count(dialog.Global) > 0;
        var useGlobal = hasGlobal ? dialog.UseGlobal : workspace.UseGlobalExcludes;

        if (dialog.Own == workspace.ExcludeMasks && dialog.Global == state.GlobalExcludeMasks
            && useGlobal == workspace.UseGlobalExcludes)
        {
            return;
        }

        workspace.ExcludeMasks = dialog.Own;
        workspace.UseGlobalExcludes = useGlobal;
        state.GlobalExcludeMasks = dialog.Global;

        var own = ExcludeRules.Count(dialog.Own);
        var global = useGlobal ? ExcludeRules.Count(dialog.Global) : 0;
        var off = hasGlobal && !useGlobal ? " Глобальные на этой вкладке выключены." : string.Empty;
        // полный вывод: сами маски, своими и глобальными порознь
        var lines = new List<string>();

        if (own > 0)
        {
            lines.Add("маски вкладки:");
            lines.AddRange(dialog.Own.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(m => !ExplorerPicks.IsPickLine(m)).Select(m => "  " + m));
        }

        if (hasGlobal)
        {
            lines.Add(useGlobal ? "глобальные маски:" : "глобальные маски (на этой вкладке выключены):");
            lines.AddRange(dialog.Global.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(m => "  " + m));
        }

        ActionLog.Info((own + global == 0
            ? $"Исключения при {action} сняты"
            : $"Исключения при {action}: у вкладки {own}, глобальных {global}") + off, null, null, workspace, lines);
    }

    /// <summary>
    /// Глобальные выключены на этой вкладке — их поле тусклое и не правится.
    /// Сами маски не стираются: включат снова — они на месте.
    /// </summary>
    private void ShowGlobalState()
    {
        if (_showingGlobal)
        {
            return;
        }

        _showingGlobal = true;

        try
        {
            // глобальных нет вовсе — выключать нечего: переключатель погашен, поле открыто для ввода.
            // Появилась первая маска — переключатель включается сам
            var empty = ExcludeRules.Count(GlobalBox.Text) == 0;

            if (empty)
            {
                _globalWasEmpty = true;
                UseGlobalSwitch.IsChecked = false;
            }
            else if (_globalWasEmpty)
            {
                _globalWasEmpty = false;
                UseGlobalSwitch.IsChecked = true;
            }

            var off = !empty && !UseGlobal;
            UseGlobalSwitch.IsEnabled = !empty && !_viewOnly;
            GlobalBox.IsReadOnly = off || _viewOnly;
            GlobalBox.Opacity = off ? 0.45 : 1;

            // рамки у «Глобально» нет: включены ли глобальные, показывает фиолетовый переключатель
        }
        finally
        {
            _showingGlobal = false;
        }
    }

    /// <summary>Поле глобальных было пустым: первая маска в нём включит переключатель.</summary>
    private bool _globalWasEmpty;

    private bool _showingGlobal;

    private void FocusBox() => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
    {
        if (OnExplorer)
        {
            OpenExplorerButton.Focus();
            return;
        }

        Box.Focus();
        Box.CaretIndex = Box.Text.Length;
    });

    /// <summary>Открыто поле глобальных, а они на этой вкладке выключены: шаблоны туда не пишутся.</summary>
    private bool Locked()
    {
        if (_viewOnly)
        {
            Note(LockedText);
            return true;
        }

        if (!Box.IsReadOnly)
        {
            return false;
        }

        Note("Глобальные исключения на этой вкладке выключены — включите их переключателем.");
        return true;
    }

    /// <summary>Шаблон дописывается строкой в конец, если такого ещё нет.</summary>
    private void OnTemplate(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: string mask } || Locked())
        {
            return;
        }

        var lines = Lines(Box.Text);

        if (lines.Any(l => Same(l, mask)))
        {
            Note($"{mask} уже есть в списке.");
            return;
        }

        lines.Add(mask);
        Put(lines);
    }

    /// <summary>Весь стандартный набор: каждая группа — под своим заголовком-комментарием, только недостающее.</summary>
    private void OnStandardSet(object sender, RoutedEventArgs e)
    {
        if (Locked())
        {
            return;
        }

        var lines = Lines(Box.Text);
        var added = 0;

        foreach (var (title, masks) in ExcludeRules.StandardSet)
        {
            var missing = masks.Where(m => !lines.Any(l => Same(l, m))).ToList();

            if (missing.Count == 0)
            {
                continue;
            }

            var header = "# ---- " + title;

            if (!lines.Contains(header))
            {
                lines.Add(header);
            }

            lines.AddRange(missing);
            added += missing.Count;
        }

        if (added == 0)
        {
            Note("Весь стандартный набор уже в списке.");
            return;
        }

        Put(lines);
        Note($"Добавлено масок: {added}.");
    }

    private static System.Windows.Media.Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static List<string> Lines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>Одна и та же маска: «node_modules» и «node_modules/», регистр не важен.</summary>
    private static bool Same(string line, string mask) =>
        string.Equals(line.TrimEnd('/'), mask.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private void Put(List<string> lines)
    {
        Box.Text = string.Join(Environment.NewLine, lines);
        Box.CaretIndex = Box.Text.Length;
        Box.ScrollToEnd();
    }

    private void Note(string text)
    {
        TemplateNote.Text = text;
        TemplateNote.Visibility = Visibility.Visible;
    }

    // ── разбор масок под полем ─────────────────────────────────────────────────

    /// <summary>Файл или папка вкладки: путь от копируемого объекта включительно, как его видят исключения.</summary>
    private sealed record Node(string Path, bool IsDir, Node[] Children, int Files);

    /// <summary>Больше этого не обходим: на таком дереве счёт тормозил бы ввод.</summary>
    private const int TreeLimit = 300_000;

    private readonly Task<List<Node>?> _tree;
    private readonly DispatcherTimer _checkTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private int _checkVersion;

    private void ScheduleCheck()
    {
        _checkTimer.Stop();
        _checkTimer.Start();
    }

    private static List<Node>? BuildTree(IReadOnlyList<TransferItem> files)
    {
        var seen = 0;
        var roots = new List<Node>();

        foreach (var item in files)
        {
            if (item.IsDirectory)
            {
                if (Walk(new DirectoryInfo(item.FullPath), item.Name, ref seen) is not { } node)
                {
                    return null;
                }

                roots.Add(node);
            }
            else
            {
                roots.Add(new Node(item.Name, false, [], 1));
            }
        }

        return roots;
    }

    private static Node? Walk(DirectoryInfo dir, string path, ref int seen)
    {
        var children = new List<Node>();
        var files = 0;

        try
        {
            foreach (var file in dir.EnumerateFiles())
            {
                children.Add(new Node(path + "/" + file.Name, false, [], 1));
                files++;
            }

            foreach (var sub in dir.EnumerateDirectories())
            {
                if (Walk(sub, path + "/" + sub.Name, ref seen) is not { } node)
                {
                    return null;
                }

                children.Add(node);
                files += node.Files;
            }
        }
        catch (IOException)
        {
            // папку не прочитать — считаем то, что успели
        }
        catch (UnauthorizedAccessException)
        {
        }

        seen += children.Count;
        return seen > TreeLimit ? null : new Node(path, true, [.. children], files);
    }

    /// <summary>Сколько файлов отсеет маска: исключённая папка — со всем содержимым, как при копировании.</summary>
    private static int Hits(ExcludeRules rules, Node node)
    {
        if (rules.Matches(node.Path, node.IsDir))
        {
            return node.Files;
        }

        var hits = 0;

        foreach (var child in node.Children)
        {
            hits += Hits(rules, child);
        }

        return hits;
    }

    /// <summary>
    /// Строка таблицы справа: маска, как понята и её число — сколько файлов вкладки отсеет («Исключить»)
    /// или вернёт из-под исключений («Включить»). Null — считать не по чему.
    /// <paramref name="Matched"/> у «Включить» — сколько файлов под неё подошло вообще: вернуть было нечего, если они и так едут.
    /// </summary>
    private sealed record CheckRow(string Mask, string Where, string What, bool Variants, int? Hits, int? Matched, string? Hint);

    private static List<CheckRow> Rows(string text) =>
        ExcludeRules.Masks(text)
            .Select(m => (Mask: m, Meaning: ExcludeRules.Explain(m)))
            .Where(m => m.Meaning is not null)
            .Select(m => new CheckRow(m.Mask, m.Meaning!.Value.Where, m.Meaning.Value.What, m.Meaning.Value.Variants, null, null, null))
            .ToList();

    /// <summary>Все файлы дерева с путями — для «Включить»: смотрит и внутрь исключённых папок.</summary>
    private static IEnumerable<string> AllFiles(Node node) =>
        node.IsDir ? node.Children.SelectMany(AllFiles) : [node.Path];

    /// <summary>Сколько файлов поедет на самом деле — тем же обходом, что план копирования.</summary>
    private static int Kept(ExcludeFilter filter, Node node)
    {
        if (filter.Excludes(node.Path, node.IsDir))
        {
            return 0;
        }

        return node.IsDir ? node.Children.Sum(c => Kept(filter, c)) : 1;
    }

    /// <summary>
    /// Таблица справа — по строке на маску открытого поля: как понята и сколько файлов вкладки задела.
    /// На «Этой вкладке» — «Исключить» и «Включить» порознь, внизу — сколько поедет при всех масках вместе.
    /// </summary>
    private async Task CheckAsync()
    {
        var version = ++_checkVersion;

        if (OnExplorer)
        {
            await CheckExplorerAsync(version);
            return;
        }

        var onGlobal = OnGlobal;
        var excludes = Rows(onGlobal ? GlobalBox.Text : OwnBox.Text);
        List<CheckRow> includes = onGlobal || !_includes ? [] : Rows(IncludeBox.Text);
        var own = ComposeOwn();
        var global = UseGlobal ? GlobalBox.Text : string.Empty;

        if (excludes.Count + includes.Count == 0)
        {
            ShowCheck([], [], null);
            return;
        }

        // смысл сразу, числа — как только прочитаны файлы
        if (!_tree.IsCompleted)
        {
            ShowCheck(excludes, includes, null);
        }

        var tree = await _tree;

        if (tree is null)
        {
            if (version == _checkVersion)
            {
                ShowCheck(excludes, includes, null);
            }

            return;
        }

        var (counted, returned, total) = await Task.Run(() =>
        {
            var files = tree.SelectMany(AllFiles).ToList();

            // исключено масками без «Включить»: от этого и считается, что «Включить» вернуло
            var ownRules = ExcludeRules.Parse(own);
            var globalRules = ExcludeRules.Parse(global);
            var excluded = files.Where(f => ownRules.MatchesSelfOrAncestor(f, false) || globalRules.MatchesSelfOrAncestor(f, false)).ToHashSet();

            var e = excludes.Select(m =>
            {
                var hits = tree.Sum(n => Hits(ExcludeRules.Parse(m.Mask), n));
                return m with { Hits = hits, Hint = hits == 0 ? Bare(m.Mask, mask => tree.Sum(n => Hits(ExcludeRules.Parse(mask), n))) : null };
            })
            // сверху то, что отсеивает больше; равные — в порядке поля
            .OrderByDescending(m => m.Hits).ToList();

            var i = includes.Select(m =>
            {
                var rules = ExcludeRules.Parse(m.Mask);
                var matched = files.Where(f => rules.MatchesSelfOrAncestor(f, false)).ToList();
                var back = matched.Count(excluded.Contains);
                return m with
                {
                    Hits = back,
                    Matched = matched.Count,
                    Hint = matched.Count == 0 ? Bare(m.Mask, mask => files.Count(f => ExcludeRules.Parse(mask).MatchesSelfOrAncestor(f, false))) : null,
                };
            }).OrderByDescending(m => m.Hits).ToList();

            var filter = new ExcludeFilter(own, global);
            return (e, i, (Kept: tree.Sum(n => Kept(filter, n)), All: files.Count));
        });

        if (version == _checkVersion)
        {
            ShowCheck(counted, returned, total);
        }
    }

    /// <summary>
    /// Справа на «Проводнике» — галочки выбранного режима: путь, едет или нет, сколько файлов решает эта галочка
    /// (глубже своей нет). Внизу — сколько поедет при галочках и масках вместе.
    /// </summary>
    private async Task CheckExplorerAsync(int version)
    {
        var own = ComposeOwn();
        var global = UseGlobal ? GlobalBox.Text : string.Empty;
        var picks = ExplorerPicks.Parse(own);
        var rows = picks.Active
            .Select(p => new CheckRow(p.Key, string.Empty, (picks.CopiedMode ? p.Value : !p.Value) ? "едет" : "не едет", false, null, null, null))
            .ToList();

        CheckEmpty.Text = "Галочек нет — действуют маски «Эта вкладка» и «Глобально».";

        var tree = await _tree;

        if (version != _checkVersion)
        {
            return;
        }

        if (tree is null)
        {
            ShowCheck(rows, [], null, explorer: picks.CopiedMode);
            return;
        }

        var (counted, total) = await Task.Run(() =>
        {
            var files = tree.SelectMany(AllFiles).ToList();
            var byPick = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var file in files)
            {
                // ближайшая галочка: сам файл или папка выше
                for (var p = file; ; p = p[..p.LastIndexOf('/')])
                {
                    if (picks.Active.ContainsKey(p))
                    {
                        byPick[p] = byPick.GetValueOrDefault(p) + 1;
                        break;
                    }

                    if (p.LastIndexOf('/') <= 0)
                    {
                        break;
                    }
                }
            }

            var counted = rows.Select(r => r with { Hits = byPick.GetValueOrDefault(r.Mask) }).OrderByDescending(r => r.Hits).ToList();
            var filter = new ExcludeFilter(own, global);
            return (counted, (Kept: tree.Sum(n => Kept(filter, n)), All: files.Count));
        });

        if (version == _checkVersion)
        {
            ShowCheck(counted, [], total, explorer: picks.CopiedMode);
        }
    }

    /// <summary>«public/manifest.json.» или «*.log;» — знак в конце от текста, а не от имени файла: сколько задело бы без него.</summary>
    private static string? Bare(string mask, Func<string, int> count) =>
        mask.TrimEnd('.', ';', ',', ':') is { Length: > 0 } bare && bare != mask && count(bare) is > 0 and var hits
            ? $"без «{mask[bare.Length..]}» — {hits}"
            : null;

    /// <param name="explorer">Галочки «Проводника» в режиме «что копируется» (true) или «что не копируется»; null — маски.</param>
    private void ShowCheck(List<CheckRow> excludes, List<CheckRow> includes, (int Kept, int All)? total, bool? explorer = null)
    {
        CheckTable.Children.Clear();
        CheckTable.RowDefinitions.Clear();

        if (explorer is null)
        {
            CheckEmpty.Text = "Масок нет — ничего не исключается.";
        }

        var any = excludes.Count + includes.Count > 0;
        CheckEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        CheckLegend.Visibility = any && explorer is null ? Visibility.Visible : Visibility.Collapsed;
        CheckTotal.Visibility = total is null ? Visibility.Collapsed : Visibility.Visible;

        if (total is { } t)
        {
            CheckTotal.Text = $"Поедет файлов: {t.Kept:N0} из {t.All:N0}";
        }

        if (explorer is { } copied)
        {
            Section(excludes, copied ? "Галочки: что копируется" : "Галочки: что не копируется", "Файлов", include: false, where: string.Empty, what: string.Empty);
            return;
        }

        Section(excludes, includes.Count > 0 ? "Исключить" : "Маска", "Отсеет", include: false);
        Section(includes, "Включить", "Вернёт", include: true);
    }

    private void Section(List<CheckRow> rows, string title, string count, bool include, string where = "Где", string what = "Что")
    {
        if (rows.Count == 0)
        {
            return;
        }

        // заголовок раздела; у второго — отступ сверху, чтобы разделы не слипались
        var header = new[]
        {
            Cell(title, "TextFillColorSecondaryBrush"),
            Cell(where, "TextFillColorTertiaryBrush"),
            Cell(what, "TextFillColorTertiaryBrush"),
            Cell(count, "TextFillColorTertiaryBrush", right: true),
        };
        header[0].FontWeight = FontWeights.SemiBold;
        AddRow(false, header);

        if (CheckTable.RowDefinitions.Count > 1)
        {
            foreach (var cell in header)
            {
                cell.Margin = new Thickness(cell.Margin.Left, 14, cell.Margin.Right, cell.Margin.Bottom);
            }
        }

        var stripe = false;

        foreach (var row in rows)
        {
            var mask = Cell(row.Mask, "TextFillColorPrimaryBrush");
            mask.FontFamily = new System.Windows.Media.FontFamily("Consolas");
            mask.TextWrapping = TextWrapping.Wrap;

            if (row.Variants)
            {
                mask.Inlines.Add(Paint(new System.Windows.Documents.Run(" +.*"), "TextFillColorTertiaryBrush"));
            }

            // ничего не задела — оранжевым: опечатка или не тот путь. «Включить», файлы которой и так едут, — тоже:
            // она ничего не меняет
            var hits = row.Hits switch
            {
                null => Cell("—", "TextFillColorTertiaryBrush", right: true),
                > 0 => Cell(row.Hits.Value.ToString("N0"), OkBrush, right: true),
                _ when include && row.Matched > 0 => Cell($"0 (и так едут: {row.Matched:N0})", WarnBrush, right: true),
                _ => Cell(row.Hint is null ? "0" : $"0 ({row.Hint})", WarnBrush, right: true),
            };

            stripe = !stripe;
            AddRow(!stripe, mask, Cell(row.Where, "TextFillColorSecondaryBrush"), Cell(row.What, "TextFillColorSecondaryBrush"), hits);
        }
    }

    private void AddRow(bool stripe, params System.Windows.Controls.TextBlock[] cells)
    {
        var index = CheckTable.RowDefinitions.Count;
        CheckTable.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

        // полосами через строку, как таблицы программы; заголовок раздела — без фона
        if (stripe)
        {
            var band = new System.Windows.Controls.Border { CornerRadius = new CornerRadius(3) };
            band.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "AppRowAltBrush");
            System.Windows.Controls.Grid.SetRow(band, index);
            System.Windows.Controls.Grid.SetColumnSpan(band, cells.Length);
            CheckTable.Children.Add(band);
        }

        for (var column = 0; column < cells.Length; column++)
        {
            System.Windows.Controls.Grid.SetRow(cells[column], index);
            System.Windows.Controls.Grid.SetColumn(cells[column], column);
            cells[column].Margin = new Thickness(column == 0 ? 6 : 0, 4, column == cells.Length - 1 ? 6 : 14, 4);
            cells[column].VerticalAlignment = VerticalAlignment.Center;
            CheckTable.Children.Add(cells[column]);
        }
    }

    private static System.Windows.Controls.TextBlock Cell(string text, object brush, bool right = false)
    {
        var cell = new System.Windows.Controls.TextBlock
        {
            FontSize = 12,
            HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };

        cell.Inlines.Add(Paint(new System.Windows.Documents.Run(text), brush));
        return cell;
    }

    /// <summary>Кисть темы — по ключу, чтобы следовать за темой; своя — как есть.</summary>
    private static System.Windows.Documents.Run Paint(System.Windows.Documents.Run run, object brush)
    {
        if (brush is string key)
        {
            run.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, key);
        }
        else
        {
            run.Foreground = (System.Windows.Media.Brush)brush;
        }

        return run;
    }

    /// <summary>Маска сработала — зелёным, ничего не задела — оранжевым: так в программе отмечено «обратите внимание».</summary>
    private static readonly System.Windows.Media.Brush OkBrush = Frozen(0x3F, 0xA3, 0x5A);

    private static readonly System.Windows.Media.Brush WarnBrush = Frozen(0xE0, 0x7A, 0x1F);

    /// <summary>Вкладка под замком: окно только показывает исключения.</summary>
    private readonly bool _viewOnly;

    private const string LockedText = "Вкладка закреплена за своим сервером — исключения здесь только посмотреть. Изменить можно, сняв замок.";

    /// <summary>Отказ под нажатой кнопкой — своей плашкой окна: плашки главного окна под ним не видно.</summary>
    private void RefuseLocked(FrameworkElement button)
    {
        var anchor = button.TransformToVisual(Toast.Parent as System.Windows.Media.Visual ?? this)
            .TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
        Toast.Show(AppLog.WarnInPlace(LockedText), anchor);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_viewOnly)
        {
            RefuseLocked(SaveButton);
            return;
        }

        if (!Changed())
        {
            Toast.Show(AppLog.WarnInPlace(SaveGate.Nothing), SaveGate.Anchor(SaveButton, Toast.Parent as System.Windows.Media.Visual ?? this));
            return;
        }

        // пустые строки и пробелы по краям не храним
        Own = ComposeOwn();
        Global = string.Join(Environment.NewLine, Lines(GlobalBox.Text));

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>
    /// Крестик при несохранённых правках — вопрос: «Сохранить», «Не сохранять» или остаться в окне.
    /// Кнопки окна решают сами: у «Сохранить» и «Отмены» результат уже стоит, и вопроса нет.
    /// </summary>
    private void OnClosingAsk(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_viewOnly || DialogResult.HasValue || !Changed())
        {
            return;
        }

        e.Cancel = true;

        var choice = ConfirmDialog.Choose(
            this,
            "Сохранить изменения в исключениях?",
            _pending?.HasChanges == true
                ? "Без сохранения пропадут правки масок и галочки, поставленные в «Проводнике»."
                : "Без сохранения правки масок пропадут.",
            "Сохранить",
            "Не сохранять");

        // решение — уже после того, как закрытие отменено: закрывать окно изнутри его же закрытия нельзя
        if (choice == 1)
        {
            Dispatcher.BeginInvoke(new Action(() => OnSave(this, new RoutedEventArgs())));
        }
        else if (choice == 2)
        {
            Dispatcher.BeginInvoke(new Action(() => DialogResult = false));
        }
    }
}
