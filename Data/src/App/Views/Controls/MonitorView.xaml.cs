using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using MultiServerSync.Models;
using MultiServerSync.Services;
using MultiServerSync.Views.Dialogs;

namespace MultiServerSync.Views.Controls;

/// <summary>
/// Мониторинг одного сервера: периоды, плашка «Включить сбор», четыре карточки — процессор, память, диск, сеть.
/// Один и тот же вид в главном окне и в вынесенных. Ничего не прокручивается и не обрезается: строки отдают
/// куски целиком, карточки уплотняются ступенями, а когда ужимать нечего — окну ставится наименьший размер
/// (<see cref="Need"/>). Опрос сервера идёт, только пока вид на экране и сервер выбран.
/// </summary>
public partial class MonitorView : UserControl
{
    private readonly MonitorCard _cpu = new();
    private readonly MonitorCard _mem = new();
    private readonly MonitorCard _disk = new();
    private readonly MonitorCard _net = new();

    private readonly Dictionary<MonitorRange, RadioButton> _rangeButtons = new();
    private readonly List<(TextBlock Full, TextBlock Short)> _rangeLabels = new();

    private ServerEntry? _server;
    private MonitorRange _range;
    private MonitorFeed? _feed;
    private bool _shortLabels;

    public MonitorView()
    {
        InitializeComponent();

        Cards.Children.Add(_cpu);
        Cards.Children.Add(_mem);
        Cards.Children.Add(_disk);
        Cards.Children.Add(_net);

        foreach (var card in new[] { _cpu, _mem, _disk, _net })
        {
            card.NeedChanged += RaiseNeed;
        }

        // колокольчик раздела: покраснеет — сигнал и окно сервера вперёд (у сети нормы нет — и колокольчика нет)
        _cpu.AlertClicked += () => ToggleAlert("cpu");
        _mem.AlertClicked += () => ToggleAlert("mem");
        _disk.AlertClicked += () => ToggleAlert("disk");

        Cards.ColumnsChanged += RaiseNeed;

        // у каждого вида своя группа: периоды главного окна и вынесенных не переключают друг друга
        var group = "MonitorRange" + Guid.NewGuid().ToString("N");

        foreach (var range in MonitorRanges.All)
        {
            var full = new TextBlock { Text = MonitorRanges.Full(range), VerticalAlignment = VerticalAlignment.Center };
            var brief = new TextBlock { Text = MonitorRanges.Short(range), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            var content = new StackPanel { Orientation = Orientation.Horizontal };

            if (range == MonitorRange.Live)
            {
                // огонёк «идёт»: у выбранного «Сейчас» мигает общим тактом программы
                var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
                dot.SetBinding(Shape.FillProperty, new System.Windows.Data.Binding(nameof(Foreground))
                {
                    RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(RadioButton), 1),
                });
                dot.SetBinding(BusyPulse.OnProperty, new System.Windows.Data.Binding(nameof(RadioButton.IsChecked))
                {
                    RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(RadioButton), 1),
                });
                content.Children.Add(dot);
            }

            content.Children.Add(full);
            content.Children.Add(brief);
            _rangeLabels.Add((full, brief));

            var button = new RadioButton
            {
                Content = content,
                GroupName = group,
                Style = (Style)Resources["MonitorRangeStyle"],
                Tag = range,
                ToolTip = range == MonitorRange.Live ? "Замер каждые 10 с, на графиках последние 5 минут" : $"История за {MonitorRanges.Full(range)}",
            };
            button.Checked += OnRangeChecked;
            _rangeButtons[range] = button;
            Ranges.Children.Add(button);
        }

        _rangeButtons[MonitorRange.Live].IsChecked = true;

        SizeChanged += (_, _) => FitLabels();
        IsVisibleChanged += (_, _) => Follow();

        Loaded += (_, _) =>
        {
            Follow();
            MonitorAlerts.Changed -= Render;
            MonitorAlerts.Changed += Render;
            MonitorDisks.Changed -= OnDiskPicked;
            MonitorDisks.Changed += OnDiskPicked;
        };
        Unloaded += (_, _) =>
        {
            Follow();
            MonitorAlerts.Changed -= Render;
            MonitorDisks.Changed -= OnDiskPicked;
        };

        // тема сменилась — огоньки, шкалы и графики берут свои цвета для неё. Подписка — пока вид в окне: закрытое
        // вынесенное окно общее событие темы держать не должно
        Core.AppTheme.Follow(this, Render);

        Render();
    }

    /// <summary>Вид в вынесенном окне: кнопки «вынести» у него нет.</summary>
    public bool IsDetached
    {
        get => DetachButton.Visibility != Visibility.Visible;
        set => DetachButton.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Кнопка справа от периодов: вынести этот сервер в отдельное окно.</summary>
    public event Action? DetachRequested;

    /// <summary>Период сменили щелчком — хозяин вида его запоминает.</summary>
    public event Action<MonitorRange>? RangeChanged;

    /// <summary>Наименьший размер вида сменился — окно пересчитывает свой минимум.</summary>
    public event Action? NeedChanged;

    public ServerEntry? Server => _server;

    public MonitorRange Range => _range;

    /// <summary>Показать сервер и период. Сервер сменился — графики «Сейчас» начинаются заново, прежний больше не опрашивается этим видом.</summary>
    public void Show(ServerEntry? server, MonitorRange range)
    {
        var serverChanged = !ReferenceEquals(server, _server);
        _range = range;

        if (_rangeButtons[range].IsChecked != true)
        {
            _showing = true;
            _rangeButtons[range].IsChecked = true;
            _showing = false;
        }

        if (serverChanged)
        {
            _server = server;

            // рамка сигнала — про прежний сервер
            foreach (var card in new[] { _cpu, _mem, _disk, _net })
            {
                MonitorAlerts.Off(card);
            }
        }

        Follow(force: serverChanged);
        _feed?.SetRange(this, range);
        RaiseNeed();
        Render();
    }

    private bool _showing;

    /// <summary>У сервера выбрали другое хранилище — «Диск» сразу его.</summary>
    private void OnDiskPicked(ServerEntry server)
    {
        if (ReferenceEquals(server, _server))
        {
            Render();
        }
    }

    /// <summary>Сработал сигнал раздела — его карточка в тускло-оранжевой рамке.</summary>
    public void Light(string key)
    {
        var card = key switch
        {
            "cpu" => _cpu,
            "mem" => _mem,
            _ => _disk,
        };

        MonitorAlerts.LightUp(card);
    }

    private void ToggleAlert(string key)
    {
        if (_server is { } server)
        {
            MonitorAlerts.Toggle(server, key);
        }
    }

    private void OnRangeChecked(object sender, RoutedEventArgs e)
    {
        if (_showing || sender is not RadioButton { Tag: MonitorRange range } || range == _range)
        {
            return;
        }

        _range = range;
        _feed?.SetRange(this, range);
        RangeChanged?.Invoke(range);
        RaiseNeed();
        Render();
    }

    /// <summary>Подписка на сервер — пока вид на экране и сервер выбран; иначе опрос этому виду не нужен.</summary>
    private void Follow(bool force = false)
    {
        var want = IsVisible && IsLoaded && _server is not null ? MonitorFeed.For(_server) : null;

        if (ReferenceEquals(want, _feed) && !force)
        {
            return;
        }

        if (_feed is not null && !ReferenceEquals(want, _feed))
        {
            _feed.Changed -= Render;
            _feed.Unwatch(this);
        }

        if (want is not null && !ReferenceEquals(want, _feed))
        {
            want.Changed += Render;
            want.Watch(this, _range);
        }

        _feed = want;
        Render();
    }

    private void OnDetach(object sender, RoutedEventArgs e)
    {
        if (_server is null)
        {
            AppLog.Hint("Сначала выберите сервер слева.", LogLevel.Warning);
            return;
        }

        DetachRequested?.Invoke();
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (_server is not { } server || _feed is not { } feed)
        {
            return;
        }

        if (feed.Installing)
        {
            AppLog.Hint("Сбор истории уже включается — ход в журнале.");
            return;
        }

        // свой сборщик стоит, но не идёт — его же ставим заново и запускаем, собранное остаётся
        var again = feed.Collector?.Source == HistorySource.OwnStopped;

        if (!ConfirmDialog.Ask(
            Window.GetWindow(this),
            again ? $"Запустить сбор истории снова на «{server.PaneLabel}»?" : $"Включить сбор истории на «{server.PaneLabel}»?",
            again
                ? "Скрипт " + Workspace.MonitorScript + " и служба mss-monitor.service поставятся заново и запустятся. "
                  + "Собранная история в " + MonitorProbe.DataDir + " останется."
                : "На сервере появятся: скрипт " + Workspace.MonitorScript + ", служба mss-monitor.service (замер раз в 10 с) "
                  + "и история в " + MonitorProbe.DataDir + " — не больше 1 МБ, файлы обрезаются сами. Без systemd ничего не ставится.",
            again ? "Запустить сбор" : "Включить сбор"))
        {
            return;
        }

        // ход — в журнале вкладки «Мониторинга», что показывает этот сервер
        await AppState.Current.MonitorTabOf(server).RunMonitorInstallAsync(server);
    }

    /// <summary>Узкая страница — периоды коротко («1 ч», «7 д»): как в макете, ниже 640 точек или когда полные не влезают.</summary>
    private void FitLabels()
    {
        var width = Root.ActualWidth;

        if (width <= 0)
        {
            return;
        }

        var full = RangesWidth(shortLabels: false);
        var detach = DetachButton.Visibility == Visibility.Visible ? DetachButton.Width + 4 : 0;
        // точка экрана запаса: на 125–250% кнопки округляются и полные подписи впритык срезались бы краем
        var wantShort = width < 640 - 16 || full + detach + Core.DevicePixel.Of(this) > width;

        if (wantShort == _shortLabels)
        {
            return;
        }

        _shortLabels = wantShort;

        foreach (var (f, s) in _rangeLabels)
        {
            f.Visibility = wantShort ? Visibility.Collapsed : Visibility.Visible;
            s.Visibility = wantShort ? Visibility.Visible : Visibility.Collapsed;
        }

        foreach (var button in _rangeButtons.Values)
        {
            button.Padding = new Thickness(wantShort ? 8 : 11, 0, wantShort ? 8 : 11, 0);
        }
    }

    /// <summary>Ширина ряда периодов с полными или короткими подписями — меряется тем же шрифтом, каким рисуется.</summary>
    private double RangesWidth(bool shortLabels)
    {
        double total = 0;
        var i = 0;

        foreach (var range in MonitorRanges.All)
        {
            var (f, s) = _rangeLabels[i++];
            var label = shortLabels ? s : f;
            var text = new FormattedText(
                label.Text,
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                12,
                Brushes.Black,
                null,
                TextFormattingMode.Display,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            // поля кнопки, огонёк у «Сейчас», зазор до следующей
            total += text.WidthIncludingTrailingWhitespace + (shortLabels ? 16 : 22) + 2 + (range == MonitorRange.Live ? 13 : 0);
        }

        // общая рамка группы: рамка и поле с каждой стороны
        return total + 6;
    }

    /// <summary>
    /// Наименьший размер вида, при котором ничего не режется: периоды коротко, вынос, плашка, карточки в своей
    /// раскладке на самой плотной ступени. В столбик (узко) карточек четыре ряда, иначе два или один.
    /// </summary>
    public Size Need
    {
        get
        {
            var card = CardNeed();
            var tools = RangesWidth(shortLabels: true) + (DetachButton.Visibility == Visibility.Visible ? DetachButton.Width + 4 : 0);
            var width = Root.Margin.Left + Root.Margin.Right + Math.Max(tools, card.Width) + 1;
            var rows = 4 / Math.Max(1, Cards.Columns);
            var notice = Notice.Visibility == Visibility.Visible ? Notice.Height + 4 : 0;
            var height = Root.Margin.Top + Root.Margin.Bottom + Tools.Height + notice + 4
                + rows * card.Height + (rows - 1) * MonitorGrid.Gap + 1;
            return new Size(Math.Ceiling(width), Math.Ceiling(height));
        }
    }

    private Size CardNeed()
    {
        double w = 0, h = 0;

        foreach (var card in new[] { _cpu, _mem, _disk, _net })
        {
            w = Math.Max(w, card.MinNeed.Width);
            h = Math.Max(h, card.MinNeed.Height);
        }

        // карточку уже меряли — иначе запас на значение с названием, пока замера не было
        return new Size(Math.Max(w, 150), Math.Max(h, 36));
    }

    private Size _lastNeed;

    private void RaiseNeed()
    {
        var need = Need;

        if (need == _lastNeed)
        {
            return;
        }

        _lastNeed = need;
        NeedChanged?.Invoke();
    }

    // ── содержимое карточек ─────────────────────────────────────────────────────

    private void Render()
    {
        var server = _server;
        Cards.Visibility = server is null ? Visibility.Hidden : Visibility.Visible;
        Hint.Visibility = server is null ? Visibility.Visible : Visibility.Collapsed;
        Hint.Text = AppState.Current.Servers.Count == 0 ? "Серверов нет — добавьте в разделе «Серверы»"
            : IsDetached ? "Выберите сервер" : "Выберите сервер слева";

        var feed = _feed;
        var live = _range == MonitorRange.Live;
        var collector = feed?.Collector;

        // плашка: выбран период истории, а истории взять неоткуда (или свой сбор стоит, но не идёт, или ставится).
        // История есть у rudog или sysstat — плашки нет и свой сбор не предлагается
        var source = collector?.Source ?? HistorySource.None;
        var noticeShown = feed is not null && !live && collector is not null
            && (feed.Installing || source is HistorySource.None or HistorySource.OwnStopped or HistorySource.SysstatDenied or HistorySource.SysstatNoSadf);
        var noticeWas = Notice.Visibility == Visibility.Visible;
        Notice.Visibility = noticeShown ? Visibility.Visible : Visibility.Collapsed;

        if (noticeShown)
        {
            var busy = feed!.Installing;
            NoticeText.Children.Clear();

            foreach (var piece in busy
                ? new[] { "Сбор истории включается", "ход — в журнале" }
                : source switch
                {
                    HistorySource.OwnStopped => new[] { "Сбор истории остановлен", "показано собранное раньше" },
                    HistorySource.SysstatDenied => new[] { "Нет прав читать историю sysstat", "«Сейчас» работает" },
                    HistorySource.SysstatNoSadf => new[] { "Есть файлы sysstat, но нет sadf", "истории не будет" },
                    _ when collector!.Systemd => new[] { "Сбор истории не включён", "«Сейчас» работает и без него" },
                    _ => new[] { "На сервере нет systemd — истории не будет", "«Сейчас» работает и без неё" },
                })
            {
                NoticeText.Children.Add(Piece(piece, busy ? "AccentTextFillColorPrimaryBrush" : "TextFillColorPrimaryBrush"));
            }

            var offers = !busy && collector!.Systemd && source is HistorySource.None or HistorySource.OwnStopped;
            InstallButton.Content = source == HistorySource.OwnStopped ? "Запустить сбор" : "Включить сбор";
            InstallButton.Visibility = offers ? Visibility.Visible : Visibility.Collapsed;
            Notice.SetResourceReference(Border.BackgroundProperty, busy ? "SubtleFillColorSecondaryBrush" : "Pick.Fill");
            Notice.SetResourceReference(Border.BorderBrushProperty, busy ? "AccentFillColorDefaultBrush" : "Pick.Stroke");
            NoticeIcon.SetResourceReference(Control.ForegroundProperty, busy ? "AccentTextFillColorPrimaryBrush" : "Pick.Text");
        }

        if (noticeWas != noticeShown)
        {
            RaiseNeed();
        }

        var report = server is null ? null : feed?.Report(_range);
        RenderInfo(feed, report);

        if (server is null)
        {
            return;
        }

        var now = feed?.Now;
        var info = feed?.Info;
        var historyRead = feed?.HistoryRead(_range) ?? false;
        var hasHistory = live || (report is not null && (report.Cur.Samples > 0 || report.Prev.Samples > 0));
        var noHistory = !live && !hasHistory;
        var empty = feed?.Trouble is not null && report is { Series.Count: 0 }
            ? "нет связи — повтор через 10 с"
            : live
            ? "первый замер — через несколько секунд"
            : collector is null || !historyRead && source is HistorySource.Own or HistorySource.OwnStopped or HistorySource.Rudog or HistorySource.Sysstat
                ? "читаю историю…"
            : source == HistorySource.Own ? "данные копятся — первая точка через минуту"
            : "истории за период нет";

        var trouble = feed?.Trouble;

        // чего в чужой истории нет (sysstat без дисков) — карточка без истории, а не с нулями
        bool Lacks(string metric) => !live && report is not null && report.Missing.Contains(metric);
        const string lacks = "в истории sysstat этого нет";

        _cpu.Show(CpuCard(now, info, report, live, noHistory || Lacks("cpu"), Lacks("cpu") ? lacks : empty, trouble));
        _mem.Show(MemCard(now, report, live, noHistory || Lacks("mem"), Lacks("mem") ? lacks : empty, trouble));
        var disks = info?.Disks ?? [];
        var disk = MonitorDisks.Pick(server, disks);
        _disk.Show(DiskCard(now, disk, disks.Count > 1, anchor => PickDisk(anchor, server, disks, disk), report, live, noHistory || Lacks("disk"), Lacks("disk") ? "в истории sysstat диска нет" : empty, trouble));
        _net.Show(NetCard(now, info, report, live, noHistory || Lacks("net"), Lacks("net") ? lacks : empty, trouble));

        foreach (var (card, key) in new[] { (_cpu, "cpu"), (_mem, "mem"), (_disk, "disk") })
        {
            var on = MonitorAlerts.IsOn(server, key);
            card.SetAlert(on, MonitorAlerts.Tip(key, on));
        }

        _net.SetAlert(null, string.Empty);
    }

    private static TextBlock Piece(string text, string brush)
    {
        var block = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return block;
    }

    /// <summary>Справа от периодов: когда обновлено, как часто, откуда история, сколько без перезагрузки — по важности.</summary>
    private void RenderInfo(MonitorFeed? feed, MonitorReport? report)
    {
        Info.Children.Clear();

        if (feed is null)
        {
            Info.ToolTip = null;
            return;
        }

        if (feed.Trouble is { } trouble)
        {
            Info.Children.Add(Piece("нет связи", "TextFillColorSecondaryBrush"));
            Info.ToolTip = $"{trouble}. Повтор — на следующем замере, через 10 с.";
        }
        else
        {
            Info.ToolTip = null;
        }

        if (feed.UpdatedAt is { } at)
        {
            Info.Children.Add(Piece("обновлено " + at.ToString("HH:mm:ss"), "TextFillColorTertiaryBrush"));
        }
        else if (feed.Trouble is null)
        {
            Info.Children.Add(Piece("подключение…", "TextFillColorTertiaryBrush"));
        }

        if (_range == MonitorRange.Live)
        {
            Info.Children.Add(Piece("каждые 10 с", "TextFillColorTertiaryBrush"));
        }

        // откуда история, когда она не своя: rudog — как есть, sysstat — и как часто он пишет
        if (_range != MonitorRange.Live && feed.Collector is { } collector)
        {
            if (collector.Source == HistorySource.Rudog)
            {
                Info.Children.Add(Piece("история: rudog", "TextFillColorTertiaryBrush"));
            }
            else if (collector.Source == HistorySource.Sysstat)
            {
                var step = report?.SourceStep ?? 0;
                var every = step >= 60 ? $", раз в {step / 60} мин" : step > 0 ? $", раз в {step} с" : string.Empty;
                Info.Children.Add(Piece("история: sysstat" + every, "TextFillColorTertiaryBrush"));

                if (feed.Trouble is null)
                {
                    Info.ToolTip = collector.SaKeep > 0
                        ? $"История — из sysstat ({collector.SaDir}), он хранит {collector.SaKeep} дн.; пики — средние за его интервал"
                        : $"История — из sysstat ({collector.SaDir}); пики — средние за его интервал";
                }
            }
        }

        if (feed.Info is { UptimeSec: > 0 } info)
        {
            Info.Children.Add(Piece("без перезагрузки " + MonitorFormat.Uptime(info.UptimeSec), "TextFillColorTertiaryBrush"));
        }
    }

    /// <summary>Уровень по проценту и слова статуса — как в макете: у каждой карточки свои.</summary>
    private static (string Text, Brush Color, bool Loud) Level(double pct, double warn, double bad, string warnText, string badText) =>
        pct >= bad ? (badText, Palette.Bad, true) : pct >= warn ? (warnText, Palette.Warn, true) : ("норма", Palette.Ok, false);

    /// <summary>Статус карточки: без связи и до первого замера — словами, серым огоньком.</summary>
    private static (string Text, Brush Color, bool Loud) StatusOf(MonitorSample? now, string? trouble, Func<MonitorSample, (string, Brush, bool)> level) =>
        trouble is not null ? ("нет связи", Palette.Unknown, false)
        : now is null ? ("замер…", Palette.Unknown, false)
        : level(now);

    /// <summary>Шкала занятости: серая, а при «высоко» и «перегрузке» — цветом статуса.</summary>
    private static Brush? UseColor((string, Brush Color, bool Loud) status) => status.Loud ? status.Color : null;

    /// <summary>Период словами для строки «за период».</summary>
    private static string Per(MonitorRange range) => range switch
    {
        MonitorRange.Hour => "час",
        MonitorRange.Day => "24 часа",
        MonitorRange.Week => "7 дней",
        MonitorRange.Month => "30 дней",
        MonitorRange.Quarter => "90 дней",
        _ => "5 мин",
    };

    private static string Prev(MonitorRange range) => range switch
    {
        MonitorRange.Hour => "прошлый час",
        MonitorRange.Live => "прошлые 5 мин",
        _ => "прошлые " + Per(range),
    };

    /// <summary>Сдвиг среднего к прошлому периоду; мал или сравнить не с чем — пусто, как в макете.</summary>
    private static string Shift(MonitorStat? cur, MonitorStat? prev, bool pct)
    {
        var (text, sign) = MonitorFormat.Delta(cur?.Avg, prev?.Avg, pct);
        return sign == 0 ? string.Empty : text.Replace("▲ +", "▲ ").Replace("▼ −", "▼ ");
    }

    /// <summary>Строка «за период»: среднее со сдвигом и пик с моментом; подсказка у среднего — прошлый период.</summary>
    private static MonitorFootRow Foot(string? name, MonitorStat? cur, MonitorStat? prev, Func<double, string> fmt, bool pct, MonitorRange range) =>
        new(
            name,
            cur is { } c ? fmt(c.Avg) : "—",
            Shift(cur, prev, pct),
            cur is { } m ? fmt(m.Max) : "—",
            // пика не было (всё время ноль) — и момента нет: иначе «02:00:00» от нулевой отметки
            cur is { Max: > 0 } t ? MonitorFormat.Time(t.MaxT, range) : string.Empty,
            prev is { } p ? $"{Prev(range)}: среднее {fmt(p.Avg)}, пик {fmt(p.Max)}" : "сравнить пока не с чем — история короче двух периодов");

    /// <summary>График карточки; точек ещё нет — линия лежит на нуле. Истории нет — вместо графика строка.</summary>
    private static Action<MonitorChart>? Chart(MonitorReport? report, bool noHistory, IReadOnlyList<ChartRow> rows,
        Func<double, string> fmt, Func<IReadOnlyList<MonitorPoint>, double> top, bool mirror = false)
    {
        if (report is null || noHistory)
        {
            return null;
        }

        return chart => chart.Show(new ChartData
        {
            Points = report.Series,
            Rows = rows,
            From = report.Cur.From,
            To = report.Cur.To,
            Step = report.Step,
            Top = top(report.Series),
            Format = fmt,
            Mirror = mirror,
            Range = report.Range,
        });
    }

    /// <summary>Процент «с запасом»: ступени 25 / 50 / 100 — тихий сервер выглядит тихим, а не пилой во весь рост.</summary>
    private static double PctTop(double m) => m <= 20 ? 25 : m <= 45 ? 50 : 100;

    private static MonitorCardData CpuCard(MonitorSample? now, MonitorInfo? info, MonitorReport? r, bool live, bool noHistory, string empty, string? trouble)
    {
        var status = StatusOf(now, trouble, n => Level(n.Cpu, 75, MonitorAlerts.CpuBad, "высокая загрузка", "перегружен"));
        var cur = r?.Cur.Of("cpu");
        var range = r?.Range ?? MonitorRange.Live;
        var big = new List<(string, bool)> { (now is null ? "—" : MonitorFormat.Pct(now.Cpu), true) };

        if (info is { Cores: > 0 })
        {
            big.Add((MonitorFormat.Cores(info.Cores), false));
        }

        return new MonitorCardData
        {
            Title = "Процессор",
            Short = "Процессор",
            Status = status.Text,
            StatusColor = status.Color,
            StatusLoud = status.Loud,
            Big = big,
            Use = now?.Cpu,
            UseColor = UseColor(status),
            Sub = now is null ? [] : [["ожидание диска " + MonitorFormat.Pct(now.IOWait), "отнято хостингом " + MonitorFormat.Pct(now.Steal)]],
            Period = Per(range),
            Foot = [Foot(null, cur, r?.Prev.Of("cpu"), MonitorFormat.Pct, pct: true, range)],
            Chart = Chart(r, noHistory, [new ChartRow("среднее", p => p.Cpu, p => p.CpuMax, cur?.MaxT)], MonitorFormat.Pct,
                pts => PctTop(pts.Count == 0 ? 0 : pts.Max(p => p.Cpu))),
            Empty = empty,
            NoHistory = noHistory,
        };
    }

    private static MonitorCardData MemCard(MonitorSample? now, MonitorReport? r, bool live, bool noHistory, string empty, string? trouble)
    {
        var pct = now is { MemTotal: > 0 } n ? n.MemUsed * 100.0 / n.MemTotal : 0;
        var status = StatusOf(now, trouble, _ => Level(pct, 80, MonitorAlerts.MemBad, "мало свободной", "почти кончилась"));
        var cur = r?.Cur.Of("mem");
        var range = r?.Range ?? MonitorRange.Live;
        var total = now?.MemTotal ?? 0;

        return new MonitorCardData
        {
            Title = "Память",
            Short = "Память",
            Status = status.Text,
            StatusColor = status.Color,
            StatusLoud = status.Loud,
            Big = now is null
                ? [("—", true)]
                : [(MonitorFormat.Bytes(now.MemUsed), true), ($"из {MonitorFormat.Bytes(now.MemTotal)} · {MonitorFormat.Pct(pct)}", false)],
            Use = now is null ? null : pct,
            UseColor = UseColor(status),
            Sub = now is null
                ? []
                : [["доступно " + MonitorFormat.Bytes(Math.Max(0, now.MemTotal - now.MemUsed)), "кэш " + MonitorFormat.Bytes(now.MemCache)],
                   [now.SwapTotal > 0 && now.SwapUsed > 0 ? $"подкачка {MonitorFormat.Bytes(now.SwapUsed)} из {MonitorFormat.Bytes(now.SwapTotal)}"
                       : now.SwapTotal > 0 ? "подкачка не занята" : "подкачки нет"]],
            Period = Per(range),
            Foot = [Foot(null, cur, r?.Prev.Of("mem"), MonitorFormat.Bytes, pct: false, range)],
            Chart = Chart(r, noHistory, [new ChartRow("среднее", p => p.Mem, p => p.MemMax, cur?.MaxT)], MonitorFormat.Bytes,
                pts => total > 0 ? total : MonitorChart.NiceMax(pts.Count == 0 ? 0 : pts.Max(p => (double)p.Mem), bytes: true)),
            Empty = empty,
            NoHistory = noHistory,
        };
    }

    /// <summary>Выбор носителя для «Диска»: что это, занято из всего; выбранное — с галочкой; в подсказке — из чего складывается.</summary>
    private static void PickDisk(FrameworkElement anchor, ServerEntry server, IReadOnlyList<MonitorDisk> disks, MonitorDisk? current)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };

        foreach (var disk in disks)
        {
            var item = new MenuItem
            {
                Header = $"{disk.Label} — {MonitorFormat.Bytes(disk.Used)} из {MonitorFormat.Bytes(disk.Total)}",
                IsCheckable = true,
                IsChecked = disk.Id == current?.Id,
                ToolTip = string.Join(" · ", new[] { disk.Name, disk.Usb ? "подключена по USB" : string.Empty }.Where(x => x.Length > 0)
                    .Concat(disk.Parts.Select(fs => $"{fs.Mount} — {fs.Device}, {fs.Type}"))),
            };
            item.Click += (_, _) => MonitorDisks.Set(server, disk);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    /// <summary>
    /// «Диск»: выбранный носитель целиком (<see cref="MonitorDisks"/>) — занято из всего устройства, строкой ниже из чего
    /// это складывается; статус — по самому заполненному записываемому разделу (прошивка роутера всегда занимает больше
    /// половины чипа — по всему носителю он горел бы всегда). Скорость чтения и записи — всего сервера. Носителей
    /// несколько — заголовок в углу открывает выбор; записываемых разделов нет (только прошивка) — без тревоги.
    /// </summary>
    private static MonitorCardData DiskCard(MonitorSample? now, MonitorDisk? disk, bool many, Action<FrameworkElement>? pick,
        MonitorReport? r, bool live, bool noHistory, string empty, string? trouble)
    {
        var has = now is not null && disk is { Total: > 0 };
        var pct = has ? disk!.Pct : 0;
        var worst = disk?.Worst;
        var status = StatusOf(now, trouble, _ => worst is null
            ? ("только чтение", Palette.Unknown, false)
            : Level(worst.Pct, 75, MonitorAlerts.DiskBad, "мало места", "место кончается"));
        var rd = r?.Cur.Of("rd");
        var wr = r?.Cur.Of("wr");
        var range = r?.Range ?? MonitorRange.Live;
        var sub = new List<IReadOnlyList<string>>();

        // из чего складывается носитель — первой строкой: по ней видно, что всё определено верно
        if (has)
        {
            sub.Add(disk!.Pieces());
        }

        if (now is not null)
        {
            sub.Add(["сейчас чтение " + MonitorFormat.Rate(now.Rd), "запись " + MonitorFormat.Rate(now.Wr)]);
        }

        // место на диске у sysstat не пишется — изменения за период не показываем, а не «почти не менялось»
        if (!live && r is { Cur.Samples: > 0 } && !r.Missing.Contains("space"))
        {
            var delta = r.Cur.DiskDelta;
            sub.Add([$"за {Per(range)} занято " + (Math.Abs(delta) < 1048576 ? "почти столько же" : (delta > 0 ? "+" : "−") + MonitorFormat.Bytes(Math.Abs(delta)))]);
        }

        return new MonitorCardData
        {
            Title = disk?.Kind ?? "Диск",
            TitleNote = disk is { Note.Length: > 0 } ? "· " + disk.Note : string.Empty,
            TitlePick = many ? pick : null,
            Short = disk?.Kind ?? "Диск",
            Status = status.Text,
            StatusColor = status.Color,
            StatusLoud = status.Loud,
            Big = !has
                ? [("—", true)]
                : [(MonitorFormat.Bytes(disk!.Used), true),
                   ($"из {MonitorFormat.Bytes(disk.Total)} · {MonitorFormat.Pct(pct)} · свободно {MonitorFormat.Bytes(disk.Free)}", false)],
            Use = has ? pct : null,
            UseColor = UseColor(status),
            Sub = sub,
            Period = Per(range),
            Foot =
            [
                Foot("чтение", rd, r?.Prev.Of("rd"), MonitorFormat.Rate, pct: false, range),
                Foot("запись", wr, r?.Prev.Of("wr"), MonitorFormat.Rate, pct: false, range),
            ],
            Chart = Chart(r, noHistory,
                [new ChartRow("чтение", p => p.Rd, p => p.RdMax, rd?.MaxT), new ChartRow("запись", p => p.Wr, p => p.WrMax, wr?.MaxT)],
                MonitorFormat.Rate,
                pts => Math.Max(MonitorChart.NiceMax(pts.Count == 0 ? 0 : pts.Max(p => Math.Max(p.Rd, p.Wr)), bytes: true), 512 * 1024),
                mirror: true),
            Empty = empty,
            NoHistory = noHistory,
        };
    }

    private static MonitorCardData NetCard(MonitorSample? now, MonitorInfo? info, MonitorReport? r, bool live, bool noHistory, string empty, string? trouble)
    {
        // у сети нормы нет — огонёк только когда связи нет или замера ещё не было
        var status = StatusOf(now, trouble, _ => (string.Empty, Palette.Ok, false));
        var rx = r?.Cur.Of("rx");
        var tx = r?.Cur.Of("tx");
        var range = r?.Range ?? MonitorRange.Live;
        var sub = new List<IReadOnlyList<string>>();

        if (r is { Cur.Samples: > 0 } report)
        {
            sub.Add([$"за {Per(range)} прошло ↓ {MonitorFormat.Bytes(report.Cur.RxBytes)}", "↑ " + MonitorFormat.Bytes(report.Cur.TxBytes)]);
        }

        return new MonitorCardData
        {
            Title = "Сеть",
            TitleNote = info?.Iface is { Length: > 0 } name ? "· " + name : string.Empty,
            Short = "Сеть",
            Status = status.Text,
            StatusColor = status.Color,
            StatusLoud = status.Loud,
            Big = now is null
                ? [("—", true)]
                : [("↓ " + MonitorFormat.Rate(now.Rx), true), ("↑ " + MonitorFormat.Rate(now.Tx), true)],
            Sub = sub,
            Period = Per(range),
            Foot =
            [
                Foot("↓ входящий", rx, r?.Prev.Of("rx"), MonitorFormat.Rate, pct: false, range),
                Foot("↑ исходящий", tx, r?.Prev.Of("tx"), MonitorFormat.Rate, pct: false, range),
            ],
            Chart = Chart(r, noHistory,
                [new ChartRow("входящий", p => p.Rx, p => p.RxMax, rx?.MaxT), new ChartRow("исходящий", p => p.Tx, p => p.TxMax, tx?.MaxT)],
                MonitorFormat.Rate,
                pts => Math.Max(MonitorChart.NiceMax(pts.Count == 0 ? 0 : pts.Max(p => Math.Max(p.Rx, p.Tx)), bytes: true), 1024 * 1024),
                mirror: true),
            Empty = empty,
            NoHistory = noHistory,
        };
    }
}

/// <summary>
/// Четыре карточки: 2×2; узко — столбиком; широко и низко — в ряд. Порог — наименьшая удобная ширина
/// карточки 240, как в макете, и не уже, чем карточке нужно, чтобы ничего не резать.
/// </summary>
public sealed class MonitorGrid : Panel
{
    public const double Gap = 4;

    private const double Comfortable = 240;

    public int Columns { get; private set; } = 2;

    /// <summary>Раскладка сменилась — от неё зависит наименьшая высота окна.</summary>
    public event Action? ColumnsChanged;

    private double CardMinWidth() =>
        InternalChildren.OfType<MonitorCard>().Select(c => c.MinNeed.Width).DefaultIfEmpty(0).Max();

    private int Decide(Size size)
    {
        var min = Math.Max(Comfortable, CardMinWidth());

        if (size.Width >= 4 * min + 3 * Gap && size.Height <= 330)
        {
            return 4;
        }

        return size.Width < 2 * min + Gap ? 1 : 2;
    }

    protected override Size MeasureOverride(Size available)
    {
        var size = new Size(double.IsInfinity(available.Width) ? 600 : available.Width, double.IsInfinity(available.Height) ? 400 : available.Height);
        var columns = Decide(size);

        if (columns != Columns)
        {
            Columns = columns;
            Dispatcher.BeginInvoke(() => ColumnsChanged?.Invoke());
        }

        var rows = Math.Max(1, (InternalChildren.Count + columns - 1) / columns);
        var cell = new Size(
            Math.Max(0, (size.Width - Gap * (columns - 1)) / columns),
            Math.Max(0, (size.Height - Gap * (rows - 1)) / rows));

        foreach (UIElement child in InternalChildren)
        {
            child.Measure(cell);
        }

        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Columns;
        var rows = Math.Max(1, (InternalChildren.Count + columns - 1) / columns);
        var w = Math.Max(0, (finalSize.Width - Gap * (columns - 1)) / columns);
        var h = Math.Max(0, (finalSize.Height - Gap * (rows - 1)) / rows);

        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var col = i % columns;
            var row = i / columns;
            InternalChildren[i].Arrange(new Rect(col * (w + Gap), row * (h + Gap), w, h));
        }

        return finalSize;
    }
}
