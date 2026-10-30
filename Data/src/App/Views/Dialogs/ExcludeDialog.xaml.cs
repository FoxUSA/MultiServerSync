using System.Windows;
using System.Windows.Threading;
using MultiServerSync.Core;
using MultiServerSync.Services;
using Wpf.Ui.Controls;

namespace MultiServerSync.Views.Dialogs;

/// <summary>
/// Исключения: свои у вкладки и общие на все вкладки «Копирования», «Проводника» и «Скачивания».
/// Готовые шаблоны — в один щелчок, «Стандартный набор» — всё лишнее разом.
/// </summary>
public partial class ExcludeDialog : FluentWindow
{
    private ExcludeDialog(string own, string global, bool useGlobal)
    {
        InitializeComponent();

        // зелёная рамка «Эта вкладка» горит, только пока в её поле есть маски, — прямо по ходу правки;
        // у «Глобально» рамки нет, их состояние — фиолетовый переключатель
        OwnBox.TextChanged += (_, _) => Ring(OwnTab, OwnBox.Text, OwnRingBrush);
        GlobalBox.TextChanged += (_, _) => ShowGlobalState();
        UseGlobalSwitch.Checked += (_, _) => ShowGlobalState();
        UseGlobalSwitch.Unchecked += (_, _) => ShowGlobalState();

        OwnBox.Text = own;
        GlobalBox.Text = global;
        UseGlobalSwitch.IsChecked = useGlobal;
        ShowGlobalState();
        TemplatesList.ItemsSource = ExcludeRules.Templates;

        // открывается там, где закрыли в прошлый раз
        Tabs.SelectedIndex = AppState.Current.Settings.ExcludeDialogOnGlobal ? 1 : 0;

        Loaded += (_, _) => FocusBox();
        Tabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, Tabs))
            {
                TemplateNote.Visibility = Visibility.Collapsed;
                FocusBox();
            }
        };

        Closed += (_, _) =>
        {
            var onGlobal = Tabs.SelectedIndex == 1;
            var settings = AppState.Current.Settings;

            if (settings.ExcludeDialogOnGlobal != onGlobal)
            {
                settings.ExcludeDialogOnGlobal = onGlobal;
                AppState.Current.SaveSettings();
            }
        };
    }

    private string Own { get; set; } = string.Empty;

    private string Global { get; set; } = string.Empty;

    private bool UseGlobal => UseGlobalSwitch.IsChecked == true;

    /// <summary>Поле открытой вкладки окна — шаблоны дописываются в него.</summary>
    private System.Windows.Controls.TextBox Box => Tabs.SelectedIndex == 1 ? GlobalBox : OwnBox;

    /// <summary>
    /// Открывает исключения вкладки и глобальные; сохранённое сразу ставит на место, итог — плашкой.
    /// <paramref name="action"/> — «копировании» или «скачивании», для подписи итога.
    /// </summary>
    public static void Edit(Window? owner, Workspace workspace, string action)
    {
        var state = AppState.Current;
        var dialog = new ExcludeDialog(workspace.ExcludeMasks, state.GlobalExcludeMasks, workspace.UseGlobalExcludes) { Owner = owner };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

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
            lines.AddRange(dialog.Own.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(m => "  " + m));
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

            UseGlobalSwitch.IsEnabled = !empty;
            GlobalBox.IsReadOnly = !empty && !UseGlobal;
            GlobalBox.Opacity = GlobalBox.IsReadOnly ? 0.45 : 1;

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
        Box.Focus();
        Box.CaretIndex = Box.Text.Length;
    });

    /// <summary>Открыто поле глобальных, а они на этой вкладке выключены: шаблоны туда не пишутся.</summary>
    private bool Locked()
    {
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

    /// <summary>Цвета плашки на кнопке «Исключения»: свои — тускло-зелёный, общие — фиолетовый.</summary>
    private static readonly System.Windows.Media.Brush OwnRingBrush = Frozen(0x2E, 0x7D, 0x3E);

    private static System.Windows.Media.Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>Есть маски — рамка ярлыка цветом исключений, нет — рамка обычной кнопки из стиля.</summary>
    private static void Ring(System.Windows.Controls.Control ring, string text, System.Windows.Media.Brush brush)
    {
        if (ExcludeRules.Count(text) > 0)
        {
            ring.BorderBrush = brush;
        }
        else
        {
            ring.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
        }
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

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // пустые строки и пробелы по краям не храним
        Own = string.Join(Environment.NewLine, Lines(OwnBox.Text));
        Global = string.Join(Environment.NewLine, Lines(GlobalBox.Text));

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
