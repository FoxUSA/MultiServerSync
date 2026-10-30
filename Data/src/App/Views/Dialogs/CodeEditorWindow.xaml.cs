using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using MultiServerSync.Services;
using MultiServerSync.Views.Controls;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MultiServerSync.Views.Dialogs;

/// <summary>
/// «Править»: свой редактор для конфигов и скриптов — на сервере блокнота нет, а у пользователя
/// может не оказаться нормального. Номера строк, подсветка, поиск с заменой. Сохранение ничего
/// не портит: кодировка, BOM и концы строк — как были (вставленный текст подстраивается под
/// концы строк файла), пишется в тот же файл (владелец и права прежние), а если файл успели
/// поменять после открытия — спросит, прежде чем затереть. После записи сверяется SHA-256.
/// </summary>
public partial class CodeEditorWindow : FluentWindow
{
    /// <summary>Открытые редакторы: второй раз тот же файл — к уже открытому окну.</summary>
    private static readonly Dictionary<string, CodeEditorWindow> Opened = new(StringComparer.Ordinal);

    private readonly IFileSource _source;
    private readonly RemoteSession? _session;
    private readonly string _path;
    private readonly string _name;
    private readonly string _key;
    private readonly string _where;
    private readonly FindMarker _marker = new();

    private TextFile? _file;

    /// <summary>SHA-256 того, что сейчас в файле по нашим сведениям: при открытии и после сохранения.</summary>
    private string _hash = string.Empty;

    private bool _busy;
    private bool _closeAnyway;

    private CodeEditorWindow(IFileSource source, string path, string name, string key)
    {
        InitializeComponent();

        // у редактора своя ссылка на сессию: панель может уйти на другой сервер, а правка — остаться
        if (source is RemoteSource remote)
        {
            _session = RemoteSession.Acquire(remote.Server, AppState.Current.Settings);
            _source = new RemoteSource(_session);
            _where = remote.Server.Display;
        }
        else
        {
            _source = source;
            _where = LocalSource.ComputerTitle;
        }

        _path = path;
        _name = name;
        _key = key;

        PathText.Text = $"{_where}:  {source.Display(path)}";
        PathText.ToolTip = PathText.Text;
        UpdateTitle();
        SetupEditor();

        Closing += OnClosing;
        Closed += (_, _) =>
        {
            Opened.Remove(_key);
            _session?.Release();
        };
    }

    /// <summary>Открыть файл в редакторе, а если он уже открыт — показать то окно.</summary>
    public static void Open(Window? owner, IFileSource source, string path, string name)
    {
        var key = source is RemoteSource remote ? $"{remote.Server.Id}|{path}" : "local|" + path.ToUpperInvariant();

        if (Opened.TryGetValue(key, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            return;
        }

        var window = new CodeEditorWindow(source, path, name, key);

        if (owner is { IsLoaded: true })
        {
            window.Owner = owner;
        }

        Opened[key] = window;
        window.Show();
        _ = window.LoadAsync();
    }

    private bool IsRemote => _session is not null;

    private void SetupEditor()
    {
        var dark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
        CodeHighlighting.ApplyTheme(dark);

        var options = Editor.Options;
        options.ConvertTabsToSpaces = false;
        options.EnableHyperlinks = false;
        options.EnableEmailHyperlinks = false;
        options.HighlightCurrentLine = true;

        var area = Editor.TextArea;
        var accent = TryFindResource("SystemAccentColor") is Color a ? a : Color.FromRgb(0x00, 0x78, 0xD4);
        area.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x70, accent.R, accent.G, accent.B));
        area.SelectionForeground = null;
        area.SelectionBorder = null;
        area.SelectionCornerRadius = 2;
        area.Caret.CaretBrush = TryFindResource("TextFillColorPrimaryBrush") as Brush;
        area.TextView.CurrentLineBackground = new SolidColorBrush(dark ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0C, 0, 0, 0));
        area.TextView.CurrentLineBorder = null;
        area.TextView.BackgroundRenderers.Add(_marker);
        _marker.Brush = new SolidColorBrush(dark ? Color.FromArgb(0x60, 0xD1, 0x8B, 0x16) : Color.FromArgb(0x60, 0xF5, 0xC5, 0x18));

        area.Caret.PositionChanged += (_, _) => UpdateInfo();
        Editor.TextChanged += (_, _) => UpdateTitle();
        Editor.PreviewKeyDown += OnEditorKeyDown;
        PreviewKeyDown += OnWindowKeyDown;
    }

    // ── чтение ───────────────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        _busy = true;
        ShowMessage("Файл читается…");

        try
        {
            var data = await _source.ReadFileAsync(_path, TextFile.Limit);

            if (TextFile.Decode(data) is not { } file)
            {
                ShowMessage("Это не текстовый файл — в редакторе он не правится, чтобы не испортить.");
                return;
            }

            _file = file;
            _hash = Sha(data);

            Editor.SyntaxHighlighting = CodeHighlighting.For(_name, file.Text, IsRemote);
            Editor.Document = new TextDocument(file.Text);
            Editor.Document.UndoStack.ClearAll();
            Editor.IsModified = false;
            Editor.Visibility = Visibility.Visible;
            Message.Visibility = Visibility.Collapsed;
            SaveButton.IsEnabled = true;

            UpdateTitle();
            UpdateInfo();
            Status($"Открыт: {Size(data.Length)}");
            Editor.Focus();
            Editor.TextArea.Focus();
        }
        catch (Exception ex)
        {
            ShowMessage($"Файл не открылся: {Reason(ex)}");
        }
        finally
        {
            _busy = false;
        }
    }

    private void ShowMessage(string text)
    {
        Message.Text = text;
        Message.Visibility = Visibility.Visible;
        Editor.Visibility = Visibility.Hidden;
    }

    private async void OnReload(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (Editor.IsModified
            && !ConfirmDialog.Ask(this, "Перечитать файл?", "Несохранённые правки пропадут.", "Перечитать"))
        {
            return;
        }

        var caret = Editor.TextArea.Caret.Line;
        await LoadAsync();

        if (_file is not null && caret <= Editor.Document.LineCount)
        {
            Editor.TextArea.Caret.Line = caret;
            Editor.ScrollToLine(caret);
        }
    }

    // ── сохранение ───────────────────────────────────────────────────────────

    private async void OnSave(object sender, RoutedEventArgs e) => await SaveAsync();

    private async Task<bool> SaveAsync()
    {
        if (_file is not { } file || _busy)
        {
            return false;
        }

        byte[] data;

        try
        {
            data = file.Encode(Editor.Text);
        }
        catch (EncoderFallbackException)
        {
            Status($"Не сохранено: в тексте есть знаки, которых нет в кодировке файла ({file.EncodingName})", error: true);
            return false;
        }

        _busy = true;
        SaveButton.IsEnabled = false;
        Status("Сохраняется…");

        try
        {
            // файл поменяли после открытия (на сервере, другой программой) — молча не затирать
            var now = await _source.HashAsync(_path, CancellationToken.None);

            if (!string.Equals(now, _hash, StringComparison.OrdinalIgnoreCase)
                && !ConfirmDialog.Ask(
                    this,
                    "Файл изменился после открытия",
                    "Его поменяли, пока он был открыт здесь. Сохранить — значит заменить те изменения вашими. «Перечитать» покажет новую версию.",
                    "Заменить"))
            {
                Status("Не сохранено: файл изменился после открытия");
                return false;
            }

            var expected = Sha(data);
            await _source.WriteFileAsync(_path, data);

            var written = await _source.HashAsync(_path, CancellationToken.None);

            if (!string.Equals(written, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("после записи сумма файла не совпала с сохранённым текстом — откройте файл заново и проверьте");
            }

            _hash = expected;
            Editor.IsModified = false;
            UpdateTitle();
            Status($"Сохранено в {DateTime.Now:HH:mm:ss}, SHA-256 сверена");
            ActionLog.Success($"Изменён файл {_source.Display(_path)}", ActionLog.Where(_source), AppSection.Explorer, null,
                ["сохранено из встроенного редактора", $"SHA-256 после записи сверена: {expected}"]);
            return true;
        }
        catch (Exception ex)
        {
            Status($"Не сохранено: {Reason(ex)}", error: true);
            ActionLog.Error($"Не сохранён файл {_source.Display(_path)}: {Reason(ex)}", ActionLog.Where(_source), AppSection.Explorer);
            return false;
        }
        finally
        {
            _busy = false;
            SaveButton.IsEnabled = true;
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeAnyway || !Editor.IsModified)
        {
            return;
        }

        e.Cancel = true;

        var choice = ConfirmDialog.Choose(this, $"Сохранить изменения в {_name}?", string.Empty, "Сохранить", "Не сохранять");

        if (choice == 0 || (choice == 1 && !await SaveAsync()))
        {
            return;
        }

        _closeAnyway = true;
        Close();
    }

    // ── клавиши ──────────────────────────────────────────────────────────────

    private async void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        var shift = Keyboard.Modifiers == ModifierKeys.Shift;

        if (ctrl && e.Key == Key.S)
        {
            e.Handled = true;
            await SaveAsync();
        }
        else if (ctrl && e.Key == Key.F)
        {
            e.Handled = true;
            OpenFind(replace: false);
        }
        else if (ctrl && e.Key == Key.H)
        {
            e.Handled = true;
            OpenFind(replace: true);
        }
        else if (ctrl && e.Key == Key.G)
        {
            e.Handled = true;
            GoToLine();
        }
        else if (e.Key == Key.F3 && (Keyboard.Modifiers == ModifierKeys.None || shift))
        {
            e.Handled = true;
            Find(forward: !shift);
        }
        else if (e.Key == Key.Escape && FindBar.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            CloseFind();
        }
    }

    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+колесо меняет размер шрифта в окне, а Ctrl+0 возвращает
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.D0 or Key.NumPad0)
        {
            e.Handled = true;
            Editor.FontSize = 13;
        }
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && Editor.IsMouseOver)
        {
            e.Handled = true;
            Editor.FontSize = Math.Clamp(Editor.FontSize + (e.Delta > 0 ? 1 : -1), 8, 32);
            return;
        }

        base.OnPreviewMouseWheel(e);
    }

    private void GoToLine()
    {
        if (_file is null)
        {
            return;
        }

        var count = Editor.Document.LineCount;
        var dialog = new InputDialog("Перейти к строке", $"Номер строки, от 1 до {count}:", Editor.TextArea.Caret.Line.ToString()) { Owner = this };

        if (dialog.ShowDialog() == true && int.TryParse(dialog.Value, out var line))
        {
            line = Math.Clamp(line, 1, count);
            Editor.TextArea.Caret.Location = new TextLocation(line, 1);
            Editor.ScrollToLine(line);
            Editor.TextArea.Focus();
        }
    }

    private void OnWrap(object sender, RoutedEventArgs e) => Editor.WordWrap = WrapBox.IsChecked == true;

    // ── поиск и замена ───────────────────────────────────────────────────────

    private void OnFind(object sender, RoutedEventArgs e) => OpenFind(replace: false);

    private void OpenFind(bool replace)
    {
        if (_file is null)
        {
            return;
        }

        FindBar.Visibility = Visibility.Visible;

        // выделенное в одной строке — сразу в поиск, как в VS Code
        if (Editor.SelectionLength is > 0 and < 200 && !Editor.SelectedText.Contains('\n'))
        {
            FindBox.Text = Editor.SelectedText;
        }

        var box = replace ? ReplaceBox : FindBox;
        box.Focus();
        box.SelectAll();
        UpdateMarks();
    }

    private void OnCloseFind(object sender, RoutedEventArgs e) => CloseFind();

    private void CloseFind()
    {
        FindBar.Visibility = Visibility.Collapsed;
        _marker.Pattern = null;
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        Editor.TextArea.Focus();
    }

    private StringComparison Comparison => CaseBox.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private void OnFindChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        UpdateMarks();

        // по мере набора — к первому совпадению от курсора, как в VS Code
        if (FindBox.Text.Length > 0)
        {
            Find(forward: true, from: Editor.SelectionStart);
        }
    }

    private void OnCaseChanged(object sender, RoutedEventArgs e) => UpdateMarks();

    private void OnFindNext(object sender, RoutedEventArgs e) => Find(forward: true);

    private void OnFindPrevious(object sender, RoutedEventArgs e) => Find(forward: false);

    private void OnFindKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Find(forward: Keyboard.Modifiers != ModifierKeys.Shift);
        }
    }

    private void OnReplaceKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ReplaceOne();
        }
    }

    private void UpdateMarks()
    {
        _marker.Pattern = FindBox.Text.Length > 0 ? FindBox.Text : null;
        _marker.Comparison = Comparison;
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);

        if (_marker.Pattern is not { } pattern || _file is null)
        {
            FindCount.Text = string.Empty;
            return;
        }

        var count = Occurrences(Editor.Text, pattern, Comparison).Count();
        FindCount.Text = count == 0 ? "нет совпадений" : $"найдено: {count}";
    }

    private void Find(bool forward, int? from = null)
    {
        var pattern = FindBox.Text;

        if (pattern.Length == 0 || _file is null)
        {
            if (FindBar.Visibility != Visibility.Visible)
            {
                OpenFind(replace: false);
            }

            return;
        }

        var text = Editor.Text;
        var start = from ?? (forward ? Editor.SelectionStart + Editor.SelectionLength : Editor.SelectionStart);

        // по кругу: дошли до конца — с начала
        var at = forward
            ? text.IndexOf(pattern, Math.Min(start, text.Length), Comparison) is var f and >= 0 ? f : text.IndexOf(pattern, Comparison)
            : start > 0 && text.LastIndexOf(pattern, start - 1, Comparison) is var b and >= 0 ? b : text.LastIndexOf(pattern, Comparison);

        if (at < 0)
        {
            return;
        }

        Editor.Select(at, pattern.Length);
        var location = Editor.Document.GetLocation(at);
        Editor.ScrollTo(location.Line, location.Column);
    }

    private void OnReplace(object sender, RoutedEventArgs e) => ReplaceOne();

    private void ReplaceOne()
    {
        var pattern = FindBox.Text;

        if (pattern.Length == 0 || _file is null)
        {
            return;
        }

        if (string.Equals(Editor.SelectedText, pattern, Comparison))
        {
            Editor.Document.Replace(Editor.SelectionStart, Editor.SelectionLength, ReplaceBox.Text);
        }

        Find(forward: true);
        UpdateMarks();
    }

    private void OnReplaceAll(object sender, RoutedEventArgs e)
    {
        var pattern = FindBox.Text;

        if (pattern.Length == 0 || _file is null)
        {
            return;
        }

        var hits = Occurrences(Editor.Text, pattern, Comparison).ToList();
        var document = Editor.Document;

        // одной правкой: Ctrl+Z вернёт всё разом
        document.BeginUpdate();

        try
        {
            for (var i = hits.Count - 1; i >= 0; i--)
            {
                document.Replace(hits[i], pattern.Length, ReplaceBox.Text);
            }
        }
        finally
        {
            document.EndUpdate();
        }

        UpdateMarks();
        Status(hits.Count == 0 ? "Заменять нечего" : $"Заменено: {hits.Count}");
    }

    private static IEnumerable<int> Occurrences(string text, string pattern, StringComparison comparison)
    {
        for (var at = text.IndexOf(pattern, comparison); at >= 0; at = text.IndexOf(pattern, at + pattern.Length, comparison))
        {
            yield return at;
        }
    }

    // ── строка состояния и заголовок ─────────────────────────────────────────

    private void UpdateTitle() => Title = (Editor.IsModified ? "● " : string.Empty) + $"{_name} — {_where}";

    private void UpdateInfo()
    {
        if (_file is not { } file)
        {
            InfoText.Text = string.Empty;
            return;
        }

        var caret = Editor.TextArea.Caret;
        var syntax = Editor.SyntaxHighlighting?.Name ?? "текст";
        InfoText.Text = $"Строка {caret.Line}, столбец {caret.Column}    {file.EncodingName}    {TextFile.NewLineName(Editor.Text)}    {syntax}";
    }

    private void Status(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = error
            ? TryFindResource("SystemFillColorCriticalBrush") as Brush
            : TryFindResource("TextFillColorPrimaryBrush") as Brush;
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static string Size(long bytes) => bytes < 1024 ? $"{bytes} Б" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} КБ" : $"{bytes / 1024.0 / 1024:0.#} МБ";

    private static string Reason(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "нет прав на этот файл",
        Renci.SshNet.Common.SftpPermissionDeniedException => "нет прав на этот файл",
        Renci.SshNet.Common.SftpPathNotFoundException or FileNotFoundException => "файла уже нет",
        _ => ex.Message,
    };

    /// <summary>Подсветка всех совпадений поиска в видимых строках.</summary>
    private sealed class FindMarker : IBackgroundRenderer
    {
        public string? Pattern { get; set; }

        public StringComparison Comparison { get; set; }

        public Brush? Brush { get; set; }

        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (Pattern is not { Length: > 0 } pattern || textView.Document is not { } document || !textView.VisualLinesValid)
            {
                return;
            }

            foreach (var line in textView.VisualLines)
            {
                var start = line.FirstDocumentLine.Offset;
                var end = line.LastDocumentLine.EndOffset;
                var text = document.GetText(start, end - start);

                foreach (var at in Occurrences(text, pattern, Comparison))
                {
                    var builder = new BackgroundGeometryBuilder { CornerRadius = 2, AlignToWholePixels = true };
                    builder.AddSegment(textView, new TextSegment { StartOffset = start + at, Length = pattern.Length });

                    if (builder.CreateGeometry() is { } geometry)
                    {
                        drawingContext.DrawGeometry(Brush, null, geometry);
                    }
                }
            }
        }
    }
}
