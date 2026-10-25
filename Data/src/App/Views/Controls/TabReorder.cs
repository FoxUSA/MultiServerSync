using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using MultiServerSync.Services;

namespace MultiServerSync.Views.Controls;

/// <summary>
/// Перестановка ярлыков мышью, как в браузере: схватил ярлык — он едет за мышью поверх
/// остальных, соседи плавно расступаются, отпустил — ярлык встаёт в освободившееся место.
/// Escape или потеря мыши — всё возвращается как было.
///
/// Ярлыки лежат в ряд (StackPanel) или краткими кирпичиками в два ряда по столбцам (WrapPanel),
/// размер у всех одинаковый: ярлык встаёт ровно на место того, кого сменил. Сам список —
/// внутри внешнего ScrollViewer: у края ленты она подкручивается сама.
/// Первые <c>fixedCount</c> ярлыков закреплены: их не двигают и никого перед ними не пускают.
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

    private readonly ListBox _list;
    private readonly ScrollViewer _scroller;
    private readonly Func<object, bool> _canDrag;
    private readonly int _fixedCount;
    private readonly Action<object, int> _move;
    private readonly DispatcherTimer _edgeTimer;

    private ListBoxItem? _pressed;

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
    private List<ListBoxItem> _items = [];
    private Vector[] _origins = [];
    private int _from;
    private int _target;
    private Vector _grabOffset;

    /// <summary>Где была мышь (в рамке ленты), когда ярлык оторвался, — от неё меряется <see cref="EdgeArm"/>.</summary>
    private double _dragStartX;
    private bool _edgeLogged;

    public TabReorder(ListBox list, ScrollViewer scroller, Func<object, bool> canDrag, int fixedCount, Action<object, int> move)
    {
        _list = list;
        _scroller = scroller;
        _canDrag = canDrag;
        _fixedCount = fixedCount;
        _move = move;

        _edgeTimer = new DispatcherTimer(DispatcherPriority.Input, list.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _edgeTimer.Tick += (_, _) => OnEdgeTick();

        list.PreviewMouseLeftButtonDown += OnMouseDown;
        list.PreviewMouseMove += OnMouseMove;
        list.PreviewMouseLeftButtonUp += OnMouseUp;
        list.LostMouseCapture += (_, _) => Cancel();
        list.PreviewKeyDown += OnKeyDown;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = null;

        if (_dragging || _settling || e.ClickCount > 1)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;

        // крестик и карандаш — не перетаскивание
        if (FindAncestor<ButtonBase>(source) is not null)
        {
            return;
        }

        if (FindAncestor<ListBoxItem>(source) is { } item && _canDrag(item.DataContext))
        {
            _pressed = item;
            _pressPoint = e.GetPosition(_scroller);
            _pressInItem = (Vector)e.GetPosition(item);
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _pressed = null;
            return;
        }

        // мышь у списка захвачена: без этого ListBox сам выделял бы ярлыки, над которыми едем
        if (_dragging)
        {
            e.Handled = true;
            Follow(e.GetPosition(_panel));
            return;
        }

        if (_pressed is null)
        {
            return;
        }

        var delta = e.GetPosition(_scroller) - _pressPoint;

        if (Math.Abs(delta.X) < Math.Max(DragStart, SystemParameters.MinimumHorizontalDragDistance)
            && Math.Abs(delta.Y) < Math.Max(DragStart, SystemParameters.MinimumVerticalDragDistance))
        {
            return;
        }

        AppLog.Debug(
            $"Ярлык «{Label(_pressed.DataContext)}» подхвачен: мышь сдвинулась на {delta.X:0},{delta.Y:0} от нажатия, "
            + $"лента сдвинута на {_scroller.HorizontalOffset:0} из {_scroller.ScrollableWidth:0}.");

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

    /// <summary>Ярлык оторвался от ленты: запоминаем, где кто стоял, и поднимаем его над соседями.</summary>
    private bool Begin(ListBoxItem item)
    {
        var count = _list.Items.Count;
        var items = new List<ListBoxItem>(count);

        for (var i = 0; i < count; i++)
        {
            if (_list.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container)
            {
                return false;
            }

            items.Add(container);
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

        // место ярлыка без сдвигов анимации: смещение в ленте, а не на экране
        _origins = items.Select(i => VisualTreeHelper.GetOffset(i)).ToArray();

        _grabOffset = _pressInItem;

        foreach (var container in items)
        {
            Place(container, new Vector());
        }

        Panel.SetZIndex(item, 100);
        item.Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.35, Direction = 270 };

        _dragging = true;
        _dragStartX = Mouse.GetPosition(_scroller).X;
        _edgeLogged = false;
        _list.CaptureMouse();
        _edgeTimer.Start();
        return true;
    }

    /// <summary>Ярлык за мышью; встаёт туда, к чьей середине ближе, — остальные уступают место.</summary>
    private void Follow(Point inPanel)
    {
        var dragged = _items[_from];
        var size = new Vector(dragged.ActualWidth, dragged.ActualHeight);

        // ездит только в пределах мест, куда его можно поставить
        var movable = _origins.Skip(_fixedCount).ToArray();
        var pos = (Vector)inPanel - _grabOffset;
        pos.X = Math.Clamp(pos.X, movable.Min(o => o.X), movable.Max(o => o.X));
        pos.Y = Math.Clamp(pos.Y, movable.Min(o => o.Y), movable.Max(o => o.Y));

        Place(dragged, pos - _origins[_from]);

        var center = pos + size / 2;
        var target = _from;
        var best = double.MaxValue;

        for (var i = _fixedCount; i < _items.Count; i++)
        {
            var distance = (_origins[i] + size / 2 - center).LengthSquared;

            if (distance < best)
            {
                best = distance;
                target = i;
            }
        }

        if (target == _target)
        {
            return;
        }

        _target = target;

        for (var i = 0; i < _items.Count; i++)
        {
            if (i != _from)
            {
                Animate(_items[i], _origins[NewIndex(i)] - _origins[i], Shift);
            }
        }
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

    /// <summary>Отпустили: ярлык доезжает до своего места, и только тогда список переставляется.</summary>
    private void Drop()
    {
        var dragged = _items[_from];
        var item = dragged.DataContext;
        var from = _from;
        var target = _target;

        AppLog.Debug(target == from
            ? $"Ярлык «{Label(item)}» отпущен на своём месте ({from + 1})."
            : $"Ярлык «{Label(item)}» отпущен: место {from + 1} → {target + 1}.");

        Stop();
        _settling = true;

        // ярлыки одного размера — встаёт ровно на место того, кого сменил
        Animate(dragged, _origins[target] - _origins[from], Settle, () =>
        {
            // перестановка и сброс сдвигов — в одном кадре: ярлыки не мигают
            if (target != from)
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

        AppLog.Debug($"Перетаскивание ярлыка «{Label(dragged.DataContext)}» отменено.");

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
        _edgeTimer.Stop();

        if (_list.IsMouseCaptured)
        {
            _list.ReleaseMouseCapture();
        }
    }

    private void Reset()
    {
        foreach (var container in _items)
        {
            Place(container, new Vector());

            container.ClearValue(Panel.ZIndexProperty);
            container.ClearValue(UIElement.EffectProperty);
        }

        _items = [];
        _panel = null;
        _settling = false;
    }

    /// <summary>У края ленты она подкручивается, пока мышь там держат, — и ярлык едет вместе с ней.</summary>
    private void OnEdgeTick()
    {
        if (!_dragging || _panel is null)
        {
            return;
        }

        var x = Mouse.GetPosition(_scroller).X;
        var step = 0.0;

        // лента едет, только если мышь повели к этому краю, а не подхватили ярлык уже у края
        if (x < EdgeZone && _dragStartX - x >= EdgeArm)
        {
            step = -Math.Ceiling((EdgeZone - x) / 3);
        }
        else if (x > _scroller.ViewportWidth - EdgeZone && x - _dragStartX >= EdgeArm)
        {
            step = Math.Ceiling((x - (_scroller.ViewportWidth - EdgeZone)) / 3);
        }

        if (step == 0)
        {
            return;
        }

        var offset = Math.Clamp(_scroller.HorizontalOffset + step, 0, _scroller.ScrollableWidth);

        if (offset == _scroller.HorizontalOffset)
        {
            return;
        }

        if (!_edgeLogged)
        {
            _edgeLogged = true;
            AppLog.Debug($"Лента поехала у {(step < 0 ? "левого" : "правого")} края: мышь в {x:0} из {_scroller.ViewportWidth:0}, "
                + $"подхвачен в {_dragStartX:0}.");
        }

        _scroller.ScrollToHorizontalOffset(offset);
        _scroller.UpdateLayout();
        Follow(Mouse.GetPosition(_panel));
    }

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

    /// <summary>Свой сдвиг у каждого ярлыка: общий замороженный из стиля не годится.</summary>
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

    /// <summary>Название ярлыка для журнала.</summary>
    private static string Label(object? item) => item switch
    {
        Workspace workspace => workspace.Title,
        ServerSet set => set.Title,
        _ => item?.ToString() ?? "?",
    };

    private static T? FindAncestor<T>(DependencyObject? node)
        where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
            {
                return match;
            }

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return null;
    }
}
