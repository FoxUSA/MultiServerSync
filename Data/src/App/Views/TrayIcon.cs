using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using MultiServerSync.Core;
using Wpf.Ui.Controls;
using Forms = System.Windows.Forms;
using MenuItem = Wpf.Ui.Controls.MenuItem;

namespace MultiServerSync.Views;

/// <summary>
/// Значок программы в трее, справа внизу. Крестик окна прячет его сюда, а не закрывает
/// программу: щелчок по значку — окно обратно, правая кнопка — меню «Открыть» / «Выход».
///
/// Сам значок — из WinForms (в WPF его нет), а меню — обычное WPF-меню в оформлении
/// программы: системное меню WinForms выглядело бы чужим.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly ContextMenu _menu;

    public TrayIcon(Action show, Action exit)
    {
        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = AppVersion.ProductName,
            Visible = true,
        };

        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                show();
            }
            else if (e.Button == Forms.MouseButtons.Right)
            {
                OpenMenu();
            }
        };

        _menu = new ContextMenu();
        _menu.Items.Add(Item("Открыть", SymbolRegular.WindowNew24, show));
        _menu.Items.Add(new Separator());
        _menu.Items.Add(Item("Выход", SymbolRegular.Power24, exit));
    }

    private static MenuItem Item(string header, SymbolRegular symbol, Action click)
    {
        var item = new MenuItem { Header = header, Icon = new SymbolIcon { Symbol = symbol } };
        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>
    /// Меню у курсора. Без активации своего окна оно не закрылось бы щелчком мимо:
    /// окно программы спрятано, и закрывать меню некому.
    /// </summary>
    private void OpenMenu()
    {
        _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        _menu.IsOpen = true;

        if (PresentationSource.FromVisual(_menu) is HwndSource source)
        {
            SetForegroundWindow(source.Handle);
        }
    }

    private static System.Drawing.Icon LoadIcon()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));

        using var stream = resource!.Stream;
        return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        // иначе значок висит в трее, пока над ним не проведут мышью
        _icon.Visible = false;
        _icon.Icon?.Dispose();
        _icon.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
