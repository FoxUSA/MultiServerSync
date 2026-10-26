using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MultiServerSync.Services;
using MultiServerSync.Views.Dialogs;

namespace MultiServerSync.Views.Controls;

public enum TransferDirection
{
    /// <summary>«Копирование»: с компьютера на серверы.</summary>
    Upload,

    /// <summary>«Скачивание»: с серверов сюда.</summary>
    Download,

    /// <summary>«Проводник»: в обе стороны.</summary>
    Both,
}

/// <summary>
/// Параметры переноса — один блок на три раздела. Смысл параметров везде один и тот же,
/// меняются только подписи: куда идёт перенос.
/// </summary>
public partial class TransferOptionsPanel : UserControl
{
    public static readonly DependencyProperty DirectionProperty = DependencyProperty.Register(
        nameof(Direction),
        typeof(TransferDirection),
        typeof(TransferOptionsPanel),
        new PropertyMetadata(TransferDirection.Upload, (d, _) => ((TransferOptionsPanel)d).ApplyTexts()));

    public TransferOptionsPanel()
    {
        InitializeComponent();

        // у каждого блока своя группа кнопок: на одной странице их может быть два (вкладка и окно)
        var group = "Mode" + Guid.NewGuid().ToString("N");

        foreach (var choice in new[] { AllChoice, MissingChoice, DateChoice, HashChoice })
        {
            choice.GroupName = group;
        }

        // свёрнутая строка повторяет то, что выбрано, — при любом щелчке и при смене вкладки
        foreach (var toggle in new ToggleButton[] { AllChoice, MissingChoice, DateChoice, HashChoice, DateOption, VerifyOption, ReplaceOption })
        {
            toggle.Checked += (_, _) => UpdateSummary();
            toggle.Unchecked += (_, _) => UpdateSummary();
        }

        DataContextChanged += (_, _) => Dispatcher.BeginInvoke(new Action(UpdateSummary));

        ApplyTexts();
    }

    /// <summary>Свернуть можно у «Копирования» и «Скачивания»; в окне «Проводника» блок всегда целиком.</summary>
    private bool CanFold => Direction != TransferDirection.Both;

    private bool Folded
    {
        get => Direction == TransferDirection.Download
            ? AppState.Current.Settings.DownloadOptionsFolded
            : AppState.Current.Settings.UploadOptionsFolded;
        set
        {
            if (Direction == TransferDirection.Download)
            {
                AppState.Current.Settings.DownloadOptionsFolded = value;
            }
            else
            {
                AppState.Current.Settings.UploadOptionsFolded = value;
            }

            AppState.Current.SaveSettings();
        }
    }

    private void OnFold(object sender, RoutedEventArgs e)
    {
        Folded = !Folded;
        ApplyFold();
    }

    /// <summary>Надпись «Параметры копирования» — как стрелка: сворачивает и разворачивает.</summary>
    private void OnHeaderClick(object sender, MouseButtonEventArgs e)
    {
        if (CanFold)
        {
            e.Handled = true;
            OnFold(sender, e);
        }
    }

    /// <summary>Свёрнутая строка раскрывается щелчком в любом её месте; сворачивается — только стрелкой или надписью.</summary>
    private void OnFrameClick(object sender, MouseButtonEventArgs e)
    {
        if (CanFold && Folded)
        {
            e.Handled = true;
            OnFold(sender, e);
        }
    }

    /// <summary>Свёрнуто — шапка и строка выбранного; развёрнуто — все параметры.</summary>
    private void ApplyFold()
    {
        var folded = CanFold && Folded;

        FoldButton.Visibility = CanFold ? Visibility.Visible : Visibility.Collapsed;
        FoldIcon.Symbol = folded ? Wpf.Ui.Controls.SymbolRegular.ChevronRight24 : Wpf.Ui.Controls.SymbolRegular.ChevronDown24;
        FoldButton.ToolTip = folded ? "Развернуть параметры" : "Свернуть в одну строку";
        Body.Visibility = folded ? Visibility.Collapsed : Visibility.Visible;
        SummaryText.Visibility = folded ? Visibility.Visible : Visibility.Collapsed;
        HeaderRow.Margin = folded ? new Thickness(0) : new Thickness(0, 0, 0, 4);
        Frame.Cursor = folded ? Cursors.Hand : null;
        Frame.ToolTip = folded ? "Развернуть параметры" : null;
        HeaderText.Cursor = CanFold ? Cursors.Hand : null;

        UpdateSummary();
    }

    /// <summary>Выбранный режим и включённые галочки — через точку, теми же словами, что в блоке.</summary>
    private void UpdateSummary()
    {
        var mode = new[] { AllChoice, MissingChoice, DateChoice, HashChoice }.FirstOrDefault(c => c.IsChecked == true);
        var options = new[] { DateOption, VerifyOption, ReplaceOption }
            .Where(o => o.IsChecked == true && o.Visibility == Visibility.Visible);

        var parts = new ContentControl?[] { mode }.Concat(options).OfType<ContentControl>().Select(c => c.Content as string).OfType<string>();
        SummaryText.Text = string.Join("  ·  ", parts);
        SummaryText.ToolTip = SummaryText.Text;
    }

    public TransferDirection Direction
    {
        get => (TransferDirection)GetValue(DirectionProperty);
        set => SetValue(DirectionProperty, value);
    }

    private void ApplyTexts()
    {
        switch (Direction)
        {
            case TransferDirection.Download:
                HeaderText.Text = "ПАРАМЕТРЫ СКАЧИВАНИЯ";
                ModeTitle.Text = "Скачивать с сервера:";
                Set(AllChoice, "все файлы", "Каждый файл скачивается заново, что бы ни лежало в папке.");
                Set(MissingChoice, "только те, которых здесь нет", "Файлы, которые уже есть в папке, не трогаются совсем.");
                Set(DateChoice, "отличающиеся по дате изменения", "Файл скачивается, если дата изменения или размер разошлись с серверными.");
                Set(HashChoice, "отличающиеся по контрольной сумме",
                    "Где размер совпал, сравниваются SHA-256 здесь и на сервере; скачивается только то, что отличается по содержимому.");
                Set(DateOption, "сохранять дату изменения", "Скачанному файлу ставится дата изменения, как на сервере");
                Set(VerifyOption, "после скачивания сверять суммы",
                    "Проверка уже скачанного: SHA-256 здесь против sha256sum на сервере — только для файлов этого захода.");
                ExcludeButton.Visibility = Visibility.Collapsed;
                ReplaceOption.Visibility = Visibility.Collapsed;
                break;

            case TransferDirection.Both:
                HeaderText.Text = "ПАРАМЕТРЫ КОПИРОВАНИЯ";
                ModeTitle.Text = "Копировать между компьютером и сервером:";
                Set(AllChoice, "все выделенные файлы", "Каждый файл копируется заново, что бы ни лежало в папке назначения.");
                Set(MissingChoice, "только те, которых там нет", "Файлы, которые уже есть в папке назначения, не трогаются совсем.");
                Set(DateChoice, "отличающиеся по дате изменения", "Файл копируется, если дата изменения или размер разошлись.");
                Set(HashChoice, "отличающиеся по контрольной сумме",
                    "Копируется только то, что отличается по содержимому (SHA-256). Надёжнее всего, но на больших объёмах это отдельная сверка.");
                Set(DateOption, "сохранять дату изменения", "Скопированному файлу ставится дата изменения оригинала");
                Set(VerifyOption, "после копирования сверять суммы",
                    "Проверка уже скопированного: SHA-256 с обеих сторон — только для файлов этого захода.");
                ExcludeButton.Visibility = Visibility.Visible;
                ReplaceOption.Visibility = Visibility.Visible;
                break;

            default:
                HeaderText.Text = "ПАРАМЕТРЫ КОПИРОВАНИЯ";
                ModeTitle.Text = "Отправлять на сервер:";
                Set(AllChoice, "все отмеченные файлы",
                    "Каждый отмеченный файл отправляется заново, что бы ни лежало на сервере. Ничего не сверяется — дольше всего, но результат заведомо совпадает с локальным.");
                Set(MissingChoice, "только те, которых нет на сервере", "Существующие файлы не трогаются совсем, даже если они отличаются.");
                Set(DateChoice, "отличающиеся по дате изменения",
                    "Файл отправляется, если дата изменения разошлась с серверной в любую сторону. Размер сверяется всегда. Нужна галочка «сохранять дату изменения».");
                Set(HashChoice, "отличающиеся по контрольной сумме",
                    "Перед копированием на сервере считается sha256sum файлов назначения, и отправляется только то, что отличается по содержимому. Надёжнее всего, но на больших объёмах это отдельная сверка.");
                Set(DateOption, "сохранять дату изменения", "После загрузки файлу на сервере проставляется дата изменения локального оригинала");
                Set(VerifyOption, "после копирования сверять суммы",
                    "Проверка уже отправленного: после копирования на сервере запускается sha256sum и хеши сравниваются с локальными. Только для файлов этого захода.");
                ExcludeButton.Visibility = Visibility.Collapsed;
                ReplaceOption.Visibility = Visibility.Collapsed;
                break;
        }

        ApplyFold();
    }

    private static void Set(ContentControl control, string text, string tip)
    {
        control.Content = text;
        control.ToolTip = tip;
    }

    private void OnExcludes(object sender, RoutedEventArgs e)
    {
        if (DataContext is not Workspace workspace
            || ExcludeDialog.Ask(Window.GetWindow(this), workspace.ExcludeMasks) is not { } masks
            || masks == workspace.ExcludeMasks)
        {
            return;
        }

        workspace.ExcludeMasks = masks;
        var count = Core.ExcludeRules.Count(masks);
        AppLog.Hint(count > 0 ? $"Исключения при копировании: {count}." : "Исключения при копировании сняты.");
    }
}
