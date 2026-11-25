using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MultiServerSync.Views.Controls;

/// <summary>Строка «за период»: подпись (у сети и диска — «↓ входящий», «чтение»), среднее со сдвигом, пик с моментом.</summary>
public sealed record MonitorFootRow(string? Name, string Avg, string AvgNote, string Peak, string PeakNote, string? AvgTip = null);

/// <summary>Что показать в карточке. Chart = null — вместо графика строка <see cref="Empty"/>.</summary>
public sealed class MonitorCardData
{
    /// <summary>Название — в правом верхнем углу, рядом со статусом.</summary>
    public required string Title { get; init; }

    /// <summary>Мелкая приписка к названию: «· ens33», «· /».</summary>
    public string TitleNote { get; init; } = string.Empty;

    /// <summary>Название в углу — выбор (у «Диска», когда хранилищ несколько): стрелка и щелчок открывают список у этого места.</summary>
    public Action<FrameworkElement>? TitlePick { get; init; }

    /// <summary>Название, когда карточке тесно и оно встаёт в строку со значением.</summary>
    public required string Short { get; init; }

    /// <summary>Статус словами; пусто — огонька нет (у сети, пока связь есть).</summary>
    public required string Status { get; init; }

    /// <summary>Огонёк статуса: зелёный — норма, жёлтый — высоко, красный — перегрузка, серый — неизвестно.</summary>
    public required Brush StatusColor { get; init; }

    /// <summary>Не норма: слово статуса тем же цветом, что огонёк.</summary>
    public bool StatusLoud { get; init; }

    /// <summary>Крупное значение: куски с Main = true — крупным, остальные — подписи к нему.</summary>
    public required IReadOnlyList<(string Text, bool Main)> Big { get; init; }

    /// <summary>Тонкая шкала занятости в процентах; null — у карточки её нет (место под неё остаётся).</summary>
    public double? Use { get; init; }

    /// <summary>Цвет шкалы: серая, а при «высоко» и «перегрузке» — цветом статуса.</summary>
    public Brush? UseColor { get; init; }

    /// <summary>Две строки пояснения; в строке — куски через «·», что не влезло — уходит целиком.</summary>
    public required IReadOnlyList<IReadOnlyList<string>> Sub { get; init; }

    /// <summary>Период словами для строки «за период»: «час», «24 часа», «5 мин».</summary>
    public required string Period { get; init; }

    /// <summary>Строка «за период»: одна без подписи или две с подписями.</summary>
    public required IReadOnlyList<MonitorFootRow> Foot { get; init; }

    /// <summary>Нарисовать график; null — графика нет, вместо него <see cref="Empty"/>.</summary>
    public Action<MonitorChart>? Chart { get; init; }

    public string Empty { get; init; } = string.Empty;

    /// <summary>Истории за период нет: остаётся только то, что известно сейчас, — без строки «за период».</summary>
    public bool NoHistory { get; init; }
}

/// <summary>
/// Карточка метрики «Мониторинга» — как в макете «Здоровье сервера»: крупное число (название и статус — в углу),
/// тонкая шкала, две строки пояснения, график, строка «за период» со средним и пиком. Жирное в карточке одно —
/// крупное число. Плотность — ступенями по фактическому размеру: всё → одна строка пояснения → без пояснений →
/// без строки «за период» → значение с названием в одну строку и график → только значение. На каждой ступени всё
/// оставшееся видно целиком; что не влезло по ширине внутри строки — уходит целым куском (<see cref="FitPanel"/>).
/// </summary>
public sealed class MonitorCard : Border
{
    private readonly CardStack _stack;

    public MonitorCard()
    {
        // рабочая поверхность внутри страницы — ступенью темнее карточки, как таблицы и вывод
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorSecondaryBrush");
        SetResourceReference(BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        UseLayoutRounding = true;

        // рамка сработавшего сигнала — поверх своей, в 2 точки, ничего внутри не сдвигает
        _lit.SetResourceReference(Border.BorderBrushProperty, "Pick.Stroke");
        var root = new Grid();
        root.Children.Add(_stack = new CardStack());
        root.Children.Add(_lit);
        Child = root;
    }

    private readonly Border _lit = new()
    {
        BorderThickness = new Thickness(2),
        CornerRadius = new CornerRadius(8),
        Margin = new Thickness(-1),
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };

    /// <summary>Сработал сигнал этого раздела — тускло-оранжевая рамка, пока <see cref="Services.MonitorAlerts"/> её не погасит.</summary>
    public bool Lit
    {
        get => _lit.Visibility == Visibility.Visible;
        set => _lit.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Кнопка сигнала: null — у раздела её нет (у сети нет нормы); true — включён.</summary>
    public void SetAlert(bool? on, string tip) => _stack.SetAlert(on, tip);

    /// <summary>Щелчок по кнопке сигнала.</summary>
    public event Action? AlertClicked
    {
        add => _stack.AlertClicked += value;
        remove => _stack.AlertClicked -= value;
    }

    /// <summary>Наименьший размер, при котором видно хотя бы значение с названием.</summary>
    public Size MinNeed => new(_stack.MinNeed.Width + 2, _stack.MinNeed.Height + 2);

    public void Show(MonitorCardData data) => _stack.Show(data);

    /// <summary>Сменилась минимальная потребность карточки — окно пересчитывает свой минимум.</summary>
    public event Action? NeedChanged
    {
        add => _stack.NeedChanged += value;
        remove => _stack.NeedChanged -= value;
    }

    private sealed class CardStack : Panel
    {
        private const double Gap = 8;

        /// <summary>Наименьшая высота графика на каждой ступени: с подписями шкалы и времени меньше не читается.</summary>
        private static readonly double[] ChartMin = [72, 64, 56, 48, 40, 0];

        private static readonly Thickness Wide = new(14, 10, 14, 10);
        private static readonly Thickness Tight = new(10, 5, 10, 5);

        /// <summary>Первая строка: крупное значение слева, название и статус — в правом углу.</summary>
        private readonly DockPanel _top = new() { LastChildFill = true };
        private readonly StackPanel _corner = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 4, 0, 0) };
        private readonly TextBlock _title = Text(13, FontWeights.Medium, "TextFillColorSecondaryBrush");
        private readonly TextBlock _titleNote = Text(12, FontWeights.Normal, "TextFillColorTertiaryBrush");
        private readonly TextBlock _status = Text(12, FontWeights.Normal, "TextFillColorTertiaryBrush");

        /// <summary>Название с припиской — щёлкается, когда в карточке есть выбор (<see cref="MonitorCardData.TitlePick"/>).</summary>
        private readonly Border _pick = new() { CornerRadius = new CornerRadius(4), Padding = new Thickness(4, 1, 4, 1), Margin = new Thickness(-4, -1, -4, -1), Background = Brushes.Transparent };
        private readonly Wpf.Ui.Controls.SymbolIcon _pickArrow = new() { Symbol = Wpf.Ui.Controls.SymbolRegular.ChevronDown24, FontSize = 11, Margin = new Thickness(4, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        private Action<FrameworkElement>? _titlePick;
        private readonly Ellipse _statusDot = new() { Width = 7, Height = 7, Margin = new Thickness(12, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly FitPanel _big = new() { Gap = 10, Baseline = true };
        private readonly FitPanel _bigTight = new() { Gap = 8, Baseline = true };
        private readonly Border _use = new() { Height = 3, CornerRadius = new CornerRadius(1.5) };
        private readonly Border _useFill = new() { CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left };
        private readonly StackPanel _sub = new();
        private readonly MonitorChart _chart = new();
        private readonly FitPanel _empty = new() { Align = HorizontalAlignment.Center };
        private readonly Border _foot = new() { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 8, 0, 0) };

        private UIElement[] Rows => [_top, _bigTight, _use, _sub, _chart, _empty, _foot];

        /// <summary>Кнопка сигнала: в правом нижнем углу, а на тесной ступени — в конце строки со значением.</summary>
        private readonly Border _alert = new() { CornerRadius = new CornerRadius(4), Cursor = Cursors.Hand, Visibility = Visibility.Collapsed };
        private readonly Wpf.Ui.Controls.SymbolIcon _alertIcon = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private bool _hasAlert;
        private bool _alertOn;

        /// <summary>Размер кнопки и значка — обычно и на тесной ступени; отступ от края карточки и зазор до текста.</summary>
        private const double AlertSize = 22, AlertTightSize = 18, AlertIcon = 13, AlertTightIcon = 12, AlertInset = 5, AlertGap = 4;

        private bool _hasUse;
        private bool _noHistory;
        private bool _hasChart;
        private int _level;

        /// <summary>Сколько ширины строке «за период» нужно, чтобы видны были подписи и числа (пояснения могут уйти).</summary>
        private double _footNeed;

        public CardStack()
        {
            ClipToBounds = true;

            _titleNote.Margin = new Thickness(4, 0, 0, 0);
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(_title);
            titleRow.Children.Add(_titleNote);
            titleRow.Children.Add(_pickArrow);
            _pick.Child = titleRow;
            _pickArrow.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
            _pick.MouseEnter += (_, _) => PaintPick();
            _pick.MouseLeave += (_, _) => PaintPick();
            _pick.MouseLeftButtonUp += (_, e) =>
            {
                if (_titlePick is { } pick)
                {
                    e.Handled = true;
                    pick(_pick);
                }
            };
            _corner.Children.Add(_pick);
            _corner.Children.Add(_statusDot);
            _corner.Children.Add(_status);
            DockPanel.SetDock(_corner, Dock.Right);
            _top.Children.Add(_corner);
            _top.Children.Add(_big);

            _use.SetResourceReference(Border.BackgroundProperty, "ControlStrokeColorDefaultBrush");
            _use.Child = _useFill;
            _foot.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");

            foreach (var row in Rows)
            {
                Children.Add(row);
            }

            _alert.Child = _alertIcon;
            _alert.MouseEnter += (_, _) => PaintAlert();
            _alert.MouseLeave += (_, _) => PaintAlert();
            _alert.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                AlertClicked?.Invoke();
            };
            Children.Add(_alert);
        }

        public event Action? NeedChanged;

        public event Action? AlertClicked;

        /// <summary>Под мышью выбор подсвечивается, как кнопка без рамки.</summary>
        private void PaintPick()
        {
            if (_titlePick is not null && _pick.IsMouseOver)
            {
                _pick.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
            }
            else
            {
                _pick.Background = Brushes.Transparent;
            }
        }

        public void SetAlert(bool? on, string tip)
        {
            var had = _hasAlert;
            _hasAlert = on is not null;
            _alertOn = on == true;
            _alert.Visibility = _hasAlert ? Visibility.Visible : Visibility.Collapsed;
            _alert.ToolTip = _hasAlert ? tip : null;
            _alertIcon.Symbol = _alertOn ? Wpf.Ui.Controls.SymbolRegular.AlertOn24 : Wpf.Ui.Controls.SymbolRegular.AlertOff24;
            PaintAlert();

            if (had != _hasAlert)
            {
                InvalidateMeasure();
            }
        }

        /// <summary>Спокойная — фон страницы, тонкие белые рамка и значок; включена — тускло-оранжевая, как «выбранное».</summary>
        private void PaintAlert()
        {
            var hover = _alert.IsMouseOver;
            _alert.SetResourceReference(Border.BackgroundProperty, _alertOn
                ? hover ? "Pick.FillHover" : "Pick.Fill"
                : hover ? "SubtleFillColorSecondaryBrush" : "SolidBackgroundFillColorQuarternaryBrush");
            _alert.SetResourceReference(Border.BorderBrushProperty, _alertOn ? "Pick.Stroke" : "TextFillColorPrimaryBrush");
            _alertIcon.SetResourceReference(Control.ForegroundProperty, _alertOn ? "Pick.Text" : "TextFillColorPrimaryBrush");
        }

        /// <summary>Сколько ширины нижняя строка ступени отдаёт кнопке сигнала.</summary>
        private double AlertReserve(int level) =>
            _hasAlert ? Math.Max(0, AlertInset + (level >= 5 ? AlertTightSize : AlertSize) + AlertGap - PaddingOf(level).Right) : 0;

        /// <summary>Самое нужное: значение с названием в одну строку. Держится наибольшим за время работы, чтобы окно не дёргалось.</summary>
        public Size MinNeed { get; private set; }

        private static TextBlock Text(double size, FontWeight weight, string brush, string text = "")
        {
            var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight, VerticalAlignment = VerticalAlignment.Center };
            block.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return block;
        }

        public void Show(MonitorCardData data)
        {
            _title.Text = data.Title;
            _titleNote.Text = data.TitleNote;
            _titlePick = data.TitlePick;
            _pickArrow.Visibility = _titlePick is null ? Visibility.Collapsed : Visibility.Visible;
            _pick.Cursor = _titlePick is null ? null : Cursors.Hand;
            _pick.ToolTip = _titlePick is null ? null : "Что показывать: у сервера несколько хранилищ";
            PaintPick();
            _titleNote.Visibility = data.TitleNote.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _status.Text = data.Status;
            _statusDot.Fill = data.StatusColor;
            _statusDot.Visibility = _status.Visibility = data.Status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (data.StatusLoud)
            {
                _status.Foreground = data.StatusColor;
            }
            else
            {
                _status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            }

            FillBig(_big, data, tight: false);
            FillBig(_bigTight, data, tight: true);

            // шкала: место под неё есть всегда — крупные числа карточек одного ряда стоят на одной высоте
            _hasUse = true;
            _useFill.Tag = Math.Clamp(data.Use ?? 0, 0, 100);
            _use.Visibility = Visibility.Visible;
            _use.Opacity = data.Use is null ? 0 : 1;

            if (data.UseColor is { } useColor)
            {
                _useFill.Background = useColor;
            }
            else
            {
                _useFill.SetResourceReference(Border.BackgroundProperty, "TextFillColorSecondaryBrush");
            }

            _sub.Children.Clear();

            foreach (var line in data.Sub)
            {
                var row = new FitPanel { Gap = 6, Separator = "·" };
                row.SetResourceReference(FitPanel.SeparatorBrushProperty, "TextFillColorTertiaryBrush");
                TextElement.SetFontSize(row, 12);

                foreach (var piece in line)
                {
                    row.Children.Add(Text(12, FontWeights.Normal, "TextFillColorTertiaryBrush", piece));
                }

                _sub.Children.Add(row);
            }

            _noHistory = data.NoHistory;
            _hasChart = data.Chart is not null;

            if (data.Chart is { } draw)
            {
                draw(_chart);
            }

            _empty.Children.Clear();

            if (!_hasChart && data.Empty.Length > 0)
            {
                _empty.Children.Add(Text(12, FontWeights.Normal, "TextFillColorTertiaryBrush", data.Empty));
            }

            FillFoot(data);

            InvalidateMeasure();
            InvalidateArrange();
        }

        private static void FillBig(FitPanel panel, MonitorCardData data, bool tight)
        {
            panel.Children.Clear();

            // в тесной строке нет ни статуса, ни шкалы — огонёк статуса перед названием, а при «высоко» число цветом статуса
            if (tight)
            {
                var name = Text(12, FontWeights.Medium, "TextFillColorSecondaryBrush");

                if (data.Status.Length > 0)
                {
                    var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 6, 0), Fill = data.StatusColor };
                    name.Inlines.Add(new InlineUIContainer(dot) { BaselineAlignment = BaselineAlignment.Center });
                }

                name.Inlines.Add(new Run(data.Short));
                panel.Children.Add(name);
                panel.ToolTip = data.Status.Length > 0 ? data.Status : null;
            }

            foreach (var (text, main) in data.Big)
            {
                var block = main
                    ? Text(tight ? 15 : 28, FontWeights.SemiBold, "TextFillColorPrimaryBrush", text)
                    : Text(13, FontWeights.Normal, "TextFillColorSecondaryBrush", text);

                if (tight && main && data.StatusLoud)
                {
                    block.Foreground = data.StatusColor;
                }

                panel.Children.Add(block);
            }
        }

        /// <summary>
        /// Строка «за период»: подписи тихие, числа обычным весом. Одна величина — «Среднее за 24 часа · ◆ Пик»
        /// над числами; две — «За 24 часа · среднее · ◆ пик» и по строке на величину.
        /// </summary>
        private void FillFoot(MonitorCardData data)
        {
            var grid = new Grid();
            var named = data.Foot.Any(r => r.Name is not null);
            _footNeed = 0;

            if (data.Foot.Count == 0)
            {
                _foot.Child = null;
                return;
            }

            if (named)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 84 });
            }

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (var i = 0; i <= data.Foot.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            void Put(UIElement e, int row, int col)
            {
                Grid.SetRow(e, row);
                Grid.SetColumn(e, col);
                grid.Children.Add(e);
            }

            var c0 = named ? 1 : 0;

            if (named)
            {
                Put(Text(11.5, FontWeights.Normal, "TextFillColorTertiaryBrush", "За " + data.Period), 0, 0);
            }

            Put(Text(11.5, FontWeights.Normal, "TextFillColorTertiaryBrush", named ? "среднее" : "Среднее за " + data.Period), 0, c0);
            Put(PeakHead(named ? "пик" : "Пик"), 0, c0 + 1);

            double nameW = 0, avgW = 0, peakW = 0;

            for (var i = 0; i < data.Foot.Count; i++)
            {
                var r = data.Foot[i];

                if (named)
                {
                    var name = Text(12.5, FontWeights.Normal, "TextFillColorSecondaryBrush", r.Name ?? string.Empty);
                    name.Margin = new Thickness(0, 0, 14, 0);
                    Put(name, i + 1, 0);
                    name.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    nameW = Math.Max(nameW, Math.Max(84, name.DesiredSize.Width));
                }

                var avg = Cell(r.Avg, r.AvgNote, r.AvgTip);
                avg.Margin = new Thickness(0, 2, 14, 0);
                Put(avg, i + 1, c0);
                Put(Cell(r.Peak, r.PeakNote, null), i + 1, c0 + 1);

                avgW = Math.Max(avgW, FirstWidth(avg) + 14);
                peakW = Math.Max(peakW, FirstWidth(grid.Children[^1]));
            }

            _footNeed = nameW + Math.Max(avgW, peakW) * 2;
            _foot.Child = grid;
        }

        private static double FirstWidth(UIElement cell)
        {
            if (cell is FitPanel { Children.Count: > 0 } panel)
            {
                panel.Children[0].Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                return panel.Children[0].DesiredSize.Width;
            }

            return 0;
        }

        private static FrameworkElement PeakHead(string text)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var diamond = new Rectangle
            {
                Width = 6,
                Height = 6,
                Margin = new Thickness(1, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(45),
            };
            diamond.SetResourceReference(Shape.FillProperty, "TextFillColorSecondaryBrush");
            row.Children.Add(diamond);
            row.Children.Add(Text(11.5, FontWeights.Normal, "TextFillColorTertiaryBrush", text));
            return row;
        }

        /// <summary>Число и мелкое пояснение за ним («▲ 12%», время пика); узко — пояснение уходит целиком.</summary>
        private static FitPanel Cell(string value, string note, string? tip)
        {
            var cell = new FitPanel { Gap = 6, Baseline = true, ToolTip = tip, Margin = new Thickness(0, 2, 0, 0) };
            cell.Children.Add(Text(12.5, FontWeights.Normal, "TextFillColorPrimaryBrush", value));

            if (note.Length > 0)
            {
                cell.Children.Add(Text(11.5, FontWeights.Normal, "TextFillColorTertiaryBrush", note));
            }

            return cell;
        }

        /// <summary>
        /// Что видно на ступени: 0 — всё, 1 — одна строка пояснения, 2 — без пояснений, 3 — без строки «за период»,
        /// 4 — название в строку со значением и график, 5 — только значение.
        /// </summary>
        private IEnumerable<UIElement> Visible(int level)
        {
            if (level <= 3)
            {
                yield return _top;

                if (_hasUse)
                {
                    yield return _use;
                }
            }
            else
            {
                yield return _bigTight;
            }

            if (level <= 1 && _sub.Children.Count > 0)
            {
                yield return _sub;
            }

            if (level <= 4)
            {
                yield return _hasChart ? _chart : _empty;
            }

            if (level <= 2 && !_noHistory && _foot.Child is not null)
            {
                yield return _foot;
            }
        }

        private static Thickness PaddingOf(int level) => level >= 4 ? Tight : Wide;

        /// <summary>У ступени 1 видна только первая строка пояснения.</summary>
        private void ShowSubLines(int level)
        {
            for (var i = 0; i < _sub.Children.Count; i++)
            {
                _sub.Children[i].Visibility = level == 0 || i == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private double Need(int level, double width)
        {
            ShowSubLines(level);
            var pad = PaddingOf(level);
            var rows = Visible(level).ToList();
            var height = pad.Top + pad.Bottom + Gap * Math.Max(0, rows.Count - 1);

            foreach (var row in rows)
            {
                height += row == _chart ? ChartMin[level] : row == _empty ? Math.Max(ChartMin[level], HeightOf(row, width)) : HeightOf(row, width);
            }

            return height;
        }

        private double HeightOf(UIElement row, double width)
        {
            row.Measure(new Size(width, double.PositiveInfinity));
            return row.DesiredSize.Height;
        }

        protected override Size MeasureOverride(Size available)
        {
            var width = double.IsInfinity(available.Width) ? 400 : available.Width;
            var height = double.IsInfinity(available.Height) ? 1000 : available.Height;
            var inner = Math.Max(0, width - Wide.Left - Wide.Right);

            // ступень — первая, на которой всё видимое помещается целиком; строке «за период» нужна своя ширина
            _level = 5;

            // запас в точку экрана: на 125–250% поля и строки округляются, впритык ступень терялась бы зря
            var slack = Core.DevicePixel.Of(this);

            for (var level = 0; level <= 5; level++)
            {
                if (level <= 2 && !_noHistory && _foot.Child is not null && _footNeed + AlertReserve(level) > inner + slack)
                {
                    continue;
                }

                if (Need(level, inner) <= height + slack)
                {
                    _level = level;
                    break;
                }
            }

            ShowSubLines(_level);
            var visible = Visible(_level).ToHashSet();
            var pad = PaddingOf(_level);
            var w = Math.Max(0, width - pad.Left - pad.Right);

            var last = Visible(_level).LastOrDefault();

            foreach (var row in Rows)
            {
                if (visible.Contains(row))
                {
                    row.Measure(new Size(row == last ? Math.Max(0, w - AlertReserve(_level)) : w, double.PositiveInfinity));
                }
            }

            var alertSize = _level >= 5 ? AlertTightSize : AlertSize;
            _alert.Measure(new Size(alertSize, alertSize));

            // меньше строки «название, значение и подписи к нему» карточка не бывает: окно не сужается так, чтобы она не влезла
            _bigTight.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var essential = _bigTight.FullWidth + slack + AlertReserve(5);

            // замер без предела выше затёр раскладку — у тесной ступени строка снова меряется по своей ширине,
            // иначе она показывала все куски и обрезалась краем карточки («свободно 15.6» без «ГБ»)
            if (visible.Contains(_bigTight))
            {
                _bigTight.Measure(new Size(Math.Max(0, w - (_bigTight == last ? AlertReserve(_level) : 0)), double.PositiveInfinity));
            }
            var need = new Size(
                Math.Max(MinNeed.Width, Tight.Left + Tight.Right + essential),
                Math.Max(MinNeed.Height, Tight.Top + Tight.Bottom + _bigTight.DesiredSize.Height));

            if (need != MinNeed)
            {
                MinNeed = need;
                Dispatcher.BeginInvoke(() => NeedChanged?.Invoke());
            }

            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var visible = Visible(_level).ToList();
            var pad = PaddingOf(_level);
            var w = Math.Max(0, finalSize.Width - pad.Left - pad.Right);
            var fixedHeight = visible.Where(r => r != _chart && r != _empty).Sum(r => r.DesiredSize.Height)
                + Gap * Math.Max(0, visible.Count - 1);
            var flexible = Math.Max(0, finalSize.Height - pad.Top - pad.Bottom - fixedHeight);
            var y = pad.Top;

            foreach (var row in Rows)
            {
                if (!visible.Contains(row))
                {
                    row.Arrange(new Rect(-100000, 0, 0, 0));
                }
            }

            var last = visible.LastOrDefault();

            foreach (var row in visible)
            {
                var h = row == _chart || row == _empty ? flexible : row.DesiredSize.Height;
                var rw = row == last ? Math.Max(0, w - AlertReserve(_level)) : w;

                if (row == _empty)
                {
                    // строка вместо графика — посередине оставшегося места
                    var text = _empty.DesiredSize.Height;
                    row.Arrange(new Rect(pad.Left, y + Math.Max(0, (h - text) / 2), rw, Math.Min(h, text)));
                }
                else
                {
                    row.Arrange(new Rect(pad.Left, y, rw, h));
                }

                if (row == _use && w * ((double)(_useFill.Tag ?? 0.0) / 100) is var fill && !(Math.Abs(_useFill.Width - fill) < 0.1))
                {
                    _useFill.Width = fill;
                }

                y += h + Gap;
            }

            // кнопка сигнала: в правом нижнем углу; на тесной ступени — в конце строки со значением, по её середине
            if (_hasAlert)
            {
                var tight = _level >= 5;
                var size = tight ? AlertTightSize : AlertSize;
                var top = tight
                    ? pad.Top + Math.Max(0, (_bigTight.DesiredSize.Height - size) / 2)
                    : finalSize.Height - AlertInset - size;
                _alert.BorderThickness = new Thickness(Core.DevicePixel.Of(this));
                _alert.Width = _alert.Height = size;
                _alertIcon.FontSize = tight ? AlertTightIcon : AlertIcon;
                _alert.Arrange(new Rect(Math.Max(0, finalSize.Width - AlertInset - size), Math.Max(0, top), size, size));
            }
            else
            {
                _alert.Arrange(new Rect(-100000, 0, 0, 0));
            }

            return finalSize;
        }
    }
}

/// <summary>
/// Цвета графиков и стрелок «Мониторинга» — как в макете. Линии — синяя и оранжевая; стрелки: рост — жёлтый,
/// спад — зелёный; огоньки статуса — зелёный, жёлтый, красный, как у статусов программы.
/// Значения — в теме (Themes\…\Monitor.xaml); кисть берётся в момент рисования, после смены темы вид рисуется заново.
/// </summary>
public static class Palette
{
    public static Brush Series1 => Core.AppTheme.Brush("Monitor.Series1");
    public static Brush Series2 => Core.AppTheme.Brush("Monitor.Series2");

    public static Brush Up => Core.AppTheme.Brush("Monitor.Up");
    public static Brush Down => Core.AppTheme.Brush("Monitor.Down");

    public static Brush Ok => Core.AppTheme.Brush("Monitor.Ok");
    public static Brush Warn => Core.AppTheme.Brush("Monitor.Warn");
    public static Brush Bad => Core.AppTheme.Brush("Monitor.Bad");
    public static Brush Unknown => Core.AppTheme.Brush("Monitor.Unknown");
}
