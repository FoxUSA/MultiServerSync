using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged old)
            {
                old.PropertyChanged -= OnTabChanged;
            }

            if (e.NewValue is INotifyPropertyChanged tab)
            {
                tab.PropertyChanged += OnTabChanged;
            }

            ApplyLock();
        };

        ApplyTexts();
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Workspace.IsPaneLocked))
        {
            ApplyLock();
        }
    }

    /// <summary>
    /// Под замком вкладки на «Копировании» и «Скачивании» параметры не меняются.
    /// В окне «Проводника» замок про сервер панели, а не про параметры.
    /// </summary>
    private void ApplyLock()
    {
        var locked = Direction != TransferDirection.Both && DataContext is Workspace { IsPaneLocked: true };

        Body.IsEnabled = !locked;
        LockMute.SetIsOn(Body, locked);

        // у каждого параметра своё описание: под замком оно остаётся, снизу добавляется про замок
        foreach (var toggle in Toggles)
        {
            LockMute.SetIsOn(toggle, locked);
        }
    }

    private ToggleButton[] Toggles => [AllChoice, MissingChoice, DateChoice, HashChoice, DateOption, VerifyOption, ReplaceOption];

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
                // подписи короткие, как на «Копировании»: блок занимает треть ширины страницы
                HeaderText.Text = "ПАРАМЕТРЫ СКАЧИВАНИЯ";
                ModeTitle.Text = "Скачивать с сервера:";
                Set(AllChoice, "все файлы", "Каждый файл скачивается заново, что бы ни лежало в папке.");
                Set(MissingChoice, "которых здесь нет", "Файлы, которые уже есть в папке, не трогаются совсем.");
                Set(DateChoice, "с другой датой", "Файл скачивается, если дата изменения или размер разошлись с серверными.");
                Set(HashChoice, "с другой суммой",
                    "Где размер совпал, сравниваются SHA-256 здесь и на сервере; скачивается только то, что отличается по содержимому.");
                Set(DateOption, "сохранять дату", "Скачанному файлу ставится дата изменения, как на сервере");
                Set(VerifyOption, "сверять суммы",
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
                // подписи короткие: блок занимает треть ширины страницы, и длинные в два столбца не встают
                Set(AllChoice, "все отмеченные",
                    "Каждый отмеченный файл отправляется заново, что бы ни лежало на сервере. Ничего не сверяется — дольше всего, но результат заведомо совпадает с локальным.");
                Set(MissingChoice, "которых нет на сервере", "Существующие файлы не трогаются совсем, даже если они отличаются.");
                Set(DateChoice, "с другой датой",
                    "Файл отправляется, если дата изменения разошлась с серверной в любую сторону. Размер сверяется всегда. Нужна галочка «сохранять дату».");
                Set(HashChoice, "с другой суммой",
                    "Перед копированием на сервере считается sha256sum файлов назначения, и отправляется только то, что отличается по содержимому. Надёжнее всего, но на больших объёмах это отдельная сверка.");
                Set(DateOption, "сохранять дату", "После загрузки файлу на сервере проставляется дата изменения локального оригинала");
                Set(VerifyOption, "сверять суммы",
                    "Проверка уже отправленного: после копирования на сервере запускается sha256sum и хеши сравниваются с локальными. Только для файлов этого захода.");
                ExcludeButton.Visibility = Visibility.Collapsed;
                ReplaceOption.Visibility = Visibility.Collapsed;
                break;
        }

        // на страницах столбцы плотнее; у окна «Проводника» отступ из стиля
        foreach (var toggle in Toggles)
        {
            if (Direction == TransferDirection.Both)
            {
                toggle.ClearValue(MarginProperty);
            }
            else
            {
                toggle.Margin = new Thickness(toggle is CheckBox ? -10.67 : 0, 1, 16, 1);
            }
        }
    }

    private static void Set(ContentControl control, string text, string tip)
    {
        control.Content = text;
        control.ToolTip = tip;
    }

    private void OnExcludes(object sender, RoutedEventArgs e)
    {
        if (DataContext is Workspace workspace)
        {
            ExcludeDialog.Edit(Window.GetWindow(this), workspace, "копировании");
        }
    }
}
