using System.Windows;
using MultiServerSync.Services;
using Wpf.Ui.Controls;

namespace MultiServerSync.Views.Controls;

/// <summary>
/// «Остановить» — одна и та же кнопка везде, где идёт операция: под выводом, в отдельном окне
/// вывода. Что именно делает остановка, решает <see cref="OperationBatch.StopAsync"/>;
/// операцию кнопка берёт из своего DataContext.
/// </summary>
public sealed class StopButton : Button
{
    /// <summary>Подсказка — общая и для значков-квадратиков, которые зовут ту же остановку.</summary>
    public const string Hint =
        "Остановить: команде на сервере — Ctrl+C, не отпустила за 3 с — снять принудительно; "
        + "копирование и скачивание обрываются сразу. Повторное нажатие — без ожидания";

    public StopButton()
    {
        // наследник ui:Button сам тему не подхватит — берём её стиль явно
        SetResourceReference(StyleProperty, typeof(Button));

        Content = "Остановить";
        Appearance = ControlAppearance.Danger;
        Icon = new SymbolIcon(SymbolRegular.Stop24);
        Padding = new Thickness(8, 2, 8, 2);
        FontSize = 11;
        ToolTip = Hint;

        Click += async (_, _) =>
        {
            if (DataContext is OperationBatch batch)
            {
                await batch.StopAsync();
            }
        };
    }
}
