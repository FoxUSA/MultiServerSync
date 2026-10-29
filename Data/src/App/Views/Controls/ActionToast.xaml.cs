using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MultiServerSync.Services;

namespace MultiServerSync.Views.Controls;

/// <summary>Страница, у которой своя плашка о нажатии — у главной кнопки, а не в углу окна.</summary>
public interface IToastHost
{
    ActionToast PageToast { get; }
}

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
            Body.Padding = value ? new Thickness(18, 12, 18, 12) : new Thickness(12, 7, 12, 7);
            Dot.Width = Dot.Height = value ? 9 : 7;
            Dot.Margin = value ? new Thickness(0, 7, 11, 0) : new Thickness(0, 5, 9, 0);
        }
    }

    /// <summary>Угловая плашка: повтор записи журнала. Записи, показанные на месте, она пропускает.</summary>
    /// <returns>Плашка показала запись.</returns>
    public bool Show(LogEntry entry)
    {
        if (entry.ShownInPlace || entry.IsServerLine)
        {
            return false;
        }

        Display(entry);
        return true;
    }

    /// <summary>Погасить сразу, без угасания: новая запись встала в другую плашку или сменился раздел.</summary>
    public void HideNow()
    {
        _timer.Stop();
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        Visibility = Visibility.Collapsed;
    }

    /// <summary>Плашка на месте: показывает запись, сделанную <see cref="AppLog.WarnInPlace"/>.</summary>
    public void ShowInPlace(LogEntry entry) => Display(entry);

    private FrameworkElement? _anchor;

    /// <summary>
    /// Плашка встаёт сразу справа от кнопки, по её середине. Сама плашка лежит в сетке страницы
    /// поверх всех рядов: место пересчитывается, а раскладку она не раздвигает.
    /// </summary>
    public void Follow(FrameworkElement anchor)
    {
        _anchor = anchor;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;

        anchor.SizeChanged += (_, _) => Place();
        SizeChanged += (_, _) => Place();

        if (Parent is FrameworkElement parent)
        {
            parent.SizeChanged += (_, _) => Place();
        }
    }

    private void Place()
    {
        if (_anchor is not { IsVisible: true } anchor || Parent is not FrameworkElement parent)
        {
            return;
        }

        Point spot;

        try
        {
            spot = anchor.TranslatePoint(new Point(anchor.ActualWidth, anchor.ActualHeight / 2), parent);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        var left = spot.X + 8;
        Card.MaxWidth = Math.Max(200, Math.Min(460, parent.ActualWidth - left - 4));
        // снизу запас с избытком: кнопка стоит у самого низа страницы, плашка чуть выше неё —
        // без запаса сетка ужимала плашку под остаток места и у строки срезался низ (хвосты «у», «р», «д»)
        Margin = new Thickness(left, Math.Max(0, spot.Y - ActualHeight / 2), 0, -1000);
    }

    private void Display(LogEntry entry)
    {
        Message.Text = entry.Text;
        Dot.Fill = DotBrush(entry.Level);

        // предупреждение — тусклой оранжевой рамкой, как истёкший «Хранить лог»; остальное — рамкой темы
        // рамка всегда тусклая оранжевая — рамкой темы плашку было плохо видно; предупреждение
        // ещё и на тусклом оранжевом фоне, ошибка — на тусклом красном и в красной рамке.
        // Фон полупрозрачный поверх плашки под ним, поэтому тема остаётся видна
        switch (entry.Level)
        {
            case LogLevel.Error:
                Body.BorderBrush = ErrorStroke;
                Body.Background = ErrorFill;
                break;

            case LogLevel.Warning:
                Body.BorderBrush = WarningStroke;
                Body.Background = WarningFill;
                break;

            default:
                Body.BorderBrush = WarningStroke;
                Body.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorTertiaryBrush");
                break;
        }

        Visibility = Visibility.Visible;
        Place();
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

    private static readonly Brush WarningStroke = Freeze(new SolidColorBrush(Color.FromArgb(0x80, 0xE0, 0x7A, 0x1F)));
    private static readonly Brush WarningFill = Freeze(new SolidColorBrush(Color.FromArgb(0x38, 0xE0, 0x7A, 0x1F)));
    private static readonly Brush ErrorStroke = Freeze(new SolidColorBrush(Color.FromArgb(0x80, 0xE8, 0x5C, 0x5C)));
    private static readonly Brush ErrorFill = Freeze(new SolidColorBrush(Color.FromArgb(0x38, 0xE8, 0x5C, 0x5C)));

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
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
