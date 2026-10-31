using System.Collections.ObjectModel;
using System.ComponentModel;
using MultiServerSync.Core;
using MultiServerSync.Models;

namespace MultiServerSync.Services;

/// <summary>Параметры копирования окна, пока оно не в фокусе. Окно в фокусе держит их в полях вкладки.</summary>
public sealed record CopyOptions(
    bool Overwrite,
    bool PreserveTimestamps,
    bool VerifyChecksums,
    bool MatchByDate,
    bool MatchByHash,
    string ExcludeMasks,
    bool UseGlobalExcludes);

/// <summary>
/// Окно «Копирования» и «Скачивания»: свой список, свой путь или папка, свои параметры. Панель кнопок
/// и параметры на странице одни и показывают окно в фокусе. «Выполнить» идёт по всем окнам сверху вниз.
/// </summary>
public sealed class CopyWindow : ObservableObject
{
    private readonly Workspace _owner;
    private string _remotePath;
    private string _localFolder = string.Empty;
    private string _name = string.Empty;
    private int _number = 1;
    private bool _isMulti;
    private bool _isLast = true;
    private bool _isFocused;
    private bool _isFailed;

    public CopyWindow(Workspace owner, string remotePath)
    {
        _owner = owner;
        _remotePath = remotePath;

        // предупреждения и замок живут на вкладке, разметка окна смотрит на них отсюда
        owner.PropertyChanged += OnOwnerChanged;
    }

    public ObservableCollection<TransferItem> Items { get; } = new();

    /// <summary>Что скачивать: файлы и папки, каждая строка со своего сервера.</summary>
    public ObservableCollection<DownloadItem> Downloads { get; } = new();

    public CopyOptions? Options { get; set; }

    public string RemotePath
    {
        get => _remotePath;
        set { if (Set(ref _remotePath, value)) { _owner.SaveWindow(); } }
    }

    /// <summary>Из файла: без сохранения.</summary>
    public void RestorePath(string value) => Set(ref _remotePath, value, nameof(RemotePath));

    /// <summary>Куда скачивать. С нескольких серверов — каждый в свою подпапку по имени сервера.</summary>
    public string LocalFolder
    {
        get => _localFolder;
        set { if (Set(ref _localFolder, value ?? string.Empty)) { _owner.SaveWindow(); } }
    }

    /// <summary>Из файла: без сохранения.</summary>
    public void RestoreFolder(string value) => Set(ref _localFolder, value, nameof(LocalFolder));

    public int Number
    {
        get => _number;
        set { if (Set(ref _number, value)) { RaiseMany(nameof(NumberText), nameof(NameText)); } }
    }

    public string NumberText => $"#{Number}";

    /// <summary>Своё название окна; пусто — на плашке «Окно N».</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value ?? string.Empty))
            {
                Raise(nameof(NameText));
                _owner.SaveWindow();
            }
        }
    }

    /// <summary>Из файла: без сохранения.</summary>
    public void RestoreName(string value)
    {
        if (Set(ref _name, value, nameof(Name)))
        {
            Raise(nameof(NameText));
        }
    }

    public string NameText => _name.Length > 0 ? _name : $"Окно {Number}";

    /// <summary>Как окно зовётся в журнале и выводе: номер и своё название, если оно есть.</summary>
    public string LogName => _name.Length > 0 ? $"{NumberText} «{_name}»" : NumberText;

    /// <summary>Плашка названия: кнопка «Название» включена и окон больше одного.</summary>
    public bool ShowsName => IsMulti && _owner.ShowWindowNames;

    public bool IsMulti
    {
        get => _isMulti;
        set { if (Set(ref _isMulti, value)) { Raise(nameof(ShowsName)); } }
    }

    /// <summary>Самое нижнее окно: на нём висят предупреждения всей вкладки.</summary>
    public bool IsLast
    {
        get => _isLast;
        set { if (Set(ref _isLast, value)) { RaiseNotices(); } }
    }

    public bool IsFocused
    {
        get => _isFocused;
        set => Set(ref _isFocused, value);
    }

    /// <summary>Окно встало на суммах: ненадолго красная рамка, гасит её страница, когда окно увидели.</summary>
    public bool IsFailed
    {
        get => _isFailed;
        set
        {
            if (Set(ref _isFailed, value) && value)
            {
                FailureShown?.Invoke();
            }
        }
    }

    /// <summary>Красная рамка появилась: страницам пора начать отсчёт.</summary>
    public static event Action? FailureShown;

    public bool IsPaneLocked => _owner.IsPaneLocked;

    public bool IsNotPaneLocked => !_owner.IsPaneLocked;

    public IReadOnlyList<string> Notices => IsLast ? _owner.Notices : [];

    public bool HasNotices => Notices.Count > 0;

    public bool? AllItemsSelected
    {
        get
        {
            var picked = Items.Count(i => i.IsSelected);

            if (Items.Count == 0 || picked == 0)
            {
                return false;
            }

            return picked == Items.Count ? true : null;
        }
    }

    public void RaiseItems() => Raise(nameof(AllItemsSelected));

    public bool? AllDownloadsSelected
    {
        get
        {
            var picked = Downloads.Count(d => d.IsSelected);

            if (Downloads.Count == 0 || picked == 0)
            {
                return false;
            }

            return picked == Downloads.Count ? true : null;
        }
    }

    public void RaiseDownloads() => Raise(nameof(AllDownloadsSelected));

    /// <summary>Окно убрали — вкладка ему больше не пишет.</summary>
    public void Detach() => _owner.PropertyChanged -= OnOwnerChanged;

    private void RaiseNotices() => RaiseMany(nameof(Notices), nameof(HasNotices));

    private void OnOwnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Workspace.Notices):
                RaiseNotices();
                break;
            case nameof(Workspace.ShowWindowNames):
                Raise(nameof(ShowsName));
                break;
            case nameof(Workspace.IsPaneLocked):
                RaiseMany(nameof(IsPaneLocked), nameof(IsNotPaneLocked));
                break;
        }
    }
}
