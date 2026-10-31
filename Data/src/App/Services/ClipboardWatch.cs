using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MultiServerSync.Services;

/// <summary>
/// Есть ли текст в буфере обмена — для значка «Вставить»: Windows сама сообщает о каждой смене буфера.
/// Слушатель один на окно, сколько бы значков на него ни смотрело.
/// </summary>
public static class ClipboardWatch
{
    private const int WmClipboardUpdate = 0x031D;

    private static bool _listening;

    public static event Action? Changed;

    /// <summary>Значок тусклый, пока текста нет. Буфер занят другой программой — не гасить значок зря.</summary>
    public static bool HasText
    {
        get
        {
            try
            {
                return Clipboard.ContainsText();
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    /// <summary>Начать слушать через окно этого элемента; элемент уже должен быть показан.</summary>
    public static void Attach(Visual visual)
    {
        if (_listening || PresentationSource.FromVisual(visual) is not HwndSource source)
        {
            return;
        }

        _listening = true;
        AddClipboardFormatListener(source.Handle);
        source.AddHook(OnWindowMessage);
    }

    private static IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate)
        {
            Changed?.Invoke();
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);
}
