using System.Windows;
using System.Windows.Controls;
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

        ApplyTexts();
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
                break;
        }
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
        AppLog.Info(count > 0 ? $"Исключения при копировании: {count}." : "Исключения при копировании сняты.", workspace.Title);
    }
}
