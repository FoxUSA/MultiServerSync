using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using MultiServerSync.Models;
using MultiServerSync.Services;

namespace MultiServerSync.Views.Controls;

/// <summary>
/// Перестановка мышью, как ярлыков в браузере: схватил — элемент едет за мышью поверх
/// остальных, соседи плавно расступаются, отпустил — он встаёт в освободившееся место.
/// Escape или потеря мыши — всё возвращается как было.
///
/// Один метод на всё, что переставляется: ярлыки вкладок и наборов, кнопки разделов,
/// серверы в панели слева. Элементы в один ряд или столбик (StackPanel) могут быть разного
/// размера — соседи сдвигаются ровно на размер перетаскиваемого. В остальных раскладках
/// (краткие кирпичики в два ряда, личные вкладки у правого края) размер у всех одинаковый:
/// элемент встаёт ровно на место того, кого сменил.
/// Если элементы внутри ScrollViewer, у его края список подкручивается сам.
/// Первые <c>fixedCount</c> элементов закреплены: их не двигают и никого перед ними не пускают.
/// </summary>
public sealed class TabReorder
{
    private static readonly Duration Shift = new(TimeSpan.FromMilliseconds(160));
    private static readonly Duration Settle = new(TimeSpan.FromMilliseconds(130));
    private static readonly IEasingFunction Ease = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });

    /// <summary>Ширина полосы у края ленты, где она начинает подкручиваться.</summary>
    private const double EdgeZone = 36;

    /// <summary>
    /// На сколько надо повести мышь с зажатой кнопкой, чтобы ярлык оторвался от ленты.
    /// Системные 4 точки ловили дрожание руки при обычном щелчке.
    /// </summary>
    private const double DragStart = 8;

    /// <summary>
    /// На сколько надо повести мышь к краю ленты от места, где ярлык подхватили, чтобы лента
    /// поехала. Иначе ярлык, подхваченный у самого края, сразу уезжал вместе с лентой
    /// и при отпускании вставал на чужое место — вкладки менялись местами от щелчка.
    /// </summary>
    private const double EdgeArm = 16;

    /// <summary>Кто ловит мышь: список или панель с элементами.</summary>
    private readonly FrameworkElement _host;
    private readonly ScrollViewer? _scroller;
    private readonly Func<List<FrameworkElement>?> _containers;
    private readonly Func<DependencyObject?, FrameworkElement?> _containerOf;
    private readonly Func<FrameworkElement, object?> _itemOf;
    private readonly Func<object, bool> _canDrag;
    private int _fixedCount;

    /// <summary>Сколько первых элементов закреплено; лента архива — без закреплённых.</summary>
    public int FixedCount
    {
        get => _fixedCount;
        set => _fixedCount = value;
    }
    private readonly Action<object, int> _move;

    /// <summary>
    /// Можно ли схватить за кнопку внутри элемента. У ярлыков нельзя — там крестик и карандаш;
    /// у кнопок разделов и галочек серверов сам элемент и есть кнопка.
    /// </summary>
    private readonly bool _grabButtons;

    /// <summary>Лента прокручивается сверху вниз, а не слева направо.</summary>
    private readonly bool _verticalScroll;
    private readonly DispatcherTimer _edgeTimer;

    private FrameworkElement? _pressed;

    /// <summary>
    /// Где нажали — относительно рамки ленты, а не самого списка. Щелчок по ярлыку у края
    /// подкручивает ленту, чтобы показать его целиком, и список уезжает из-под неподвижной мыши:
    /// по координатам списка это выглядело как рывок мышью, начиналось перетаскивание,
    /// и вкладки переставлялись от простого щелчка.
    /// </summary>
    private Point _pressPoint;

    /// <summary>Где нажали внутри ярлыка: за эту точку он и едет за мышью.</summary>
    private Vector _pressInItem;

    private bool _dragging;
    private bool _settling;
    private Panel? _panel;
    private List<FrameworkElement> _items = [];
    private Vector[] _origins = [];
    private int _from;
    private int _target;
    private Vector _grabOffset;

    /// <summary>Элементы в один ряд или столбик: размеры могут быть разные.</summary>
    private bool _linear;
    private bool _vertical;

    /// <summary>Шаг каждого элемента вдоль ряда: от его начала до начала следующего.</summary>
    private double[] _strides = [];

    /// <summary>Где была мышь (в рамке ленты), когда ярлык оторвался, — от неё меряется <see cref="EdgeArm"/>.</summary>
    private double _dragStartAt;
    private bool _edgeLogged;

    /// <summary>Элементы списка: ярлыки вкладок, серверы в панели. Без прокрутки — <paramref name="scroller"/> null.</summary>
    public TabReorder(
        ItemsControl list,
        ScrollViewer? scroller,
        Func<object, bool> canDrag,
        int fixedCount,
        Action<object, int> move,
        bool grabButtons = false)
        : this(
            list,
            scroller,
            () => ContainersOf(list),
            source => ContainerOf(list, source),
            container => list.ItemContainerGenerator.ItemFromContainer(container),
            canDrag,
            fixedCount,
            move,
            grabButtons)
    {
    }

    /// <summary>Сами дети панели — кнопки разделов. Элемент для <paramref name="move"/> — сам ребёнок.</summary>
    public TabReorder(Panel panel, Func<object, bool> canDrag, Action<object, int> move)
        : this(
            panel,
            null,
            () => panel.Children.OfType<FrameworkElement>().ToList(),
            source => ChildOf(panel, source),
            container => container,
            canDrag,
            fixedCount: 0,
            move,
            grabButtons: true)
    {
    }

    private TabReorder(
        FrameworkElement host,
        ScrollViewer? scroller,
        Func<List<FrameworkElement>?> containers,
        Func<DependencyObject?, FrameworkElement?> containerOf,
        Func<FrameworkElement, object?> itemOf,
        Func<object, bool> canDrag,
        int fixedCount,
        Action<object, int> move,
        bool grabButtons)
    {
        _host = host;
        _scroller = scroller;
        _containers = containers;
        _containerOf = containerOf;
        _itemOf = itemOf;
        _canDrag = canDrag;
        _fixedCount = fixedCount;
        _move = move;
        _grabButtons = grabButtons;
        _verticalScroll = scroller is { HorizontalScrollBarVisibility: ScrollBarVisibility.Disabled };

        _edgeTimer = new DispatcherTimer(DispatcherPriority.Input, host.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _edgeTimer.Tick += (_, _) => OnEdgeTick();

        host.PreviewMouseLeftButtonDown += OnMouseDown;
        host.PreviewMouseMove += OnMouseMove;
        host.PreviewMouseLeftButtonUp += OnMouseUp;

        // кнопка внутри тоже ловит мышь и теряет её, когда перетаскивание забирает мышь себе, —
        // это событие всплывает сюда же, но отменять из-за него нечего
        host.LostMouseCapture += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, _host))
            {
                Cancel();
            }
        };
        host.PreviewKeyDown += OnKeyDown;
    }

    /// <summary>
    /// Куда ещё можно бросить элемент, кроме места в ряду, — значок архива вкладок. Бросили
    /// на него — вместо перестановки <see cref="DroppedOut"/>.
    /// </summary>
    public FrameworkElement? OutTarget { get; set; }

    public Action<object>? DroppedOut { get; set; }

    /// <summary>Мышь с элементом над <see cref="OutTarget"/> или ушла с него — подсветить цель.</summary>
    public Action<bool>? OverOutTarget { get; set; }

    private bool _overOut;

    private bool MouseOverOut =>
        OutTarget is { IsVisible: true } target && new Rect(target.RenderSize).Contains(Mouse.GetPosition(target));

    private void UpdateOverOut()
    {
        var over = _dragging && MouseOverOut;

        if (over == _overOut)
        {
            return;
        }

        _overOut = over;
        OverOutTarget?.Invoke(over);

        if (_items.Count > _from)
        {
            // над целью ярлык бледнеет: отпустишь — уйдёт туда, а не на место в ряду
            _items[_from].Opacity = over ? 0.5 : 1;
        }
    }

    /// <summary>Относительно чего меряется ход мыши: рамка ленты или, если её нет, сама панель.</summary>
    private FrameworkElement Frame => _scroller ?? _host;

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = null;

        if (_dragging || _settling || e.ClickCount > 1)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;

        if (_containerOf(source) is not { } container)
        {
            return;
        }

        // крестик и карандаш на ярлыке — не перетаскивание
        if (!_grabButtons && FindAncestor<ButtonBase>(source, container) is not null)
        {
            return;
        }

        if (_itemOf(container) is { } item && _canDrag(item))
        {
            _pressed = container;
            _pressPoint = e.GetPosition(Frame);
            _pressInItem = (Vector)e.GetPosition(container);
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _pressed = null;
            return;
        }

        // мышь захвачена: без этого список сам выделял бы ярлыки, над которыми едем
        if (_dragging)
        {
            e.Handled = true;
            Follow(e.GetPosition(_panel));
            UpdateOverOut();
            return;
        }

        if (_pressed is null)
        {
            return;
        }

        var delta = e.GetPosition(Frame) - _pressPoint;

        if (Math.Abs(delta.X) < Math.Max(DragStart, SystemParameters.MinimumHorizontalDragDistance)
            && Math.Abs(delta.Y) < Math.Max(DragStart, SystemParameters.MinimumVerticalDragDistance))
        {
            return;
        }

        AppLog.Debug($"«{Label(_itemOf(_pressed))}» подхвачен: мышь сдвинулась на {delta.X:0},{delta.Y:0} от нажатия"
            + (_scroller is null ? "." : $", лента сдвинута на {Offset:0} из {Scrollable:0}."));

        if (Begin(_pressed))
        {
            e.Handled = true;
            Follow(e.GetPosition(_panel));
        }

        _pressed = null;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        _pressed = null;

        if (!_dragging)
        {
            return;
        }

        e.Handled = true;
        Drop();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_dragging && e.Key == Key.Escape)
        {
            e.Handled = true;
            Cancel();
        }
    }

    /// <summary>Элемент оторвался от ленты: запоминаем, где кто стоял, и поднимаем его над соседями.</summary>
    private bool Begin(FrameworkElement item)
    {
        if (_containers() is not { } items)
        {
            return false;
        }

        var from = items.IndexOf(item);

        if (from < _fixedCount || VisualTreeHelper.GetParent(item) is not Panel panel)
        {
            return false;
        }

        _panel = panel;
        _items = items;
        _from = from;
        _target = from;
        _linear = panel is StackPanel;
        _vertical = panel is StackPanel { Orientation: Orientation.Vertical };

        // место элемента без сдвигов анимации: смещение в ленте, а не на экране
        _origins = items.Select(i => VisualTreeHelper.GetOffset(i)).ToArray();
        _strides = Strides(items, _origins);

        _grabOffset = _pressInItem;

        foreach (var container in items)
        {
            Place(container, new Vector());
        }

        Panel.SetZIndex(item, 100);
        item.Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.35, Direction = 270 };

        _dragging = true;
        _dragStartAt = Along(Mouse.GetPosition(Frame), _verticalScroll);
        _edgeLogged = false;
        _host.CaptureMouse();

        if (_scroller is not null)
        {
            _edgeTimer.Start();
        }

        return true;
    }

    /// <summary>
    /// Шаг вдоль ряда: от начала элемента до начала следующего — в него входят и отступы.
    /// У последнего следующего нет: его размер плюс тот же зазор, что у соседа.
    /// </summary>
    private double[] Strides(List<FrameworkElement> items, Vector[] origins)
    {
        var strides = new double[items.Count];

        for (var i = 0; i < items.Count; i++)
        {
            strides[i] = i + 1 < items.Count
                ? Along(origins[i + 1]) - Along(origins[i])
                : Size(items[i]) + (i > 0 ? strides[i - 1] - Size(items[i - 1]) : 0);
        }

        return strides;
    }

    /// <summary>Элемент за мышью; остальные уступают ему место.</summary>
    private void Follow(Point inPanel)
    {
        var dragged = _items[_from];
        var size = new Vector(dragged.ActualWidth, dragged.ActualHeight);
        var pos = (Vector)inPanel - _grabOffset;

        int target;

        if (_linear)
        {
            // ездит вдоль ряда от первого незакреплённого места до конца последнего элемента
            var start = Along(_origins[_fixedCount]);
            var last = _items.Count - 1;
            var end = Along(_origins[last]) + _strides[last] - _strides[_from];
            var along = Math.Clamp(Along(pos), start, Math.Max(start, end));
            pos = _vertical ? new Vector(_origins[_from].X, along) : new Vector(along, _origins[_from].Y);

            // встаёт за каждым, чью середину перешла его середина
            var center = along + Size(dragged) / 2;
            target = _from;

            for (var i = _fixedCount; i < _items.Count; i++)
            {
                var middle = Along(_origins[i]) + Size(_items[i]) / 2;

                if (i > _from && center > middle)
                {
                    target++;
                }
                else if (i < _from && center < middle)
                {
                    target--;
                }
            }
        }
        else
        {
            // ездит только в пределах мест, куда его можно поставить
            var movable = _origins.Skip(_fixedCount).ToArray();
            pos.X = Math.Clamp(pos.X, movable.Min(o => o.X), movable.Max(o => o.X));
            pos.Y = Math.Clamp(pos.Y, movable.Min(o => o.Y), movable.Max(o => o.Y));

            // встаёт туда, к чьей середине ближе
            var center = pos + size / 2;
            var best = double.MaxValue;
            target = _from;

            for (var i = _fixedCount; i < _items.Count; i++)
            {
                var distance = (_origins[i] + size / 2 - center).LengthSquared;

                if (distance < best)
                {
                    best = distance;
                    target = i;
                }
            }
        }

        Place(dragged, pos - _origins[_from]);

        if (target == _target)
        {
            return;
        }

        _target = target;

        for (var i = 0; i < _items.Count; i++)
        {
            if (i != _from)
            {
                Animate(_items[i], ShiftOf(i), Shift);
            }
        }
    }

    /// <summary>На сколько сдвинуть элемент <paramref name="i"/>, если перетаскиваемый поставить на _target.</summary>
    private Vector ShiftOf(int i)
    {
        if (!_linear)
        {
            return _origins[NewIndex(i)] - _origins[i];
        }

        var step = 0.0;

        if (_from < _target && i > _from && i <= _target)
        {
            step = -_strides[_from];
        }
        else if (_from > _target && i >= _target && i < _from)
        {
            step = _strides[_from];
        }

        return _vertical ? new Vector(0, step) : new Vector(step, 0);
    }

    /// <summary>Куда встанет ярлык <paramref name="i"/>, если перетаскиваемый поставить на _target.</summary>
    private int NewIndex(int i)
    {
        if (_from < _target && i > _from && i <= _target)
        {
            return i - 1;
        }

        if (_from > _target && i >= _target && i < _from)
        {
            return i + 1;
        }

        return i;
    }

    /// <summary>Куда доедет перетаскиваемый, встав на <paramref name="target"/>.</summary>
    private Vector LandingOf(int target)
    {
        if (!_linear || target <= _from)
        {
            return _origins[target] - _origins[_from];
        }

        // вперёд по ряду: встаёт в конец места, которое освободил последний сдвинутый
        var step = Along(_origins[target]) + _strides[target] - _strides[_from] - Along(_origins[_from]);
        return _vertical ? new Vector(0, step) : new Vector(step, 0);
    }

    /// <summary>Отпустили: элемент доезжает до своего места, и только тогда список переставляется.</summary>
    private void Drop()
    {
        var dragged = _items[_from];
        var item = _itemOf(dragged);
        var from = _from;
        var target = _target;

        // бросили на значок архива: ряд возвращается как был, элемент уходит туда
        if (MouseOverOut && item is not null && DroppedOut is { } dropOut)
        {
            AppLog.Debug($"«{Label(item)}» брошен на {OutTarget?.Name}.");
            Stop();
            Reset();
            dropOut(item);
            return;
        }

        AppLog.Debug(target == from
            ? $"«{Label(item)}» отпущен на своём месте ({from + 1})."
            : $"«{Label(item)}» отпущен: место {from + 1} → {target + 1}.");

        Stop();
        _settling = true;

        Animate(dragged, LandingOf(target), Settle, () =>
        {
            // перестановка и сброс сдвигов — в одном кадре: элементы не мигают
            if (target != from && item is not null)
            {
                _move(item, target);
            }

            Reset();
        });
    }

    /// <summary>Отмена: все едут на свои прежние места.</summary>
    private void Cancel()
    {
        if (!_dragging)
        {
            return;
        }

        var dragged = _items[_from];

        AppLog.Debug($"Перетаскивание «{Label(_itemOf(dragged))}» отменено.");

        Stop();
        _settling = true;

        foreach (var container in _items)
        {
            if (container != dragged)
            {
                Animate(container, new Vector(), Shift);
            }
        }

        Animate(dragged, new Vector(), Settle, Reset);
    }

    private void Stop()
    {
        _dragging = false;
        UpdateOverOut();
        _edgeTimer.Stop();

        if (_host.IsMouseCaptured)
        {
            _host.ReleaseMouseCapture();
        }
    }

    private void Reset()
    {
        foreach (var container in _items)
        {
            Place(container, new Vector());

            container.ClearValue(Panel.ZIndexProperty);
            container.ClearValue(UIElement.EffectProperty);
            container.ClearValue(UIElement.OpacityProperty);
        }

        _items = [];
        _panel = null;
        _settling = false;
    }

    private double Offset => _verticalScroll ? _scroller!.VerticalOffset : _scroller!.HorizontalOffset;

    private double Scrollable => _verticalScroll ? _scroller!.ScrollableHeight : _scroller!.ScrollableWidth;

    /// <summary>У края ленты она подкручивается, пока мышь там держат, — и элемент едет вместе с ней.</summary>
    private void OnEdgeTick()
    {
        if (!_dragging || _panel is null || _scroller is null)
        {
            return;
        }

        var at = Along(Mouse.GetPosition(_scroller), _verticalScroll);
        var viewport = _verticalScroll ? _scroller.ViewportHeight : _scroller.ViewportWidth;
        var step = 0.0;

        // лента едет, только если мышь повели к этому краю, а не подхватили элемент уже у края
        if (at < EdgeZone && _dragStartAt - at >= EdgeArm)
        {
            step = -Math.Ceiling((EdgeZone - at) / 3);
        }
        else if (at > viewport - EdgeZone && at - _dragStartAt >= EdgeArm)
        {
            step = Math.Ceiling((at - (viewport - EdgeZone)) / 3);
        }

        if (step == 0)
        {
            return;
        }

        var offset = Math.Clamp(Offset + step, 0, Scrollable);

        if (offset == Offset)
        {
            return;
        }

        if (!_edgeLogged)
        {
            _edgeLogged = true;
            AppLog.Debug($"Лента поехала у {(step < 0 ? "начала" : "конца")}: мышь в {at:0} из {viewport:0}, "
                + $"подхвачен в {_dragStartAt:0}.");
        }

        if (_verticalScroll)
        {
            _scroller.ScrollToVerticalOffset(offset);
        }
        else
        {
            _scroller.ScrollToHorizontalOffset(offset);
        }

        _scroller.UpdateLayout();
        Follow(Mouse.GetPosition(_panel));
    }

    private double Along(Vector v) => _vertical ? v.Y : v.X;

    private double Size(FrameworkElement element) => _vertical ? element.ActualHeight : element.ActualWidth;

    private static double Along(Point p, bool vertical) => vertical ? p.Y : p.X;

    /// <summary>Сдвиг без анимации: прежняя, если шла, снимается.</summary>
    private static void Place(UIElement element, Vector shift)
    {
        var transform = Translate(element);
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        transform.X = shift.X;
        transform.Y = shift.Y;
    }

    private static void Animate(UIElement element, Vector to, Duration duration, Action? done = null)
    {
        var transform = Translate(element);

        var x = new DoubleAnimation(to.X, duration) { EasingFunction = Ease };
        var y = new DoubleAnimation(to.Y, duration) { EasingFunction = Ease };

        // обе оси едут одинаково долго — хватит отметки об окончании одной
        if (done is not null)
        {
            x.Completed += (_, _) => done();
        }

        transform.BeginAnimation(TranslateTransform.XProperty, x);
        transform.BeginAnimation(TranslateTransform.YProperty, y);
    }

    /// <summary>Свой сдвиг у каждого элемента: общий замороженный из стиля не годится.</summary>
    private static TranslateTransform Translate(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform { IsFrozen: false } own)
        {
            return own;
        }

        var transform = new TranslateTransform();
        element.RenderTransform = transform;
        return transform;
    }

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// <summary>Название для журнала.</summary>
    private static string Label(object? item) => item switch
    {
        Workspace workspace => workspace.Title,
        ServerSet set => set.Title,
        ServerCheck check => check.Server.Display,
        FrameworkElement element => element.Name,
        _ => item?.ToString() ?? "?",
    };

    /// <summary>Обёртки всех элементов списка; null — какие-то ещё не созданы.</summary>
    private static List<FrameworkElement>? ContainersOf(ItemsControl list)
    {
        var count = list.Items.Count;
        var items = new List<FrameworkElement>(count);

        for (var i = 0; i < count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
            {
                return null;
            }

            items.Add(container);
        }

        return items;
    }

    /// <summary>Обёртка элемента списка, внутри которой нажали.</summary>
    private static FrameworkElement? ContainerOf(ItemsControl list, DependencyObject? node)
    {
        while (node is not null && !ReferenceEquals(node, list))
        {
            if (node is FrameworkElement element && list.ItemContainerGenerator.IndexFromContainer(element) >= 0)
            {
                return element;
            }

            node = ParentOf(node);
        }

        return null;
    }

    /// <summary>Ребёнок панели, внутри которого нажали.</summary>
    private static FrameworkElement? ChildOf(Panel panel, DependencyObject? node)
    {
        while (node is not null && !ReferenceEquals(node, panel))
        {
            var parent = ParentOf(node);

            if (ReferenceEquals(parent, panel))
            {
                return node as FrameworkElement;
            }

            node = parent;
        }

        return null;
    }

    /// <summary>Ближайший <typeparamref name="T"/> между нажатым местом и элементом, сам элемент не в счёт.</summary>
    private static T? FindAncestor<T>(DependencyObject? node, DependencyObject stop)
        where T : DependencyObject
    {
        while (node is not null && !ReferenceEquals(node, stop))
        {
            if (node is T match)
            {
                return match;
            }

            node = ParentOf(node);
        }

        return null;
    }

    private static DependencyObject? ParentOf(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);
}
