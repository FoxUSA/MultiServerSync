using System.Collections.ObjectModel;

namespace MultiServerSync.Models;

/// <summary>Запись списка копирования, пережившая перезапуск программы.</summary>
public sealed class SavedTransferItem
{
    public string Path { get; set; } = string.Empty;

    public bool IsSelected { get; set; } = true;
}

/// <summary>
/// Шестерёнка у «Терминал / Журнал»: какие разовые действия раздела писать в терминал и в журнал.
/// Сервер — действия на сервере, Компьютер — на этом ПК, Программа — списки, вкладки, наборы.
/// Ошибки и предупреждения пишутся всегда, запуски «Выполнить» это не трогает.
/// </summary>
public sealed class ActionOutputPrefs
{
    public bool ServerTerminal { get; set; } = true;

    public bool ServerJournal { get; set; } = true;

    public bool ComputerTerminal { get; set; } = true;

    public bool ComputerJournal { get; set; } = true;

    public bool ProgramTerminal { get; set; } = true;

    public bool ProgramJournal { get; set; } = true;
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

    /// <summary>
    /// Сверять SHA-256 после копирования (на сервере запускается sha256sum). С чистой установки
    /// включено: вместе с выбором «по контрольной сумме» это умолчание всех трёх разделов.
    /// </summary>
    public bool VerifyChecksums { get; set; } = true;

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

    /// <summary>Закладки папок проводника на компьютере — общие для всех вкладок.</summary>
    public List<string> ExplorerLocalBookmarks { get; set; } = new();

    /// <summary>Закладки папок на сервере — свои у каждого сервера (ключ — Id сервера).</summary>
    public Dictionary<string, List<string>> ExplorerRemoteBookmarks { get; set; } = new();

    /// <summary>Последние папки на компьютере.</summary>
    public List<string> ExplorerLocalRecent { get; set; } = new();

    /// <summary>Последние папки на сервере — у каждого сервера свои.</summary>
    public Dictionary<string, List<string>> ExplorerRemoteRecent { get; set; } = new();

    /// <summary>
    /// Галочки «IP» и «User» в 2.0.8 — по разделу. Теперь они свои у каждой вкладки
    /// (<see cref="WorkspaceState.PaneShowIp"/>), отсюда берутся один раз при переносе.
    /// </summary>
    public bool PaneShowIpCommand { get; set; }

    public bool PaneShowIpFiles { get; set; }

    public bool PaneShowUserCommand { get; set; }

    public bool PaneShowUserFiles { get; set; }

    /// <summary>
    /// Высота нижней панели — одна на все разделы. При окне по умолчанию (820) панель повыше срезала
    /// кнопки запуска на странице копирования, поэтому здесь ровно столько,
    /// чтобы страница помещалась целиком.
    /// </summary>
    public double LogPanelHeight { get; set; } = 220;

    public bool LogPanelVisible { get; set; } = true;

    /// <summary>
    /// Прежняя общая галочка «Сохранять лог». С 27.10.2026 галочка у каждой вкладки своя;
    /// отсюда её берут только вкладки, сохранённые до этого, — чтобы включённое не пропало.
    /// </summary>
    public bool KeepOutputLog { get; set; }

    /// <summary>
    /// Срок жизни вывода в терминале, секунд (0 — 99 минут): столько вывод живёт после окончания
    /// последнего запуска, на нуле терминал вкладки очищается. Запуск раньше — дописывается ниже.
    /// </summary>
    public int OutputLifeSeconds { get; set; } = 300;

    /// <summary>Размер шрифта вывода — лупой над выводом или Ctrl + колесо.</summary>
    public double OutputFontSize { get; set; } = 12;

    /// <summary>Команда закончилась — вывод всех серверов сам уходит в буфер обмена.</summary>
    public bool AutoCopyAll { get; set; }

    /// <summary>Команда закончилась — вывод сервера, выбранного в терминале, сам уходит в буфер.</summary>
    public bool AutoCopyCurrent { get; set; }

    /// <summary>
    /// CopyPass до 3.0.9 включительно — одна на всю программу. Теперь только запасное значение
    /// для раздела, у которого своей записи ещё нет.
    /// </summary>
    public bool CopyPass { get; set; }

    /// <summary>
    /// CopyPass у каждого раздела своя (ключ — имя раздела): скопированный программой лог
    /// сам вставляется в первое поле ввода другой программы и уходит Enter.
    /// </summary>
    public Dictionary<string, bool> CopyPassBySection { get; set; } = new();

    /// <summary>
    /// Галочка «*.txt» по разделам: автокопия лога длиннее предела уходит в буфер файлом с полным
    /// логом, а не обрезанным текстом. Работает вместе с «авто», как CopyPass.
    /// </summary>
    public Dictionary<string, bool> LogAsFileBySection { get; set; } = new();

    /// <summary>Свитч «Везде» рядом с «Выполнить» — у каждого раздела свой: «Команда», «Копирование», «Скачивание».</summary>
    public Dictionary<string, bool> EverywhereBySection { get; set; } = new();

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
    /// Общие исключения — маски для всех вкладок «Копирования», «Проводника» и «Скачивания»,
    /// вдобавок к своим у вкладки. С чистой установки пусто.
    /// </summary>
    public string GlobalExcludeMasks { get; set; } = string.Empty;

    /// <summary>Окно исключений в прошлый раз закрыли на вкладке общих — там и откроется.</summary>
    public bool ExcludeDialogOnGlobal { get; set; }

    /// <summary>
    /// Куда писать разовые действия — у каждого раздела своё (ключ — имя раздела). Раздела нет —
    /// всё включено.
    /// </summary>
    public Dictionary<string, ActionOutputPrefs> ActionOutputBySection { get; set; } = new();

    /// <summary>
    /// Чем зашифрованы пароли серверов: Windows — учётной записью (по умолчанию, без вопросов),
    /// Master — мастер-паролем, который спрашивается при запуске.
    /// </summary>
    public string PasswordProtection { get; set; } = "Windows";

    /// <summary>Соль для ключа из мастер-пароля, base64.</summary>
    public string MasterSalt { get; set; } = string.Empty;

    /// <summary>Проверочная строка, зашифрованная ключом: по ней узнаём, что пароль верный.</summary>
    public string MasterCheck { get; set; } = string.Empty;

    /// <summary>
    /// «Авто» у «Все логи» в каждом разделе своя (ключ — имя раздела): операция закончилась —
    /// вывод всех серверов сам уходит в буфер обмена.
    /// </summary>
    public Dictionary<string, bool> AutoCopyAllBySection { get; set; } = new();

    /// <summary>«Авто» у «Текущий лог» в каждом разделе своя: вывод выбранного сервера — в буфер.</summary>
    public Dictionary<string, bool> AutoCopyCurrentBySection { get; set; } = new();

    /// <summary>
    /// До 3.0.9 включительно «авто» были парой на «Копирование», «Скачивание» и «Проводник» —
    /// теперь только запасное значение для раздела без своей записи. Как и <see cref="AutoCopyAll"/>
    /// с <see cref="AutoCopyCurrent"/> — для остальных разделов.
    /// </summary>
    public bool AutoCopyAllAfterCopy { get; set; }

    /// <summary>Копирование закончилось — вывод сервера, выбранного в терминале, сам уходит в буфер.</summary>
    public bool AutoCopyCurrentAfterCopy { get; set; }

    /// <summary>
    /// Копирование вывода в разделе «Копирование» берёт и вывод личных команд вкладки:
    /// в буфере один отчёт — сначала копирование, потом команды.
    /// </summary>
    public bool CopyWithChain { get; set; }

    /// <summary>
    /// Сортировка таблиц: имя таблицы → «свойство|asc|desc». Порядок, выбранный щелчком
    /// по заголовку, переживает смену вкладки и закрытие программы.
    /// </summary>
    public Dictionary<string, string> GridSort { get; set; } = new();

    /// <summary>Параметры копирования свёрнуты в одну строку: у «Копирования» и у «Скачивания» — свой выбор.</summary>
    public bool UploadOptionsFolded { get; set; }

    public bool DownloadOptionsFolded { get; set; }

    /// <summary>Порядок кнопок разделов, переставленный мышью: имена кнопок слева направо.</summary>
    public List<string> SectionOrder { get; set; } = new();

    /// <summary>Размер отдельного окна вывода — оно открывается таким же, каким его закрыли.</summary>
    // ── где был человек при выходе: программа открывается там же, будто и не закрывалась ──

    /// <summary>Открытый раздел (имя из <c>AppSection</c>). Пусто — «Серверы».</summary>
    public string LastSection { get; set; } = string.Empty;

    /// <summary>Положение окна; null — посреди экрана, как при первом запуске.</summary>
    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    public bool WindowMaximized { get; set; }

    /// <summary>Внизу был открыт «Журнал», а не «Терминал».</summary>
    public bool BottomOnJournal { get; set; }

    /// <summary>Фильтр журнала по разделу — подпись пункта; пусто — «Все разделы».</summary>
    public string JournalFilter { get; set; } = string.Empty;

    public bool JournalAutoScroll { get; set; } = true;

    /// <summary>Вывод был вынесен в отдельное окно.</summary>
    public bool OutputDetached { get; set; }

    /// <summary>Список запусков был вынесен в отдельное окно.</summary>
    public bool RunsDetached { get; set; }

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
