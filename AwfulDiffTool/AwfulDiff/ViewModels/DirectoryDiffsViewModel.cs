using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using AwfulDiff.Models;
using AwfulDiff.Services;

namespace AwfulDiff.ViewModels;

/// <summary>
/// View model for the directory-diffs window. Owns the full (unfiltered) comparison tree from
/// the last Compare; the two show-unchanged toggles re-derive the visible tree via FilterTree
/// with no disk I/O. Toggles persist immediately (same pattern as ShowMergedOutput in the merge
/// window). When both paths are valid directories at construction, the compare starts
/// automatically — this covers both launch paths (toolbar button prefilled from the open files'
/// folders, and folder drag-and-drop).
/// </summary>
public class DirectoryDiffsViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly MainWindowViewModel _mainVm;

    /// <summary>Full tree from the last Compare — FilterTree re-runs against this on toggle changes.</summary>
    private List<DirEntry>? _fullRoot;

    // ── Paths ───────────────────────────────────────────────────────────────────

    private string _leftPath = "";
    public string LeftPath { get => _leftPath; set => SetProperty(ref _leftPath, value); }

    private string _rightPath = "";
    public string RightPath { get => _rightPath; set => SetProperty(ref _rightPath, value); }

    // ── Show-unchanged toggles (persisted immediately) ──────────────────────────

    private bool _showUnchangedFiles;
    public bool ShowUnchangedFiles
    {
        get => _showUnchangedFiles;
        set
        {
            if (_showUnchangedFiles == value) return;
            _showUnchangedFiles = value;

            var s = SettingsService.Load();
            s.DirDiffShowUnchangedFiles = value;
            SettingsService.Save(s);

            ApplyFilter();
        }
    }

    private bool _showUnchangedDirs;
    public bool ShowUnchangedDirs
    {
        get => _showUnchangedDirs;
        set
        {
            if (_showUnchangedDirs == value) return;
            _showUnchangedDirs = value;

            var s = SettingsService.Load();
            s.DirDiffShowUnchangedDirs = value;
            SettingsService.Save(s);

            ApplyFilter();
        }
    }

    // ── Results ─────────────────────────────────────────────────────────────────

    /// <summary>Full (unfiltered) root entries from the last Compare, or null before the first one.</summary>
    public List<DirEntry>? FullRoot => _fullRoot;

    private List<DirEntry> _rootItems = new();
    public List<DirEntry> RootItems { get => _rootItems; private set => SetProperty(ref _rootItems, value); }

    private bool _isComparing;
    public bool IsComparing { get => _isComparing; private set => SetProperty(ref _isComparing, value); }

    /// <summary>True only after a completed compare produced an empty visible tree.</summary>
    private bool _isResultEmpty;
    public bool IsResultEmpty { get => _isResultEmpty; private set => SetProperty(ref _isResultEmpty, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    private int _warningCount;
    public int WarningCount
    {
        get => _warningCount;
        private set
        {
            if (_warningCount == value) return;
            _warningCount = value;
            OnPropertyChanged(nameof(WarningCount));
            OnPropertyChanged(nameof(HasWarnings));
        }
    }

    public bool HasWarnings => _warningCount > 0;

    /// <summary>Warnings from the last compare (status-bar ToolTip content).</summary>
    private string _warningsText = "";
    public string WarningsText { get => _warningsText; private set => SetProperty(ref _warningsText, value); }

    // ── Commands ────────────────────────────────────────────────────────────────

    public ICommand CompareCommand { get; }

    public DirectoryDiffsViewModel(MainWindowViewModel mainVm, string? initialLeftDir, string? initialRightDir)
    {
        _mainVm = mainVm;
        var settings = SettingsService.Load();
        _showUnchangedFiles = settings.DirDiffShowUnchangedFiles;
        _showUnchangedDirs = settings.DirDiffShowUnchangedDirs;
        LeftPath = initialLeftDir ?? "";
        RightPath = initialRightDir ?? "";
        CompareCommand = new RelayCommand(Compare);

        // Auto-compare when both paths are valid directories — the window is always launched
        // with at least one meaningful path, so this fires for drag-drop and (when both open
        // files have folders) for the toolbar button.
        if (Directory.Exists(LeftPath) && Directory.Exists(RightPath))
            Compare();
    }

    /// <summary>Compares on a background thread; filters and displays the result on completion.</summary>
    public void Compare()
    {
        if (IsComparing) return;
        if (!Directory.Exists(LeftPath) || !Directory.Exists(RightPath))
        {
            MessageBox.Show("One or both directories do not exist.", "Directory Diff",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RecordRecentDirs(LeftPath, RightPath);

        IsComparing = true;
        IsResultEmpty = false;
        StatusText = $"Comparing {LeftPath} … {RightPath}";

        string left = LeftPath, right = RightPath;
        Task.Run(() => DirectoryDiff.Compare(left, right))
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        IsComparing = false;
                        StatusText = "Comparison failed: " + (t.Exception?.GetBaseException().Message ?? "unknown error");
                    });
                    return;
                }

                var result = t.Result;
                _fullRoot = result.RootEntries;
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    ApplyFilter();
                    StatusText = $"{result.FilesCompared} files · {result.IdenticalFiles} identical · " +
                                 $"{result.DifferentFiles} different · {result.OnlyInLeftFiles} only in left · " +
                                 $"{result.OnlyInRightFiles} only in right · {result.ElapsedMs} ms";
                    WarningCount = result.Warnings.Count;
                    WarningsText = string.Join("\n", result.Warnings);
                    IsComparing = false;
                });
            });
    }

    private const int MaxRecentDirs = 8;

    /// <summary>Pushes the given pair to the front of the persisted recent list (deduped, capped).</summary>
    private static void RecordRecentDirs(string left, string right)
    {
        var s = SettingsService.Load();
        s.DirDiffRecentDirs.RemoveAll(r =>
            string.Equals(r.Left, left, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Right, right, StringComparison.OrdinalIgnoreCase));
        s.DirDiffRecentDirs.Insert(0, new DirDiffRecentEntry { Left = left, Right = right });
        if (s.DirDiffRecentDirs.Count > MaxRecentDirs)
            s.DirDiffRecentDirs.RemoveRange(MaxRecentDirs, s.DirDiffRecentDirs.Count - MaxRecentDirs);
        SettingsService.Save(s);
    }

    /// <summary>
    /// Routes directories dropped onto the window: two or more fill left+right in drop order;
    /// a single one fills the targeted side when given (toolbar drop zones), otherwise goes to
    /// the empty side (left preferred) or replaces the right when both are set.
    /// Auto-compares once both sides hold valid directories.
    /// </summary>
    public void ApplyDroppedDirectories(string[] dirs, bool? side = null)
    {
        if (dirs.Length >= 2)
        {
            LeftPath = dirs[0];
            RightPath = dirs[1];
        }
        else if (side == true)
            LeftPath = dirs[0];
        else if (side == false)
            RightPath = dirs[0];
        else if (string.IsNullOrWhiteSpace(LeftPath))
            LeftPath = dirs[0];
        else
            RightPath = dirs[0];

        if (Directory.Exists(LeftPath) && Directory.Exists(RightPath))
            Compare();
    }

    /// <summary>Swaps the two path boxes and re-compares (OnlyInLeft/OnlyInRight flip sides).</summary>
    public void SwapPaths()
    {
        (LeftPath, RightPath) = (RightPath, LeftPath);
        if (Directory.Exists(LeftPath) && Directory.Exists(RightPath))
            Compare();
    }

    /// <summary>Re-runs FilterTree over the cached full tree (no disk I/O) and refreshes the display.</summary>
    private void ApplyFilter()
    {
        if (_fullRoot == null) return;
        RootItems = DirectoryDiff.FilterTree(_fullRoot, ShowUnchangedFiles, ShowUnchangedDirs);
        IsResultEmpty = RootItems.Count == 0;
    }

    /// <summary>Opens the file (and its counterpart, if any) in the main window and activates it.
    /// If that window was closed, a new one is created around the same view model — a closed WPF
    /// Window cannot be re-Shown (Show throws InvalidOperationException), but the VM outlives its
    /// windows, so all open-file/diff state carries over.</summary>
    public void OpenInMain(DirEntry entry)
    {
        if (!_mainVm.OpenFilePair(entry.PathLeft, entry.PathRight)) return;

        var app = Application.Current;
        // OfType<MainWindow>, not app.MainWindow — that property is reassigned when windows
        // close, so it could point at this (folder-diff) window.
        var existing = app?.Windows.OfType<MainWindow>().FirstOrDefault();
        if (existing != null)
        {
            existing.Activate();
            return;
        }

        // Main window is gone — recreate it around the same VM, and re-point Application.MainWindow
        // at it: StartMerge closes Application.Current.MainWindow, which would otherwise be this
        // window.
        var win = new MainWindow(_mainVm);
        if (app != null) app.MainWindow = win;
        win.Show();
        win.Activate();
    }

    // ── INotifyPropertyChanged plumbing (same pattern as MergeWindowViewModel) ──

    private void SetProperty<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
