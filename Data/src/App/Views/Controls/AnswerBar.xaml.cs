using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MultiServerSync.Models;
using MultiServerSync.Services;

namespace MultiServerSync.Views.Controls;

/// <summary>
/// Строка ответа команде — под выводом в терминале и в отдельном окне вывода.
/// DataContext — строка результата сервера, чей вывод показан. Ответ уходит как набранный
/// в терминале с Enter; сервер без терминала его не повторяет, поэтому в выводе ответ
/// дописывается к вопросу нами.
/// </summary>
public partial class AnswerBar : UserControl
{
    public AnswerBar()
    {
        InitializeComponent();
    }

    /// <summary>Откуда брать все серверы прогона — для «Всем серверам».</summary>
    public Func<OperationBatch?>? BatchSource { get; set; }

    private void OnSendAnswer(object sender, RoutedEventArgs e) => Send(all: false);

    private void OnSendAnswerAll(object sender, RoutedEventArgs e) => Send(all: true);

    /// <summary>Enter — этому серверу, Ctrl+Enter — всем, Ctrl+D — конец ввода.</summary>
    private void OnAnswerKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Send(all: ctrl);
        }
        else if (ctrl && e.Key == Key.D)
        {
            e.Handled = true;
            CloseInput();
        }
    }

    private void Send(bool all)
    {
        var text = AnswerBox.Text;
        var shown = text.Length == 0 ? "Enter" : text.Length <= 12 ? $"«{text}»" : "строка";

        if (all)
        {
            var targets = BatchSource?.Invoke()?.Results.Where(r => r.Input is { IsOpen: true }).ToList() ?? [];

            if (targets.Count == 0)
            {
                AppLog.Warn("Отвечать некому — команда уже ни на одном сервере не идёт.");
                return;
            }

            var sent = targets.Count(r => r.Input!.Send(text));
            AppLog.Info($"Ответ {shown} отправлен на серверов: {sent}.");
        }
        else
        {
            if (DataContext is not OperationResult { Input: { } input } result)
            {
                AppLog.Warn("Сначала выберите сервер, где идёт команда.");
                return;
            }

            if (!input.Send(text))
            {
                AppLog.Warn($"{result.Server.PaneLabel}: команда уже не принимает ввод.");
                return;
            }

            AppLog.Info($"Ответ {shown} отправлен: {result.Server.PaneLabel}.");
        }

        AnswerBox.Clear();
    }

    /// <summary>Ctrl+D: команде больше ничего не придёт — как конец ввода в терминале.</summary>
    private void CloseInput()
    {
        if (DataContext is OperationResult { Input: { } input } result)
        {
            input.Close();
            AppLog.Info($"Ввод закрыт (Ctrl+D): {result.Server.PaneLabel}.");
        }
    }
}
