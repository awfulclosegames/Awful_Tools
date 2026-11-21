using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace AwfulDiff.Services;

/// <summary>Kind of a node in a directory comparison tree.</summary>
public enum DirEntryKind
{
    File,
    Directory
}

/// <summary>Status of a file that exists on one or both sides.</summary>
public enum FileEntryStatus
{
    Identical,
    Different,
    OnlyInLeft,
    OnlyInRight
}

/// <summary>Status of a directory in the comparison tree.</summary>
public enum FolderStatus
{
    NoDifferences,
    HasDifferences,
    OnlyInLeft,
    OnlyInRight
}

/// <summary>
/// One node of a directory comparison tree. Display strings are derived from the status and
/// metadata so the TreeView template needs no converters. Lives in a WPF-free file: the full
/// tree is built once by <see cref="DirectoryDiff.Compare"/> and <see cref="DirectoryDiff.FilterTree"/>
/// reassigns <see cref="VisibleChildren"/> on every node whenever the show-unchanged toggles change.
/// </summary>
public class DirEntry : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Leaf name as it appears on disk (left-side spelling preferred when only case differs).</summary>
    public string Name { get; init; } = "";

    /// <summary>Backslash-separated path relative to the compared roots.</summary>
    public string RelativePath { get; init; } = "";

    public DirEntryKind Kind { get; init; }

    /// <summary>Files only.</summary>
    public FileEntryStatus? FileStatus { get; set; }

    /// <summary>Directories only.</summary>
    public FolderStatus? FolderStatus { get; set; }

    /// <summary>Absolute path on the left side; null when absent there.</summary>
    public string? PathLeft { get; set; }

    /// <summary>Absolute path on the right side; null when absent there.</summary>
    public string? PathRight { get; set; }

    /// <summary>File size in bytes, or -1 when absent on that side (and for directories).</summary>
    public long SizeLeft { get; set; } = -1;
    public long SizeRight { get; set; } = -1;

    public DateTime TimeLeft { get; set; }
    public DateTime TimeRight { get; set; }

    // ── Display strings (derived — always consistent with the status) ──────────

    /// <summary>Status glyph shown in the tree's first column.</summary>
    // (The FolderStatus arms are fully qualified — the property above shadows the enum type name
    // inside this class, so a bare `FolderStatus.X` would be member access on the nullable.)
    public string Glyph => Kind == DirEntryKind.File
        ? FileStatus switch
        {
            FileEntryStatus.Identical   => "·",
            FileEntryStatus.Different   => "Δ",
            FileEntryStatus.OnlyInLeft  => "◄",
            _                           => "►",
        }
        : FolderStatus switch
        {
            AwfulDiff.Services.FolderStatus.NoDifferences  => "▫",
            AwfulDiff.Services.FolderStatus.HasDifferences => "▣",
            AwfulDiff.Services.FolderStatus.OnlyInLeft     => "◄",
            _                                              => "►",
        };

    public string SizeLeftText  => SizeLeft  < 0 ? "" : FileService.FormatFileSize(SizeLeft);
    public string SizeRightText => SizeRight < 0 ? "" : FileService.FormatFileSize(SizeRight);
    public string TimeLeftText  => SizeLeft  < 0 ? "" : TimeLeft.ToString("yyyy-MM-dd HH:mm");
    public string TimeRightText => SizeRight < 0 ? "" : TimeRight.ToString("yyyy-MM-dd HH:mm");

    /// <summary>Full (unfiltered) children (files and directories), set once by Compare.</summary>
    public List<DirEntry> Children { get; } = new();

    private bool _isExpanded = true;

    /// <summary>
    /// Tree expansion state, bound two-way to the row's TreeViewItem. Defaults to true so a
    /// fresh compare opens fully expanded (the previous XAML default); the context-menu
    /// expand/collapse-all actions flip it recursively over <see cref="Children"/>.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    private List<DirEntry> _visibleChildren = new();

    /// <summary>
    /// Filtered children shown by the tree. Reassigned (not cleared/refilled) by FilterTree so
    /// the HierarchicalDataTemplate rebuilds its containers.
    /// </summary>
    public List<DirEntry> VisibleChildren
    {
        get => _visibleChildren;
        set
        {
            if (ReferenceEquals(_visibleChildren, value)) return;
            _visibleChildren = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VisibleChildren)));
        }
    }
}

/// <summary>Outcome of a full directory comparison: the root list plus summary counts.</summary>
public class DirectoryDiffResult
{
    public List<DirEntry> RootEntries { get; } = new();

    public int FilesCompared;
    public int IdenticalFiles;
    public int DifferentFiles;
    public int OnlyInLeftFiles;
    public int OnlyInRightFiles;

    public long ElapsedMs;

    /// <summary>Per-file read failures that fell back to the metadata verdict.</summary>
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Recursive directory comparison. Pure file-system logic (no WPF types) so it is testable and
/// safe to run on a background thread. Entries are paired by relative path case-insensitively
/// (Windows convention); files compare size+last-write-time first and only read bytes when the
/// metadata differs.
/// </summary>
public static class DirectoryDiff
{
    /// <summary>
    /// Compares two directories recursively and returns the full (unfiltered) tree.
    /// Directories present on only one side are leaves — their contents are not enumerated.
    /// </summary>
    public static DirectoryDiffResult Compare(string dirLeft, string dirRight, CancellationToken ct = default)
    {
        var result = new DirectoryDiffResult();
        var sw = Stopwatch.StartNew();

        // Junction/symlink loop guard: a directory path is entered at most once.
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        result.RootEntries.AddRange(Merge(dirLeft, dirRight, "", result, visited, ct));
        result.ElapsedMs = sw.ElapsedMilliseconds;
        return result;
    }

    /// <summary>
    /// Recomputes VisibleChildren on every node of the full tree (post-order) and returns the
    /// visible top-level list. A file is visible when unchanged files are shown or it differs;
    /// a folder is visible when unchanged folders are shown, it exists on only one side (an empty
    /// added/removed folder IS a difference), or it still has at least one visible child.
    /// </summary>
    public static List<DirEntry> FilterTree(List<DirEntry> entries, bool showUnchangedFiles, bool showUnchangedDirs)
    {
        var kept = new List<DirEntry>(entries.Count);
        foreach (var e in entries)
        {
            if (e.Kind == DirEntryKind.Directory)
                e.VisibleChildren = FilterTree(e.Children, showUnchangedFiles, showUnchangedDirs);

            bool visible = e.Kind == DirEntryKind.File
                ? showUnchangedFiles || e.FileStatus != FileEntryStatus.Identical
                : showUnchangedDirs
                    || e.FolderStatus is FolderStatus.OnlyInLeft or FolderStatus.OnlyInRight
                    || e.VisibleChildren.Count > 0;

            if (visible) kept.Add(e);
        }
        return kept;
    }

    // ── Walk ────────────────────────────────────────────────────────────────────

    private static List<DirEntry> Merge(string leftDir, string rightDir, string relPrefix,
        DirectoryDiffResult result, HashSet<string> visited, CancellationToken ct)
    {
        var children = new List<DirEntry>();
        ct.ThrowIfCancellationRequested();

        // Loop guard: if this directory was already entered (self-referencing junction), skip it.
        if (!visited.Add(Normalize(leftDir))) return children;
        visited.Add(Normalize(rightDir));

        var leftMap = Enumerate(leftDir, result);
        var rightMap = Enumerate(rightDir, result);

        // Union of names, left-side spelling preferred (drives the displayed Name on case-only diffs).
        var names = new List<string>(leftMap.Count + rightMap.Count);
        names.AddRange(leftMap.Keys);
        foreach (var k in rightMap.Keys)
            if (!leftMap.ContainsKey(k)) names.Add(k);

        bool IsDir(string n)
        {
            if (leftMap.TryGetValue(n, out var l)) return l.IsDir;
            if (rightMap.TryGetValue(n, out var r)) return r.IsDir;
            return false;
        }

        // Directories first, then files — both ordinal-ignore-case.
        names.Sort((a, b) =>
        {
            bool ad = IsDir(a), bd = IsDir(b);
            if (ad != bd) return ad ? -1 : 1;
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        });

        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();

            bool lHas = leftMap.TryGetValue(name, out var l);
            bool rHas = rightMap.TryGetValue(name, out var r);
            string relPath = relPrefix.Length == 0 ? name : Path.Combine(relPrefix, name);

            if (lHas && rHas)
            {
                if (l.IsDir && r.IsDir)
                {
                    var sub = Merge(l.Full, r.Full, relPath, result, visited, ct);
                    bool hasDiff = false;
                    foreach (var c in sub)
                        if (SubtreeHasDifference(c)) { hasDiff = true; break; }

                    var e = new DirEntry
                    {
                        Name = name,
                        RelativePath = relPath,
                        Kind = DirEntryKind.Directory,
                        PathLeft = l.Full,
                        PathRight = r.Full,
                        FolderStatus = hasDiff ? FolderStatus.HasDifferences : FolderStatus.NoDifferences
                    };
                    e.Children.AddRange(sub);
                    children.Add(e);
                }
                else if (!l.IsDir && !r.IsDir)
                {
                    var fl = new FileInfo(l.Full);
                    var fr = new FileInfo(r.Full);
                    var status = CompareFileContent(l.Full, r.Full, fl, fr, relPath, result.Warnings);

                    var e = new DirEntry
                    {
                        Name = name,
                        RelativePath = relPath,
                        Kind = DirEntryKind.File,
                        FileStatus = status,
                        PathLeft = l.Full,
                        PathRight = r.Full
                    };
                    FillMeta(e, fl, left: true);
                    FillMeta(e, fr, left: false);
                    children.Add(e);
                    CountFile(result, status);
                }
                else
                {
                    // Same name but different kind (dir vs file) — report both as one-sided entries.
                    if (l.IsDir) children.Add(MakeFolderOneSided(name, relPath, l.Full, FolderStatus.OnlyInLeft));
                    else children.Add(MakeFileOneSided(name, relPath, l.Full, FileEntryStatus.OnlyInLeft, result));
                    if (r.IsDir) children.Add(MakeFolderOneSided(name, relPath, r.Full, FolderStatus.OnlyInRight));
                    else children.Add(MakeFileOneSided(name, relPath, r.Full, FileEntryStatus.OnlyInRight, result));
                }
            }
            else if (lHas)
            {
                children.Add(l.IsDir
                    ? MakeFolderOneSided(name, relPath, l.Full, FolderStatus.OnlyInLeft)
                    : MakeFileOneSided(name, relPath, l.Full, FileEntryStatus.OnlyInLeft, result));
            }
            else // rHas
            {
                children.Add(r.IsDir
                    ? MakeFolderOneSided(name, relPath, r.Full, FolderStatus.OnlyInRight)
                    : MakeFileOneSided(name, relPath, r.Full, FileEntryStatus.OnlyInRight, result));
            }
        }

        return children;
    }

    /// <summary>Child names of a directory keyed case-insensitively; unreadable dirs warn and compare as empty.</summary>
    private static Dictionary<string, (bool IsDir, string Full)> Enumerate(string dir, DirectoryDiffResult result)
    {
        var map = new Dictionary<string, (bool IsDir, string Full)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in Directory.EnumerateDirectories(dir))
                map[Path.GetFileName(p)] = (true, p);
            foreach (var p in Directory.EnumerateFiles(dir))
                if (!map.ContainsKey(Path.GetFileName(p)))
                    map[Path.GetFileName(p)] = (false, p);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Warnings.Add($"{dir}: could not read directory ({ex.Message})");
        }
        return map;
    }

    /// <summary>
    /// Identical when size AND last-write-time match (no disk read); otherwise reads both files
    /// and compares bytes. A read failure falls back to the metadata verdict — which is
    /// Different here, since we only reach this point when the metadata already differed.
    /// Not routed through FileService.LoadFile: that decodes text lines and pollutes its cache.
    /// </summary>
    private static FileEntryStatus CompareFileContent(string leftFile, string rightFile,
        FileInfo fl, FileInfo fr, string relPath, List<string> warnings)
    {
        if (fl.Length == fr.Length && fl.LastWriteTime == fr.LastWriteTime)
            return FileEntryStatus.Identical;

        try
        {
            byte[] bl = File.ReadAllBytes(leftFile);
            byte[] br = File.ReadAllBytes(rightFile);
            return bl.SequenceEqual(br) ? FileEntryStatus.Identical : FileEntryStatus.Different;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            warnings.Add($"{relPath}: read failed ({ex.Message}) — treated as different");
            return FileEntryStatus.Different;
        }
    }

    private static bool SubtreeHasDifference(DirEntry e) => e.Kind == DirEntryKind.File
        ? e.FileStatus != FileEntryStatus.Identical
        : e.FolderStatus is FolderStatus.HasDifferences or FolderStatus.OnlyInLeft or FolderStatus.OnlyInRight;

    private static void CountFile(DirectoryDiffResult result, FileEntryStatus status)
    {
        result.FilesCompared++;
        switch (status)
        {
            case FileEntryStatus.Identical: result.IdenticalFiles++; break;
            case FileEntryStatus.Different: result.DifferentFiles++; break;
            case FileEntryStatus.OnlyInLeft: result.OnlyInLeftFiles++; break;
            case FileEntryStatus.OnlyInRight: result.OnlyInRightFiles++; break;
        }
    }

    private static DirEntry MakeFileOneSided(string name, string relPath, string path, FileEntryStatus status, DirectoryDiffResult result)
    {
        var e = new DirEntry
        {
            Name = name,
            RelativePath = relPath,
            Kind = DirEntryKind.File,
            FileStatus = status
        };
        if (status == FileEntryStatus.OnlyInLeft) e.PathLeft = path; else e.PathRight = path;
        try { FillMeta(e, new FileInfo(path), left: status == FileEntryStatus.OnlyInLeft); }
        catch { /* metadata stays -1/empty */ }
        CountFile(result, status);
        return e;
    }

    /// <summary>One-sided directory — a leaf in v1 (contents are not enumerated).</summary>
    private static DirEntry MakeFolderOneSided(string name, string relPath, string path, FolderStatus side)
    {
        var e = new DirEntry
        {
            Name = name,
            RelativePath = relPath,
            Kind = DirEntryKind.Directory,
            FolderStatus = side
        };
        if (side == FolderStatus.OnlyInLeft) e.PathLeft = path; else e.PathRight = path;
        return e;
    }

    private static void FillMeta(DirEntry e, FileInfo fi, bool left)
    {
        if (left) { e.SizeLeft = fi.Length; e.TimeLeft = fi.LastWriteTime; }
        else { e.SizeRight = fi.Length; e.TimeRight = fi.LastWriteTime; }
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');
}
