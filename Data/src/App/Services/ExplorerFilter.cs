using System.IO;
using MultiServerSync.Core;
using MultiServerSync.Models;

namespace MultiServerSync.Services;

/// <summary>
/// Фильтр вкладки копирования галочками в «Проводнике». Корень называется как вкладка; окно одно — в нём его строки,
/// окон больше — папка на каждое окно, в ней его строки. Выше строк не подняться.
/// Галочки читаются из масок своего окна (<see cref="ExplorerPicks"/>). Поставленные здесь — черновик: в маски окон
/// они уходят, только когда в окне исключений нажали «Сохранить» (<see cref="Commit"/>). Режим «что копируется /
/// не копируется» — один на все окна.
/// </summary>
public sealed class ExplorerFilter
{
    /// <summary>Окна, где галочки поменяли: в маски пишутся только они.</summary>
    private readonly HashSet<Part> _dirty = [];

    /// <summary>Окно копирования в фильтре: его строки и его галочки.</summary>
    public sealed class Part(CopyWindow window, IReadOnlyList<string> roots, ExplorerPicks picks)
    {
        public CopyWindow Window { get; } = window;

        /// <summary>Полные пути строк окна.</summary>
        public IReadOnlyList<string> Roots { get; } = roots;

        /// <summary>Строка окна — папка.</summary>
        public IReadOnlyList<bool> RootIsDir { get; } = roots.Select(Directory.Exists).ToList();

        public ExplorerPicks Picks { get; } = picks;
    }

    public ExplorerFilter(Workspace source)
    {
        Source = source;
        Parts = source.CopyWindows
            .Where(w => w.Items.Count > 0)
            .Select(w => new Part(w, w.Items.Select(i => i.FullPath).ToList(), ExplorerPicks.Parse(source.MasksOf(w))))
            .ToList();

        // режим — как у окна в фокусе; у остальных он в маски попадёт, только когда их тронут
        var focused = Parts.FirstOrDefault(p => ReferenceEquals(p.Window, source.FocusedWindow)) ?? Parts.FirstOrDefault();
        CopiedMode = focused?.Picks.CopiedMode ?? true;

        foreach (var part in Parts)
        {
            part.Picks.CopiedMode = CopiedMode;
        }

        Files = new LocalSource(Parts.Select(p => (Title(p.Window), p.Roots)).ToList(), source.Title);
        _saved = Current;
        _savedMode = CopiedMode;
    }

    /// <summary>Галочки и режим при открытии — с чем сравнивать, есть ли что сохранять.</summary>
    private readonly IReadOnlyList<string> _saved;

    private readonly bool _savedMode;

    /// <summary>Галочки или режим отличаются от масок окон: окну исключений есть что сохранить.</summary>
    public bool HasChanges => CopiedMode != _savedMode || !Current.SequenceEqual(_saved);

    /// <summary>Строки галочек окна — для окна исключений; окна нет в фильтре — null.</summary>
    public IReadOnlyList<string>? LinesOf(CopyWindow window) =>
        Parts.FirstOrDefault(p => ReferenceEquals(p.Window, window))?.Picks.Lines().ToList();

    /// <summary>Вкладка копирования, с которой пришли.</summary>
    public Workspace Source { get; }

    /// <summary>Окна со строками — по порядку на странице.</summary>
    public IReadOnlyList<Part> Parts { get; }

    /// <summary>Файлы для панели: корень — строки окон.</summary>
    public LocalSource Files { get; }

    /// <summary>Режим «что копируется»; нет — «что не копируется». Один на все окна.</summary>
    public bool CopiedMode { get; private set; }

    /// <summary>Папка окна в корне: «Окно 2 · old».</summary>
    private static string Title(CopyWindow window) =>
        string.IsNullOrWhiteSpace(window.Name) ? $"Окно {window.Number}" : $"Окно {window.Number} · {window.Name.Trim()}";

    /// <summary>Окно, в чьей папке или строках путь; вне строк — окно в фокусе.</summary>
    public CopyWindow WindowAt(string path) =>
        PartOf(path)?.Window ?? Parts.FirstOrDefault(p => ReferenceEquals(p.Window, Source.FocusedWindow))?.Window ?? Source.FocusedWindow;

    private Part? PartOf(string fullPath) =>
        Files.GroupOf(fullPath) is { } group ? Parts[group] : Parts.FirstOrDefault(p => Relative(p, fullPath) is not null);

    /// <summary>Строка — папка окна в корне (окон больше одного): галочка на ней — на все его строки.</summary>
    private Part? PartFolder(FsEntry entry) =>
        Parts.Count > 1 && Files.GroupOf(entry.FullPath) is { } group && entry.FullPath == LocalSource.GroupPath(group) ? Parts[group] : null;

    /// <summary>Маски «Эта вкладка» и «Глобально» окна без галочек: от них галочки начинаются.</summary>
    private ExcludeFilter MasksOf(Part part) =>
        new(Source.MasksOf(part.Window), Source.GlobalExcludesOf(part.Window) ? AppState.Current.GlobalExcludeMasks : string.Empty);

    /// <summary>Путь от строки окна, как его видят маски: «public/geo/index.html». Null — вне строк окна.</summary>
    private static string? Relative(Part part, string fullPath)
    {
        foreach (var root in part.Roots)
        {
            var name = Path.GetFileName(root.TrimEnd('\\'));

            if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }

            if (fullPath.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return name + "/" + fullPath[(root.TrimEnd('\\').Length + 1)..].Replace('\\', '/');
            }
        }

        return null;
    }

    /// <summary>Строка едет: по галочкам, где они стоят, иначе по маскам.</summary>
    private static bool Goes(Part part, string relative, bool isDirectory, ExcludeFilter masks) =>
        part.Picks.Goes(relative) ?? !masks.DroppedByMasks(relative, isDirectory);

    private bool Shown(Part part, string relative, bool isDirectory, ExcludeFilter masks) =>
        Goes(part, relative, isDirectory, masks) == CopiedMode;

    /// <summary>Папка окна отмечена, если отмечены все его строки.</summary>
    private bool PartShown(Part part)
    {
        var masks = MasksOf(part);
        return part.Roots.Select((root, i) => Shown(part, Relative(part, root)!, part.RootIsDir[i], masks)).All(x => x);
    }

    /// <summary>Какие строки открытой папки отмечены: в режиме «что копируется» — то, что едет, иначе — то, что не едет.</summary>
    public IReadOnlyCollection<string> TickedNames(IReadOnlyList<FsEntry> entries)
    {
        var names = new List<string>();

        foreach (var entry in entries.Where(e => e.Kind == FsKind.Normal))
        {
            if (PartFolder(entry) is { } folder)
            {
                if (PartShown(folder))
                {
                    names.Add(entry.Name);
                }

                continue;
            }

            if (PartOf(entry.FullPath) is { } part && Relative(part, entry.FullPath) is { } relative
                && Shown(part, relative, entry.IsDirectory, MasksOf(part)))
            {
                names.Add(entry.Name);
            }
        }

        return names;
    }

    /// <summary>
    /// Человек поменял галочки в открытой папке: где строка теперь отмечена иначе, чем выходило, — своя галочка.
    /// Совпала с тем, что решают папка выше или маски, — лишняя, убирается. Галочка на папке окна — на все его строки.
    /// </summary>
    public void Edit(IReadOnlyList<FsEntry> entries, IReadOnlyCollection<string> ticked)
    {
        if (DateTime.UtcNow - _lastEdit > TimeSpan.FromMilliseconds(400))
        {
            _editing = false;
        }

        var before = Current;
        var changed = false;

        foreach (var entry in entries.Where(e => e.Kind == FsKind.Normal))
        {
            var want = ticked.Contains(entry.Name);

            if (PartFolder(entry) is { } folder)
            {
                if (want == PartShown(folder))
                {
                    continue;
                }

                var masks = MasksOf(folder);

                for (var i = 0; i < folder.Roots.Count; i++)
                {
                    Pick(folder, Relative(folder, folder.Roots[i])!, folder.RootIsDir[i], want, masks);
                }

                changed = true;
                continue;
            }

            if (PartOf(entry.FullPath) is not { } part || Relative(part, entry.FullPath) is not { } relative)
            {
                continue;
            }

            var own = MasksOf(part);

            if (want != Shown(part, relative, entry.IsDirectory, own))
            {
                Pick(part, relative, entry.IsDirectory, want, own);
                changed = true;
            }
        }

        if (changed && !Current.SequenceEqual(before))
        {
            // рамка мышью меняет галочки по строке — одна протяжка одним шагом, пока не отпустили
            if (!_editing)
            {
                _back.Add(new Snapshot("галочки", CopiedMode, before));
                _forward.Clear();
                UndoJournal.RaiseChanged();
            }

            _editing = true;
            _lastEdit = DateTime.UtcNow;
        }
    }

    /// <summary>Когда меняли галочки последний раз: пауза дольше 400 мс — протяжка кончилась, дальше новый шаг отмены.</summary>
    private DateTime _lastEdit;

    /// <summary>Своя галочка строке — только если без неё строка вышла бы другой.</summary>
    private void Pick(Part part, string relative, bool isDirectory, bool want, ExcludeFilter masks)
    {
        part.Picks.Set(relative, want);

        // без своей галочки строка была бы такой же — своя не нужна
        part.Picks.Active.Remove(relative);

        if (Shown(part, relative, isDirectory, masks) != want)
        {
            part.Picks.Active[relative] = want;
        }

        _dirty.Add(part);
    }

    /// <summary>Правка галочек идёт — следующие изменения до сохранения в тот же шаг.</summary>
    private bool _editing;

    public void SetMode(bool copied)
    {
        if (CopiedMode == copied)
        {
            return;
        }

        // под замком режим переключается только посмотреть его галочки: сохранить его нельзя
        if (!Source.IsPaneLocked)
        {
            Remember(copied ? "режим «Что копируется»" : "режим «Что не копируется»");
            _dirty.UnionWith(Parts);
        }

        ApplyMode(copied);
    }

    private void ApplyMode(bool copied)
    {
        CopiedMode = copied;

        foreach (var part in Parts)
        {
            part.Picks.CopiedMode = copied;
        }
    }

    /// <summary>«Сбросить»: галочки выбранного режима во всех окнах — снова по маскам «Эта вкладка» и «Глобально».</summary>
    public void Reset()
    {
        if (Parts.All(p => p.Picks.Active.Count == 0))
        {
            return;
        }

        Remember("сброс галочек");

        foreach (var part in Parts.Where(p => p.Picks.Active.Count > 0))
        {
            part.Picks.Active.Clear();
            _dirty.Add(part);
        }
    }

    // ── стрелки отмены: своя память, пока вкладка-фильтр открыта ───────────────

    /// <summary>Галочки всех окон строками масок, по порядку окон.</summary>
    private sealed record Snapshot(string What, bool CopiedMode, IReadOnlyList<string> Lines);

    private readonly List<Snapshot> _back = [];
    private readonly List<Snapshot> _forward = [];

    /// <summary>Галочки вернули стрелкой — панели пора заново отметить строки и режим.</summary>
    public event Action? Restored;

    public string? UndoName => _back.LastOrDefault()?.What;

    public string? RedoName => _forward.LastOrDefault()?.What;

    private IReadOnlyList<string> Current => Parts.Select(p => string.Join("\n", p.Picks.Lines())).ToList();

    /// <summary>Перед правкой — как было: стрелка «Отменить» вернёт это.</summary>
    private void Remember(string what)
    {
        _back.Add(new Snapshot(what, CopiedMode, Current));
        _forward.Clear();
        UndoJournal.RaiseChanged();
    }

    /// <summary>Шаг стрелкой: назад — к прежним галочкам, вперёд — к отменённым.</summary>
    public void Step(bool undo)
    {
        var from = undo ? _back : _forward;
        var to = undo ? _forward : _back;

        if (from.Count == 0)
        {
            return;
        }

        var step = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(step with { CopiedMode = CopiedMode, Lines = Current });

        Put(step.CopiedMode, step.Lines);
        UndoJournal.RaiseChanged();
        AppLog.Hint((undo ? "Отменено: " : "Возвращено: ") + step.What);
    }

    /// <summary>Галочки и режим сейчас — окно фильтра запоминает их при открытии для «Отмены».</summary>
    public (bool CopiedMode, IReadOnlyList<string> Lines) Remembered() => (CopiedMode, Current);

    /// <summary>«Отмена» в окне фильтра: галочки и режим — как при его открытии, без шага в истории.</summary>
    public void RestoreTo((bool CopiedMode, IReadOnlyList<string> Lines) state)
    {
        if (state.CopiedMode == CopiedMode && state.Lines.SequenceEqual(Current))
        {
            return;
        }

        Put(state.CopiedMode, state.Lines);
    }

    private void Put(bool copiedMode, IReadOnlyList<string> lines)
    {
        for (var i = 0; i < Parts.Count && i < lines.Count; i++)
        {
            var restored = ExplorerPicks.Parse(lines[i]);
            var picks = Parts[i].Picks;

            foreach (var (mine, theirs) in new[] { (picks.Copied, restored.Copied), (picks.Skipped, restored.Skipped) })
            {
                mine.Clear();

                foreach (var (path, ticked) in theirs)
                {
                    mine[path] = ticked;
                }
            }
        }

        ApplyMode(copiedMode);
        _dirty.UnionWith(Parts);
        Restored?.Invoke();
    }

    /// <summary>«Сохранить» в окне исключений: галочки — в маски тех окон, где их поменяли. Возвращает, сколько окон.</summary>
    public int Commit()
    {
        _editing = false;

        // вкладка копирования под замком: фильтр только смотрят, её маски не трогаются
        if (Source.IsPaneLocked)
        {
            _dirty.Clear();
            return 0;
        }

        var written = 0;

        foreach (var part in _dirty.Where(p => Source.CopyWindows.Contains(p.Window)))
        {
            var text = part.Picks.ComposeInto(Source.MasksOf(part.Window));

            if (text != Source.MasksOf(part.Window))
            {
                Source.SetMasksOf(part.Window, text);
                written++;
            }
        }

        _dirty.Clear();
        return written;
    }
}
