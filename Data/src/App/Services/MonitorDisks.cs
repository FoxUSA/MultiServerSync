using MultiServerSync.Models;

namespace MultiServerSync.Services;

/// <summary>Записываемый раздел носителя: путь, тип, устройство и место, как у df (всего, занято, доступно).</summary>
public sealed record MonitorFs(string Mount, string Type, string Device, long Total, long Used, long Avail)
{
    /// <summary>Занято в процентах, как считает df: занято / (занято + доступно) — запас root считается занятым.</summary>
    public double Pct => Used + Avail > 0 ? Used * 100.0 / (Used + Avail) : 0;

    /// <summary>Путь коротко: «/», «/jffs», «/boot/efi»; из трёх и больше частей — последним именем («ENTWARE»).</summary>
    public string Short
    {
        get
        {
            var parts = Mount.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 3 ? parts[^1] : Mount;
        }
    }
}

/// <summary>
/// Носитель сервера для карточки «Диск» — физическое устройство целиком: флеш-память роутера (весь чип, по разделам
/// MTD) или диск из /sys/block (виртуальные — LVM, RAID, loop — пересчитаны на свои физические). Всего — размер
/// устройства; свободно — доступное на его записываемых разделах; занято — остальное (файлы, прошивка, разметка).
/// Без физического устройства (ZFS, корень контейнера) — раздел сам по себе. Как устроено у роутеров (OpenWrt, ASUS,
/// Keenetic), NAS и серверов — изучено 24.11 и проверено на двух роутерах ASUS.
/// </summary>
public sealed class MonitorDisk
{
    /// <summary>«flash» — флеш-память роутера; имя диска (sda, vda, nvme0n1); «fs:/путь» — раздел без устройства.</summary>
    public required string Id { get; init; }

    /// <summary>Имя устройства для подписи: sda, vda; у флеш-памяти и раздела без устройства — пусто.</summary>
    public string Name { get; init; } = string.Empty;

    public bool Usb { get; init; }

    /// <summary>У mmcblk: «SD» — карта, «MMC» — встроенная eMMC; иначе пусто.</summary>
    public string Media { get; init; } = string.Empty;

    /// <summary>Размер устройства (у раздела без устройства — размер раздела).</summary>
    public long Total { get; init; }

    /// <summary>Подкачка на этом устройстве (разделом).</summary>
    public long Swap { get; init; }

    public List<MonitorFs> Parts { get; } = new();

    public bool IsFlash => Id == "flash";

    public long Free => Parts.Sum(p => p.Avail);

    public long Used => Math.Max(0, Total - Free);

    public double Pct => Total > 0 ? Used * 100.0 / Total : 0;

    /// <summary>Самый заполненный записываемый раздел — по нему статус и сигнал: забитый / при пустом /data — тревога.</summary>
    public MonitorFs? Worst => Parts.MaxBy(p => p.Pct);

    public string Kind =>
        IsFlash ? "Память роутера"
        : Usb ? "Флешка"
        : Media == "SD" ? "SD-карта"
        : "Диск";

    /// <summary>Приписка: у одного раздела — его путь («/», «ENTWARE»), у нескольких — имя устройства («vda»).</summary>
    public string Note =>
        IsFlash ? string.Empty
        : Parts.Count == 1 ? Parts[0].Short
        : Name.Length > 0 ? Name
        : Parts.FirstOrDefault()?.Short ?? string.Empty;

    public string Label => Note.Length > 0 ? $"{Kind} · {Note}" : Kind;

    /// <summary>
    /// Из чего складывается: разделы «/jffs 2.5 МБ из 44.5 МБ», подкачка, остаток — у флеш-памяти «прошивка и служебное»
    /// (прошивка в двух копиях, загрузчик, разметка UBI), у диска «разметка и без разделов».
    /// </summary>
    public IReadOnlyList<string> Pieces()
    {
        var list = Parts.Select(p => $"{p.Short} {MonitorFormat.Bytes(p.Used)} из {MonitorFormat.Bytes(p.Total)}").ToList();

        // запас для root (у ext4 по умолчанию 5%): в «доступно» не входит — без этой строки пустая флешка «занята» на гигабайты
        var reserve = Parts.Sum(p => Math.Max(0, p.Total - p.Used - p.Avail));

        if (reserve > Total / 100)
        {
            // у ext2/3/4 это запас для root; у ubifs и прочих — свой резерв файловой системы
            var ext = Parts.Where(p => p.Total - p.Used - p.Avail > 0).All(p => p.Type.StartsWith("ext", StringComparison.Ordinal));
            list.Add((ext ? "запас для root " : "резерв файловой системы ") + MonitorFormat.Bytes(reserve));
        }

        if (Swap > 0)
        {
            list.Add("подкачка " + MonitorFormat.Bytes(Swap));
        }

        if (Id.StartsWith("fs:", StringComparison.Ordinal))
        {
            return list;
        }

        var rest = Total - Parts.Sum(p => p.Total) - Swap;

        // меньше процента у диска — таблица разделов и выравнивание, не о чем говорить. У диска остаток — служебное
        // файловых систем (df показывает размер раздела без него), разметка и место без разделов
        if (rest > 0 && (IsFlash || rest > Total / 100))
        {
            list.Add((IsFlash ? "прошивка и служебное " : "служебное и разметка ") + MonitorFormat.Bytes(rest));
        }

        return list;
    }
}

/// <summary>
/// Какой носитель показывает «Диск» у сервера. Выбор — за сервером (settings.json → MonitorDisks), общий для всех
/// окон; сигнал «Диск» следит за ним же. Не выбирали — носитель с корнем «/», если в корень пишется; иначе (роутер)
/// флеш-память, флешка, что есть.
/// </summary>
public static class MonitorDisks
{
    /// <summary>Выбор сменился — виды перерисовывают «Диск», сигнал забывает прежнее состояние.</summary>
    public static event Action<ServerEntry>? Changed;

    public static MonitorDisk? Pick(ServerEntry server, IReadOnlyList<MonitorDisk>? disks)
    {
        if (disks is not { Count: > 0 })
        {
            return null;
        }

        if (AppState.Current.Settings.MonitorDisks.TryGetValue(server.Id, out var saved)
            && disks.FirstOrDefault(d => d.Id == saved) is { } chosen)
        {
            return chosen;
        }

        return disks.FirstOrDefault(d => d.Parts.Any(p => p.Mount == "/"))
            ?? disks.FirstOrDefault(d => d.IsFlash)
            ?? disks.FirstOrDefault(d => d.Usb)
            ?? disks[0];
    }

    public static void Set(ServerEntry server, MonitorDisk disk)
    {
        AppState.Current.Settings.MonitorDisks[server.Id] = disk.Id;
        AppState.Current.SaveSettings();

        // настройка вида, а не действие на сервере: строка «Программы»
        ActionLog.Info($"Мониторинг: у «{server.PaneLabel}» показывается {disk.Label}", null, AppSection.Monitor,
            AppState.Current.MonitorTabOf(server));
        Changed?.Invoke(server);
    }

    /// <summary>
    /// Носители из ответа замера: «T размер смещение» — разделы флеш-памяти (чип — раздел без смещения, иначе конец
    /// самого дальнего), «D имя секторы usb тип» — диски, «W диск КБ» — подкачка, «M носитель тип устройство путь» и
    /// следом «S всего занято доступно» (КБ) — записываемые разделы.
    /// </summary>
    public static List<MonitorDisk> Parse(IEnumerable<string> lines)
    {
        var mtd = new List<(long Size, long? Offset)>();
        var disks = new Dictionary<string, (long Sectors, bool Usb, string Media)>(StringComparer.Ordinal);
        var swap = new Dictionary<string, long>(StringComparer.Ordinal);
        var parts = new List<(string Owner, MonitorFs Fs)>();
        (string Owner, string Type, string Device, string Mount)? pending = null;

        foreach (var line in lines)
        {
            var f = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            if (f.Length == 0)
            {
                continue;
            }

            switch (f[0])
            {
                case "T" when f.Length >= 2 && long.TryParse(f[1], out var size):
                    mtd.Add((size, f.Length >= 3 && long.TryParse(f[2], out var off) ? off : null));
                    break;
                case "D" when f.Length >= 3 && long.TryParse(f[2], out var sectors):
                    disks[f[1]] = (sectors, f.Length >= 4 && f[3] == "1", f.Length >= 5 ? f[4] : string.Empty);
                    break;
                case "W" when f.Length >= 3 && long.TryParse(f[2], out var kb):
                    swap[f[1]] = swap.GetValueOrDefault(f[1]) + kb * 1024;
                    break;
                case "M" when f.Length >= 5:
                    pending = (f[1], f[2], f[3], string.Join(' ', f[4..]));
                    break;
                case "S" when f.Length >= 4 && pending is { } p
                    && long.TryParse(f[1], out var total) && long.TryParse(f[2], out var used) && long.TryParse(f[3], out var avail) && total > 0:
                    parts.Add((p.Owner, new MonitorFs(p.Mount, p.Type, p.Device, total * 1024, used * 1024, avail * 1024)));
                    pending = null;
                    break;
            }
        }

        var result = new List<MonitorDisk>();

        // флеш-память: раздел без смещения — весь чип; иначе конец самого дальнего раздела
        var chip = mtd.Count == 0 ? 0
            : mtd.Any(m => m.Offset is null) ? mtd.Where(m => m.Offset is null).Max(m => m.Size)
            : mtd.Max(m => m.Size + (m.Offset ?? 0));
        var flashParts = parts.Where(p => p.Owner == "flash").Select(p => p.Fs).ToList();

        if (chip > 0 || flashParts.Count > 0)
        {
            var flash = new MonitorDisk { Id = "flash", Total = Math.Max(chip, flashParts.Sum(p => p.Total)) };
            flash.Parts.AddRange(flashParts);
            result.Add(flash);
        }

        foreach (var (name, (sectors, usb, media)) in disks)
        {
            var own = parts.Where(p => p.Owner == name).Select(p => p.Fs).ToList();

            // диск без записываемых разделов (не подключён к системе) — показывать нечего
            if (own.Count == 0)
            {
                continue;
            }

            var disk = new MonitorDisk
            {
                Id = name,
                Name = name,
                Usb = usb,
                Media = media,
                Total = Math.Max(sectors * 512, own.Sum(p => p.Total)),
                Swap = swap.GetValueOrDefault(name),
            };
            disk.Parts.AddRange(own);
            result.Add(disk);
        }

        // разделы без физического устройства (ZFS, корень контейнера) или с диском, которого нет в /sys/block
        foreach (var (owner, fs) in parts.Where(p => p.Owner != "flash" && !disks.ContainsKey(p.Owner)))
        {
            var alone = new MonitorDisk { Id = "fs:" + fs.Mount, Total = fs.Total };
            alone.Parts.Add(fs);
            result.Add(alone);
        }

        return result;
    }
}
