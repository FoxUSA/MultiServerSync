using System.IO;
using System.Runtime.InteropServices;

namespace MultiServerSync.Services;

/// <summary>
/// Окно «Открыть» — обычный проводник Windows, из которого возвращаются и файлы, и папки
/// одним выбором: мышью, с Ctrl, с Shift, по Ctrl+A.
///
/// Штатные диалоги .NET так не умеют: <c>OpenFileDialog</c> отдаёт только файлы,
/// <c>OpenFolderDialog</c> — только папки, и пользователю пришлось бы заранее решать,
/// что он собрался добавлять. Поэтому берётся тот же самый системный диалог напрямую
/// (COM-интерфейс <c>IFileOpenDialog</c>, из которого сделаны оба штатных), и в него
/// добавляется своя кнопка: она отдаёт выделенное как есть, не заходя в папки.
/// </summary>
public static class LocalPicker
{
    /// <summary>Своя кнопка внизу окна. Номер произвольный, лишь бы не совпал с чужими.</summary>
    private const uint AddButtonId = 1001;

    private const uint AllowMultiselect = 0x00000200;
    private const uint PathMustExist = 0x00000800;
    private const uint ForceFileSystem = 0x00000040;
    private const uint NoValidate = 0x00000100;
    private const uint FileMustExist = 0x00001000;

    /// <summary>SIGDN_NORMALDISPLAY — имя, как его пишет проводник («Disc (D:)» у диска).</summary>
    private const uint DisplayName = 0;

    /// <summary>SIGDN_FILESYSPATH — путь на диске, а не «Этот компьютер» и не имя библиотеки.</summary>
    private const uint FileSystemPath = 0x80058000;

    /// <summary>Windows запоминает по этому ключу, какая папка была открыта в прошлый раз.</summary>
    private static Guid _clientId = new("6f2f1a6e-6c3b-4f6a-9a1e-0d3a5d2f77c1");

    /// <summary>У «Открыть папку» своя память Windows о последней папке.</summary>
    private static Guid _folderClientId = new("b3c1e2a4-5d6f-4a7b-8c9d-0e1f2a3b4c5d");

    /// <summary>
    /// «Открыть папку», но с файлами в списке: штатное окно выбора папки файлов не показывает.
    /// Выбрать можно только папку — ту, что открыта в окне: кнопка «Выбор папки» отдаёт её,
    /// какой бы файл ни был выделен. Вложенная папка открывается двойным щелчком (или той же
    /// кнопкой, если она выделена). Пусто — окно закрыли.
    /// </summary>
    public static string? PickFolder(IntPtr owner, string? startFolder = null)
    {
        IFileOpenDialog? dialog = null;
        var cookie = 0u;

        try
        {
            dialog = (IFileOpenDialog)(object)new FileOpenDialogRcw();

            var events = new FolderEvents(dialog);

            dialog.SetTitle("Открыть папку");

            // имя в поле — не настоящий файл: проверку существования снимаем, иначе «Выбор папки»
            // упрётся в «файл не найден»
            dialog.GetOptions(out var options);
            dialog.SetOptions((options | PathMustExist | ForceFileSystem | NoValidate) & ~FileMustExist);
            dialog.SetClientGuid(ref _folderClientId);

            OpenAt(dialog, startFolder);

            dialog.SetFileNameLabel("Папка:");
            dialog.SetOkButtonLabel("Выбор папки");
            dialog.Advise(events, out cookie);

            return dialog.Show(owner) == 0 ? events.Picked : null;
        }
        catch (Exception ex)
        {
            AppLog.Hint("Не удалось открыть окно выбора папки: " + ex.Message, LogLevel.Error);
            return null;
        }
        finally
        {
            if (dialog is not null)
            {
                if (cookie != 0)
                {
                    try
                    {
                        dialog.Unadvise(cookie);
                    }
                    catch (COMException)
                    {
                        // окно уже закрыто — отписываться не от чего
                    }
                }

                Marshal.ReleaseComObject(dialog);
            }
        }
    }

    /// <summary>
    /// Показывает окно и возвращает выбранное. Пустой список — окно закрыли
    /// или ничего не выбрали.
    /// </summary>
    /// <param name="startFolder">
    /// Где открыться. Пусто или папки уже нет — там, где окно было в прошлый раз.
    /// </param>
    public static IReadOnlyList<string> Pick(IntPtr owner, string? startFolder = null)
    {
        IFileOpenDialog? dialog = null;
        var cookie = 0u;

        try
        {
            // приведение через object: у заглушки нет объявленных интерфейсов,
            // нужный запрашивается у COM-объекта уже во время выполнения
            dialog = (IFileOpenDialog)(object)new FileOpenDialogRcw();

            var events = new Events(dialog);

            dialog.SetTitle("Выберите файлы и папки для отправки");

            // свои флаги добавляются к системным, а не заменяют их: иначе теряются
            // настройки вроде «не менять текущую папку процесса»
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | AllowMultiselect | PathMustExist | ForceFileSystem | NoValidate);
            dialog.SetClientGuid(ref _clientId);

            // своя папка вкладки важнее общей памяти Windows: SetFolder перебивает её
            OpenAt(dialog, startFolder);

            // Кнопки не спорят за один смысл: наша добавляет выделенное, штатная только ходит
            // по папкам. Отобрать у штатной её роль нельзя — при выделенной папке Windows
            // заходит внутрь, и переопределить это окно не даёт. Поэтому она честно
            // называется «Войти в папку», а добавляет всегда одна кнопка.
            dialog.SetOkButtonLabel("Войти в папку");

            if (dialog is IFileDialogCustomize custom)
            {
                custom.AddPushButton(AddButtonId, "Добавить");
                custom.MakeProminent(AddButtonId);
            }

            dialog.Advise(events, out cookie);

            // ненулевой ответ — окно закрыли крестиком или «Отмена», это не ошибка
            if (dialog.Show(owner) != 0)
            {
                return [];
            }

            // своя кнопка уже сложила выделенное сама; штатная «Открыть»
            // (или двойной щелчок по файлу) оставляет результат в самом окне
            return events.Picked.Count > 0 ? events.Picked : Paths(Results(dialog));
        }
        catch (Exception ex)
        {
            AppLog.Hint("Не удалось открыть окно выбора файлов: " + ex.Message, LogLevel.Error);
            return [];
        }
        finally
        {
            if (dialog is not null)
            {
                if (cookie != 0)
                {
                    try
                    {
                        dialog.Unadvise(cookie);
                    }
                    catch (COMException)
                    {
                        // окно уже закрыто — отписываться не от чего
                    }
                }

                Marshal.ReleaseComObject(dialog);
            }
        }
    }

    private static void OpenAt(IFileOpenDialog dialog, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return;
        }

        var iid = typeof(IShellItem).GUID;

        if (SHCreateItemFromParsingName(folder, IntPtr.Zero, ref iid, out var item) != 0 || item == IntPtr.Zero)
        {
            return;
        }

        try
        {
            dialog.SetFolder(item);
        }
        catch (COMException)
        {
            // не открылась — окно возьмёт свою папку по умолчанию
        }
        finally
        {
            Marshal.Release(item);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid riid, out IntPtr item);

    private static IShellItemArray? Results(IFileOpenDialog dialog)
    {
        try
        {
            dialog.GetResults(out var array);
            return array;
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>Разворачивает выбор окна в обычные пути файловой системы.</summary>
    private static List<string> Paths(IShellItemArray? array)
    {
        var list = new List<string>();

        if (array is null)
        {
            return list;
        }

        try
        {
            array.GetCount(out var count);

            for (var i = 0u; i < count; i++)
            {
                array.GetItemAt(i, out var item);

                try
                {
                    item.GetDisplayName(FileSystemPath, out var path);

                    if (!string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
                    {
                        list.Add(path);
                    }
                }
                catch (COMException)
                {
                    // выбрано что-то без пути на диске (сетевое окружение, «Быстрый доступ») — пропускаем
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(array);
        }

        return list;
    }

    /// <summary>
    /// Слушает окно: нажатие своей кнопки забирает выделенное и закрывает окно.
    /// Если ничего не выделено — берётся сама открытая папка, чтобы «зашёл и добавил» тоже работало.
    /// </summary>
    private sealed class Events(IFileOpenDialog dialog) : IFileDialogEvents, IFileDialogControlEvents
    {
        public List<string> Picked { get; } = [];

        public void OnButtonClicked(IntPtr customize, uint id)
        {
            if (id != AddButtonId)
            {
                return;
            }

            Picked.AddRange(Paths(Selection()));

            if (Picked.Count == 0 && OpenFolder() is { } folder)
            {
                Picked.Add(folder);
            }

            if (Picked.Count > 0)
            {
                dialog.Close(0);
            }
        }

        private IShellItemArray? Selection()
        {
            try
            {
                dialog.GetSelectedItems(out var items);
                return items;
            }
            catch (COMException)
            {
                // в списке ничего не выделено
                return null;
            }
        }

        private string? OpenFolder()
        {
            try
            {
                dialog.GetFolder(out var folder);

                try
                {
                    folder.GetDisplayName(FileSystemPath, out var path);
                    return Directory.Exists(path) ? path : null;
                }
                finally
                {
                    Marshal.ReleaseComObject(folder);
                }
            }
            catch (COMException)
            {
                // открыт раздел без пути на диске — добавлять нечего
                return null;
            }
        }

        public int OnFileOk(IntPtr d) => 0;

        public int OnFolderChanging(IntPtr d, IntPtr folder) => 0;

        public void OnFolderChange(IntPtr d)
        {
        }

        public void OnSelectionChange(IntPtr d)
        {
        }

        public void OnShareViolation(IntPtr d, IntPtr item, out int response) => response = 0;

        public void OnTypeChange(IntPtr d)
        {
        }

        public void OnOverwrite(IntPtr d, IntPtr item, out int response) => response = 0;

        public void OnItemSelected(IntPtr customize, uint id, uint item)
        {
        }

        public void OnCheckButtonToggled(IntPtr customize, uint id, bool state)
        {
        }

        public void OnControlActivating(IntPtr customize, uint id)
        {
        }
    }

    /// <summary>
    /// «Открыть папку»: в поле «Папка» всегда имя открытой папки, а «Выбор папки» отдаёт
    /// открытую папку, а не выделенный файл.
    /// </summary>
    private sealed class FolderEvents(IFileOpenDialog dialog) : IFileDialogEvents
    {
        public string? Picked { get; private set; }

        public int OnFileOk(IntPtr d)
        {
            Picked = Folder(FileSystemPath) is { } path && Directory.Exists(path) ? path : null;

            // открыт раздел без пути на диске («Этот компьютер», «Сеть») — выбирать нечего, окно остаётся
            return Picked is null ? 1 : 0;
        }

        public int OnFolderChanging(IntPtr d, IntPtr folder) => 0;

        public void OnFolderChange(IntPtr d)
        {
            if (Folder(DisplayName) is { Length: > 0 } name)
            {
                dialog.SetFileName(name);
            }
        }

        private string? Folder(uint kind)
        {
            try
            {
                dialog.GetFolder(out var folder);

                try
                {
                    folder.GetDisplayName(kind, out var name);
                    return name;
                }
                finally
                {
                    Marshal.ReleaseComObject(folder);
                }
            }
            catch (COMException)
            {
                return null;
            }
        }

        public void OnSelectionChange(IntPtr d)
        {
        }

        public void OnShareViolation(IntPtr d, IntPtr item, out int response) => response = 0;

        public void OnTypeChange(IntPtr d)
        {
        }

        public void OnOverwrite(IntPtr d, IntPtr item, out int response) => response = 0;
    }

    // ── системные интерфейсы ──────────────────────────────────────────────────
    //
    // Порядок методов менять нельзя: вызовы идут по номеру в таблице, а не по имени,
    // поэтому здесь перечислены и те, которые нам не нужны. Ссылки на другие окна
    // приняты как IntPtr: они нам не нужны, а так их не надо ни создавать, ни освобождать.

    [ComImport]
    [Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class FileOpenDialogRcw
    {
    }

    [ComImport]
    [Guid("d57c7288-d4ad-4768-be02-9d969532d960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig]
        int Show(IntPtr parent);

        void SetFileTypes(uint count, IntPtr types);

        void SetFileTypeIndex(uint index);

        void GetFileTypeIndex(out uint index);

        void Advise(IFileDialogEvents events, out uint cookie);

        void Unadvise(uint cookie);

        void SetOptions(uint options);

        void GetOptions(out uint options);

        void SetDefaultFolder(IntPtr folder);

        void SetFolder(IntPtr folder);

        void GetFolder(out IShellItem folder);

        void GetCurrentSelection(out IShellItem item);

        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);

        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);

        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);

        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);

        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);

        void GetResult(out IShellItem item);

        void AddPlace(IntPtr place, int where);

        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);

        void Close(int result);

        void SetClientGuid(ref Guid client);

        void ClearClientData();

        void SetFilter(IntPtr filter);

        /// <summary>Что выбрано после закрытия окна.</summary>
        void GetResults(out IShellItemArray items);

        /// <summary>Что выделено прямо сейчас — включая папки. Ради этого всё и затевалось.</summary>
        void GetSelectedItems(out IShellItemArray items);
    }

    [ComImport]
    [Guid("973510db-7d7f-452b-8975-74a85828d354")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialogEvents
    {
        [PreserveSig]
        int OnFileOk(IntPtr dialog);

        [PreserveSig]
        int OnFolderChanging(IntPtr dialog, IntPtr folder);

        void OnFolderChange(IntPtr dialog);

        void OnSelectionChange(IntPtr dialog);

        void OnShareViolation(IntPtr dialog, IntPtr item, out int response);

        void OnTypeChange(IntPtr dialog);

        void OnOverwrite(IntPtr dialog, IntPtr item, out int response);
    }

    [ComImport]
    [Guid("36116642-D713-4B97-9B83-7484A9D00433")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialogControlEvents
    {
        void OnItemSelected(IntPtr customize, uint control, uint item);

        void OnButtonClicked(IntPtr customize, uint control);

        void OnCheckButtonToggled(IntPtr customize, uint control, bool state);

        void OnControlActivating(IntPtr customize, uint control);
    }

    [ComImport]
    [Guid("e6fdd21a-163f-4975-9c8c-a69f1ba37034")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialogCustomize
    {
        void EnableOpenDropDown(uint control);

        void AddMenu(uint control, [MarshalAs(UnmanagedType.LPWStr)] string label);

        void AddPushButton(uint control, [MarshalAs(UnmanagedType.LPWStr)] string label);

        void AddComboBox(uint control);

        void AddRadioButtonList(uint control);

        void AddCheckButton(uint control, [MarshalAs(UnmanagedType.LPWStr)] string label, bool state);

        void AddEditBox(uint control, [MarshalAs(UnmanagedType.LPWStr)] string text);

        void AddSeparator(uint control);

        void AddText(uint control, [MarshalAs(UnmanagedType.LPWStr)] string text);

        void SetControlLabel(uint control, [MarshalAs(UnmanagedType.LPWStr)] string label);

        void GetControlState(uint control, out uint state);

        void SetControlState(uint control, uint state);

        void GetEditBoxText(uint control, out IntPtr text);

        void SetEditBoxText(uint control, [MarshalAs(UnmanagedType.LPWStr)] string text);

        void GetCheckButtonState(uint control, out bool state);

        void SetCheckButtonState(uint control, bool state);

        void AddControlItem(uint control, uint item, [MarshalAs(UnmanagedType.LPWStr)] string label);

        void RemoveControlItem(uint control, uint item);

        void RemoveAllControlItems(uint control);

        void GetControlItemState(uint control, uint item, out uint state);

        void SetControlItemState(uint control, uint item, uint state);

        void GetSelectedControlItem(uint control, out uint item);

        void SetSelectedControlItem(uint control, uint item);

        void StartVisualGroup(uint control, [MarshalAs(UnmanagedType.LPWStr)] string label);

        void EndVisualGroup();

        void MakeProminent(uint control);

        void SetControlItemText(uint control, uint item, [MarshalAs(UnmanagedType.LPWStr)] string label);
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid riid, out IntPtr result);

        void GetParent(out IShellItem parent);

        void GetDisplayName(uint kind, [MarshalAs(UnmanagedType.LPWStr)] out string name);

        void GetAttributes(uint mask, out uint attributes);

        void Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport]
    [Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid riid, out IntPtr result);

        void GetPropertyStore(uint flags, ref Guid riid, out IntPtr result);

        void GetPropertyDescriptionList(IntPtr key, ref Guid riid, out IntPtr result);

        void GetAttributes(uint option, uint mask, out uint attributes);

        void GetCount(out uint count);

        void GetItemAt(uint index, out IShellItem item);

        void EnumItems(out IntPtr enumerator);
    }
}
