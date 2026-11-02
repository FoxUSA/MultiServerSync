using System.Text.RegularExpressions;
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
/// запись журнала, держится несколько секунд и гаснет. Строки отдельных серверов по ходу операции
/// сюда не идут — их десятки, итог операции и так придёт отдельной записью. Отказ встаёт под нажатой
/// кнопкой и заменяет прежний, всё остальное — столбиком посередине окна (<see cref="ToastStack"/>).
/// </summary>
public partial class ActionToast : UserControl
{
    private const double EdgeGap = 8;
    private const double ButtonGap = 4;

    /// <summary>Срок один на всех: свой у каждой плашки гасил свежую раньше той, что появилась до неё.</summary>
    private static readonly TimeSpan Life = TimeSpan.FromSeconds(5);

    private readonly DispatcherTimer _timer = new() { Interval = Life };

    /// <summary>Плашка окна сама ищет себе место; плашка на месте стоит там, где её положила разметка.</summary>
    private bool _floating;

    /// <summary>Кнопка, которой отказано, в координатах окна. Null — плашка стоит там, где её положили.</summary>
    private Rect? _anchor;

    private const double MostWidth = 460;

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

    /// <summary>Таймером не гаснет, только по щелчку: ошибка посередине окна ждёт, пока её увидят.</summary>
    public bool Sticky { get; init; }

    /// <summary>Погасла совсем: столбик плашек убирает её.</summary>
    public event Action? Hidden;

    /// <summary>Отказ под нажатой кнопкой: повтор записи журнала.</summary>
    /// <param name="anchor">Кнопка, которой отказано, в координатах окна.</param>
    public void Show(LogEntry entry, Rect anchor)
    {
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
        Display(entry);
    }

    /// <summary>Погасить сразу, без угасания: щёлкнули по окну или сменился раздел.</summary>
    public void HideNow()
    {
        _timer.Stop();
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        Visibility = Visibility.Collapsed;
        Hidden?.Invoke();
    }

    /// <summary>Плашка на месте: показывает запись, сделанную <see cref="AppLog.WarnInPlace"/>.</summary>
    public void ShowInPlace(LogEntry entry) => Display(entry);

    /// <summary>Под кнопкой, а не влезла снизу — над ней. За край окна не выходит.</summary>
    private void Place()
    {
        if (!_floating || _anchor is not { } button || Parent is not FrameworkElement { ActualWidth: > 0 } parent)
        {
            return;
        }

        var width = parent.ActualWidth;
        var height = parent.ActualHeight;

        Card.MaxWidth = Math.Min(MostWidth, Math.Max(200, width - EdgeGap * 2));
        Balance();
        Card.Measure(new Size(Card.MaxWidth, double.PositiveInfinity));

        var size = Card.DesiredSize;
        var top = button.Bottom + ButtonGap;

        if (top + size.Height > height - EdgeGap)
        {
            top = button.Top - ButtonGap - size.Height;
        }

        var left = Math.Clamp(button.Left, EdgeGap, Math.Max(EdgeGap, width - size.Width - EdgeGap));
        top = Math.Clamp(top, EdgeGap, Math.Max(EdgeGap, height - size.Height - EdgeGap));

        // справа и снизу запас с избытком: у края окна сетка ужимала плашку под остаток места
        // и у строки срезался низ (хвосты «у», «р», «д»)
        Margin = new Thickness(left, top, -2000, -2000);
    }

    /// <summary>Запись не про то, что сейчас на экране: другой раздел или другая вкладка этого раздела.</summary>
    private static bool IsElsewhere(LogEntry entry) =>
        entry.Section is { } section && AppState.Current is { TabsLoaded: true } state
        && (section != state.Section || (entry.TabText.Length > 0 && entry.TabText != state.ActiveWorkspace.Title));

    private void Display(LogEntry entry)
    {
        // ошибка пришла не отсюда — плашка говорит, откуда: сервер, раздел, вкладка, как в шапке вывода
        var text = entry.Level is LogLevel.Warning or LogLevel.Error && IsElsewhere(entry)
            ? entry.Text + Environment.NewLine + entry.PlaceText
            : entry.Text;

        Message.Text = NumberWithWord.Replace(text, "\u00A0");
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
        Balance();
        Place();
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));

        _timer.Stop();

        if (Sticky)
        {
            return;
        }

        _timer.Start();
    }

    /// <summary>Пробел между числом и коротким словом за ним: «4,0 с» не должно разрываться переносом.</summary>
    private static readonly Regex NumberWithWord = new(@"(?<=\d) (?=\p{L}{1,3}(?!\p{L}))");

    /// <summary>
    /// Ровный перенос: не влезло в строку — плашка сужается до наименьшей ширины с тем же числом строк.
    /// Так на последней строке не остаётся одинокая буква или слово.
    /// </summary>
    private void Balance()
    {
        Message.MaxWidth = double.PositiveInfinity;

        double HeightAt(double width)
        {
            Message.Measure(new Size(width, double.PositiveInfinity));
            return Message.DesiredSize.Height;
        }

        var most = Card.MaxWidth
            - Body.Padding.Left - Body.Padding.Right
            - Body.BorderThickness.Left - Body.BorderThickness.Right
            - Dot.Width - Dot.Margin.Right;

        var height = HeightAt(most);

        if (height <= HeightAt(double.PositiveInfinity))
        {
            return;
        }

        var (low, high) = (0d, most);

        while (high - low > 2)
        {
            var middle = (low + high) / 2;

            if (HeightAt(middle) > height)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        Message.MaxWidth = Math.Ceiling(high);
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
                Hidden?.Invoke();
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
