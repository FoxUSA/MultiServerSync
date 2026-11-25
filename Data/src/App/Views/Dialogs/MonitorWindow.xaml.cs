using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MultiServerSync.Models;
using MultiServerSync.Services;


namespace MultiServerSync.Views.Dialogs;

/// <summary>
/// Мониторинг сервера в отдельном окне: шапка — подпись сервера (щелчок — список серверов, окно переключается
/// на выбранный) и «+» — ещё одно окно с этим сервером; ниже — те же периоды и карточки, что в главном окне,
/// без панели серверов и журнала. Окон сколько угодно, один сервер может быть в нескольких сразу.
/// Каждое окно помнит сервер, период, место, размер и развёрнутость в настройках — по ходу, с небольшой
/// задержкой, — и при запуске открывается снова там же. Закрыл окно сам — его записи больше нет.
/// </summary>
public partial class MonitorWindow : Wpf.Ui.Controls.FluentWindow
{
    /// <summary>Открытые окна — для значков у серверов и каскада новых окон.</summary>
    private static readonly List<MonitorWindow> Shown = new();

    /// <summary>Кнопки окна в шапке (свернуть, развернуть, закрыть) и поле справа от них.</summary>
    private const double CaptionButtons = 3 * 46 + 12;

    /// <summary>Сдвиг нового окна от прежнего — каскадом, а не точь-в-точь поверх.</summary>
    private const double Cascade = 32;

    private readonly MonitorWindowState _state;
    private readonly DispatcherTimer _saveLater = new() { Interval = TimeSpan.FromMilliseconds(700) };
    /// <summary>Сервер окна; null — ещё не выбран (окно открыли правой кнопкой по разделу, а в разделе сервера нет).</summary>
    private ServerEntry? _server;
    private bool _maximized;

    private MonitorWindow(ServerEntry? server, MonitorWindowState state)
    {
        InitializeComponent();

        _server = server;
        _state = state;

        ServerIcon.Source = ShellIcons.Server;
        View.RangeChanged += _ => RememberLater();
        View.NeedChanged += FitMinimum;
        Bar.SizeChanged += (_, _) => FitHead();

        // перенесли на монитор с другим масштабом — запас на округление считается по нему
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(FitMinimum, System.Windows.Threading.DispatcherPriority.Loaded);

        _saveLater.Tick += (_, _) =>
        {
            _saveLater.Stop();
            Remember();
        };

        LocationChanged += (_, _) =>
        {
            RememberLater();
            MonitorAlerts.Touched();
        };
        SizeChanged += (_, _) => RememberLater();
        StateChanged += (_, _) =>
        {
            MonitorAlerts.Touched();

            // свёрнутое окно помнит, каким было до этого
            if (WindowState != WindowState.Minimized)
            {
                _maximized = WindowState == WindowState.Maximized;
            }

            RememberLater();
        };

        Closing += OnClosing;
        AppState.Current.Servers.CollectionChanged += OnServersChanged;
        Closed += (_, _) =>
        {
            AppState.Current.Servers.CollectionChanged -= OnServersChanged;
            Shown.Remove(this);
            MarkServers();
        };

        ShowServer();
        View.Show(server, MonitorRanges.Parse(state.Range));
    }

    /// <summary>
    /// Вынести сервер в новое окно — кнопкой справа от периодов, правой кнопкой по разделу «Мониторинг» или «+»
    /// в шапке вынесенного. Размер — как у окна-образца (или обычный), место — каскадом от него, в пределах его экрана.
    /// Сервер не выбран — окно всё равно открывается: вместо карточек подсказка, сервер выбирается в шапке.
    /// </summary>
    /// <summary>
    /// Новое окно не от другого вынесенного — такого размера, чтобы карточки были полными: с плитками «среднее / пик /
    /// раньше» и графиком. Меньше — карточки сразу уплотнялись до одних значений, и графиков не было видно.
    /// </summary>
    private const double DefaultWidth = 820;

    private const double DefaultHeight = 600;

    public static MonitorWindow Open(ServerEntry? server, MonitorRange range, Window? from)
    {
        var sample = from as MonitorWindow;
        var width = sample is not null ? NormalBounds(sample).Width : DefaultWidth;
        var height = sample is not null ? NormalBounds(sample).Height : DefaultHeight;
        var state = new MonitorWindowState
        {
            ServerId = server?.Id ?? string.Empty,
            Range = MonitorRanges.Key(range),
            Width = width,
            Height = height,
        };

        var place = Place(from, width, height);
        state.Left = place.X;
        state.Top = place.Y;

        AppState.Current.Settings.MonitorWindows.Add(state);
        AppState.Current.SaveSettings();

        var window = ShowNew(server, state);
        AppLog.Hint(server is null ? "Окно мониторинга открыто — выберите сервер в его шапке." : $"Мониторинг «{server.PaneLabel}» — в отдельном окне.");
        return window;
    }

    /// <summary>
    /// Сработал сигнал: окно этого сервера вперёд (последнее открытое из его окон; свёрнутое — разворачивается),
    /// нет окна — открывается новое; раздел в нём — в тускло-оранжевой рамке.
    /// </summary>
    public static void Alert(ServerEntry server, string key)
    {
        var window = Shown.LastOrDefault(w => ReferenceEquals(w._server, server)) ?? Open(server, MonitorRange.Live, null);

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = window._maximized ? WindowState.Maximized : WindowState.Normal;
        }

        window.Show();
        window.Activate();

        // Windows не всегда отдаёт фокус фоновой программе — поверх всех на миг, и окно выше остальных
        window.Topmost = true;
        window.Topmost = false;

        window.View.Light(key);
    }

    /// <summary>
    /// Окна, открытые при прошлом выходе, — снова, как были. Сервера, которого больше нет, окно молча не
    /// открывается и запись о нём убирается. Место за пределами нынешних экранов — окно на основном экране, размер прежний.
    /// </summary>
    public static void RestoreAll()
    {
        var settings = AppState.Current.Settings;
        // окно без сервера (его так и не выбрали) — не «пропавший сервер»: открывается как было
        var gone = settings.MonitorWindows
            .Where(s => s.ServerId.Length > 0 && AppState.Current.Servers.All(e => e.Id != s.ServerId))
            .ToList();

        foreach (var state in gone)
        {
            settings.MonitorWindows.Remove(state);
        }

        if (gone.Count > 0)
        {
            AppState.Current.SaveSettings();
        }

        foreach (var state in settings.MonitorWindows.ToList())
        {
            ShowNew(AppState.Current.Servers.FirstOrDefault(e => e.Id == state.ServerId), state);
        }
    }

    private static MonitorWindow ShowNew(ServerEntry? server, MonitorWindowState state)
    {
        var window = new MonitorWindow(server, state);
        var area = SystemParameters.WorkArea;

        window.Width = state.Width > 0 ? Math.Min(state.Width, area.Width) : DefaultWidth;
        window.Height = state.Height > 0 ? Math.Min(state.Height, area.Height) : DefaultHeight;

        if (state.Left is { } left && state.Top is { } top && OnScreen(left, top, window.Width))
        {
            window.Left = left;
            window.Top = top;
        }
        else
        {
            // монитор отключили — видимым на основном экране
            window.Left = area.Left + (area.Width - window.Width) / 2;
            window.Top = area.Top + (area.Height - window.Height) / 2;
        }

        window._maximized = state.Maximized;

        Shown.Add(window);
        window.Show();

        // развёрнутое — уже после показа: окно сначала встаёт на своё место (на своём мониторе), потом разворачивается
        // там же. Развёрнутое до показа WPF UI открывало свёрнутым
        if (state.Maximized)
        {
            void Maximize(object? sender, EventArgs e)
            {
                window.ContentRendered -= Maximize;
                window.WindowState = WindowState.Maximized;
            }

            window.ContentRendered += Maximize;
        }

        MarkServers();
        return window;
    }

    /// <summary>Заголовок окна в этом месте попадает на какой-нибудь из мониторов — так же проверяет главное окно.</summary>
    private static bool OnScreen(double left, double top, double width) =>
        left + Math.Min(100, width / 2) > SystemParameters.VirtualScreenLeft
        && left + 100 < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
        && top >= SystemParameters.VirtualScreenTop - 8
        && top + 30 < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;

    private static Rect NormalBounds(Window window) =>
        window.WindowState == WindowState.Normal || window.RestoreBounds.IsEmpty
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;

    /// <summary>Место нового окна: каскадом от окна, откуда вынесли, и не за краем его экрана.</summary>
    private static Point Place(Window? from, double width, double height)
    {
        var area = SystemParameters.WorkArea;

        if (from is null)
        {
            return new Point(area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2);
        }

        var bounds = NormalBounds(from);
        var screen = ScreenArea(from) ?? area;
        Point at;

        if (from is MonitorWindow)
        {
            at = new Point(bounds.Left + Cascade, bounds.Top + Cascade);
        }
        else
        {
            // из главного окна — посередине него, а уже открытые от него же сдвигают новое лесенкой
            var shift = Shown.Count % 8 * Cascade;
            at = new Point(bounds.Left + (bounds.Width - width) / 2 + shift, bounds.Top + (bounds.Height - height) / 2 + shift);
        }

        // вылезло за край экрана — к его левому верхнему углу, тоже лесенкой
        if (at.X + width > screen.Right || at.Y + height > screen.Bottom)
        {
            var step = Shown.Count % 8 * Cascade;
            at = new Point(screen.Left + step, screen.Top + step);
        }

        return new Point(Math.Max(screen.Left, at.X), Math.Max(screen.Top, at.Y));
    }

    /// <summary>Рабочая область экрана, на котором стоит окно, — в точках окна.</summary>
    private static Rect? ScreenArea(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;

        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(window);
        return new Rect(area.Left / dpi.DpiScaleX, area.Top / dpi.DpiScaleY, area.Width / dpi.DpiScaleX, area.Height / dpi.DpiScaleY);
    }

    /// <summary>Значок у серверов в панели слева главного окна — у кого есть вынесенные окна.</summary>
    private static void MarkServers()
    {
        var shown = Shown.Where(w => w._server is not null).Select(w => w._server!.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var server in AppState.Current.Servers)
        {
            server.HasMonitorWindow = shown.Contains(server.Id);
        }
    }

    // ── шапка ────────────────────────────────────────────────────────────────────

    private void ShowServer()
    {
        Who.Children.Clear();

        // сервер не выбран — подпись как «Сервер не выбран» у плашки главного окна
        if (_server is null)
        {
            Title = "Мониторинг";
            Who.Children.Add(new TextBlock { Text = "Сервер не выбран", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            FitHead();
            FitMinimum();
            return;
        }

        Title = $"Мониторинг · {_server.PaneLabel}";
        Who.Children.Add(new TextBlock { Text = _server.PaneLabel, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });

        if (!string.IsNullOrWhiteSpace(_server.User))
        {
            var user = new TextBlock { Text = $"({_server.User})", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(-4, 0, 0, 0) };
            user.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            Who.Children.Add(user);
        }

        var address = new TextBlock
        {
            Text = $"{_server.Host}:{_server.Port}",
            FontFamily = new FontFamily("Consolas"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
        };
        address.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        Who.Children.Add(address);

        FitHead();
        FitMinimum();
    }

    /// <summary>
    /// Шапке узко — из подписи уходят адрес, потом логин; имя и стрелка видны всегда, а подпись обнимает
    /// ровно то, что в ней видно.
    /// </summary>
    private void FitHead()
    {
        if (Bar.ActualWidth <= 0)
        {
            return;
        }

        Who.MaxWidth = Math.Max(Who.FirstWidth, Bar.ActualWidth - CaptionButtons - HeadChrome());
    }

    /// <summary>Всё в шапке, кроме кусков подписи: отступ, поля и рамка подписи, значок, стрелка, «+».</summary>
    private double HeadChrome() =>
        Head.Margin.Left + 10 + 10 + 2 + ServerIcon.Width + 8 + 10 + 12 + MoreButton.Margin.Left + MoreButton.Width + 1;

    /// <summary>
    /// Наименьший размер окна: шапка с именем сервера целиком, «+» и кнопки окна; ниже — то, что нужно карточкам
    /// на самой плотной ступени. Меньше окно не ужимается — иначе пришлось бы резать.
    /// </summary>
    private void FitMinimum()
    {
        Who.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var need = View.Need;
        var head = HeadChrome() + Who.FirstWidth + CaptionButtons;
        var chrome = Card.Margin.Left + Card.Margin.Right + Card.BorderThickness.Left + Card.BorderThickness.Right;

        // до целых точек экрана с запасом: на 125–250% поля округляются, и впритык край срезал бы «ГБ»
        MinWidth = Core.DevicePixel.Up(Math.Max(head, need.Width + chrome), this);
        MinHeight = Core.DevicePixel.Up(Bar.Height + Card.Margin.Bottom + Card.BorderThickness.Top + Card.BorderThickness.Bottom + need.Height, this);
    }

    private void OnServerTag(object sender, RoutedEventArgs e)
    {
        // у кого ещё есть окна — значок справа; выбор просто переключает это окно, даже на уже открытый сервер
        Controls.ServerMenu.Show(
            ServerTag,
            s => ReferenceEquals(s, _server),
            Switch,
            marked: s => Shown.Any(w => !ReferenceEquals(w, this) && ReferenceEquals(w._server, s)));
    }

    /// <summary>Окно на другой сервер: период, место и размер остаются, графики «Сейчас» начинаются заново.</summary>
    private void Switch(ServerEntry server)
    {
        if (ReferenceEquals(server, _server))
        {
            return;
        }

        _server = server;
        _state.ServerId = server.Id;
        ShowServer();
        View.Show(server, View.Range);
        MarkServers();
        Remember();
        AppLog.Hint($"Окно мониторинга — на сервере «{server.PaneLabel}».");
    }

    /// <summary>Сервер окна удалили из списка — окно остаётся, но без сервера: выбрать другой можно в шапке.</summary>
    private void OnServersChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_server is null || AppState.Current.Servers.Contains(_server))
        {
            return;
        }

        _server = null;
        ShowServer();
        View.Show(null, View.Range);
        MarkServers();
        Remember();
    }

    private void OnMore(object sender, RoutedEventArgs e) => Open(_server, View.Range, this);

    // ── память окна ──────────────────────────────────────────────────────────────

    private void RememberLater()
    {
        if (!IsLoaded)
        {
            return;
        }

        _saveLater.Stop();
        _saveLater.Start();
    }

    /// <summary>Место, размер, развёрнутость, сервер и период — в настройки сразу.</summary>
    private void Remember()
    {
        var bounds = NormalBounds(this);
        _state.Left = bounds.Left;
        _state.Top = bounds.Top;
        _state.Width = bounds.Width;
        _state.Height = bounds.Height;
        _state.Maximized = _maximized;
        _state.Range = MonitorRanges.Key(View.Range);
        _state.ServerId = _server?.Id ?? string.Empty;
        AppState.Current.SaveSettings();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _saveLater.Stop();
        MonitorAlerts.Touched();

        // выход из программы — не возврат в главное окно: окно откроется при следующем запуске там же
        if (App.Quitting)
        {
            Remember();
            return;
        }

        AppState.Current.Settings.MonitorWindows.Remove(_state);
        AppState.Current.SaveSettings();
        View.Show(null, View.Range);
    }
}
