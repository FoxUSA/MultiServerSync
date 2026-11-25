using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MultiServerSync.Core;

/// <summary>
/// Две темы — два угла. Всё, что зависит от темы (цвета, кисти, стили с цветом), лежит в папках
/// <c>Themes\Dark</c> и <c>Themes\Light</c>; в разметке и в коде цветов нет, только имена. Правка значения
/// в одной папке вторую тему не задевает. Имена в обеих папках одни и те же — это проверяет сборка (build.ps1).
/// Стандарты цветов обеих тем — в <c>.dev\THEME-STANDARD.md</c>.
/// <para>
/// Кисти библиотеки WPF UI: тёмная тема берёт их как есть (её <c>Library.xaml</c> пуст), светлая задаёт свои
/// значения в <c>Themes\Light\Library.xaml</c>.
/// </para>
/// </summary>
public static class AppTheme
{
    /// <summary>Файлы темы: в каждой папке один и тот же набор.</summary>
    private static readonly string[] Parts = ["Library", "Palette", "Status", "Panels", "Monitor", "Editor"];

    private static ResourceDictionary? _current;
    private static bool _applying;
    private static bool _hooked;
    private static volatile IReadOnlyDictionary<string, object> _values = new Dictionary<string, object>();

    /// <summary>Сейчас светлая тема.</summary>
    public static bool IsLight { get; private set; }

    /// <summary>Номер смены темы: по нему спрятанный элемент при показе узнаёт, что пропустил смену.</summary>
    public static int Version { get; private set; }

    /// <summary>Тема сменилась. Только поток окна.</summary>
    public static event Action? Changed;

    /// <summary>До первого окна: тёмный набор, как и тема библиотеки в App.xaml. Настройку применяет <see cref="Apply"/>.</summary>
    public static void Preload() => Load(light: false);

    /// <summary>
    /// Тема по настройке: «Dark», «Light», иначе как в Windows. Подложки нет (WindowBackdropType.None):
    /// сплошной фон держит ступени яркости окна, карточек и таблиц одинаковыми при любых обоях и в
    /// неактивном окне. WPF-UI без явного указания снова включает Mica, поэтому «как в системе» считаем сами.
    /// </summary>
    public static void Apply(string setting)
    {
        var target = Resolve(setting);

        if (!_hooked)
        {
            // тему сменила сама библиотека — свой набор следом
            _hooked = true;
            ApplicationThemeManager.Changed += (_, _) =>
            {
                if (!_applying)
                {
                    var before = IsLight;
                    Load(ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light);
                    Accent();
                    Notify(before);
                }
            };
        }

        var was = IsLight;
        var first = _current is null;

        if (target == ApplicationTheme.Light)
        {
            // светлый набор — до библиотеки: фон окна она берёт в момент смены темы
            Load(light: true);
            WindowBackgrounds(light: true);
        }
        else
        {
            WindowBackgrounds(light: false);
        }

        _applying = true;

        try
        {
            ApplicationThemeManager.Apply(target, WindowBackdropType.None);
        }
        finally
        {
            _applying = false;
        }

        Load(ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light);
        Accent();
        Notify(was, first);
    }

    /// <summary>
    /// Акцент программы — из темы, не из Windows (23.11): системный у пользователя бирюзовый, «больничный»,
    /// и на чужом компьютере был бы другим. Библиотека сама раскладывает четыре оттенка по кистям
    /// (заливка, текст акцента, текст на заливке) — с учётом уже применённой темы.
    /// </summary>
    private static void Accent()
    {
        ApplicationAccentColorManager.Apply(
            Color("Accent.System"), Color("Accent.Primary"), Color("Accent.Secondary"), Color("Accent.Tertiary"));

        // Кисти библиотеки, собранные из цветов акцента (свитч, галочка, кружок, ползунок…), обновляются
        // не всегда: взятая стилем кисть застывает с прежним цветом — свитчи оставались бирюзовыми (23.11).
        // Поэтому каждая собирается заново из уже нового цвета акцента — так же, как в теме библиотеки.
        var resources = Application.Current.Resources;

        foreach (var (key, source) in AccentBrushes)
        {
            var from = key switch
            {
                "HyperlinkButtonForeground" when !IsLight => "SystemAccentColorTertiary",
                "HyperlinkButtonForegroundPressed" when !IsLight => "SystemAccentColorSecondary",
                _ => source,
            };

            if (resources[from] is Color color)
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                resources[key] = brush;
            }
        }
    }

    /// <summary>Кисти темы WPF-UI 4.3 с цветом из акцента: имя кисти — имя цвета (как в светлой теме библиотеки).</summary>
    private static readonly (string Key, string Source)[] AccentBrushes =
    [
        ("AccentButtonBackground", "AccentFillColorDefault"),
        ("AccentButtonBackgroundPointerOver", "AccentFillColorSecondary"),
        ("AccentButtonBackgroundPressed", "AccentFillColorTertiary"),
        ("CalendarViewSelectedBackground", "SystemAccentColorPrimary"),
        ("CalendarViewSelectedBackgroundPointerOver", "AccentFillColorSecondary"),
        ("CalendarViewSelectedBorderBrush", "SystemAccentColorPrimary"),
        ("CalendarViewTodayBackground", "SystemAccentColorPrimary"),
        ("CheckBoxCheckBackgroundFillChecked", "SystemAccentColorPrimary"),
        ("CheckBoxCheckBackgroundFillCheckedPointerOver", "AccentFillColorSecondary"),
        ("CheckBoxCheckBackgroundFillCheckedPressed", "AccentFillColorTertiary"),
        ("ComboBoxBorderBrushFocused", "SystemAccentColorSecondary"),
        ("ComboBoxItemPillFillBrush", "SystemAccentColorPrimary"),
        ("HyperlinkButtonForeground", "SystemAccentColorSecondary"),
        ("HyperlinkButtonForegroundPointerOver", "SystemAccentColorTertiary"),
        ("HyperlinkButtonForegroundPressed", "SystemAccentColorPrimary"),
        ("InfoBarInformationalSeverityIconBackground", "SystemAccentColorPrimary"),
        ("ListBoxItemSelectedBackgroundThemeBrush", "SystemAccentColorPrimary"),
        ("ListViewItemPillFillBrush", "SystemAccentColorPrimary"),
        ("NavigationViewSelectionIndicatorForeground", "SystemAccentColorPrimary"),
        ("ProgressBarForeground", "SystemAccentColorPrimary"),
        ("ProgressRingForegroundThemeBrush", "SystemAccentColorPrimary"),
        ("RadioButtonOuterEllipseCheckedStroke", "SystemAccentColorPrimary"),
        ("RadioButtonOuterEllipseCheckedStrokePointerOver", "AccentFillColorTertiary"),
        ("RatingControlSelectedForeground", "SystemAccentColorPrimary"),
        ("SliderThumbBackground", "SystemAccentColorPrimary"),
        ("SliderThumbBackgroundPointerOver", "AccentFillColorSecondary"),
        ("TextControlFocusedBorderBrush", "SystemAccentColorPrimary"),
        ("ThumbRateForeground", "SystemAccentColorPrimary"),
        ("ToggleButtonBackgroundChecked", "SystemAccentColorPrimary"),
        ("ToggleButtonBackgroundCheckedPressed", "SystemAccentColorTertiary"),
        ("ToggleButtonForegroundCheckedPointerOver", "SystemAccentColorSecondary"),
        ("ToggleSwitchFillOn", "SystemAccentColorPrimary"),
        ("ToggleSwitchFillOnPointerOver", "AccentFillColorSecondary"),
        ("ToggleSwitchFillOnPressed", "AccentFillColorTertiary"),
        ("ToggleSwitchStrokeOn", "SystemAccentColorPrimary"),
        ("ToggleSwitchStrokeOnPointerOver", "AccentFillColorSecondary"),
        ("ToggleSwitchStrokeOnPressed", "AccentFillColorTertiary"),
        ("TreeViewItemSelectionIndicatorForeground", "SystemAccentColorPrimary"),
    ];

    /// <summary>
    /// До главного окна (окно мастер-пароля): светлая тема применяется сразу. Тёмная и так стоит с запуска —
    /// её, как и раньше, применяет главное окно, порядок запуска в тёмной не меняется.
    /// </summary>
    public static void ApplyEarly(string setting)
    {
        if (Resolve(setting) == ApplicationTheme.Light)
        {
            Apply(setting);
        }
    }

    private static ApplicationTheme Resolve(string setting) => setting switch
    {
        "Light" => ApplicationTheme.Light,
        "Dark" => ApplicationTheme.Dark,
        _ => ApplicationThemeManager.GetSystemTheme() switch
        {
            SystemTheme.Dark or SystemTheme.Glow or SystemTheme.CapturedMotion => ApplicationTheme.Dark,
            SystemTheme.HC1 or SystemTheme.HC2 or SystemTheme.HCBlack or SystemTheme.HCWhite => ApplicationTheme.HighContrast,
            _ => ApplicationTheme.Light,
        },
    };

    private static void Notify(bool was, bool first = false)
    {
        if (first || was != IsLight)
        {
            Version++;
            Changed?.Invoke();
        }
    }

    private const string WindowKey = "ApplicationBackgroundBrush";

    private static bool _windowHooked;

    /// <summary>Почти белый, которым библиотека сама красит окно в светлой теме, когда своего фона у окна нет.</summary>
    private static readonly Color LibraryLightWindow = System.Windows.Media.Color.FromRgb(0xFA, 0xFA, 0xFA);

    /// <summary>
    /// Фон окна библиотека берёт не из общих кистей, а из ресурсов самого окна; нет их — красит своим цветом.
    /// Светлая тема кладёт в окна свой фон (серее карточек). Тёмная ничего не кладёт и убирает положенное:
    /// там всё остаётся как у библиотеки. Обработчик новых окон появляется при первом включении светлой.
    /// </summary>
    private static void WindowBackgrounds(bool light)
    {
        if (light && !_windowHooked)
        {
            _windowHooked = true;

            // новые окна (диалоги): до того, как библиотека покрасит их при загрузке
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
            {
                if (IsLight && sender is Window window)
                {
                    Tint(window);
                }
            }));
        }

        foreach (Window window in Application.Current.Windows)
        {
            if (light)
            {
                Tint(window);
            }
            else if (window.Resources.Contains(WindowKey))
            {
                window.Resources.Remove(WindowKey);
            }
        }
    }

    private static void Tint(Window window)
    {
        window.Resources[WindowKey] = Brush(WindowKey);

        // уже покрашено библиотекой — ссылкой на кисть темы; прозрачные окна и окна со своим фоном не трогаем
        if (window.Background is SolidColorBrush { Color: var color } && color == LibraryLightWindow)
        {
            window.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, WindowKey);
        }
    }

    /// <summary>Набор темы — последним среди словарей программы: его значения сильнее библиотечных.</summary>
    private static void Load(bool light)
    {
        var merged = Application.Current.Resources.MergedDictionaries;

        if (_current is not null && IsLight == light)
        {
            if (merged.Count == 0 || !ReferenceEquals(merged[^1], _current))
            {
                merged.Remove(_current);
                merged.Add(_current);
            }

            return;
        }

        var folder = light ? "Light" : "Dark";
        var next = new ResourceDictionary();
        var values = new Dictionary<string, object>();

        foreach (var part in Parts)
        {
            var uri = new Uri($"/MultiServerSync.App;component/Themes/{folder}/{part}.xaml", UriKind.Relative);
            var dictionary = (ResourceDictionary)Application.LoadComponent(uri);

            foreach (var key in dictionary.Keys)
            {
                if (key is not string name)
                {
                    continue;
                }

                var value = dictionary[key];

                // замороженное читается из любого потока — статусы и журнал считаются в фоне
                if (value is Freezable { IsFrozen: false, CanFreeze: true } freezable)
                {
                    freezable.Freeze();
                }

                values[name] = value;
            }

            next.MergedDictionaries.Add(dictionary);
        }

        merged.Add(next);

        if (_current is not null)
        {
            merged.Remove(_current);
        }

        _current = next;
        _values = values;
        IsLight = light;
    }

    /// <summary>Кисть текущей темы по имени. Из любого потока; кисть заморожена.</summary>
    public static Brush Brush(string key) =>
        _values.TryGetValue(key, out var value) && value is Brush brush ? brush : Missing(key);

    /// <summary>Цвет текущей темы по имени: значение <c>Color</c> или цвет сплошной кисти.</summary>
    public static Color Color(string key) => _values.TryGetValue(key, out var value)
        ? value switch
        {
            Color color => color,
            SolidColorBrush solid => solid.Color,
            _ => Colors.Magenta,
        }
        : Colors.Magenta;

    private static Brush Missing(string key)
    {
        // имени нет в теме — заметный цвет вместо тихой пустоты и строка в файле лога
        Services.AppLog.Debug("Тема: нет кисти «" + key + "».");
        return Brushes.Magenta;
    }

    /// <summary>
    /// Перерисовать своё при смене темы — пока элемент на экране. Спрятанный (другая вкладка, раздел)
    /// узнаёт о пропущенной смене при показе. Для того, что берёт цвета кодом, а не <c>DynamicResource</c>.
    /// </summary>
    public static void Follow(FrameworkElement owner, Action refresh)
    {
        var seen = Version;

        void OnChanged()
        {
            seen = Version;
            refresh();
        }

        owner.Loaded += (_, _) =>
        {
            Changed -= OnChanged;
            Changed += OnChanged;

            if (seen != Version)
            {
                OnChanged();
            }
        };
        owner.Unloaded += (_, _) => Changed -= OnChanged;

        if (owner.IsLoaded)
        {
            Changed += OnChanged;
        }
    }
}
