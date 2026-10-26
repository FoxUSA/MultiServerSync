using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using MultiServerSync.Models;
using MultiServerSync.Services;
using Wpf.Ui.Controls;

namespace MultiServerSync.Views.Dialogs;

/// <summary>
/// «Свойства» файла или папки — на компьютере или на сервере. Права и владельца на сервере
/// меняет только это окно и только то, что в нём тронули: остальное остаётся как было.
/// </summary>
public partial class PropertiesDialog : FluentWindow
{
    private readonly IFileSource _source;
    private readonly IReadOnlyList<FsEntry> _items;
    private readonly CheckBox[] _bits;
    private IReadOnlyList<RemoteStat> _stats = [];
    private string _owner = string.Empty;
    private string _group = string.Empty;
    private bool _syncing;
    private CancellationTokenSource? _work;

    private PropertiesDialog(IFileSource source, IReadOnlyList<FsEntry> items)
    {
        InitializeComponent();

        _source = source;
        _items = items;
        _bits = [Ur, Uw, Ux, Gr, Gw, Gx, Or, Ow, Ox];

        Closed += (_, _) => _work?.Cancel();
        Loaded += async (_, _) => await FillAsync();
    }

    /// <summary>Что-то поменяли на сервере — панель перечитает папку.</summary>
    public bool Changed { get; private set; }

    public static bool Show(Window? owner, IFileSource source, IReadOnlyList<FsEntry> items)
    {
        var dialog = new PropertiesDialog(source, items);

        if (owner is { IsLoaded: true })
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();
        return dialog.Changed;
    }

    private async Task FillAsync()
    {
        var single = _items.Count == 1 ? _items[0] : null;
        var dirs = _items.Count(i => i.IsDirectory);

        Title = single is null ? $"Свойства: {_items.Count} объектов" : $"Свойства: {single.Name}";
        HeadName.Text = single?.Name ?? $"{_items.Count} объектов: папок {dirs}, файлов {_items.Count - dirs}";
        HeadIcon.Source = single is null
            ? ShellIcons.Folder
            : _source.IsRemote
                ? ShellIcons.ForName(single.Name, single.IsDirectory, single.IsExecutable)
                : ShellIcons.ForLocal(single.FullPath, single.IsDirectory, single.Kind == FsKind.Drive);

        TypeText.Text = single is null
            ? "разные"
            : single.Type ?? (single.IsLink ? "Ссылка" : ShellIcons.TypeName(single.Name, single.IsDirectory, single.IsExecutable));
        WhereText.Text = _source.Parent(_items[0].FullPath) is { } parent ? _source.Display(parent) : _source.Display(_items[0].FullPath);

        var files = _items.Where(i => !i.IsDirectory).ToList();
        SizeText.Text = dirs == 0 ? TransferItem.Format(files.Sum(f => f.Length)) : "—";
        MeasureButton.Visibility = dirs > 0 ? Visibility.Visible : Visibility.Collapsed;
        ModifiedText.Text = single is { Modified.Year: > 1970 } ? single.Modified.ToString("dd.MM.yyyy HH:mm:ss") : string.Empty;

        if (single is { IsLink: true, LinkTarget.Length: > 0 })
        {
            LinkLabel.Visibility = LinkText.Visibility = Visibility.Visible;
            LinkText.Text = single.LinkTarget;
        }

        HashRow.Visibility = single is { IsDirectory: false } ? Visibility.Visible : Visibility.Collapsed;
        WindowsButton.Visibility = !_source.IsRemote && single is not null ? Visibility.Visible : Visibility.Collapsed;

        if (_source is RemoteSource remote)
        {
            await FillRightsAsync(remote);
        }
    }

    // ── права и владелец на сервере ──────────────────────────────────────────

    private async Task FillRightsAsync(RemoteSource remote)
    {
        RightsPanel.Visibility = Visibility.Visible;
        ApplyButton.Visibility = Visibility.Visible;
        RecursiveBox.Visibility = _items.Any(i => i.IsDirectory && !i.IsLink) ? Visibility.Visible : Visibility.Collapsed;
        ResultText.Text = "чтение прав…";

        try
        {
            _stats = await remote.StatAsync(_items.Select(i => i.FullPath).ToList());
            var accounts = await remote.AccountsAsync();

            OwnerBox.ItemsSource = accounts.Users;
            GroupBox.ItemsSource = accounts.Groups;

            // не root — владельца не сменить; группу можно, если человек в ней состоит, — пусть пробует
            OwnerBox.IsEnabled = accounts.IsRoot;
            OwnerHint.Visibility = accounts.IsRoot ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex)
        {
            ResultText.Text = "права не прочитались: " + ex.Message;
            ApplyButton.IsEnabled = false;
            return;
        }

        ResultText.Text = string.Empty;

        if (_stats.Count == 0)
        {
            ResultText.Text = "права не прочитались";
            ApplyButton.IsEnabled = false;
            return;
        }

        // одинаковое у всех — показываем, разное — пусто и не трогается
        _owner = _stats.Select(s => s.Owner).Distinct().Count() == 1 ? _stats[0].Owner : string.Empty;
        _group = _stats.Select(s => s.Group).Distinct().Count() == 1 ? _stats[0].Group : string.Empty;
        OwnerBox.Text = _owner;
        GroupBox.Text = _group;

        _syncing = true;

        for (var i = 0; i < 9; i++)
        {
            var bit = 1 << (8 - i);
            var set = _stats.Count(s => (s.Mode & bit) != 0);
            _bits[i].IsThreeState = set != 0 && set != _stats.Count;
            _bits[i].IsChecked = set == 0 ? false : set == _stats.Count ? true : null;
        }

        OctalBox.Text = _stats.Select(s => s.Mode).Distinct().Count() == 1 ? RemoteSource.ToOctal(_stats[0].Mode) : string.Empty;
        _syncing = false;

        var dir = _stats.FirstOrDefault(s => s.Kind.Contains("directory", StringComparison.OrdinalIgnoreCase));
        DirModeBox.Text = RemoteSource.ToOctal(dir?.Mode is >= 0 ? dir.Mode : 0b111_101_101);
        FileModeBox.Text = RemoteSource.ToOctal((dir?.Mode is >= 0 ? dir.Mode : 0b111_101_101) & ~0b001_001_001 & 0x1FF);
    }

    private void OnBitClick(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox box)
        {
            // смешанное значение после щелчка становится обычным: трёх положений больше не нужно
            box.IsThreeState = false;
            box.IsChecked ??= true;
        }

        if (_syncing || _bits.Any(b => b.IsChecked is null))
        {
            OctalBox.Text = string.Empty;
            return;
        }

        _syncing = true;
        var special = _stats.Select(s => s.Mode & 0xE00).Distinct().Count() == 1 ? _stats[0].Mode & 0xE00 : 0;
        OctalBox.Text = RemoteSource.ToOctal(special | BitsMode());
        _syncing = false;
    }

    private int BitsMode()
    {
        var mode = 0;

        for (var i = 0; i < 9; i++)
        {
            if (_bits[i].IsChecked == true)
            {
                mode |= 1 << (8 - i);
            }
        }

        return mode;
    }

    private void OnOctalChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || ParseMode(OctalBox.Text) is not { } mode)
        {
            return;
        }

        _syncing = true;

        for (var i = 0; i < 9; i++)
        {
            _bits[i].IsThreeState = false;
            _bits[i].IsChecked = (mode & (1 << (8 - i))) != 0;
        }

        _syncing = false;
    }

    private void OnRecursiveClick(object sender, RoutedEventArgs e)
    {
        var deep = RecursiveBox.IsChecked == true;
        RecursiveGrid.Visibility = deep ? Visibility.Visible : Visibility.Collapsed;

        foreach (var bit in _bits)
        {
            bit.IsEnabled = !deep;
        }

        OctalBox.IsEnabled = !deep;
    }

    private static int? ParseMode(string text)
    {
        var t = text.Trim();

        if (t.Length is < 3 or > 4 || t.Any(c => c is < '0' or > '7'))
        {
            return null;
        }

        return Convert.ToInt32(t, 8);
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (_source is not RemoteSource remote)
        {
            return;
        }

        var paths = _items.Select(i => i.FullPath).ToList();
        var notes = new List<string>();
        var done = new List<string>();
        var deep = RecursiveBox.IsChecked == true;

        ApplyButton.IsEnabled = false;
        ResultText.Text = "применяю…";

        try
        {
            if (deep)
            {
                if (ParseMode(DirModeBox.Text) is not { } dirMode || ParseMode(FileModeBox.Text) is not { } fileMode)
                {
                    ResultText.Text = "права числом: три или четыре цифры от 0 до 7, например 755 и 644";
                    return;
                }

                notes.AddRange(await remote.ChmodAsync(paths, 0, dirMode, fileMode));
                done.Add($"права со вложенными: папкам {RemoteSource.ToOctal(dirMode)}, файлам {RemoteSource.ToOctal(fileMode)}");
            }
            else if (_stats.Count > 0)
            {
                // каждому — его прежние права, поправленные только в тех битах, что заданы явно
                var set = 0;
                var clear = 0;

                for (var i = 0; i < 9; i++)
                {
                    var bit = 1 << (8 - i);

                    if (_bits[i].IsChecked == true)
                    {
                        set |= bit;
                    }
                    else if (_bits[i].IsChecked == false)
                    {
                        clear |= bit;
                    }
                }

                var typed = ParseMode(OctalBox.Text);

                var groups = _stats
                    .Select(s => (s.Path, Mode: typed ?? ((s.Mode & ~clear) | set), Old: s.Mode))
                    .Where(x => x.Mode != x.Old)
                    .GroupBy(x => x.Mode)
                    .ToList();

                foreach (var group in groups)
                {
                    notes.AddRange(await remote.ChmodAsync(group.Select(g => g.Path).ToList(), group.Key, null, null));
                }

                if (groups.Count > 0)
                {
                    done.Add(groups.Count == 1 ? $"права {RemoteSource.ToOctal(groups[0].Key)}" : "права");
                }
            }

            var owner = OwnerBox.Text.Trim();
            var group2 = GroupBox.Text.Trim();
            var newOwner = owner.Length > 0 && owner != _owner ? owner : string.Empty;
            var newGroup = group2.Length > 0 && group2 != _group ? group2 : string.Empty;

            if (newOwner.Length > 0 || newGroup.Length > 0 || (deep && (owner.Length > 0 || group2.Length > 0) && (owner != _owner || group2 != _group)))
            {
                notes.AddRange(await remote.ChownAsync(paths, newOwner, newGroup, deep));
                done.Add("владелец " + string.Join(':', new[] { newOwner, newGroup }.Where(s => s.Length > 0)));
            }

            Changed |= done.Count > 0;

            foreach (var note in notes)
            {
                AppLog.Error(note, remote.Server.Display);
            }

            if (done.Count == 0)
            {
                ResultText.Text = "менять нечего — всё как было";
                return;
            }

            var summary = $"{string.Join(", ", done)}: {(_items.Count == 1 ? _items[0].FullPath : $"{_items.Count} объектов")}";

            if (notes.Count == 0)
            {
                AppLog.Success("Изменено — " + summary, remote.Server.Display);
                ResultText.Text = "готово: " + string.Join(", ", done);
            }
            else
            {
                ResultText.Text = "не всё получилось: " + notes[0];
            }

            // показать то, что стало на самом деле
            await FillRightsAsync(remote);
        }
        catch (Exception ex)
        {
            ResultText.Text = "не получилось: " + ex.Message;
            AppLog.Error("Свойства: " + ex.Message, remote.Server.Display);
        }
        finally
        {
            ApplyButton.IsEnabled = true;
        }
    }

    // ── размер и сумма ───────────────────────────────────────────────────────

    private async void OnMeasure(object sender, RoutedEventArgs e)
    {
        _work?.Cancel();
        var cts = _work = new CancellationTokenSource();

        MeasureButton.IsEnabled = false;
        SizeText.Text = "считаю…";

        try
        {
            long bytes = 0;
            var files = 0;
            var dirs = 0;

            foreach (var item in _items)
            {
                if (item.IsDirectory)
                {
                    var (b, f, d) = await _source.MeasureAsync(item.FullPath, cts.Token);
                    bytes += b;
                    files += f;
                    dirs += d + 1;
                }
                else
                {
                    bytes += item.Length;
                    files++;
                }
            }

            SizeText.Text = $"{TransferItem.Format(bytes)} ({bytes:N0} байт)";
            ContainsLabel.Visibility = ContainsText.Visibility = Visibility.Visible;
            ContainsText.Text = $"файлов: {files:N0}, папок: {dirs:N0}";
        }
        catch (OperationCanceledException)
        {
            SizeText.Text = "—";
        }
        catch (Exception ex)
        {
            SizeText.Text = "не посчиталось: " + ex.Message;
        }
        finally
        {
            MeasureButton.IsEnabled = true;
        }
    }

    private async void OnHash(object sender, RoutedEventArgs e)
    {
        if (_items is not [{ IsDirectory: false } file])
        {
            return;
        }

        _work?.Cancel();
        var cts = _work = new CancellationTokenSource();

        HashButton.IsEnabled = false;
        HashText.Text = "считаю…";

        try
        {
            HashText.Text = await _source.HashAsync(file.FullPath, cts.Token);
            HashCopy.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            HashText.Text = string.Empty;
        }
        catch (Exception ex)
        {
            HashText.Text = "не посчиталась: " + ex.Message;
        }
        finally
        {
            HashButton.IsEnabled = true;
        }
    }

    private void OnCopyHash(object sender, RoutedEventArgs e)
    {
        if (HashText.Text.Length == 64)
        {
            ClipboardText.Copy(HashText.Text);
            AppLog.Hint("Контрольная сумма скопирована.");
        }
    }

    /// <summary>Родное окно свойств Windows — для того, чего у нас нет: доступ, версии, безопасность.</summary>
    private void OnWindowsProperties(object sender, RoutedEventArgs e)
    {
        if (_items is [var single])
        {
            SHObjectProperties(IntPtr.Zero, 2, single.FullPath, null);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHObjectProperties(IntPtr hwnd, uint shopObjectType, string pszObjectName, string? pszPropertyPage);
}
