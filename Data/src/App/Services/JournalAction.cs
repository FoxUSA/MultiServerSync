namespace MultiServerSync.Services;

/// <summary>
/// Столбец «Действие» журнала и его фильтр. Считается по началу записи, а не хранится: так подписаны и
/// записи прошлых версий. Новая запись без подходящего начала просто попадёт в «Прочее».
/// </summary>
public static class JournalAction
{
    private const string Other = "Прочее";

    /// <summary>Вставка в поле — только плашка; так подписаны лишь записи прежних версий.</summary>
    private const string Paste = "Вставка";

    /// <summary>Запись прежней версии, которой теперь в логах быть не должно.</summary>
    public static bool IsNoise(string text) =>
        Of(text) == Paste
        || text.StartsWith("Обновлений нет", StringComparison.Ordinal)
        || text.StartsWith("Проверить обновления не удалось", StringComparison.Ordinal);

    /// <summary>По началу записи, сверху вниз: «Добавлено окно команды» — это добавление, а не команда.</summary>
    private static readonly (string Start, string Action)[] ByStart =
    [
        ("отменено:", "Отмена"),
        ("возвращено:", "Отмена"),

        ("создана папка", "Создание папки"),
        ("создан файл", "Создание файла"),
        ("добавлен", "Добавление"),
        ("в список", "Добавление"),
        ("в избранное", "Добавление"),
        ("продублировано", "Добавление"),

        ("вставлено", Paste),
        ("подставлена", Paste),
        ("из избранного раскрыто", Paste),

        ("переименована папка", "Переименование папки"),
        ("переименован файл", "Переименование файла"),
        ("переименовано", "Переименование"),
        ("не переименовано", "Переименование"),
        ("название", "Переименование"),

        ("убран", "Удаление"),
        ("из избранного удалено", "Удаление"),
        ("из истории", "Удаление"),
        ("из списка", "Удаление"),

        ("список", "Очистка"),
        ("очищено", "Очистка"),
        ("поле команды очищено", "Очистка"),

        ("изменён", "Изменение"),
        ("изменено", "Изменение"),
        ("обновлён на сервере", "Изменение"),
        ("правка", "Изменение"),
        ("запись избранного", "Изменение"),
        ("путь", "Изменение"),
        ("исключения", "Изменение"),
        ("свойства", "Изменение"),
        ("не сохранён файл", "Изменение"),

        ("перенесено", "Перенос"),
        ("перенос", "Перенос"),

        ("скопировано", "Копирование"),
        ("копирование", "Копирование"),
        ("скачивание", "Скачивание"),
        ("сравнение", "Сравнение"),
        ("проверка", "Проверка связи"),

        ("выполнение", "Команда"),
        ("команд", "Команда"),
        ("цепочка", "Команда"),
        ("ждёт ответа", "Команда"),
        ("ответ ", "Команда"),
        ("ввод закрыт", "Команда"),
        ("ctrl+c", "Команда"),
        ("снятие", "Команда"),
    ];

    /// <summary>Если начало не подсказало: о чём запись. «Не отмечено ни одного сервера — копировать не с чем».</summary>
    private static readonly (string Part, string Action)[] ByPart =
    [
        ("обновлен", "Обновление"),
        ("закрыт", "Удаление"),
        ("проверять", "Проверка связи"),
        ("сравн", "Сравнение"),
        ("скач", "Скачивание"),
        ("копир", "Копирование"),
        ("команд", "Команда"),
    ];

    public static string Of(string text)
    {
        var line = text.TrimStart().ToLowerInvariant();

        // только суть: дальше идут название вкладки и текст команды, а в них может быть что угодно
        var tab = line.IndexOf(" · вкладка", StringComparison.Ordinal);
        line = tab >= 0 ? line[..tab] : line;

        // «Повтор операции «Копирование»», «Везде: копирование…», «Окно 2: копирование…» — по тому, что идёт дальше
        line = Unwrap(line);

        if (line.StartsWith("удал", StringComparison.Ordinal))
        {
            return Deleted(line);
        }

        foreach (var (start, action) in ByStart)
        {
            if (line.StartsWith(start, StringComparison.Ordinal))
            {
                return action;
            }
        }

        foreach (var (part, action) in ByPart)
        {
            if (line.Contains(part, StringComparison.Ordinal))
            {
                return action;
            }
        }

        return Other;
    }

    private static string Unwrap(string line)
    {
        const string retry = "повтор операции «";

        if (line.StartsWith(retry, StringComparison.Ordinal))
        {
            return line[retry.Length..];
        }

        if (line.Contains("переставлено", StringComparison.Ordinal))
        {
            return "изменено";
        }

        var colon = line.IndexOf(": ", StringComparison.Ordinal);

        return colon > 0 && (line.StartsWith("окно ", StringComparison.Ordinal) || line.StartsWith("везде", StringComparison.Ordinal))
            ? line[(colon + 2)..]
            : line;
    }

    /// <summary>«Удалено: 1 папку в /tmp» — что именно; папки и файлы вместе, серверы и прочее — просто удаление.</summary>
    private static string Deleted(string line)
    {
        var dirs = line.Contains("папк", StringComparison.Ordinal);
        var files = line.Contains("файл", StringComparison.Ordinal);

        return dirs == files ? "Удаление" : dirs ? "Удаление папки" : "Удаление файла";
    }
}
