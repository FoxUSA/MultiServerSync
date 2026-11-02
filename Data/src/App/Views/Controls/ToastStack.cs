using System.Windows;
using System.Windows.Controls;
using MultiServerSync.Services;

namespace MultiServerSync.Views.Controls;

/// <summary>
/// Плашки посередине окна: столбиком, свежая снизу, срок у всех один. Записи, что идут одна за другой,
/// не затирают друг друга. Та же запись подряд новой плашки не даёт, а продлевает прежнюю.
/// Ошибка таймером не гаснет: человек мог отойти на время долгой работы. Закрывает её любой щелчок.
/// Не влезают в окно — уходит самая старая, ошибки в последнюю очередь.
/// </summary>
public sealed class ToastStack : StackPanel
{
    private const double EdgeGap = 8;
    private const double ToastGap = 4;

    private bool _watching;

    public ToastStack()
    {
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Top;
    }

    public void Show(LogEntry entry)
    {
        if (!_watching && Parent is FrameworkElement parent)
        {
            _watching = true;

            // пока плашки видны, окно может поменять размер
            parent.SizeChanged += (_, _) => Fit();
        }

        if (Children.Count > 0
            && Children[^1] is ActionToast { Tag: LogEntry last } newest
            && last.Text == entry.Text
            && last.PlaceText == entry.PlaceText
            && last.Level == entry.Level)
        {
            newest.ShowInPlace(entry);
            return;
        }

        var toast = new ActionToast
        {
            Tag = entry,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, ToastGap),
            Sticky = entry.Level is LogLevel.Error,
        };

        toast.Hidden += () =>
        {
            Children.Remove(toast);
            Fit();
        };

        Children.Add(toast);
        toast.ShowInPlace(entry);
        Fit();
    }

    /// <summary>Погасить все сразу: сменился раздел.</summary>
    public void Clear()
    {
        foreach (var toast in Children.OfType<ActionToast>().ToList())
        {
            toast.HideNow();
        }
    }

    /// <summary>Первая плашка стоит на трети окна сверху, ближе к тому, куда смотрят; остальные идут под ней.</summary>
    private void Fit()
    {
        if (Children.Count == 0 || Parent is not FrameworkElement { ActualHeight: > 0 } parent)
        {
            return;
        }

        var width = parent.ActualWidth;

        double HeightOf(UIElement toast)
        {
            toast.Measure(new Size(width, double.PositiveInfinity));
            return toast.DesiredSize.Height;
        }

        var top = Math.Max(EdgeGap, parent.ActualHeight / 3 - HeightOf(Children[0]) / 2);

        // её уход сам вызовет Fit заново
        if (Children.Count > 1 && top + Children.OfType<UIElement>().Sum(HeightOf) > parent.ActualHeight - EdgeGap)
        {
            // свежая остаётся всегда: её только что написали
            var older = Children.OfType<ActionToast>().SkipLast(1).ToList();
            (older.FirstOrDefault(t => !t.Sticky) ?? older[0]).HideNow();
            return;
        }

        Margin = new Thickness(EdgeGap, top, EdgeGap, 0);
    }
}
