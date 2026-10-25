using System.Collections.ObjectModel;

namespace MultiServerSync.Models;

/// <summary>Запись списка копирования, пережившая перезапуск программы.</summary>
public sealed class SavedTransferItem
{
    public string Path { get; set; } = string.Empty;

    public bool IsSelected { get; set; } = true;
}

public sealed class AppSettings
{
    /// <summary>Light | Dark | System</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>Сколько серверов обрабатывать одновременно.</summary>
    public int MaxParallel { get; set; } = 8;

    public int ConnectTimeoutSeconds { get; set; } = 20;

    public int CommandTimeoutSeconds { get; set; } = 600;

    // ── параметры копирования: умолчания для вновь создаваемых вкладок ──

    /// <summary>Перезаписывать существующие файлы при копировании.</summary>
    public bool OverwriteFiles { get; set; } = true;

    /// <summary>Переносить на сервер дату изменения локальных файлов и папок.</summary>
    public bool PreserveTimestamps { get; set; } = true;

    /// <summary>Сверять SHA-256 после копирования (на сервере запускается sha256sum).</summary>
    public bool VerifyChecksums { get; set; }

    /// <summary>
    /// Устаревшее: список копирования переехал в UserData\workspaces.json, у каждой вкладки свой.
    /// Поле осталось, чтобы перенести список из старой версии при первом запуске.
    /// </summary>
    public List<SavedTransferItem> CopyList { get; set; } = new();

    public string LastRemotePath { get; set; } = "/";

    /// <summary>
    /// История наблюдаемая: выпадающие списки привязаны к ней напрямую,
    /// поэтому удаление одной строки крестиком сразу видно в интерфейсе.
    /// </summary>
    public ObservableCollection<string> RemotePathHistory { get; set; } = new();

    public ObservableCollection<string> CommandHistory { get; set; } = new();

    /// <summary>Избранные команды: команда плюс название, что она делает.</summary>
    public ObservableCollection<CommandSnippet> CommandBook { get; set; } = new();

    /// <summary>Id сервера, который открывается в обзоре папок по умолчанию.</summary>
    public string BrowseServerId { get; set; } = string.Empty;

    public double WindowWidth { get; set; } = 1280;

    public double WindowHeight { get; set; } = 820;

    /// <summary>Размер окна доски вкладок: кирпичи в нём раскладываются по его ширине.</summary>
    public double BoardWidth { get; set; } = 760;

    public double BoardHeight { get; set; } = 560;

    /// <summary>
    /// Краткие ярлыки вкладок: только название и статус, без счётчиков серверов и отметок,
    /// маленькими кирпичиками в два ряда. У каждого раздела свой выбор.
    /// </summary>
    public bool CompactTabsServers { get; set; }

    public bool CompactTabsCommand { get; set; }

    public bool CompactTabsFiles { get; set; }

    /// <summary>
    /// Галочки «IP» и «User» в 2.0.8 — по разделу. Теперь они свои у каждой вкладки
    /// (<see cref="WorkspaceState.PaneShowIp"/>), отсюда берутся один раз при переносе.
    /// </summary>
    public bool PaneShowIpCommand { get; set; }

    public bool PaneShowIpFiles { get; set; }

    public bool PaneShowUserCommand { get; set; }

    public bool PaneShowUserFiles { get; set; }

    /// <summary>
    /// Высота нижней панели. При окне по умолчанию (820) панель повыше срезала
    /// кнопки запуска на странице копирования, поэтому здесь ровно столько,
    /// чтобы страница помещалась целиком.
    /// </summary>
    public double LogPanelHeight { get; set; } = 220;

    public bool LogPanelVisible { get; set; } = true;

    /// <summary>
    /// Сохранять лог вывода: следующий прогон дописывается к прежнему выводу сервера
    /// через отбивку. Снято — вывод каждый раз начинается с чистого.
    /// </summary>
    public bool KeepOutputLog { get; set; }

    /// <summary>Команда закончилась — вывод всех серверов сам уходит в буфер обмена.</summary>
    public bool AutoCopyAll { get; set; }

    /// <summary>Команда закончилась — вывод сервера, выбранного в терминале, сам уходит в буфер.</summary>
    public bool AutoCopyCurrent { get; set; }

    /// <summary>
    /// Защита от двойного запуска: ту же команду сразу после окончания прошлой — только
    /// повторным нажатием. Выключается в «Настройках».
    /// </summary>
    public bool RepeatGuard { get; set; } = true;

    /// <summary>
    /// Умный режим: перед запуском спросить, если сервер непривычный для этой команды
    /// или вкладки копирования. Выключен, пока не включат переключателем в панели слева.
    /// </summary>
    public bool SmartMode { get; set; }

    /// <summary>
    /// Чем зашифрованы пароли серверов: Windows — учётной записью (по умолчанию, без вопросов),
    /// Master — мастер-паролем, который спрашивается при запуске.
    /// </summary>
    public string PasswordProtection { get; set; } = "Windows";

    /// <summary>Соль для ключа из мастер-пароля, base64.</summary>
    public string MasterSalt { get; set; } = string.Empty;

    /// <summary>Проверочная строка, зашифрованная ключом: по ней узнаём, что пароль верный.</summary>
    public string MasterCheck { get; set; } = string.Empty;

    /// <summary>Копирование закончилось — вывод всех серверов сам уходит в буфер обмена.</summary>
    public bool AutoCopyAllAfterCopy { get; set; }

    /// <summary>Копирование закончилось — вывод сервера, выбранного в терминале, сам уходит в буфер.</summary>
    public bool AutoCopyCurrentAfterCopy { get; set; }

    /// <summary>
    /// Сортировка таблиц: имя таблицы → «свойство|asc|desc». Порядок, выбранный щелчком
    /// по заголовку, переживает смену вкладки и закрытие программы.
    /// </summary>
    public Dictionary<string, string> GridSort { get; set; } = new();

    /// <summary>Размер отдельного окна вывода — оно открывается таким же, каким его закрыли.</summary>
    public double OutputWidth { get; set; } = 900;

    public double OutputHeight { get; set; } = 620;

    public void PushHistory(ObservableCollection<string> list, string value, int limit = 25)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (string.Equals(list[i], value, StringComparison.Ordinal))
            {
                list.RemoveAt(i);
            }
        }

        list.Insert(0, value);

        while (list.Count > limit)
        {
            list.RemoveAt(list.Count - 1);
        }
    }
}
