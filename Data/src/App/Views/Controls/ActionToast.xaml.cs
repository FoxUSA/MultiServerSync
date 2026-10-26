using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MultiServerSync.Services;

namespace MultiServerSync.Views.Controls;

/// <summary>
/// Тихая плашка в правом нижнем углу: что сделано по последнему нажатию. Без неё кнопки
/// вроде «Копировать» или «Удалить» выглядели так, будто ничего не произошло, — запись
/// уходила только в журнал. Плашка повторяет запись журнала, держится несколько секунд
/// и гаснет; новая запись заменяет прежнюю. Строки отдельных серверов по ходу операции
/// сюда не идут — их десятки, итог операции и так придёт отдельной записью.
/// </summary>
public partial class ActionToast : UserControl
{
    private readonly DispatcherTimer _timer = new();

    public ActionToast()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Hide();
    }

    /// <summary>
    /// Крупная плашка — для той, что ставится посреди рабочего места (поля команды),
    /// а не в угол окна: там мелкую не замечают.
    /// </summary>
    public bool Large
    {
        set
        {
            Message.FontSize = value ? 15 : 12;
            Card.MaxWidth = value ? 820 : 460;
            Body.Padding = value ? new Thickness(18, 12, 18, 12) : new Thickness(12, 8, 12, 8);
            Dot.Width = Dot.Height = value ? 9 : 7;
            Dot.Margin = value ? new Thickness(0, 7, 11, 0) : new Thickness(0, 5, 9, 0);
        }
    }

    /// <summary>Угловая плашка: повтор записи журнала. Записи, показанные на месте, она пропускает.</summary>
    public void Show(LogEntry entry)
    {
        if (entry.ShownInPlace || entry.IsServerLine)
        {
            return;
        }

        Display(entry);
    }

    /// <summary>Плашка на месте: показывает запись, сделанную <see cref="AppLog.WarnInPlace"/>.</summary>
    public void ShowInPlace(LogEntry entry) => Display(entry);

    private void Display(LogEntry entry)
    {
        Message.Text = entry.Text;
        Dot.Fill = DotBrush(entry.Level);

        Visibility = Visibility.Visible;
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));

        // длинную запись дольше читать
        _timer.Stop();
        _timer.Interval = TimeSpan.FromSeconds(Math.Clamp(3 + entry.Text.Length / 40.0, 3, 8));
        _timer.Start();
    }

    private void Hide()
    {
        _timer.Stop();

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(400));
        fade.Completed += (_, _) =>
        {
            // за время угасания могла прийти новая запись — тогда плашка остаётся
            if (!_timer.IsEnabled)
            {
                Visibility = Visibility.Collapsed;
            }
        };

        BeginAnimation(OpacityProperty, fade);
    }

    private Brush DotBrush(LogLevel level)
    {
        var key = level switch
        {
            LogLevel.Success => "SystemFillColorSuccessBrush",
            LogLevel.Warning => "SystemFillColorCautionBrush",
            LogLevel.Error => "SystemFillColorCriticalBrush",
            _ => "AccentTextFillColorPrimaryBrush",
        };

        return TryFindResource(key) as Brush ?? Brushes.Gray;
    }
}
