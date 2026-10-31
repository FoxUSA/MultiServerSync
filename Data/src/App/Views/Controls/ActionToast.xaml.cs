using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MultiServerSync.Services;

namespace MultiServerSync.Views.Controls;

/// <summary>
/// Плашка: что сделано по последнему нажатию. Без неё кнопки вроде «Копировать» или «Удалить»
/// выглядели так, будто ничего не произошло, — запись уходила только в журнал. Плашка повторяет
/// запись журнала, держится несколько секунд и гаснет; новая запись заменяет прежнюю. Строки
/// отдельных серверов по ходу операции сюда не идут — их десятки, итог операции и так придёт
/// отдельной записью. Отказ встаёт под нажатой кнопкой, всё остальное — посередине окна.
/// </summary>
public partial class ActionToast : UserControl
{
    private const double EdgeGap = 8;
    private const double ButtonGap = 4;

    private readonly DispatcherTimer _timer = new();

    /// <summary>Плашка окна сама ищет себе место; плашка на месте стоит там, где её положила разметка.</summary>
    private bool _floating;

    /// <summary>Кнопка, которой отказано, в координатах окна. Null — посередине окна.</summary>
    private Rect? _anchor;

    private double _maxWidth = 460;

    private Window? _window;

    public ActionToast()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Hide();

        // щелчок в любом месте окна закрывает плашку: прочитано, ждать таймер незачем.
        // Сквозь плашку щелчок проходит, поэтому и по ней самой тоже
        Loaded += (_, _) =>
        {
            ReleaseWindow();
            _window = Window.GetWindow(this);

            if (_window is not null)
            {
                _window.PreviewMouseDown += OnWindowPress;
            }
        };
        Unloaded += (_, _) => ReleaseWindow();
    }

    private void ReleaseWindow()
    {
        if (_window is not null)
        {
            _window.PreviewMouseDown -= OnWindowPress;
            _window = null;
        }
    }

    private void OnWindowPress(object sender, MouseButtonEventArgs e) => HideNow();

    /// <summary>
    /// Крупная плашка — для той, что ставится посреди рабочего места (поля команды),
    /// а не в угол окна: там мелкую не замечают.
    /// </summary>
    public bool Large
    {
        set
        {
            Message.FontSize = value ? 15 : 12;
            _maxWidth = value ? 820 : 460;
            Card.MaxWidth = _maxWidth;
            Body.Padding = value ? new Thickness(18, 12, 18, 12) : new Thickness(12, 7, 12, 7);
            Dot.Width = Dot.Height = value ? 9 : 7;
            Dot.Margin = value ? new Thickness(0, 7, 11, 0) : new Thickness(0, 5, 9, 0);
        }
    }

    /// <summary>Плашка окна: повтор записи журнала. Записи, показанные на месте, она пропускает.</summary>
    /// <param name="anchor">Кнопка, которой отказано, в координатах окна; null — посередине окна.</param>
    public void Show(LogEntry entry, Rect? anchor)
    {
        if (entry.ShownInPlace || entry.IsServerLine)
        {
            return;
        }

        if (!_floating)
        {
            _floating = true;

            // пока плашка видна, окно может поменять размер
            SizeChanged += (_, _) => Place();

            if (Parent is FrameworkElement parent)
            {
                parent.SizeChanged += (_, _) => Place();
            }
        }

        _anchor = anchor;

        // посередине окна мелкое предупреждение глаза не находят
        Large = anchor is null && entry.Level is LogLevel.Warning or LogLevel.Error;
        Display(entry);
    }

    /// <summary>Погасить сразу, без угасания: щёлкнули по окну или сменился раздел.</summary>
    public void HideNow()
    {
        _timer.Stop();
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        Visibility = Visibility.Collapsed;
    }

    /// <summary>Плашка на месте: показывает запись, сделанную <see cref="AppLog.WarnInPlace"/>.</summary>
    public void ShowInPlace(LogEntry entry) => Display(entry);

    /// <summary>Под кнопкой, а не влезла снизу — над ней; посередине окна — по его нынешнему размеру. За край окна не выходит.</summary>
    private void Place()
    {
        if (!_floating || Parent is not FrameworkElement { ActualWidth: > 0 } parent)
        {
            return;
        }

        var width = parent.ActualWidth;
        var height = parent.ActualHeight;

        Card.MaxWidth = Math.Min(_maxWidth, Math.Max(200, width - EdgeGap * 2));
        Card.Measure(new Size(Card.MaxWidth, double.PositiveInfinity));

        var size = Card.DesiredSize;
        double left;
        double top;

        if (_anchor is { } button)
        {
            left = button.Left;
            top = button.Bottom + ButtonGap;

            if (top + size.Height > height - EdgeGap)
            {
                top = button.Top - ButtonGap - size.Height;
            }
        }
        else
        {
            left = (width - size.Width) / 2;

            // не на самой середине, а выше: на трети окна сверху, ближе к тому, куда смотрят
            top = height / 3 - size.Height / 2;
        }

        left = Math.Clamp(left, EdgeGap, Math.Max(EdgeGap, width - size.Width - EdgeGap));
        top = Math.Clamp(top, EdgeGap, Math.Max(EdgeGap, height - size.Height - EdgeGap));

        // справа и снизу запас с избытком: у края окна сетка ужимала плашку под остаток места
        // и у строки срезался низ (хвосты «у», «р», «д»)
        Margin = new Thickness(left, top, -2000, -2000);
    }

    private void Display(LogEntry entry)
    {
        Message.Text = entry.Text;
        Dot.Fill = DotBrush(entry.Level);

        // рамка тусклая оранжевая — рамкой темы плашку было плохо видно; удача — в зелёной; ошибка — на тусклом
        // красном и в красной рамке. Фон полупрозрачный поверх плашки под ним, поэтому тема остаётся видна
        switch (entry.Level)
        {
            case LogLevel.Error:
                Body.BorderBrush = ErrorStroke;
                Body.Background = ErrorFill;
                break;

            case LogLevel.Success:
                Body.BorderBrush = SuccessStroke;
                Body.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorTertiaryBrush");
                break;

            default:
                Body.BorderBrush = WarningStroke;
                Body.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorTertiaryBrush");
                break;
        }

        Visibility = Visibility.Visible;
        Place();
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));

        // длинную запись дольше читать; отказ у кнопки ждут и он короче держится, чем посередине окна
        _timer.Stop();
        _timer.Interval = TimeSpan.FromSeconds(_anchor is null
            ? Math.Clamp(3 + entry.Text.Length / 40.0, 3, 8)
            : Math.Clamp(1.5 + entry.Text.Length / 50.0, 1.5, 4));
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
    private static readonly Brush SuccessStroke = Freeze(new SolidColorBrush(Color.FromArgb(0x80, 0x6C, 0xCB, 0x5F)));
    private static readonly Brush ErrorStroke =Freeze(new SolidColorBrush(Color.FromArgb(0x80, 0xE8, 0x5C, 0x5C)));
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
