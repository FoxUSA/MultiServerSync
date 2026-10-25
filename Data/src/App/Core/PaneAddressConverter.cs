using System.Globalization;
using System.Windows.Data;

namespace MultiServerSync.Core;

/// <summary>
/// Строка под сервером в панели «Целевые сервера»: адрес, пользователь или оба сразу
/// в привычном виде <c>user@host</c> — смотря какие галочки «IP» и «User» стоят под списком.
/// Значения по порядку: пользователь, адрес, показывать пользователя, показывать адрес.
/// Пустая строка — показывать нечего, и строка под сервером прячется.
/// </summary>
public sealed class PaneAddressConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var user = values.ElementAtOrDefault(0) as string ?? string.Empty;
        var host = values.ElementAtOrDefault(1) as string ?? string.Empty;
        var showUser = values.ElementAtOrDefault(2) is true && user.Length > 0;
        var showHost = values.ElementAtOrDefault(3) is true && host.Length > 0;

        return (showUser, showHost) switch
        {
            (true, true) => $"{user}@{host}",
            (true, false) => user,
            (false, true) => host,
            _ => string.Empty,
        };
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
