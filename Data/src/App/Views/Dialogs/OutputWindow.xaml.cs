using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MultiServerSync.Models;
using MultiServerSync.Services;
using Wpf.Ui.Controls;

namespace MultiServerSync.Views.Dialogs;

/// <summary>
/// Вывод сервера в отдельном окне. Панель в главном окне остаётся на месте, но пока
/// это окно открыто, вывод показывается здесь: на втором мониторе его видно целиком,
/// не отбирая место у страницы. Какой сервер показывать, решает выбор в таблице
/// главного окна — окно следом за ним переключается само.
/// </summary>
public partial class OutputWindow : FluentWindow
{
    /// <summary>Окно одно: повторное нажатие кнопки его закрывает, а не открывает второе.</summary>
    private static OutputWindow? _open;

    private OutputWindow()
    {
        InitializeComponent();

        var settings = AppState.Current.Settings;

        // размер прошлого раза, но в пределах экрана: окно переживает смену монитора
        Width = Clamp(settings.OutputWidth, MinWidth, SystemParameters.WorkArea.Width);
        Height = Clamp(settings.OutputHeight, MinHeight, SystemParameters.WorkArea.Height);

        InputBindings.Add(new KeyBinding(new Core.RelayCommand(_ => Close()), Key.Escape, ModifierKeys.None));

        // «Всем серверам» — по прогону, который показан в строке состояния
        Answer.BatchSource = () => StatusHost.DataContext as OperationBatch;

        Closing += OnClosing;
    }

    /// <summary>Вывод открыт в отдельном окне — панель в главном окне пока пустая.</summary>
    public static bool IsOpen => _open is not null;

    /// <summary>Окно открылось или закрылось: панель вывода перерисовывает себя.</summary>
    public static event Action? OpenChanged;

    /// <summary>Открывает окно вывода или поднимает уже открытое.</summary>
    public static void Open(Window owner, OperationBatch? batch, OperationResult? result)
    {
        if (_open is not null)
        {
            if (_open.WindowState == WindowState.Minimized)
            {
                _open.WindowState = WindowState.Normal;
            }

            _open.ShowBatch(batch);
            _open.ShowResult(result);
            _open.Activate();
            return;
        }

        var window = new OutputWindow { Owner = owner };
        _open = window;

        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_open, window))
            {
                _open = null;
            }

            OpenChanged?.Invoke();
            AppLog.Hint("Окно вывода закрыто — вывод вернулся в панель главного окна.");
        };

        window.ShowBatch(batch);
        window.ShowResult(result);
        window.Show();

        OpenChanged?.Invoke();
        AppLog.Hint("Вывод открыт в отдельном окне.");
    }

    /// <summary>Закрывает окно вывода, если оно открыто.</summary>
    public static void CloseIfOpen() => _open?.Close();

    /// <summary>Показать вывод этого сервера — за выбором в таблице главного окна.</summary>
    public static void Track(OperationResult? result) => _open?.ShowResult(result);

    /// <summary>Прогон, состояние которого пишется в строку внизу окна.</summary>
    public static void TrackBatch(OperationBatch? batch) => _open?.ShowBatch(batch);

    private void ShowResult(OperationResult? result)
    {
        if (ReferenceEquals(DataContext, result))
        {
            return;
        }

        DataContext = result;
        OutputBox.ScrollToEnd();
    }

    /// <summary>Итог прогона живёт своей привязкой: заголовок и вывод показывают один сервер.</summary>
    private void ShowBatch(OperationBatch? batch) => StatusHost.DataContext = batch;

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (DataContext is not OperationResult result)
        {
            AppLog.Hint("Сначала выберите сервер в таблице главного окна.", LogLevel.Warning);
            return;
        }

        var text = result.Output.TrimEnd();

        if (text.Length == 0)
        {
            AppLog.Hint($"У сервера {result.Title} вывода пока нет.", LogLevel.Warning);
            return;
        }

        if (result.TimeLine is { } time)
        {
            text += Environment.NewLine + time;
        }

        // имя и адрес сервера сверху — иначе в буфере не понять, к чему лог
        text = result.CopyHeader((StatusHost.DataContext as OperationBatch)?.IsSummary == true) + Environment.NewLine + text;

        if (ClipboardText.CopyTerminalOutput(text + Environment.NewLine))
        {
            AppLog.Hint($"Вывод скопирован: {result.Title}.");
        }
    }

    /// <summary>Корзина: убирает накопленный вывод показанного сервера.</summary>
    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (DataContext is not OperationResult result)
        {
            AppLog.Hint("Сначала выберите сервер в таблице главного окна.", LogLevel.Warning);
            return;
        }

        if (result.Output.Length == 0)
        {
            AppLog.Hint($"У сервера {result.Title} вывода и так нет.");
            return;
        }

        result.ClearOutput();
        AppLog.Hint($"Вывод очищен: {result.Title}.");
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var settings = AppState.Current.Settings;

        // у развёрнутого окна Width — размер во весь экран: запоминаем тот, к которому оно вернётся
        var size = WindowState == WindowState.Normal
            ? new Size(Width, Height)
            : new Size(RestoreBounds.Width, RestoreBounds.Height);

        settings.OutputWidth = Clamp(size.Width, MinWidth, SystemParameters.WorkArea.Width);
        settings.OutputHeight = Clamp(size.Height, MinHeight, SystemParameters.WorkArea.Height);

        AppState.Current.SaveSettings();
    }

    private static double Clamp(double value, double min, double max) =>
        double.IsNaN(value) || value < min ? min : value > max ? max : value;
}
