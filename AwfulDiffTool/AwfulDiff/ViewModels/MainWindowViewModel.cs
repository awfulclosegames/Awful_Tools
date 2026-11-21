using Microsoft.Win32;
using AwfulDiff.Models;
using AwfulDiff.Services;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Input;

namespace AwfulDiff.ViewModels;

/// <summary>
/// Main view model — manages file state, diff computation, settings, and commands.
/// Proper MVVM separation: no direct UI manipulation, raises PropertyChanged for binding.
/// </summary>
public class MainWindowViewModel : INotifyPropertyChanged
{
    // ── File State ─────────────────────────────────────────────────────────────

    private FileModel? _leftFile;
    public FileModel? LeftFile
    {
        get => _leftFile;
        private set => SetProperty(ref _leftFile, value);
    }

    private FileModel? _rightFile;
    public FileModel? RightFile
    {
        get => _rightFile;
        private set => SetProperty(ref _rightFile, value);
    }

    // ── Working Copy / Dirty State (for "Apply to Right") ────────────────────────

    /// <summary>
    /// Mutable copy of the right file's lines. Used when applying changes so we can
    /// modify content without touching disk until Save is invoked.
    /// </summary>
    private List<string>? _rightWorkingLines;

    /// <summary>
    /// Byte-level counterpart of <c>_rightWorkingLines</c> for hex edits. Invariant: at most one
    /// of the two is non-null — the IsHexView setter materializes one into the other on switch.
    /// </summary>
    private byte[]? _rightWorkingBytes;

    /// <summary>
    /// Byte range to re-select once the next hex display completes. Set by ApplyRightByteEdit and
    /// consumed in DisplayHexView — display is async and Lines reassignment resets the viewer's
    /// byte selection, so the restore must be event-driven from display completion.
    /// </summary>
    private (int StartByte, int ByteCount)? _pendingByteRestore;

    private bool _hasUnsavedChanges;
    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set
        {
            if (SetProperty(ref _hasUnsavedChanges, value))
            {
                // Update the dirty indicator string bound in the UI
                RightDirtyIndicator = _hasUnsavedChanges ? " ●" : "";
            }
        }
    }

    private string _rightDirtyIndicator = "";
    public string RightDirtyIndicator
    {
        get => _rightDirtyIndicator;
        private set
        {
            if (SetProperty(ref _rightDirtyIndicator, value))
            {
                // Update RightTitle to include/exclude the dirty indicator
                RefreshRightTitle();
            }
        }
    }

    /// <summary>
    /// Refreshes RightTitle to include the dirty indicator when there are unsaved changes.
    /// </summary>
    private void RefreshRightTitle()
    {
        if (RightFile != null)
        {
            RightTitle = $"{RightFile.DisplayName}{(_hasUnsavedChanges ? " ●" : "")}";
        }
    }

    // ── Edit Mode (transient — never persisted, resets on every new diff) ─────

    private bool _editRightEnabled;
    public bool EditRightEnabled
    {
        get => _editRightEnabled;
        set
        {
            if (!SetProperty(ref _editRightEnabled, value)) return;
            OnPropertyChanged(nameof(CanEditRight));
            if (value && !_isHexView)
                // The text editor only exists in text mode — in hex the editable surfaces are the
                // data inspector and the pane-header hex field, so don't prime (or debounce) it.
                RightEditorText = LinesToEditorText(_rightWorkingLines ?? RightFile?.Lines);
            else if (!value)
                StopEditDebounce();
        }
    }

    /// <summary>
    /// Editing needs a real two-file diff — disabled in single-file mode. Works in both text and
    /// hex (hex edits flow through ApplyRightByteEdit / the data inspector, not the text editor).
    /// </summary>
    public bool CanEditRight => LeftFile != null && RightFile != null;

    /// <summary>
    /// The right file's current bytes including pending hex edits — byte source for the data
    /// inspector's right panel and for direct-hex-entry appends.
    /// </summary>
    public byte[]? RightEditableBytes => _rightWorkingBytes ?? RightFile?.RawBytes;

    /// <summary>
    /// Text of the pane-header hex entry field (both layouts bind this one property). Parsing and
    /// commit live in the window — they need the focused viewer's byte selection.
    /// </summary>
    private string _hexEntryText = "";
    public string HexEntryText
    {
        get => _hexEntryText;
        set => SetProperty(ref _hexEntryText, value);
    }

    /// <summary>
    /// Raised after a hex display completes when ApplyRightByteEdit stashed a byte range to
    /// re-select (the fresh insert). The window maps it onto the active layout's right viewer.
    /// </summary>
    public event Action<(int StartByte, int ByteCount)>? RightByteSelectionToRestore;

    private string _rightEditorText = "";
    public string RightEditorText
    {
        get => _rightEditorText;
        set
        {
            if (!SetProperty(ref _rightEditorText, value)) return;
            if (_editRightEnabled) RestartEditDebounce();
        }
    }

    // VM-owned (not code-behind): the VM outlives its windows — a directory-diff double-click
    // recreates MainWindow around this same instance.
    private System.Windows.Threading.DispatcherTimer? _editDebounceTimer;

    private void RestartEditDebounce()
    {
        if (_editDebounceTimer == null)
        {
            _editDebounceTimer = new System.Windows.Threading.DispatcherTimer();
            _editDebounceTimer.Interval = TimeSpan.FromMilliseconds(300);
            _editDebounceTimer.Tick += (_, _) => ApplyEditorTextToWorkingCopy();
        }
        _editDebounceTimer.Stop();
        _editDebounceTimer.Start();
    }

    private void StopEditDebounce()
    {
        _editDebounceTimer?.Stop();
    }

    /// <summary>
    /// Pushes the editor text into the right working copy and re-diffs so the left pane
    /// updates live (debounced while typing). Returns false when nothing changed.
    /// </summary>
    private bool FlushEditorTextToWorkingCopy()
    {
        // Hex edits never flow through the text editor — skip if a mode switch left the debounce
        // timer alive while hex is active.
        if (!_editRightEnabled || _isHexView || RightFile == null) return false;

        var parsed = _rightEditorText.Split("\r\n").ToList();
        // Matches disk or the already-applied working copy → nothing to do. This also makes
        // programmatic refreshes (post-apply, post-save) free — no redundant re-diff.
        if (LinesEqual(parsed, RightFile.Lines)) return false;
        if (_rightWorkingLines != null && LinesEqual(parsed, _rightWorkingLines)) return false;

        _rightWorkingLines = parsed;
        HasUnsavedChanges = true;
        return true;
    }

    private void ApplyEditorTextToWorkingCopy()
    {
        if (!FlushEditorTextToWorkingCopy()) return;

        // Keep the working copy (unlike a reload) and re-diff against it — ComputeAndDisplayDiffAsync
        // already builds the synthetic right side from _rightWorkingLines.
        InvalidateCache(resetWorkingCopy: false);
        ComputeAndDisplayDiffAsync();
    }

    /// <summary>
    /// Turns edit mode off without prompting — called whenever a new diff starts (file load,
    /// open pair, swap, refresh) so the transient flag never leaks across them.
    /// </summary>
    private void ResetEditRight()
    {
        StopEditDebounce();
        EditRightEnabled = false;
        RightEditorText = "";
        OnPropertyChanged(nameof(CanEditRight));
    }

    private static string LinesToEditorText(IReadOnlyList<string>? lines)
        => lines == null ? "" : string.Join("\r\n", lines);

    private static bool LinesEqual(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i])) return false;
        return true;
    }

    // ── Diff Results (exposed for binding to viewers) ──────────────────────────

    private IReadOnlyList<DiffLine>? _fileALines;
    public IReadOnlyList<DiffLine>? FileALines
    {
        get => _fileALines;
        set => SetProperty(ref _fileALines, value);
    }

    private IReadOnlyList<DiffLine>? _fileBLines;
    public IReadOnlyList<DiffLine>? FileBLines
    {
        get => _fileBLines;
        set => SetProperty(ref _fileBLines, value);
    }

   // ── Title Bar ────────────────────────────────────────────────────────

    private string _titleSubtitle = "";
    public string TitleSubtitle
    {
        get => _titleSubtitle;
        private set => SetProperty(ref _titleSubtitle, value);
    }

    private string _titleFileInfo = "";
    public string TitleFileInfo
    {
        get => _titleFileInfo;
        private set => SetProperty(ref _titleFileInfo, value);
    }

    // ── Viewer Metadata ────────────────────────────────────────────────────────

    private string _leftTitle = "(no file loaded)";
    public string LeftTitle
    {
        get => _leftTitle;
        set => SetProperty(ref _leftTitle, value);
    }

    private string _leftMeta = "";
    public string LeftMeta
    {
        get => _leftMeta;
        set => SetProperty(ref _leftMeta, value);
    }

    private bool _leftShowNoDifferences;
    public bool LeftShowNoDifferences
    {
        get => _leftShowNoDifferences;
        set => SetProperty(ref _leftShowNoDifferences, value);
    }

    private string _rightTitle = "(no file loaded)";
    public string RightTitle
    {
        get => _rightTitle;
        set => SetProperty(ref _rightTitle, value);
    }

    private string _rightMeta = "";
    public string RightMeta
    {
        get => _rightMeta;
        set => SetProperty(ref _rightMeta, value);
    }

    private bool _rightShowNoDifferences;
    public bool RightShowNoDifferences
    {
        get => _rightShowNoDifferences;
        set => SetProperty(ref _rightShowNoDifferences, value);
    }

    // ── Unified (git-style) layout display ─────────────────────────────────────

    private IReadOnlyList<DiffLine> _unifiedLines = Array.Empty<DiffLine>();
    public IReadOnlyList<DiffLine> UnifiedLines
    {
        get => _unifiedLines;
        set => SetProperty(ref _unifiedLines, value);
    }

    // File paths for syntax language detection (bound to FastDiffViewer.FilePath)

    private string? _leftFilePath;
    public string? LeftFilePath
    {
        get => _leftFilePath;
        set => SetProperty(ref _leftFilePath, value);
    }

    private string? _rightFilePath;
    public string? RightFilePath
    {
        get => _rightFilePath;
        set => SetProperty(ref _rightFilePath, value);
    }

    private string? _unifiedFilePath;
    public string? UnifiedFilePath
    {
        get => _unifiedFilePath;
        set => SetProperty(ref _unifiedFilePath, value);
    }

    private string _unifiedTitle = "(no file loaded)";
    public string UnifiedTitle
    {
        get => _unifiedTitle;
        set => SetProperty(ref _unifiedTitle, value);
    }

    private string _unifiedMeta = "";
    public string UnifiedMeta
    {
        get => _unifiedMeta;
        set => SetProperty(ref _unifiedMeta, value);
    }

    private bool _unifiedShowNoDifferences;
    public bool UnifiedShowNoDifferences
    {
        get => _unifiedShowNoDifferences;
        set => SetProperty(ref _unifiedShowNoDifferences, value);
    }

    /// <summary>
    /// Hex inspector: display name of the currently selected data type. Set by MainWindow from
    /// HexInspector.TypeSizeChanged (the VM doesn't own the control); persisted on exit. Not a
    /// bindable setting — no recompute is triggered by it.
    /// </summary>
    public string HexInspectorTypeName { get; set; } = "int32";

    // ── UI State ───────────────────────────────────────────────────────────────

    private LayoutMode _layoutMode = LayoutMode.SideBySide;
    public LayoutMode LayoutMode
    {
        get => _layoutMode;
        set
        {
            if (_layoutMode == value) return;
            _layoutMode = value;
            // Computed bools have no backing field — raise each so XAML DataTriggers update.
            OnPropertyChanged(nameof(LayoutMode));
            OnPropertyChanged(nameof(IsSideBySide));
            OnPropertyChanged(nameof(IsTopBottom));
            OnPropertyChanged(nameof(IsUnified));
            OnPropertyChanged(nameof(IsTwoPaneLayout));
        }
    }

    /// <summary>True when the side-by-side (left|right) two-pane layout is active.</summary>
    public bool IsSideBySide => _layoutMode == LayoutMode.SideBySide;

    /// <summary>True when the top-bottom (top/bottom) two-pane layout is active.</summary>
    public bool IsTopBottom => _layoutMode == LayoutMode.TopBottom;

    /// <summary>True when the git-style single-panel layout is active.</summary>
    public bool IsUnified => _layoutMode == LayoutMode.Unified;

    /// <summary>True for either two-pane layout — sync points and scroll sync only apply there.</summary>
    public bool IsTwoPaneLayout => _layoutMode != LayoutMode.Unified;

    // Per-mode remembered values for the four mode-independent toggles. The active fields
    // (_syncScroll etc.) hold whichever mode is currently displayed: SwitchModeToggles swaps
    // them in when the view mode changes, and each setter mirrors user changes into the
    // active mode's stash so text and hex keep independent settings (persisted separately).
    private bool _textSyncScroll = true;
    private bool _hexSyncScroll = true;
    private bool _textShowGaps;
    private bool _hexShowGaps;
    private bool _textShowInlineDiffs = true;
    private bool _hexShowInlineDiffs = true;
    private bool _textTimestampMode;
    private bool _hexTimestampMode;

    private bool _syncScroll = true;
    public bool SyncScroll
    {
        get => _syncScroll;
        set
        {
            if (!SetProperty(ref _syncScroll, value)) return;
            if (_isHexView) _hexSyncScroll = value; else _textSyncScroll = value;
        }
    }

    private double? _syncScrollRatioLeft;
    public double? SyncScrollRatioLeft
    {
        get => _syncScrollRatioLeft;
        set => SetProperty(ref _syncScrollRatioLeft, value);
    }

    private double? _syncScrollRatioRight;
    public double? SyncScrollRatioRight
    {
        get => _syncScrollRatioRight;
        set => SetProperty(ref _syncScrollRatioRight, value);
    }

    private bool _showGaps;
    public bool ShowGaps
    {
        get => _showGaps;
        set
        {
            if (SetProperty(ref _showGaps, value))
            {
                if (_isHexView) _hexShowGaps = value; else _textShowGaps = value;
                UpdateSyncPointDisplayLines();
                DisplayCachedResult();
            }
        }
    }

    private bool _useStructuralAnalysis = true;
    public bool UseStructuralAnalysis
    {
        get => _useStructuralAnalysis;
        set
        {
            if (SetProperty(ref _useStructuralAnalysis, value))
            {
                ApplyStructuralStamp();
                // Same-instance reassignment is a SetProperty no-op — hand viewers fresh copies so
                // PropertyChanged fires and OnRender picks up the (re)stamped rows.
                if (_cachedDiff.HasValue && !_isHexView)
                {
                    var (aList, bList) = GetCachedLists();
                    FileALines = new List<DiffLine>(aList);
                    FileBLines = new List<DiffLine>(bList);
                    UnifiedLines = new List<DiffLine>(_cachedUnified ?? Array.Empty<DiffLine>());
                }
            }
        }
    }

    private bool _ignoreWhitespace;
    public bool IgnoreWhitespace
    {
        get => _ignoreWhitespace;
        set
        {
            if (SetProperty(ref _ignoreWhitespace, value))
            {
                ComputeAndDisplayDiffAsync();
            }
        }
    }

    private bool _showInlineDiffs = true;
    public bool ShowInlineDiffs
    {
        get => _showInlineDiffs;
        set
        {
            if (SetProperty(ref _showInlineDiffs, value))
            {
                if (_isHexView) _hexShowInlineDiffs = value; else _textShowInlineDiffs = value;
                ComputeAndDisplayDiffAsync();   // inline regions apply to text and hex rows alike
            }
        }
    }

    private bool _hexPerByte;
    public bool HexPerByte
    {
        get => _hexPerByte;
        set
        {
            if (SetProperty(ref _hexPerByte, value))
            {
                // Changing the resync mode changes which rows are Modified vs Unchanged, so the
                // hex diff must be recomputed (the cache is fingerprinted on this flag too).
                ComputeAndDisplayDiffAsync();
            }
        }
    }

    /// <summary>
    /// Hex inspector panel height in pixels. Read by MainWindow when showing the panel and
    /// updated after a separator drag; persisted like window geometry (no binding, no notify).
    /// </summary>
    public double HexInspectorHeight { get; set; } = 180;

    private bool _timestampMode;
    public bool TimestampMode
    {
        get => _timestampMode;
        set
        {
            if (SetProperty(ref _timestampMode, value))
            {
                if (_isHexView) _hexTimestampMode = value; else _textTimestampMode = value;
                // Both engines honor timestamps — the older file is always the base side — so a
                // toggle recomputes in whichever view is active.
                ComputeAndDisplayDiffAsync();
            }
        }
    }

    private bool _isHexView;
    public bool IsHexView
    {
        get => _isHexView;
        set
        {
            if (SetProperty(ref _isHexView, value))
            {
                // Hex lists are null-padded per file, not row-aligned — the unified layout can't
                // render them, so entering hex snaps back to a two-pane layout.
                if (value && _layoutMode == LayoutMode.Unified)
                    SetLayout(LayoutMode.SideBySide);
                // Editing works in both modes — materialize the pending working copy into the
                // target mode's representation so unsaved edits survive the switch.
                MaterializeWorkingCopyForMode(value);
                OnPropertyChanged(nameof(CanEditRight));
                // Each mode keeps its own toggle settings — swap in the target mode's values
                // before recomputing so the new view is displayed with them.
                SwitchModeToggles(toHex: value);
                OnPropertyChanged(nameof(IsTextView));
                // Sync-point count/visibility are mode-aware — re-raise for whichever list is active now
                OnPropertyChanged(nameof(SyncPointCount));
                OnPropertyChanged(nameof(HasSyncPoints));
                ComputeAndDisplayDiffAsync();
            }
        }
    }

    /// <summary>
    /// Keeps at most one of _rightWorkingLines/_rightWorkingBytes non-null across a mode switch:
    /// text→hex encodes the line working copy to bytes (same encoding Save writes); hex→text
    /// decodes the byte working copy back into lines. No-op when nothing is pending.
    /// </summary>
    private void MaterializeWorkingCopyForMode(bool toHex)
    {
        if (toHex && _rightWorkingLines != null)
        {
            _rightWorkingBytes = GetEncodingForFile().GetBytes(string.Join("\n", _rightWorkingLines));
            _rightWorkingLines = null;
        }
        else if (!toHex && _rightWorkingBytes != null)
        {
            _rightWorkingLines = FileService.DecodeLines(_rightWorkingBytes);
            _rightWorkingBytes = null;
        }
        else
            return;

        StopEditDebounce();   // the editor's content is about to change out from under it
        _pendingByteRestore = null;
        if (_editRightEnabled && !toHex)
            RightEditorText = LinesToEditorText(_rightWorkingLines);
    }

    /// <summary>True when the text view is active (inverse of IsHexView) — shows text-only toolbar items.</summary>
    public bool IsTextView => !_isHexView;

    private DiffAlgorithm _diffAlgorithm = DiffAlgorithm.Patience;
    public DiffAlgorithm DiffAlgorithm
    {
        get => _diffAlgorithm;
        set
        {
            if (SetProperty(ref _diffAlgorithm, value))
            {
                ComputeAndDisplayDiffAsync();
            }
        }
    }

    // ── Status Bar ─────────────────────────────────────────────────────────────

    private string _statusLeft = "◈ (no left file)";
    public string StatusLeft
    {
        get => _statusLeft;
        set
        {
            var changed = SetProperty(ref _statusLeft, value);
            System.Diagnostics.Debug.WriteLine($"[StatusLeft] \"{value?.Substring(0, Math.Min(40, value.Length))}\" PropertyChanged={changed}");
        }
    }

    private string _statusRight = "◈ (no right file)";
    public string StatusRight
    {
        get => _statusRight;
        set
        {
            var changed = SetProperty(ref _statusRight, value);
            System.Diagnostics.Debug.WriteLine($"[StatusRight] \"{value?.Substring(0, Math.Min(40, value.Length))}\" PropertyChanged={changed}");
        }
    }

    private string _statusInfo = "";
    public string StatusInfo
    {
        get => _statusInfo;
        set
        {
            var changed = SetProperty(ref _statusInfo, value);
            System.Diagnostics.Debug.WriteLine($"[StatusInfo] \"{value?.Substring(0, Math.Min(60, value?.Length ?? 0))}\" PropertyChanged={changed}");
        }
    }

    // ── Progress Overlay State ─────────────────────────────────────────────────

    private bool _isComputing;
    public bool IsComputing
    {
        get => _isComputing;
        private set => SetProperty(ref _isComputing, value);
    }

    private double _progressValue;
    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value);
    }

    private string _progressText = "";
    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    // Delayed progress overlay: only show if computation takes > 0.5 s
    private System.Threading.Timer? _progressTimer;
    private bool _progressOverlayVisible;

    // Cancel button: revealed once a hex Per-Byte computation has been running for > 1 s
    private System.Threading.Timer? _cancelButtonTimer;
    private bool _showCancelButton;
    public bool ShowCancelButton
    {
        get => _showCancelButton;
        private set => SetProperty(ref _showCancelButton, value);
    }

    // ── Diff Result Cache ──────────────────────────────────────────────────────

    private (IReadOnlyList<DiffLine> A, IReadOnlyList<DiffLine> B)? _cachedDiff;
    private (IReadOnlyList<DiffLine> A, IReadOnlyList<DiffLine> B)? _cachedInterleaved;
    private IReadOnlyList<DiffLine>? _cachedUnified;   // git-style list built from the interleaved pair
    /// <summary>
    /// Structural-analysis findings for the cached text diff (null = none / unsupported type).
    /// Recomputed at every cache-building site; stamped onto rows by ApplyStructuralStamp.
    /// </summary>
    private List<StructuralFinding>? _cachedFindings;
    // The standard slots hold hex rows while hex view is active — the text cache-check must
    // not treat them as a valid text result (fingerprint fields are stale across the switch).
    private bool _cacheIsHex;
    private int _cachedDiffCountA;
    private int _cachedDiffCountB;
    private bool _cachedIgnoreWhitespace;
    private bool _cachedUseTimestamps;
    private bool _cachedShowInlineDiffs;
    private DiffAlgorithm _cachedDiffAlgorithm = DiffAlgorithm.Patience;

    // Hex diff cache — the byte-aligned row lists plus how many segments fell back to
    // positional display. Fingerprinted by RawBytes reference identity + lengths + the inline
    // flag + timestamp direction: FileService allocates fresh arrays on every load/refresh, so
    // reference equality is a reliable "same bytes" test without hashing megabytes. Swapped
    // records whether the inputs were exchanged for timestamp mode (older file as base).
    private (byte[] ARef, byte[] BRef, bool Inline, bool PerByte, bool Swapped, IReadOnlyList<DiffLine> AlignedA, IReadOnlyList<DiffLine> AlignedB, IReadOnlyList<DiffLine> FixedA, IReadOnlyList<DiffLine> FixedB, int FallbackSegments)? _hexCache;

    /// <summary>True when the hex cache was computed from exactly these byte arrays, inline + per-byte settings, and timestamp direction.</summary>
    private bool HexCacheValid(byte[] aBytes, byte[] bBytes, bool swapped) =>
        _hexCache.HasValue &&
        ReferenceEquals(_hexCache.Value.ARef, aBytes) &&
        ReferenceEquals(_hexCache.Value.BRef, bBytes) &&
        _hexCache.Value.Inline == _showInlineDiffs &&
        _hexCache.Value.PerByte == _hexPerByte &&
        _hexCache.Value.Swapped == swapped;

    // ── Cancel Token for In-Flight Computation ─────────────────────────────────

    private CancellationTokenSource? _pendingCts;

    //****************************************************************
    //  DEBUG TESTING
    // ── Swap Performance Timing (temp debug utility) ───────────────────────────

    private Stopwatch? _swapStopwatch;

    /// <summary>
    /// If a swap timing measurement is in progress, stop it and log to console.
    /// Schedules the stop at Render priority so we capture UI render time too.
    /// Call from display completion points. (Temp debug utility — remove when done.)
    /// </summary>
    private void LogSwapTimeIfMeasuring()
    {
        if (_swapStopwatch == null) return;

        var sw = _swapStopwatch;
        _swapStopwatch = null;

        // Schedule at Render priority so we capture time until WPF has rendered
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            sw.Stop();
            Console.WriteLine($"[SWAP TIMING] Total: {sw.ElapsedMilliseconds}ms ({sw.Elapsed.TotalMilliseconds:F1}ms)");
        }, System.Windows.Threading.DispatcherPriority.Render);
    }
    //  DEBUG TESTING
    //****************************************************************

    // ── Sync Points ────────────────────────────────────────────────────────────

    /// <summary>
    /// User-defined sync points that partition the diff into independent segments.
    /// Stored as 1-based original line numbers (LineA, LineB).
    /// </summary>
    private List<(int LineA, int LineB)> _syncPoints = new();

    public IList<(int LineA, int LineB)> SyncPoints => _syncPoints.AsReadOnly();

    /// <summary>
    /// User-defined byte-level sync points for hex view. Stored as 0-based file offsets
    /// (ByteA, ByteB). Independent of the text-mode line list; unlike text mode, any two
    /// bytes may be paired — no equality requirement (the set must stay co-monotone).
    /// </summary>
    private List<(int ByteA, int ByteB)> _hexSyncPoints = new();

    public IList<(int ByteA, int ByteB)> HexSyncPoints => _hexSyncPoints.AsReadOnly();

    private int _syncPointCount;
    public int SyncPointCount
    {
        // Mode-aware: reports whichever list is active in the current view.
        get => _isHexView ? _hexSyncPoints.Count : _syncPointCount;
        private set
        {
            if (SetProperty(ref _syncPointCount, value))
            {
                OnPropertyChanged(nameof(HasSyncPoints));
            }
        }
    }

    /// <summary>True when there is at least one active sync point in the current view mode.</summary>
    public bool HasSyncPoints => SyncPointCount > 0;

    /// <summary>Raise count/visibility for hex additions (the backing field tracks text mode only).</summary>
    private void RaiseHexSyncCountChanged()
    {
        OnPropertyChanged(nameof(SyncPointCount));
        OnPropertyChanged(nameof(HasSyncPoints));
    }

    // Pending sync line: when the user right-clicks a line to propose a sync point,
    // the opposite viewer highlights this line index as pending confirmation.
    private int _pendingSyncLineA = -1;
    public int PendingSyncLineA
    {
        get => _pendingSyncLineA;
        set => SetProperty(ref _pendingSyncLineA, value);
    }

    private int _pendingSyncLineB = -1;
    public int PendingSyncLineB
    {
        get => _pendingSyncLineB;
        set => SetProperty(ref _pendingSyncLineB, value);
    }

    // Display-line indices of active sync points in each file's diff list.
    // Populated after diff computation so the viewer can render link markers.
    private IReadOnlyList<int> _syncPointLinesA = Array.Empty<int>();
    public IReadOnlyList<int> SyncPointLinesA
    {
        get => _syncPointLinesA;
        private set => SetProperty(ref _syncPointLinesA, value);
    }

    private IReadOnlyList<int> _syncPointLinesB = Array.Empty<int>();
    public IReadOnlyList<int> SyncPointLinesB
    {
        get => _syncPointLinesB;
        private set => SetProperty(ref _syncPointLinesB, value);
    }

    // ── Commands ───────────────────────────────────────────────────────────────

    public ICommand OpenLeftFileCommand { get; }
    public ICommand OpenRightFileCommand { get; }
    public ICommand SwapFilesCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand CancelComputationCommand { get; }
    public ICommand ToggleLayoutCommand { get; }
    public ICommand ToggleViewModeCommand { get; }
    public ICommand SaveRightFileCommand { get; }
    public ICommand SaveAsRightFileCommand { get; }
    public ICommand DiscardChangesCommand { get; }
    public ICommand ClearSyncPointsCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand StartMergeCommand { get; }

    // ── Constructor ────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the view model. Optional file paths (from the CLI) are loaded into the left and
    /// right panels after settings are restored — the same path a user pick via File → Open takes.
    /// </summary>
    public MainWindowViewModel(string? initialLeftFile = null, string? initialRightFile = null)
    {
        OpenLeftFileCommand = new RelayCommand(OpenLeftFile);
        OpenRightFileCommand = new RelayCommand(OpenRightFile);
        SwapFilesCommand = new RelayCommand(SwapFiles);
        RefreshCommand = new RelayCommand(RefreshFiles);
        CancelComputationCommand = new RelayCommand(() => _pendingCts?.Cancel());
 ToggleLayoutCommand = new RelayCommand<object>(p =>
        {
            // Toolbar buttons pass a mode name ("SideBySide"/"TopBottom"/"Unified");
            // F11 passes null and cycles through all three modes.
            if (p is string s && Enum.TryParse<LayoutMode>(s, out var target))
                SetLayout(target);
            else
                CycleLayout();
        });

        ToggleViewModeCommand = new RelayCommand(() => { IsHexView = !_isHexView; });
        SaveRightFileCommand = new RelayCommand(SaveRightFile);
        SaveAsRightFileCommand = new RelayCommand(SaveAsRightFile);
        DiscardChangesCommand = new RelayCommand(DiscardChanges);
        ClearSyncPointsCommand = new RelayCommand(ClearSyncPoints);
        SettingsCommand = new RelayCommand(OpenSettings);
        StartMergeCommand = new RelayCommand(StartMerge);

        LoadSettings();

        // Files passed on the command line (parsed by App) load through the same path as
        // File → Open, including its failure handling.
        if (!string.IsNullOrWhiteSpace(initialLeftFile))
            LoadFileIntoPanel(initialLeftFile, isLeft: true);
        if (!string.IsNullOrWhiteSpace(initialRightFile))
            LoadFileIntoPanel(initialRightFile, isLeft: false);
    }

    // ── Settings Persistence ───────────────────────────────────────────────────

    /// <summary>
    /// Swap the active toggle values when switching between text and hex views: stash the
    /// leaving mode's current values (defensive — the setters already mirror every change)
    /// and load the entering mode's remembered ones. IgnoreWhitespace is text-only and stays.
    /// </summary>
    private void SwitchModeToggles(bool toHex)
    {
        if (toHex)
        {
            _textSyncScroll = _syncScroll;
            _textShowGaps = _showGaps;
            _textShowInlineDiffs = _showInlineDiffs;
            _textTimestampMode = _timestampMode;

            _syncScroll = _hexSyncScroll;
            _showGaps = _hexShowGaps;
            _showInlineDiffs = _hexShowInlineDiffs;
            _timestampMode = _hexTimestampMode;
        }
        else
        {
            _hexSyncScroll = _syncScroll;
            _hexShowGaps = _showGaps;
            _hexShowInlineDiffs = _showInlineDiffs;
            _hexTimestampMode = _timestampMode;

            _syncScroll = _textSyncScroll;
            _showGaps = _textShowGaps;
            _showInlineDiffs = _textShowInlineDiffs;
            _timestampMode = _textTimestampMode;
        }

        OnPropertyChanged(nameof(SyncScroll));
        OnPropertyChanged(nameof(ShowGaps));
        OnPropertyChanged(nameof(ShowInlineDiffs));
        OnPropertyChanged(nameof(TimestampMode));
    }

    private void LoadSettings()
    {
        var s = SettingsService.Load();
        // Legacy files have no layoutMode (deserializes to null) — fall back to the old bool.
        _layoutMode = Enum.TryParse<LayoutMode>(s.LayoutMode, out var lm)
            ? lm
            : (s.IsSideBySide ? LayoutMode.SideBySide : LayoutMode.TopBottom);
        // Per-mode toggles: a null per-mode value means the settings file predates them —
        // seed that mode from the legacy shared value. Gaps only display with sync scroll on.
        bool textSync = s.TextSyncScroll ?? s.SyncScroll;
        bool hexSync = s.HexSyncScroll ?? s.SyncScroll;
        _textSyncScroll = textSync;
        _hexSyncScroll = hexSync;
        _textShowGaps = (s.TextShowGaps ?? s.ShowGaps) && textSync;
        _hexShowGaps = (s.HexShowGaps ?? s.ShowGaps) && hexSync;
        _textShowInlineDiffs = s.TextShowInlineDiffs ?? s.ShowInlineDiffs;
        _hexShowInlineDiffs = s.HexShowInlineDiffs ?? s.ShowInlineDiffs;
        _textTimestampMode = s.TextTimestampMode ?? s.TimestampMode;
        _hexTimestampMode = s.HexTimestampMode ?? s.TimestampMode;

        _isHexView = s.HexView;
        // Active values = the entering (persisted) mode's remembered settings
        _syncScroll = _isHexView ? _hexSyncScroll : _textSyncScroll;
        _showGaps = _isHexView ? _hexShowGaps : _textShowGaps;
        _ignoreWhitespace = s.IgnoreWhitespace;   // text-only, single value
        _useStructuralAnalysis = s.UseStructuralAnalysis;   // text-only, single value
        _showInlineDiffs = _isHexView ? _hexShowInlineDiffs : _textShowInlineDiffs;
        _timestampMode = _isHexView ? _hexTimestampMode : _textTimestampMode;
        _hexPerByte = s.HexPerByte;
        // Clamp: a corrupt/hand-edited value must not collapse the panel or swallow the panes.
        HexInspectorHeight = Math.Clamp(s.HexInspectorHeight, 60, 1200);
        _diffAlgorithm = Enum.TryParse<DiffAlgorithm>(s.DiffAlgorithm, out var alg) ? alg : DiffAlgorithm.Patience;

        // Register per-theme custom colours before applying the theme so the built theme
        // includes them (SetCustomColors re-applies immediately if its theme is already active).
        foreach (var kv in s.CustomColors)
            ThemeManager.SetCustomColors(kv.Key, kv.Value);

        // Apply theme and font scale (FastDiffViewer brushes + UI resources)
        ThemeManager.ApplyTheme(s.ThemeName, s.FontScale);

        // Apply syntax coloring toggle (propagates to all live viewer instances)
        ThemeManager.SetSyntaxColoring(s.UseSyntaxColoring);

        // Notify for all settings so bindings pick them up
        OnPropertyChanged(nameof(LayoutMode));
        OnPropertyChanged(nameof(IsSideBySide));
        OnPropertyChanged(nameof(IsTopBottom));
        OnPropertyChanged(nameof(IsUnified));
        OnPropertyChanged(nameof(IsTwoPaneLayout));
        OnPropertyChanged(nameof(SyncScroll));
        OnPropertyChanged(nameof(ShowGaps));
        OnPropertyChanged(nameof(IgnoreWhitespace));
        OnPropertyChanged(nameof(UseStructuralAnalysis));
        OnPropertyChanged(nameof(ShowInlineDiffs));
        OnPropertyChanged(nameof(HexPerByte));
        OnPropertyChanged(nameof(TimestampMode));
        OnPropertyChanged(nameof(IsHexView));
        OnPropertyChanged(nameof(IsTextView));
        OnPropertyChanged(nameof(DiffAlgorithm));
    }

    /// <summary>
    /// Called by MainWindow on close — persist settings and cancel pending work.
    /// </summary>
    public void SaveAndCleanup()
    {
        // Load existing settings (preserves window geometry saved by MainWindow)
        // and update the toggle values.
        var s = SettingsService.Load();
        s.LayoutMode = _layoutMode.ToString();
        s.IsSideBySide = _layoutMode == LayoutMode.SideBySide;   // legacy compat for older builds
        // Per-mode toggle values (always non-null once saved)
        s.TextSyncScroll = _textSyncScroll;
        s.TextShowGaps = _textShowGaps;
        s.TextShowInlineDiffs = _textShowInlineDiffs;
        s.TextTimestampMode = _textTimestampMode;
        s.HexSyncScroll = _hexSyncScroll;
        s.HexShowGaps = _hexShowGaps;
        s.HexShowInlineDiffs = _hexShowInlineDiffs;
        s.HexTimestampMode = _hexTimestampMode;

        // Legacy shared fields — keep writing (active mode's values) for older builds
        s.SyncScroll = _syncScroll;
        s.ShowGaps = _showGaps;
        s.IgnoreWhitespace = _ignoreWhitespace;
        s.UseStructuralAnalysis = _useStructuralAnalysis;
        s.ShowInlineDiffs = _showInlineDiffs;
        s.HexPerByte = _hexPerByte;
        s.TimestampMode = _timestampMode;
        s.HexView = _isHexView;
        s.DiffAlgorithm = _diffAlgorithm.ToString();
        s.UseSyntaxColoring = ThemeManager.UseSyntaxColoring;
        s.HexInspectorType = HexInspectorTypeName;
        s.HexInspectorHeight = HexInspectorHeight;

        SettingsService.Save(s);
        _pendingCts?.Cancel();
        _pendingCts?.Dispose();
        StopEditDebounce();   // edit mode is transient — the timer dies with this cleanup
    }

    /// <summary>
    /// Opens the Settings dialog as a modal window.
    /// </summary>
    public void OpenSettings()
    {
        var dialog = new SettingsDialog();
        if (dialog.ShowDialog() == true)
        {
            // The dialog persists the engine choice itself; sync it here so a changed value
            // goes through the property setter and triggers a recompute. Assigning the same
            // value is a no-op (SetProperty), so an unchanged selection does not re-diff.
            var s = SettingsService.Load();
            if (Enum.TryParse<DiffAlgorithm>(s.DiffAlgorithm, out var alg))
                DiffAlgorithm = alg;
        }
    }

    /// <summary>
    /// Returns true if the user chose to proceed (save or discard). Call before loading a new file.
    /// Shows a dialog asking Save / Don't Save / Cancel. Returns false if user cancelled.
    /// </summary>
    public bool PromptSaveIfNeeded()
    {
        // Flush in-flight editor typing first — otherwise a prompt within the debounce window
        // would ask about (and save/discard) text that hasn't reached the working copy yet.
        if (_editRightEnabled) FlushEditorTextToWorkingCopy();

        if (!_hasUnsavedChanges) return true;

        var result = System.Windows.MessageBox.Show(
            $"You have unsaved changes to \"{RightFile?.DisplayName}\".\n\n" +
            "Would you like to save them before continuing?",
            "Unsaved Changes",
            System.Windows.MessageBoxButton.YesNoCancel,
            System.Windows.MessageBoxImage.Question);

        switch (result)
        {
            case System.Windows.MessageBoxResult.Yes:
                SaveRightFile();
                return true;
            case System.Windows.MessageBoxResult.No:
                return true;   // discard, proceed
            default:
                return false;  // Cancel — abort the load
        }
    }

    /// <summary>
    /// Called by MainWindow.OnClosing. Returns true if the window is allowed to close.
    /// If there are unsaved changes, shows a dialog (Save / Don't Save / Cancel).
    /// </summary>
    public bool HandleClosing()
    {
        // Same debounce guard as PromptSaveIfNeeded — a close within 300 ms of the last
        // keystroke must still see those keystrokes.
        if (_editRightEnabled) FlushEditorTextToWorkingCopy();

        if (!_hasUnsavedChanges) return true;

        var result = System.Windows.MessageBox.Show(
            $"You have unsaved changes to \"{RightFile?.DisplayName}\".\n\n" +
            "Do you want to save before exiting?",
            "Unsaved Changes",
            System.Windows.MessageBoxButton.YesNoCancel,
            System.Windows.MessageBoxImage.Question);

        switch (result)
        {
            case System.Windows.MessageBoxResult.Yes:
                SaveRightFile();
                // If save still has unsaved changes (e.g. SaveAs cancelled), block close
                return !_hasUnsavedChanges;
            case System.Windows.MessageBoxResult.No:
                return true;   // discard and exit
            default:
                return false;  // Cancel — stay open
        }
    }

    /// <summary>
    /// Entry point for the optional 3-way merge: settles any pending 2-way edits, collects the
    /// three files (base + A + B) via MergeSetupDialog, then opens the merge window and closes
    /// this one. Aborts in place if the user cancels or a file fails to load.
    /// </summary>
    private void StartMerge()
    {
        // Resolve any pending 2-way edits before leaving (Cancel aborts).
        if (!PromptSaveIfNeeded()) return;

        // A "don't save" answer above leaves the dirty flag set — clear it so closing the
        // window at the end doesn't trigger the close-time prompt a second time.
        if (_hasUnsavedChanges)
        {
            _rightWorkingLines = null;
            _rightWorkingBytes = null;
            _pendingByteRestore = null;
            HasUnsavedChanges = false;
        }

        // Leaving the 2-way view — edit mode is transient and must not leak into whatever
        // window this VM next drives.
        ResetEditRight();

        var dlg = new MergeSetupDialog(LeftFile?.FilePath, RightFile?.FilePath);
        if (dlg.ShowDialog() != true) return;

        var baseFile = FileService.LoadFile(dlg.SelectedBase);
        var fileA = FileService.LoadFile(dlg.SelectedA);
        var fileB = FileService.LoadFile(dlg.SelectedB);
        if (baseFile == null || fileA == null || fileB == null)
        {
            System.Windows.MessageBox.Show(
                "Failed to load one or more of the selected files.\nPlease check that the paths exist and are readable.",
                "3-Way Merge", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return;   // stay in the 2-way window
        }

        // Show the merge window BEFORE closing this one — the app uses OnLastWindowClose, so
        // reversing the order would exit the process.
        new ThreeWayMergeWindow(new MergeWindowViewModel(baseFile, fileA, fileB)).Show();
        System.Windows.Application.Current.MainWindow?.Close();
    }

    // ── Commands Implementation ────────────────────────────────────────────────

    private void OpenLeftFile()
    {
        var path = OpenFileDialog();
        if (path != null)
        {
            PromptSaveIfNeeded();
            LoadFileIntoPanel(path, isLeft: true);
        }
    }

    private void OpenRightFile()
    {
        var path = OpenFileDialog();
        if (path != null)
        {
            PromptSaveIfNeeded();
            LoadFileIntoPanel(path, isLeft: false);
        }
    }

    private string? OpenFileDialog()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Text Files|*.txt;*.cs;*.vb;*.xml;*.json;*.yaml;*.yml;*.md;*.ini;*.cfg|" +
                     "Source Files|*.cs;*.vb;*.cpp;*.h;*.hpp;*.js;*.ts;*.py;*.java;*.rs;*.go|" +
                     "Data Files|*.csv;*.sql;*.xml;*.json;*.yaml;*.yml;*.toml|" +
                     "All Files|*.*",
            Title = "Select a file to compare"
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>
    /// Public method for loading files from drag-and-drop (called by MainWindow code-behind).
    /// </summary>
    public void LoadFileIntoPanel(string filePath, bool isLeft)
    {
        var file = FileService.LoadFile(filePath);
        if (file == null)
        {
            System.Windows.MessageBox.Show(
                $"Could not load file:\n{filePath}",
                "Load Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            return;
        }

        if (isLeft) LeftFile = file; else RightFile = file;

        // Pick the view mode from the file itself: known text/code types and files without null
        // bytes open in the text view; only genuinely binary content with an unknown extension
        // forces hex. This also flips a remembered hex view back to text when a text file loads.
        IsHexView = file.RequiresHexView();

        // Loading a new file invalidates existing sync points (both view modes)
        _syncPoints.Clear();
        SyncPointCount = 0;
        if (_hexSyncPoints.Count > 0)
        {
            _hexSyncPoints.Clear();
            RaiseHexSyncCountChanged();
        }

        // A loaded file is a new diff — edit mode resets (and the checkbox re-evaluates).
        ResetEditRight();

        InvalidateCache();
        ComputeAndDisplayDiffAsync();
    }

    /// <summary>
    /// Opens the directory diff window (non-modal, like the merge window). By default the folder
    /// fields are prefilled with the last compared pair so the window reopens with its previous
    /// contents; before any compare has been recorded (first run) they fall back to the open
    /// files' directories. With <paramref name="useOpenFileDirs"/> (Ctrl held on the toolbar
    /// button) the parent folders of the two open files are used instead, even when a recent
    /// pair exists. Opening a second window doesn't replace any files, so no save prompt is
    /// needed here.
    /// </summary>
    public void OpenDirectories(bool useOpenFileDirs = false)
    {
        string? leftDir = null, rightDir = null;

        if (useOpenFileDirs && LeftFile != null && RightFile != null)
        {
            // Ctrl-click: the open files' parent folders beat the recent pair. A side with no
            // usable directory falls through to the default below.
            leftDir  = Path.GetDirectoryName(LeftFile.FilePath);
            rightDir = Path.GetDirectoryName(RightFile.FilePath);
        }

        if (leftDir == null || rightDir == null)
        {
            // The recent pair wins even when files are open — deriving from the open files' folders
            // would otherwise mask the previously compared directories on every launch (CLI VCS
            // integrations always start with files loaded). Paths stay editable and drag-drop still
            // overrides; the VM auto-compares when both prefilled paths exist.
            var recent = SettingsService.Load().DirDiffRecentDirs;
            if (recent.Count > 0)
            {
                leftDir  = recent[0].Left;
                rightDir = recent[0].Right;
            }
            else
            {
                leftDir  = LeftFile  != null ? Path.GetDirectoryName(LeftFile.FilePath)  : null;
                rightDir = RightFile != null ? Path.GetDirectoryName(RightFile.FilePath) : null;
            }
        }

        new DirectoryDiffsWindow(this, leftDir, rightDir).Show();
    }

    /// <summary>
    /// Opens a file pair in this window (called by the directory diff window on double-click).
    /// A null side clears that panel — a one-sided-only file opens alone with the counterpart
    /// showing "(no file loaded)". Returns false if aborted (unsaved-changes Cancel or an
    /// unreadable file).
    /// </summary>
    public bool OpenFilePair(string? pathA, string? pathB)
    {
        if (pathA == null && pathB == null) return false;

        // Gate on pending edits in the right panel (Cancel aborts).
        if (!PromptSaveIfNeeded()) return false;

        FileModel? a = pathA != null ? FileService.LoadFile(pathA) : null;
        FileModel? b = pathB != null ? FileService.LoadFile(pathB) : null;
        if ((pathA != null && a == null) || (pathB != null && b == null))
        {
            System.Windows.MessageBox.Show(
                $"Could not load file:\n{(a == null ? pathA : pathB)}",
                "Load Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            return false;
        }

        // Assigning null through the (private) setters clears that panel.
        LeftFile = a;
        RightFile = b;

        // Pick the view mode from the loaded files (mirrors LoadFileIntoPanel): hex only when a
        // side is genuinely binary with an unknown extension. Known text/code types and
        // non-binary content open in the text view, flipping a remembered hex view back off.
        IsHexView = (a != null && a.RequiresHexView()) || (b != null && b.RequiresHexView());

        // Opening new files invalidates existing sync points (both view modes).
        _syncPoints.Clear();
        SyncPointCount = 0;
        if (_hexSyncPoints.Count > 0)
        {
            _hexSyncPoints.Clear();
            RaiseHexSyncCountChanged();
        }

        // The recompute is what resets the cleared panel's rows/titles — never skip it: with zero
        // files ComputeAndDisplayDiffAsync only updates the status and leaves stale rows on
        // screen, and with one file DisplaySingleFile resets the empty slot. InvalidateCache's
        // default resetWorkingCopy also clears any dirty state left by a "don't save" answer.
        // A new pair is a new diff — edit mode resets (critical for the directory-diff path,
        // which recreates the window around this same VM).
        ResetEditRight();

        InvalidateCache();
        ComputeAndDisplayDiffAsync();
        return true;
    }

   private void SwapFiles()
    {
        // A swap is a new diff — edit mode resets before the sides exchange places.
        ResetEditRight();

        // Start timing for swap performance measurement (temp debug)
        _swapStopwatch = Stopwatch.StartNew();

        var temp = LeftFile;
        LeftFile = RightFile;
        RightFile = temp;

        // Swap sync points (both modes) to match the swapped files
        _syncPoints = _syncPoints.Select(sp => (sp.LineB, sp.LineA)).ToList();
        _hexSyncPoints = _hexSyncPoints.Select(sp => (sp.ByteB, sp.ByteA)).ToList();

        // If we have a cached diff result and timestamp mode is not active,
        // flip statuses in-place instead of recomputing the entire diff.
        // This makes swaps instant — no background computation needed.
        // Works in gap mode too: FindMatches canonicalizes its argument order, so a fresh
        // recompute with reversed sides is always the exact mirror of the cached result and
        // the flip (verified by the swap-equivalence tests in AwfulDiff.Tests) matches it.
        // Skip flip when:
        // - timestamp mode is cached (direction determined by file dates, not left/right — both
        //   text and hex honor it, so a pane swap must recompute to keep the older file as base)
        // - working copy exists (cached diff was computed against modified right file, not original)
        if (_cachedDiff.HasValue && _rightWorkingLines == null && !_cachedUseTimestamps)
        {
            var (aList, bList) = _cachedDiff.Value;

            // Flip statuses in-place: Removed ↔ Added, Modified stays Modified
            FlipDiffListStatuses(aList);
            FlipDiffListStatuses(bList);

            // The interleaved (gap) cache must flip too — it holds the same lines with Gap
            // placeholders. Gap entries keep their status and stay in their own file's list,
            // so flipping + swapping is exactly equivalent to a fresh recompute (verified by
            // the swap-equivalence tests in AwfulDiff.Tests). If we don't do this, enabling gap
            // view or "Apply to Right" after a swap displays the pre-swap lists — wrong files,
            // wrong line numbers.
            if (_cachedInterleaved.HasValue)
            {
                var (intA, intB) = _cachedInterleaved.Value;
                FlipDiffListStatuses(intA);
                FlipDiffListStatuses(intB);
                _cachedInterleaved = (intB, intA);
                // Hex is always two-pane — there is no unified list to rebuild.
                _cachedUnified = _isHexView
                    ? Array.Empty<DiffLine>()
                    : DiffEngine.BuildUnified(intB, intA);
            }

            // Swap cached diff counts to match the swapped sides
            (_cachedDiffCountA, _cachedDiffCountB) = (_cachedDiffCountB, _cachedDiffCountA);

            // The hex cache fingerprint names the byte arrays per side — re-point it at the
            // swapped files so subsequent hex recomputes don't treat the flip as stale.
            if (_hexCache.HasValue && LeftFile != null && RightFile != null)
                _hexCache = (LeftFile.RawBytes, RightFile.RawBytes, _hexCache.Value.Inline, _hexCache.Value.PerByte, _hexCache.Value.Swapped,
                             _hexCache.Value.AlignedB, _hexCache.Value.AlignedA,
                             _hexCache.Value.FixedB, _hexCache.Value.FixedA,
                             _hexCache.Value.FallbackSegments);

            // Swap the cached tuple so the cache is internally consistent with the swap.
            // This ensures DisplayCachedResult returns the correct lists and that every
            // subsequent swap produces different list references (so PropertyChanged fires).
            _cachedDiff = (bList, aList);

            DisplayCachedResult();
            UpdateSyncPointDisplayLines();
            return;
        }

        InvalidateCache();
        ComputeAndDisplayDiffAsync();
    }

    /// <summary>
    /// Flip diff statuses in a list in-place when swapping sides.
    /// Removed ↔ Added, Modified keeps all properties as-is (Text is this file's content,
    /// OriginalText is the paired other file's text, ChangedRegions highlights regions in Text
    /// that differ from OriginalText — none of these change after a swap because each line
    /// stays with its file and the list swap puts it on the correct viewer).
    /// Unchanged/Gap stay the same.
    /// </summary>
    private static void FlipDiffListStatuses(IReadOnlyList<DiffLine> list)
    {
        for (int i = 0; i < list.Count; i++)
        {
            var line = list[i];
            switch (line.Status)
            {
                case DiffStatus.Removed:
                    line.Status = DiffStatus.Added;
                    break;
                case DiffStatus.Added:
                    line.Status = DiffStatus.Removed;
                    break;
                case DiffStatus.Modified:
                    // No change needed. Text is this file's content (stays with the file).
                    // OriginalText is the paired other file's text (stays as-is).
                    // ChangedRegions highlights regions in Text differing from OriginalText
                    // (still valid — neither changed).
                    break;
            }
        }
    }

    private void RefreshFiles()
    {
        if (LeftFile?.FilePath != null)
        {
            var refreshed = FileService.LoadFile(LeftFile.FilePath);
            if (refreshed != null) LeftFile = refreshed;
        }

        if (RightFile?.FilePath != null)
        {
            var refreshed = FileService.LoadFile(RightFile.FilePath);
            if (refreshed != null) RightFile = refreshed;
        }

        // A refresh reloads both files from disk — a new diff, so edit mode resets.
        ResetEditRight();

        InvalidateCache();
        ComputeAndDisplayDiffAsync();
    }

   /// <summary>
    /// Switches to the given layout. No-op when already there (button click on the active
    /// mode) or when asking for Unified while hex view is active (unified can't render hex).
    /// </summary>
    private void SetLayout(LayoutMode mode)
    {
        if (mode == LayoutMode.Unified && _isHexView) return;
        if (_layoutMode == mode) { UpdateStatus(); return; }
        LayoutMode = mode;
        UpdateStatus();
    }

    /// <summary>
    /// F11: cycle SideBySide → TopBottom → Unified → … In hex view only the two-pane
    /// layouts are reachable, so it toggles between them.
    /// </summary>
    private void CycleLayout()
    {
        LayoutMode next = _layoutMode switch
        {
            LayoutMode.SideBySide => LayoutMode.TopBottom,
            LayoutMode.TopBottom => _isHexView ? LayoutMode.SideBySide : LayoutMode.Unified,
            _ => LayoutMode.SideBySide // Unified only reachable in text view
        };
        SetLayout(next);
    }

    /// <summary>Display name for the status bar.</summary>
    private string LayoutName => _layoutMode switch
    {
        LayoutMode.SideBySide => "Side-by-Side",
        LayoutMode.TopBottom => "Top-Bottom",
        _ => "Unified"
    };

    // Layout name last written into StatusInfo — UpdateStatus's Replace needs the *previous*
    // value to find the text it's replacing, and with three modes there's no way to derive it
    // from the current one (the old two-state code inverted a bool).
    private string _statusLayoutName = "Side-by-Side";

    // ── Sync Points ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Add a sync point that declares lineA in file A matches lineB in file B.
    /// Lines are 1-based original line numbers. After adding, the diff is recomputed.
    /// </summary>
    public void AddSyncPoint(int lineA, int lineB)
    {
        if (LeftFile == null || RightFile == null) return;

        // Use working copy line count for bounds when changes have been applied;
        // OriginalLineNumber values in the diff then refer to the working copy, not
        // the original on-disk file.
        int rightLineCount = _rightWorkingLines != null
            ? _rightWorkingLines.Count
            : RightFile.Lines.Count;

        // Validate: line numbers must be within bounds
        if (lineA < 1 || lineA > LeftFile.Lines.Count) return;
        if (lineB < 1 || lineB > rightLineCount) return;

        // Check lines actually match – use the working copy when one exists so that
        // after applying changes the line numbers still resolve to the right content.
        string textA = LeftFile.Lines[lineA - 1];
        string textB = _rightWorkingLines != null
            ? _rightWorkingLines[lineB - 1]
            : RightFile.Lines[lineB - 1];
        if (textA != textB)
        {
            System.Windows.MessageBox.Show(
                $"Lines do not have identical content.\n" +
                $"Left L{lineA}: \"{textA.Trim()}\"\n" +
                $"Right L{lineB}: \"{textB.Trim()}\"",
                "Sync Point Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        // Avoid duplicates
        if (_syncPoints.Any(sp => sp.LineA == lineA && sp.LineB == lineB)) return;

        _syncPoints.Add((lineA, lineB));
        SyncPointCount = _syncPoints.Count;

        InvalidateCache(resetWorkingCopy: false);
        ComputeAndDisplayDiffAsync();
    }

    /// <summary>
    /// Remove a specific sync point and recompute the diff.
    /// </summary>
    public void RemoveSyncPoint(int lineA, int lineB)
    {
        if (_syncPoints.RemoveAll(sp => sp.LineA == lineA && sp.LineB == lineB) > 0)
        {
            SyncPointCount = _syncPoints.Count;
            InvalidateCache(resetWorkingCopy: false);
            ComputeAndDisplayDiffAsync();
        }
    }

    /// <summary>
    /// Clear all sync points (whichever mode is active) and recompute the diff from scratch.
    /// </summary>
    private void ClearSyncPoints()
    {
        if (_isHexView)
        {
            if (_hexSyncPoints.Count == 0) return;
            _hexSyncPoints.Clear();
            RaiseHexSyncCountChanged();
        }
        else
        {
            if (_syncPoints.Count == 0) return;
            _syncPoints.Clear();
            SyncPointCount = 0;
        }
        InvalidateCache(resetWorkingCopy: false);
        ComputeAndDisplayDiffAsync();
    }

    /// <summary>
    /// Hex view: add a byte-level sync point pairing byteA and byteB (0-based file offsets).
    /// Unlike text mode the bytes need not be equal; the full anchor set must stay strictly
    /// co-monotone (no crossing or duplicate pairs) or the diff engine cannot honor it.
    /// </summary>
    public void AddHexSyncPoint(int byteA, int byteB)
    {
        if (LeftFile == null || RightFile == null) return;

        // Validate: byte offsets must be within bounds
        if (byteA < 0 || byteA >= LeftFile.RawBytes.Length) return;
        if (byteB < 0 || byteB >= RightFile.RawBytes.Length) return;

        // Avoid duplicates
        if (_hexSyncPoints.Any(sp => sp.ByteA == byteA && sp.ByteB == byteB)) return;

        var trial = _hexSyncPoints.Select(sp => (sp.ByteA, sp.ByteB)).ToList();
        trial.Add((byteA, byteB));
        if (!ByteDiff.IsAnchorSetOrdered(trial))
        {
            System.Windows.MessageBox.Show(
                $"This sync point would cross an existing one.\n" +
                $"Sync points must preserve file order on both sides.",
                "Sync Point Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        _hexSyncPoints.Add((byteA, byteB));
        RaiseHexSyncCountChanged();

        InvalidateCache(resetWorkingCopy: false);
        ComputeAndDisplayDiffAsync();
    }

    /// <summary>
    /// Hex view: remove a specific byte-level sync point and recompute the diff.
    /// </summary>
    public void RemoveHexSyncPoint(int byteA, int byteB)
    {
        if (_hexSyncPoints.RemoveAll(sp => sp.ByteA == byteA && sp.ByteB == byteB) > 0)
        {
            RaiseHexSyncCountChanged();
            InvalidateCache(resetWorkingCopy: false);
            ComputeAndDisplayDiffAsync();
        }
    }

   /// <summary>
    /// Map sync point original line numbers to display-line indices in the cached diff,
    /// so the viewers can render link markers at those positions.
    /// Uses whichever list is actually being displayed (interleaved when gaps are shown,
    /// independent otherwise) so the indices match what the viewer renders.
    /// </summary>
    private void UpdateSyncPointDisplayLines()
    {
        if (_isHexView)
        {
            UpdateHexSyncPointDisplayLines();
            return;
        }

        if (!_cachedDiff.HasValue || _syncPoints.Count == 0)
        {
            SyncPointLinesA = Array.Empty<int>();
            SyncPointLinesB = Array.Empty<int>();
            return;
        }

        // Use the same lists that GetCachedLists() returns — i.e., the ones actually displayed
        var (fileA, fileB) = _showGaps && _cachedInterleaved.HasValue
            ? _cachedInterleaved.Value
            : _cachedDiff.Value;

        var linesA = new List<int>();
        foreach (var sp in _syncPoints)
        {
            for (int i = 0; i < fileA.Count; i++)
            {
                if (fileA[i].OriginalLineNumber == sp.LineA)
                {
                    linesA.Add(i);
                    break;
                }
            }
        }
        linesA.Sort();
        SyncPointLinesA = linesA.AsReadOnly();

        var linesB = new List<int>();
        foreach (var sp in _syncPoints)
        {
            for (int i = 0; i < fileB.Count; i++)
            {
                if (fileB[i].OriginalLineNumber == sp.LineB)
                {
                    linesB.Add(i);
                    break;
                }
            }
        }
        linesB.Sort();
        SyncPointLinesB = linesB.AsReadOnly();
    }

    /// <summary>
    /// Hex view: map each byte anchor to the display row index whose slots cover it.
    /// Uses the same lists GetCachedLists() returns so indices match what the viewer renders
    /// (gap rows included when ShowGaps is on, filtered out otherwise).
    /// </summary>
    private void UpdateHexSyncPointDisplayLines()
    {
        if (_hexSyncPoints.Count == 0 || _cachedDiff == null)
        {
            SyncPointLinesA = Array.Empty<int>();
            SyncPointLinesB = Array.Empty<int>();
            return;
        }

        var (fileA, fileB) = GetCachedLists();

        var linesA = new List<int>();
        foreach (var sp in _hexSyncPoints)
        {
            int idx = FindHexRowForByte(fileA, sp.ByteA);
            if (idx >= 0) linesA.Add(idx);
        }
        linesA.Sort();
        SyncPointLinesA = linesA.AsReadOnly();

        var linesB = new List<int>();
        foreach (var sp in _hexSyncPoints)
        {
            int idx = FindHexRowForByte(fileB, sp.ByteB);
            if (idx >= 0) linesB.Add(idx);
        }
        linesB.Sort();
        SyncPointLinesB = linesB.AsReadOnly();
    }

    /// <summary>
    /// Display index of the row holding the given file byte, or −1. A row's occupied slots
    /// form a contiguous run in file space, so [ByteStart, ByteStart + popcount(mask)) is exact.
    /// </summary>
    private static int FindHexRowForByte(IReadOnlyList<DiffLine> rows, int fileByte)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.ByteStart == null) continue;
            int start = row.ByteStart.Value;
            if (fileByte >= start && fileByte < start + HexFormat.SlotCount(row.HexSlotMask))
                return i;
        }
        return -1;
    }

    // ── Apply / Save / Discard ───────────────────────────────────────────────────

    /// <summary>
    /// Apply the change at the given line index in FileB (right viewer) to make the
    /// right file match the left file. Uses the interleaved diff representation.
    /// After applying, recomputes the diff and marks the right file as dirty.
    /// </summary>
    public void ApplyChangeToRight(int lineIndex)
    {
        if (RightFile == null || _cachedInterleaved == null) return;

        // "Apply to Right" requires the interleaved (gap) representation for row alignment.
        // If ShowGaps is off, force it on so the viewer line indices match the interleaved data
        // (via the property so the per-mode stash mirrors the change too).
        if (!_showGaps)
            ShowGaps = true;

        // Always use the interleaved representation for pairing
        var (intA, intB) = (_cachedInterleaved.Value.A, _cachedInterleaved.Value.B);

        var bLine = lineIndex >= 0 && lineIndex < intB.Count ? intB[lineIndex] : null;
        if (bLine == null) return;

        var aLine = intA[lineIndex]; // Paired row in file A

        // Initialize working copy on first apply
        if (_rightWorkingLines == null)
        {
            _rightWorkingLines = new List<string>(RightFile.Lines);
        }

        bool applied = false;
        int? removedOriginalLine = null;
        int? insertBeforeOriginal = null;

        switch (bLine.Status)
        {
            case DiffStatus.Modified:
                // aLine is also Modified at the same index → swap text from left to right
                if (aLine.Status == DiffStatus.Modified && !string.IsNullOrEmpty(aLine.Text))
                {
                    // Find the position in working copy corresponding to bLine's original line number
                    int targetIdx = GetWorkingCopyIndexFromOriginal(bLine.OriginalLineNumber, intB, lineIndex);
                    if (targetIdx >= 0 && targetIdx < _rightWorkingLines.Count)
                    {
                        _rightWorkingLines[targetIdx] = aLine.Text;
                        applied = true;
                    }
                }
                break;

            case DiffStatus.Added:
                // Line exists only in B → remove it from working copy
                int removeIdx = GetWorkingCopyIndexFromOriginal(bLine.OriginalLineNumber, intB, lineIndex);
                if (removeIdx >= 0 && removeIdx < _rightWorkingLines.Count)
                {
                    _rightWorkingLines.RemoveAt(removeIdx);
                    removedOriginalLine = bLine.OriginalLineNumber;
                    applied = true;
                }
                break;

            case DiffStatus.Gap:
                // Gap on B means aLine is Removed → insert into working copy at the right position
                if (aLine.Status == DiffStatus.Removed && !string.IsNullOrEmpty(aLine.Text))
                {
                    int insertIdx = GetInsertIndexForRemovedLine(lineIndex, intA, intB);
                    if (insertIdx >= 0 && insertIdx <= _rightWorkingLines.Count)
                    {
                        _rightWorkingLines.Insert(insertIdx, aLine.Text);
                        insertBeforeOriginal = CountBLinesBeforeIndex(intB, lineIndex) + 1;
                        applied = true;
                    }
                }
                break;
        }

        if (applied)
        {
            HasUnsavedChanges = true;

            // Adjust sync points to account for structural changes in the working copy
            AdjustSyncPointsForLineBChanges(
                removedOriginalLine.HasValue ? new[] { removedOriginalLine.Value } : Array.Empty<int>(),
                insertBeforeOriginal.HasValue ? new[] { insertBeforeOriginal.Value } : Array.Empty<int>());

            // Create a synthetic FileModel from working copy for diff recomputation
            var workingLines = _rightWorkingLines!;
            var syntheticRight = new FileModel
            {
                FilePath = RightFile.FilePath,
                Lines = workingLines,
                RawBytes = Array.Empty<byte>(),
                LastWriteTime = RightFile.LastWriteTime,
                FileSize = null,
                DisplayName = RightFile.DisplayName,
                FormattedSize = $"{workingLines.Count} lines (modified)",
                FormattedTime = RightFile.FormattedTime
            };

            // Recompute diff using left file vs modified working copy
            // Manually clear caches without resetting working copy state
            _cachedDiff = null;
            _cachedInterleaved = null;
            _cachedUnified = null;
            _cachedFindings = null;
            _cachedDiffCountA = 0;
            _cachedDiffCountB = 0;

            bool ignoreWhitespace = _ignoreWhitespace;
            bool useTimestamps = _timestampMode;

            var (linesA, linesB, intA2, intB2) = DiffEngine.Compute(
                LeftFile!, syntheticRight, ignoreWhitespace, useTimestamps,
                syncPoints: BuildEffectiveSyncPoints(LeftFile!.Lines.Count, workingLines!.Count),
                computeInlineDiffs: _showInlineDiffs,
                algorithm: _diffAlgorithm);

            _cachedDiff = (linesA.AsReadOnly(), linesB.AsReadOnly());
            _cachedInterleaved = (intA2.AsReadOnly(), intB2.AsReadOnly());
            _cachedUnified = DiffEngine.BuildUnified(intA2, intB2);
            // Structural analysis — advisory findings stamped onto rows at display time.
            var (aToB, bToA) = BuildAlignmentMaps(intA2, intB2);
            _cachedFindings = StructuralDiff.Analyze(LeftFile!, syntheticRight, aToB, bToA);
            _cachedIgnoreWhitespace = ignoreWhitespace;
            _cachedUseTimestamps = useTimestamps;
            _cachedShowInlineDiffs = _showInlineDiffs;
            _cachedDiffAlgorithm = _diffAlgorithm;
            UpdateSyncPointDisplayLines();

            int countA = 0, countB = 0;
            foreach (var l in linesA) if (l.Status != DiffStatus.Unchanged) countA++;
            foreach (var l in linesB) if (l.Status != DiffStatus.Unchanged) countB++;
            _cachedDiffCountA = countA;
            _cachedDiffCountB = countB;

            DisplayCachedResult();

            // Keep the editor in sync when "Apply to Right" runs while edit mode is on —
            // the pending debounce tick then no-ops via the equality guard.
            if (_editRightEnabled)
                RightEditorText = LinesToEditorText(_rightWorkingLines);
        }
    }

    /// <summary>
    /// Apply changes at multiple line indices in one batch. The method handles each
    /// status type with the correct processing order:
    /// - Modified lines: replaced in any order (no structural change)
    /// - Added lines: removed bottom-to-top so lower indices stay valid
    /// - Gap lines: inserted top-to-bottom with cumulative shift compensation
    /// After all changes, recomputes the diff once and marks the right file as dirty.
    /// </summary>
    public void ApplyChangesToRight(List<int> lineIndices)
    {
        if (RightFile == null || _cachedInterleaved == null || lineIndices.Count == 0) return;

        // "Apply to Right" requires the interleaved (gap) representation for row alignment.
        // Via the property so the per-mode stash mirrors the forced change too.
        if (!_showGaps)
            ShowGaps = true;

        var (intA, intB) = (_cachedInterleaved.Value.A, _cachedInterleaved.Value.B);

        // Initialize working copy on first apply
        if (_rightWorkingLines == null)
        {
            _rightWorkingLines = new List<string>(RightFile.Lines);
        }

        bool anyApplied = false;

        // Separate indices by status for correct processing order
        var modifiedIndices = new List<int>();
        var addedIndices = new List<int>();
        var gapIndices = new List<int>();

        foreach (var idx in lineIndices)
        {
            var bLine = idx >= 0 && idx < intB.Count ? intB[idx] : null;
            if (bLine == null) continue;

            switch (bLine.Status)
            {
                case DiffStatus.Modified: modifiedIndices.Add(idx); break;
                case DiffStatus.Added: addedIndices.Add(idx); break;
                case DiffStatus.Gap: gapIndices.Add(idx); break;
            }
        }

        // 1. Modified — replace text (no structural change, any order)
        foreach (var lineIndex in modifiedIndices)
        {
            var bLine = intB[lineIndex];
            var aLine = intA[lineIndex];
            if (aLine.Status == DiffStatus.Modified && !string.IsNullOrEmpty(aLine.Text))
            {
                int targetIdx = GetWorkingCopyIndexFromOriginal(bLine.OriginalLineNumber, intB, lineIndex);
                if (targetIdx >= 0 && targetIdx < _rightWorkingLines.Count)
                {
                    _rightWorkingLines[targetIdx] = aLine.Text;
                    anyApplied = true;
                }
            }
        }

        // 2. Added — remove lines bottom-to-top so lower indices remain valid
        // Collect original line numbers removed for sync point adjustment.
        var removedOriginals = new List<int>();
        foreach (var lineIndex in addedIndices.OrderByDescending(i => i))
        {
            var bLine = intB[lineIndex];
            int removeIdx = GetWorkingCopyIndexFromOriginal(bLine.OriginalLineNumber, intB, lineIndex);
            if (removeIdx >= 0 && removeIdx < _rightWorkingLines.Count)
            {
                _rightWorkingLines.RemoveAt(removeIdx);
                removedOriginals.Add(bLine.OriginalLineNumber);
                anyApplied = true;
            }
        }

        // 3. Gap — insert lines top-to-bottom with cumulative shift compensation
        // Each insertion shifts all subsequent working copy positions by +1.
        // Collect "insert before original" for sync point adjustment.
        var insertBeforeOriginals = new List<int>();
        int insertionShift = 0;
        foreach (var lineIndex in gapIndices.OrderBy(i => i))
        {
            var bLine = intB[lineIndex];
            var aLine = intA[lineIndex];
            if (aLine.Status == DiffStatus.Removed && !string.IsNullOrEmpty(aLine.Text))
            {
                int insertIdx = GetInsertIndexForRemovedLine(lineIndex, intA, intB) + insertionShift;
                if (insertIdx >= 0 && insertIdx <= _rightWorkingLines.Count)
                {
                    _rightWorkingLines.Insert(insertIdx, aLine.Text);
                    insertBeforeOriginals.Add(CountBLinesBeforeIndex(intB, lineIndex) + 1);
                    anyApplied = true;
                    insertionShift++;
                }
            }
        }

        if (anyApplied)
        {
            HasUnsavedChanges = true;

            // Adjust sync points to account for structural changes in the working copy
            AdjustSyncPointsForLineBChanges(removedOriginals, insertBeforeOriginals);

            // Create a synthetic FileModel from working copy for diff recomputation
            var workingLines = _rightWorkingLines!;
            var syntheticRight = new FileModel
            {
                FilePath = RightFile.FilePath,
                Lines = workingLines,
                RawBytes = Array.Empty<byte>(),
                LastWriteTime = RightFile.LastWriteTime,
                FileSize = null,
                DisplayName = RightFile.DisplayName,
                FormattedSize = $"{workingLines.Count} lines (modified)",
                FormattedTime = RightFile.FormattedTime
            };

            // Recompute diff using left file vs modified working copy
            _cachedDiff = null;
            _cachedInterleaved = null;
            _cachedUnified = null;
            _cachedFindings = null;
            _cachedDiffCountA = 0;
            _cachedDiffCountB = 0;

            bool ignoreWhitespace = _ignoreWhitespace;
            bool useTimestamps = _timestampMode;

            var (linesA, linesB, intA2, intB2) = DiffEngine.Compute(
                LeftFile!, syntheticRight, ignoreWhitespace, useTimestamps,
                syncPoints: BuildEffectiveSyncPoints(LeftFile!.Lines.Count, workingLines!.Count),
                computeInlineDiffs: _showInlineDiffs,
                algorithm: _diffAlgorithm);

            _cachedDiff = (linesA.AsReadOnly(), linesB.AsReadOnly());
            _cachedInterleaved = (intA2.AsReadOnly(), intB2.AsReadOnly());
            _cachedUnified = DiffEngine.BuildUnified(intA2, intB2);
            // Structural analysis — advisory findings stamped onto rows at display time.
            var (aToB, bToA) = BuildAlignmentMaps(intA2, intB2);
            _cachedFindings = StructuralDiff.Analyze(LeftFile!, syntheticRight, aToB, bToA);
            _cachedIgnoreWhitespace = ignoreWhitespace;
            _cachedUseTimestamps = useTimestamps;
            _cachedShowInlineDiffs = _showInlineDiffs;
            _cachedDiffAlgorithm = _diffAlgorithm;
            UpdateSyncPointDisplayLines();

            int countA = 0, countB = 0;
            foreach (var l in linesA) if (l.Status != DiffStatus.Unchanged) countA++;
            foreach (var l in linesB) if (l.Status != DiffStatus.Unchanged) countB++;
            _cachedDiffCountA = countA;
            _cachedDiffCountB = countB;

            DisplayCachedResult();

            // Keep the editor in sync when "Apply to Right" runs while edit mode is on —
            // the pending debounce tick then no-ops via the equality guard.
            if (_editRightEnabled)
                RightEditorText = LinesToEditorText(_rightWorkingLines);
        }
    }
    /// how many lines belong to the right file.
    /// </summary>
    private int GetWorkingCopyIndexFromOriginal(int originalLineNumber, IReadOnlyList<DiffLine> intB, int targetIndex)
    {
        // The simplest correct approach: walk from the start of the interleaved list
        // and count how many non-Added, non-Gap lines we've encountered up to the target.
        // Since the working copy mirrors the original right file (minus applied changes),
        // OriginalLineNumber - 1 gives us the 0-based index in the original file.
        // For a first apply, this is exact. After multiple applies, we need the walk.

        int wcIndex = 0;
        for (int i = 0; i <= targetIndex; i++)
        {
            var line = intB[i];
            if (line.Status == DiffStatus.Gap)
                continue; // Gap doesn't correspond to a working copy line

            if (i == targetIndex && line.Status != DiffStatus.Gap)
                return wcIndex; // This is the target's position in working copy

            wcIndex++;
        }

        return -1; // Should not reach here
    }

    /// <summary>
    /// Find the correct insertion index in the working copy for a Removed line from file A.
    /// A Gap on B (with Removed on A) means we need to insert the A line into the right file.
    /// </summary>
    private int GetInsertIndexForRemovedLine(int targetIndex, IReadOnlyList<DiffLine> intA, IReadOnlyList<DiffLine> intB)
    {
        // Walk up to the target and count how many lines belong to the right file
        int wcIndex = 0;
        for (int i = 0; i < targetIndex; i++)
        {
            var bLine = intB[i];
            if (bLine.Status == DiffStatus.Gap)
                continue; // Gap on B → not a right-file line
            wcIndex++;
        }
        return wcIndex;
    }

    // ── Sync Point Adjustment ───────────────────────────────────────────────────

    /// <summary>
    /// Adjust sync point LineB values to account for structural changes
    /// (insertions/removals) made to the right file's working copy.
    /// </summary>
    /// <param name="removedOriginalLines">Original line numbers that were removed from the working copy.</param>
    /// <param name="insertBeforeOriginals">Original line numbers that insertions occurred before (i.e., the first existing original line after each insertion point).</param>
    private void AdjustSyncPointsForLineBChanges(IEnumerable<int> removedOriginalLines, IEnumerable<int> insertBeforeOriginals)
    {
        if (_syncPoints.Count == 0) return;

        // Removals: for each removed original line, decrement LineB for sync points below it
        foreach (int removed in removedOriginalLines)
        {
            for (int i = 0; i < _syncPoints.Count; i++)
            {
                if (_syncPoints[i].LineB > removed)
                    _syncPoints[i] = (_syncPoints[i].LineA, _syncPoints[i].LineB - 1);
            }
        }

        // Insertions: for each insertion, increment LineB for sync points at or after the inserted content
        foreach (int insertBefore in insertBeforeOriginals)
        {
            for (int i = 0; i < _syncPoints.Count; i++)
            {
                if (_syncPoints[i].LineB >= insertBefore)
                    _syncPoints[i] = (_syncPoints[i].LineA, _syncPoints[i].LineB + 1);
            }
        }
    }

    /// <summary>
    /// Count how many non-Gap B-lines appear before the given display index in the interleaved list.
    /// This gives the original line number that is just before the insertion point, which is used
    /// to determine which sync points need adjustment for an insertion.
    /// </summary>
    private int CountBLinesBeforeIndex(IReadOnlyList<DiffLine> intB, int displayIndex)
    {
        int count = 0;
        for (int i = 0; i < displayIndex && i < intB.Count; i++)
        {
            if (intB[i].Status != DiffStatus.Gap)
                count++;
        }
        return count; // 1-based: the original line number just before the insertion point
    }

    /// <summary>
    /// Build sync point list for DiffEngine.Compute, filtering out any whose indices
    /// exceed the current file dimensions (can happen after working copy modifications).
    /// </summary>
    private List<(int aIdx, int bIdx)>? BuildEffectiveSyncPoints(int aLineCount, int bLineCount)
    {
        if (_syncPoints.Count == 0) return null;

        var effective = new List<(int aIdx, int bIdx)>();
        foreach (var sp in _syncPoints)
        {
            if (sp.LineA >= 1 && sp.LineA <= aLineCount && sp.LineB >= 1 && sp.LineB <= bLineCount)
                effective.Add((sp.LineA - 1, sp.LineB - 1));
        }
        return effective.Count > 0 ? effective : null;
    }

    private void SaveRightFile()
    {
        if (RightFile == null || !_hasUnsavedChanges || (_rightWorkingLines == null && _rightWorkingBytes == null)) return;
        if (string.IsNullOrEmpty(RightFile.FilePath))
        {
            SaveAsRightFile();
            return;
        }

        try
        {
            if (_rightWorkingBytes != null)
                File.WriteAllBytes(RightFile.FilePath, _rightWorkingBytes);
            else
                File.WriteAllText(RightFile.FilePath, string.Join("\n", _rightWorkingLines!), GetEncodingForFile());

            // Reload the file from disk to get fresh metadata
            var reloaded = FileService.LoadFile(RightFile.FilePath);
            if (reloaded != null)
            {
                RightFile = reloaded;
                _rightWorkingLines = null;
                _rightWorkingBytes = null;
                _pendingByteRestore = null;
                HasUnsavedChanges = false;
                InvalidateCache();
                ComputeAndDisplayDiffAsync();
                // Editing survives save — refresh the editor from the reloaded file.
                if (_editRightEnabled)
                    RightEditorText = LinesToEditorText(RightFile.Lines);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Failed to save file:\n{ex.Message}",
                "Save Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private void SaveAsRightFile()
    {
        if (RightFile == null || !_hasUnsavedChanges || (_rightWorkingLines == null && _rightWorkingBytes == null)) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            // Byte working copies are binary — lead with All Files so the default filter doesn't
            // hide the file's natural extension.
            Filter = _rightWorkingBytes != null
                ? "All Files|*.*"
                : "Text Files|*.txt;*.cs;*.vb;*.xml;*.json;*.yaml;*.yml;*.md;*.ini;*.cfg|All Files|*.*",
            Title = "Save modified file as...",
            InitialDirectory = Path.GetDirectoryName(RightFile.FilePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            FileName = RightFile.DisplayName
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            if (_rightWorkingBytes != null)
                File.WriteAllBytes(dialog.FileName, _rightWorkingBytes);
            else
                File.WriteAllText(dialog.FileName, string.Join("\n", _rightWorkingLines!), GetEncodingForFile());

            // Load the saved file
            var reloaded = FileService.LoadFile(dialog.FileName);
            if (reloaded != null)
            {
                RightFile = reloaded;
                _rightWorkingLines = null;
                _rightWorkingBytes = null;
                _pendingByteRestore = null;
                HasUnsavedChanges = false;
                InvalidateCache();
                ComputeAndDisplayDiffAsync();
                // Editing survives Save As — refresh the editor from the reloaded file.
                if (_editRightEnabled)
                    RightEditorText = LinesToEditorText(RightFile.Lines);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Failed to save file:\n{ex.Message}",
                "Save Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private void DiscardChanges()
    {
        if (!_hasUnsavedChanges) return;

        var result = System.Windows.MessageBox.Show(
            "Discard all changes to the right file and reload from disk?",
            "Discard Changes",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (result == System.Windows.MessageBoxResult.Yes && RightFile?.FilePath != null)
        {
            var reloaded = FileService.LoadFile(RightFile.FilePath);
            if (reloaded != null)
            {
                RightFile = reloaded;
                _rightWorkingLines = null;
                _rightWorkingBytes = null;
                _pendingByteRestore = null;
                HasUnsavedChanges = false;
                InvalidateCache();
                ComputeAndDisplayDiffAsync();
                // Editing survives discard — the editor now shows the disk content.
                if (_editRightEnabled)
                    RightEditorText = LinesToEditorText(RightFile.Lines);
            }
        }
    }

    private static Encoding GetEncodingForFile()
    {
        // Default to UTF-8 without BOM for saved files
        return Encoding.UTF8;
    }

    // ── Diff Computation (Async + Cached) ──────────────────────────────────────

    private void ComputeAndDisplayDiffAsync()
    {
        // Hex view mode — bypass text diff, compute byte-aligned rows on a background thread
        if (_isHexView)
        {
            ComputeHexDiffAsync();
            return;
        }
        _pendingByteRestore = null;   // a text display can't restore a byte selection

        // Only one file loaded — display it without computing a diff
        if (LeftFile != null && RightFile == null)
        {
            DisplaySingleFile(leftLines: CreateUnchangedLines(LeftFile.Lines), leftFile: LeftFile);
            UpdateStatus();
            //LogSwapTimeIfMeasuring();
            return;
        }

        if (LeftFile == null && RightFile != null)
        {
            DisplaySingleFile(rightLines: CreateUnchangedLines(RightFile.Lines), rightFile: RightFile);
            UpdateStatus();
            //LogSwapTimeIfMeasuring();
            return;
        }

        // Neither file loaded
        if (LeftFile == null || RightFile == null)
        {
            UpdateStatus();
            return;
        }

        bool ignoreWhitespace = _ignoreWhitespace;
        bool useTimestamps = _timestampMode;

        // Check cache first — instant display if options haven't changed (and the slots hold
        // text rows, not hex — hex view reuses the same slots)
        if (!_cacheIsHex && _cachedDiff.HasValue && _cachedIgnoreWhitespace == ignoreWhitespace && _cachedUseTimestamps == useTimestamps && _cachedShowInlineDiffs == _showInlineDiffs && _cachedDiffAlgorithm == _diffAlgorithm)
        {
            DisplayCachedResult();
            return;
        }

       // Cancel any previous in-flight computation
        _progressTimer?.Dispose();
        _progressTimer = null;
        _cancelButtonTimer?.Dispose();
        _cancelButtonTimer = null;
        ShowCancelButton = false;
        if (_progressOverlayVisible)
        {
            IsComputing = false;
            _progressOverlayVisible = false;
        }
        try { _pendingCts?.Cancel(); } catch { }
        try { _pendingCts?.Dispose(); } catch { }
        _pendingCts = new CancellationTokenSource();

        // When changes have been applied, diff against the working copy so that
        // OriginalLineNumber values match what the viewer displays.
        FileModel rightSide = _rightWorkingLines != null
            ? new FileModel
            {
                FilePath = RightFile.FilePath,
                Lines = _rightWorkingLines,
                RawBytes = Array.Empty<byte>(),
                LastWriteTime = RightFile.LastWriteTime,
                FileSize = null,
                DisplayName = RightFile.DisplayName,
                FormattedSize = $"{_rightWorkingLines.Count} lines (modified)",
                FormattedTime = RightFile.FormattedTime
            }
            : RightFile;

        int aCount = LeftFile.Lines.Count;
        int bCount = rightSide.Lines.Count;

        // Show progress (delayed: only appear if computation takes > 0.5 s)
        _progressOverlayVisible = false;
        ProgressValue = 0;
        ProgressText = "Starting diff computation...";
        StatusInfo = $"⧖ Computing diff... ({aCount} vs {bCount} lines)";

        // Timer that shows the overlay after 0.5 s if computation is still running
        _progressTimer = new System.Threading.Timer(_ =>
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                _progressOverlayVisible = true;
                IsComputing = true;
            }));
        }, null, 500, Timeout.Infinite);

        // Run diff on background thread
        _ = Task.Run(() =>
        {
            try
            {
                var (linesA, linesB, intA, intB) = DiffEngine.Compute(
                    LeftFile, rightSide, ignoreWhitespace, useTimestamps,
                    reportProgress: (pct, status) =>
                    {
                        // Non-blocking progress update via dispatcher
                        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            ProgressValue = pct;
                            ProgressText = status;
                        }));
                    },
                    syncPoints: BuildEffectiveSyncPoints(LeftFile.Lines.Count, rightSide.Lines.Count),
                    computeInlineDiffs: _showInlineDiffs,
                    algorithm: _diffAlgorithm);

               // Cache results
                _cacheIsHex = false;
                _cachedDiff = (linesA.AsReadOnly(), linesB.AsReadOnly());
                _cachedInterleaved = (intA.AsReadOnly(), intB.AsReadOnly());
                _cachedUnified = DiffEngine.BuildUnified(intA, intB);
                // Structural analysis — advisory findings stamped onto rows at display time.
                var (aToB, bToA) = BuildAlignmentMaps(intA, intB);
                _cachedFindings = StructuralDiff.Analyze(LeftFile, rightSide, aToB, bToA);
                _cachedIgnoreWhitespace = ignoreWhitespace;
                _cachedUseTimestamps = useTimestamps;
                _cachedShowInlineDiffs = _showInlineDiffs;
                _cachedDiffAlgorithm = _diffAlgorithm;
                UpdateSyncPointDisplayLines();

                // Pre-compute diff counts
                int countA = 0, countB = 0;
                foreach (var l in linesA) if (l.Status != DiffStatus.Unchanged) countA++;
                foreach (var l in linesB) if (l.Status != DiffStatus.Unchanged) countB++;
                _cachedDiffCountA = countA;
                _cachedDiffCountB = countB;

                // Post display update to UI thread
                _progressTimer?.Dispose();
                _progressTimer = null;
                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_progressOverlayVisible)
                        IsComputing = false;
                    DisplayCachedResult();

                    string resultMsg = (_cachedDiffCountA + _cachedDiffCountB) == 0
                        ? $"✓ Files are identical ({aCount} vs {bCount} lines)"
                        : $"✓ Diff complete — {_cachedDiffCountA + _cachedDiffCountB} changes ({aCount} vs {bCount} lines)";

                    StatusInfo = $"{resultMsg}  |  " +
                        $"Layout: {LayoutName}  |  " +
                        $"Scroll: {(_syncScroll ? "Synced" : "Independent")}  |  " +
                        $"[Ctrl+L] Left  [Ctrl+Shift+R] Right  [Ctrl+R] Refresh  [Ctrl+S] Swap  [F11] Layout";
                    _statusLayoutName = LayoutName;
                }));
            }
            catch (OperationCanceledException)
            {
                _progressTimer?.Dispose();
                _progressTimer = null;
                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_progressOverlayVisible)
                        IsComputing = false;
                    UpdateStatus();
                }));
            }
            catch (Exception ex)
            {
                _progressTimer?.Dispose();
                _progressTimer = null;
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    if (_progressOverlayVisible)
                        IsComputing = false;
                    System.Windows.MessageBox.Show(
                        $"Diff computation error:\n{ex.Message}",
                        "Error",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Error);
                });
            }
        });
    }

       /// <summary>
    /// Hex view: compute the byte-aligned diff with ByteDiff on a background thread.
    /// Mirrors the text path's async pattern (0.5 s delayed progress overlay, cancellable via
    /// _pendingCts) and caches the aligned rows so ShowGaps/swap work without recomputing.
    /// </summary>
    private void ComputeHexDiffAsync()
    {
        // Neither file loaded, or only one — DisplayHexView handles both (single-file dump)
        if (LeftFile == null || RightFile == null)
        {
            DisplayHexView();
            return;
        }

        byte[] paneA = LeftFile.RawBytes;
        // Working copy first — pending hex edits live in a fresh array, which also defeats the
        // reference-identity hex cache so an edit always re-diffs.
        byte[] paneB = _rightWorkingBytes ?? RightFile.RawBytes;

        // Timestamp mode: the older file is always the base (A/before) side regardless of pane
        // position — mirror DiffEngine.Compute's swap-then-swap-back so each pane keeps its file.
        bool swapped = false;
        byte[] aBytes = paneA, bBytes = paneB;
        List<(int, int)> anchors = _hexSyncPoints.Select(sp => (sp.ByteA, sp.ByteB)).ToList();
        if (_timestampMode
            && LeftFile.LastWriteTime.HasValue
            && RightFile.LastWriteTime.HasValue
            && LeftFile.LastWriteTime.Value > RightFile.LastWriteTime.Value)
        {
            (aBytes, bBytes) = (bBytes, aBytes);
            anchors = _hexSyncPoints.Select(sp => (sp.ByteB, sp.ByteA)).ToList();
            swapped = true;
        }
        // Keep the swap-flip guard (SwapFiles) in sync even when the cache hits below.
        _cachedUseTimestamps = _timestampMode;

        // Check cache first — instant display if the same byte arrays and inline setting are loaded
        if (HexCacheValid(paneA, paneB, swapped))
        {
            DisplayHexView();
            return;
        }

        // Cancel any previous in-flight computation
        _progressTimer?.Dispose();
        _progressTimer = null;
        _cancelButtonTimer?.Dispose();
        _cancelButtonTimer = null;
        ShowCancelButton = false;
        if (_progressOverlayVisible)
        {
            IsComputing = false;
            _progressOverlayVisible = false;
        }
        try { _pendingCts?.Cancel(); } catch { }
        try { _pendingCts?.Dispose(); } catch { }
        _pendingCts = new CancellationTokenSource();

        int aCount = paneA.Length;
        int bCount = paneB.Length;

        // Show progress (delayed: only appear if computation takes > 0.5 s)
        _progressOverlayVisible = false;
        ProgressValue = 0;
        ProgressText = "Starting byte-aligned diff...";
        StatusInfo = $"⧖ Computing hex diff... ({aCount} vs {bCount} bytes)";

        // Timer that shows the overlay after 0.5 s if computation is still running
        _progressTimer = new System.Threading.Timer(_ =>
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                _progressOverlayVisible = true;
                IsComputing = true;
            }));
        }, null, 500, Timeout.Infinite);

        // Timer that reveals the Cancel button after 1 s if the computation is still running.
        // Hex only — the text engine doesn't take a cancellation token, so there's nothing to cancel.
        _cancelButtonTimer = new System.Threading.Timer(_ =>
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsComputing)
                    ShowCancelButton = true;
            }));
        }, null, 1000, Timeout.Infinite);

        bool inlineDiffs = _showInlineDiffs;
        bool perByte = _hexPerByte;

        // Run diff on background thread
        _ = Task.Run(() =>
        {
            try
            {
                var (rowsA, rowsB, fallbackSegments) = ByteDiff.ComputeWithSyncPoints(
                    aBytes, bBytes, anchors, inlineDiffs, autoResync: perByte,
                    reportProgress: (pct, status) =>
                    {
                        // Non-blocking progress update via dispatcher
                        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            ProgressValue = pct;
                            ProgressText = status;
                        }));
                    },
                    ct: _pendingCts.Token);

                // Dense fixed-address rows (Show Gaps OFF) repacked from the same aligned pairing —
                // no second diff pass. DisplayHexView fans both representations into the cache slots.
                var (fixedA, fixedB) = ByteDiff.BuildFixedAddressRows(
                    aBytes, aBytes.Length, bBytes, bBytes.Length, rowsA, rowsB);

                // Return to pane order — the left pane always shows LeftFile and the right pane the
                // right file, regardless of which one is the timestamp base.
                if (swapped)
                {
                    (rowsA, rowsB) = (rowsB, rowsA);
                    (fixedA, fixedB) = (fixedB, fixedA);
                }

                _hexCache = (paneA, paneB, inlineDiffs, perByte, swapped,
                             rowsA.AsReadOnly(), rowsB.AsReadOnly(), fixedA, fixedB, fallbackSegments);
                UpdateSyncPointDisplayLines();

                // Pre-compute diff counts (Gap fillers don't count as changes)
                int countA = 0, countB = 0;
                foreach (var l in rowsA) if (l.Status != DiffStatus.Unchanged && l.Status != DiffStatus.Gap) countA++;
                foreach (var l in rowsB) if (l.Status != DiffStatus.Unchanged && l.Status != DiffStatus.Gap) countB++;
                _cachedDiffCountA = countA;
                _cachedDiffCountB = countB;

                // Post display update to UI thread
                _progressTimer?.Dispose();
                _progressTimer = null;
                _cancelButtonTimer?.Dispose();
                _cancelButtonTimer = null;
                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_progressOverlayVisible)
                        IsComputing = false;
                    ShowCancelButton = false;
                    DisplayHexView();
                }));
            }
            catch (OperationCanceledException)
            {
                _progressTimer?.Dispose();
                _progressTimer = null;
                _cancelButtonTimer?.Dispose();
                _cancelButtonTimer = null;
                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_progressOverlayVisible)
                        IsComputing = false;
                    ShowCancelButton = false;
                    StatusInfo = "⊘ Hex diff computation cancelled";
                }));
            }
            catch (Exception ex)
            {
                _progressTimer?.Dispose();
                _progressTimer = null;
                _cancelButtonTimer?.Dispose();
                _cancelButtonTimer = null;
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    if (_progressOverlayVisible)
                        IsComputing = false;
                    ShowCancelButton = false;
                    System.Windows.MessageBox.Show(
                        $"Hex diff computation error:\n{ex.Message}",
                        "Error",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Error);
                });
            }
        });
    }

    private void DisplayCachedResult()
    {
        if (_cachedDiff.HasValue)
        {
            ApplyStructuralStamp();   // (re)stamp advisory notes before the rows reach viewers
            var (aList, bList) = GetCachedLists();

            FileALines = aList;
            FileBLines = bList;
            LeftFilePath = LeftFile?.FilePath;
            RightFilePath = RightFile?.FilePath;

            // Update titles and metadata (hex view reports bytes, not lines)
            LeftTitle = LeftFile?.DisplayName ?? "(no file loaded)";
            LeftMeta = LeftFile != null
                ? (_isHexView
                    ? $"{LeftFile.RawBytes.Length} bytes | {LeftFile.FormattedSize} | {LeftFile.FilePath}"
                    : $"{LeftFile.Lines.Count} lines | {LeftFile.FormattedSize} | {LeftFile.FilePath}")
                : "";
            RefreshRightTitle();
            RightMeta = RightFile != null
                ? (_isHexView
                    ? $"{RightFile.RawBytes.Length} bytes | {RightFile.FormattedSize} | {RightFile.FilePath}"
                    : $"{RightFile.Lines.Count} lines | {RightFile.FormattedSize} | {RightFile.FilePath}")
                : "";

            // Unified (git-style) list — always synthesized from the interleaved pair, so
            // Show Gaps is a content no-op in this layout.
            UnifiedLines = _cachedUnified ?? Array.Empty<DiffLine>();
            UnifiedFilePath = LeftFile?.FilePath ?? RightFile?.FilePath;
            UnifiedTitle = LeftFile != null && RightFile != null
                ? $"{LeftFile.DisplayName}  →  {RightFile.DisplayName}"
                : (LeftFile?.DisplayName ?? RightFile?.DisplayName ?? "(no file loaded)");
            UnifiedMeta = (LeftFile != null && RightFile != null)
                ? $"{LeftFile.Lines.Count} vs {RightFile.Lines.Count} lines | unified"
                : (LeftFile != null ? LeftMeta : RightMeta);
            UnifiedShowNoDifferences = AllUnchanged(UnifiedLines);

            // "Files are identical" is a property of the files, not of the display list. With
            // gaps hidden (and for dense hex rows), the side missing lines/bytes carries no
            // non-Unchanged rows at all — a per-list scan would label that pane identical on
            // its own while the partner pane shows the change. The diff counts are computed
            // once from the canonical pairing, so they're representation-independent.
            bool identical = (_cachedDiffCountA + _cachedDiffCountB) == 0;
            LeftShowNoDifferences = identical;
            RightShowNoDifferences = identical;
        }
        UpdateStatus();

        // Temp debug: log swap timing if measuring
        //LogSwapTimeIfMeasuring();
    }

    private void DisplaySingleFile(
        IReadOnlyList<DiffLine>? leftLines = null, FileModel? leftFile = null,
        IReadOnlyList<DiffLine>? rightLines = null, FileModel? rightFile = null)
    {
        if (leftLines != null && leftFile != null)
        {
            FileALines = leftLines;
            LeftFilePath = leftFile.FilePath;
            LeftTitle = leftFile.DisplayName;
            LeftMeta = $"{leftFile.Lines.Count} lines | {leftFile.FormattedSize} | {leftFile.FilePath}";
            LeftShowNoDifferences = false;
        }
        else
        {
            FileALines = Array.Empty<DiffLine>();
            LeftFilePath = null;
            LeftTitle = "(no file loaded)";
            LeftMeta = "";
            LeftShowNoDifferences = false;
        }

        if (rightLines != null && rightFile != null)
        {
            FileBLines = rightLines;
            RightFilePath = rightFile.FilePath;
            RightTitle = rightFile.DisplayName;
            RightMeta = $"{rightFile.Lines.Count} lines | {rightFile.FormattedSize} | {rightFile.FilePath}";
            RightShowNoDifferences = false;
        }
        else
        {
            FileBLines = Array.Empty<DiffLine>();
            RightFilePath = null;
            RightTitle = "(no file loaded)";
            RightMeta = "";
            RightShowNoDifferences = false;
        }

        // Unified layout shows whichever single file is loaded (hex single-file routes here too).
        if (leftLines != null && leftFile != null)
        {
            UnifiedLines = leftLines;
            UnifiedFilePath = leftFile.FilePath;
            UnifiedTitle = leftFile.DisplayName;
            UnifiedMeta = $"{leftFile.Lines.Count} lines | {leftFile.FormattedSize} | {leftFile.FilePath}";
        }
        else if (rightLines != null && rightFile != null)
        {
            UnifiedLines = rightLines;
            UnifiedFilePath = rightFile.FilePath;
            UnifiedTitle = rightFile.DisplayName;
            UnifiedMeta = $"{rightFile.Lines.Count} lines | {rightFile.FormattedSize} | {rightFile.FilePath}";
        }
        else
        {
            UnifiedLines = Array.Empty<DiffLine>();
            UnifiedFilePath = null;
            UnifiedTitle = "(no file loaded)";
            UnifiedMeta = "";
        }
        UnifiedShowNoDifferences = false;
    }

    private static IReadOnlyList<DiffLine> CreateUnchangedLines(List<string> lines)
    {
        var result = new DiffLine[lines.Count];
        for (int i = 0; i < lines.Count; i++)
            result[i] = new DiffLine
            {
                OriginalLineNumber = i + 1,
                Text = lines[i],
                Status = DiffStatus.Unchanged,
                IsFromFileA = true
            };
        return result;
    }

    private static bool AllUnchanged(IReadOnlyList<DiffLine> lines)
    {
        if (lines.Count == 0) return false;
        foreach (var line in lines)
            if (line.Status != DiffStatus.Unchanged) return false;
        return true;
    }

    // ── Hex View ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies a byte-level edit to the right file's working copy: replaces
    /// [startByte, startByte+removeCount) with insertBytes (pure insert when removeCount == 0,
    /// pure delete when insertBytes is empty), adjusts the hex sync points, marks the file dirty
    /// and re-diffs. Hex mode + two files only; no-op otherwise.
    /// </summary>
    public void ApplyRightByteEdit(int startByte, int removeCount, byte[] insertBytes)
    {
        if (!_isHexView || LeftFile == null || RightFile == null) return;

        var source = _rightWorkingBytes ?? (byte[])RightFile.RawBytes.Clone();
        _rightWorkingBytes = ByteEdit.Splice(source, startByte, removeCount, insertBytes);

        int delta = insertBytes.Length - removeCount;
        if (_hexSyncPoints.Count > 0)
            ByteEdit.AdjustSyncPoints(_hexSyncPoints, startByte, delta, _rightWorkingBytes.Length);

        HasUnsavedChanges = true;
        // Re-select the freshly inserted bytes once the re-diff has displayed.
        _pendingByteRestore = insertBytes.Length > 0 ? (startByte, insertBytes.Length) : null;

        InvalidateCache(resetWorkingCopy: false);
        ComputeAndDisplayDiffAsync();
    }

    private void DisplayHexView()
    {
        // Neither file loaded
        if (LeftFile == null && RightFile == null)
        {
            FileALines = Array.Empty<DiffLine>();
            FileBLines = Array.Empty<DiffLine>();
            UnifiedLines = Array.Empty<DiffLine>();   // defensive — unified grid is hidden in hex anyway
            LeftFilePath = null; RightFilePath = null; UnifiedFilePath = null;
            LeftTitle = "(no file loaded)"; LeftMeta = ""; LeftShowNoDifferences = false;
            RightTitle = "(no file loaded)"; RightMeta = ""; RightShowNoDifferences = false;
            UpdateStatus();
            return;
        }

        // Both files loaded — display the cached byte-aligned diff
        if (LeftFile != null && RightFile != null)
        {
            if (_hexCache == null)
            {
                // Computation cancelled or still in flight — keep whatever is displayed
                UpdateStatus();
                return;
            }

            var cache = _hexCache.Value;

            // Fan the row representations out into the standard cache slots so ShowGaps
            // selection, the swap-flip path and sync-marker display all work through existing
            // machinery. Aligned rows (with mid-row holes) serve Show Gaps ON; dense fixed-address
            // rows serve Show Gaps OFF (GetCachedLists picks by _showGaps).
            _cacheIsHex = true;
            _cachedInterleaved = (cache.AlignedA, cache.AlignedB);
            _cachedDiff = (cache.FixedA, cache.FixedB);
            _cachedUnified = Array.Empty<DiffLine>();   // hex is always two-pane

            DisplayCachedResult();

            // A byte edit just displayed — hand the fresh range back to the window so it can
            // re-select it (the Lines reassignment above reset the viewer's byte selection).
            if (_pendingByteRestore is { } restore)
            {
                _pendingByteRestore = null;
                RightByteSelectionToRestore?.Invoke(restore);
            }

            int diffCount = _cachedDiffCountA + _cachedDiffCountB;
            string resultMsg = diffCount == 0
                ? "✓ Files are identical (hex)"
                : $"Δ {diffCount} line(s) differ (hex)";
            if (cache.FallbackSegments > 0)
                resultMsg += cache.FallbackSegments == 1
                    ? "  |  ⚠ 1 region shown positionally"
                    : $"  |  ⚠ {cache.FallbackSegments} regions shown positionally";

            StatusInfo = $"{resultMsg}  |  Hex view  |  " +
                $"Layout: {LayoutName}  |  " +
                $"Scroll: {(_syncScroll ? "Synced" : "Independent")}  |  " +
                $"[Ctrl+L] Left  [Ctrl+Shift+R] Right  [Ctrl+R] Refresh  [Ctrl+S] Swap  [F11] Layout";
            _statusLayoutName = LayoutName;

            UpdateStatus();
            return;
        }

        // Only one file loaded — display as hex dump with Unchanged status
        if (LeftFile != null)
        {
            var hexLines = GenerateSingleHexDump(LeftFile.RawBytes);
            DisplaySingleFile(leftLines: hexLines, leftFile: LeftFile);
        }
        else if (RightFile != null)
        {
            var hexLines = GenerateSingleHexDump(RightFile.RawBytes);
            DisplaySingleFile(rightLines: hexLines, rightFile: RightFile);
        }

        UpdateStatus();

        // Temp debug: log swap timing if measuring
        //LogSwapTimeIfMeasuring();
    }

    /// <summary>
    /// Generate a single-file hex dump as Unchanged DiffLines, with ByteStart/mask stamped so
    /// hit-testing and the byte inspector work identically to the aligned two-file rows.
    /// </summary>
    private static IReadOnlyList<DiffLine> GenerateSingleHexDump(byte[] bytes)
    {
        int lineCount = HexFormat.BytesToHexLines(bytes.Length);
        var result = new DiffLine[lineCount];

        for (int i = 0; i < lineCount; i++)
        {
            int startOffset = i * HexFormat.BytesPerLine;
            int count = Math.Min(HexFormat.BytesPerLine, bytes.Length - startOffset);
            result[i] = new DiffLine
            {
                OriginalLineNumber = i + 1,
                Text = HexFormat.FormatContiguousRow(startOffset, bytes, bytes.Length),
                Status = DiffStatus.Unchanged,
                IsFromFileA = true,
                ByteStart = startOffset,
                HexSlotMask = (1 << count) - 1
            };
        }

        return result;
    }

    // ── Layout-switch scroll mapping ───────────────────────────────────────────
    // Canonical space = index into the interleaved A list (row-aligned with B, so one
    // canonical index identifies one aligned row pair). MainWindow converts a scroll
    // anchor from the layout being left into this space, then maps it onto the target.
    // All maps are identity when no diff is cached: single-file mode shares one list
    // reference across every viewer (and hex lists are already row-aligned), so raw
    // indices agree without conversion.

    /// <summary>Displayed left-pane line index → canonical interleaved index.</summary>
    public int PaneLeftIndexToCanonical(int paneIndex) => PaneSideIndexToCanonical(paneIndex, sideA: true);

    /// <summary>Displayed right-pane line index → canonical interleaved index.</summary>
    public int PaneRightIndexToCanonical(int paneIndex) => PaneSideIndexToCanonical(paneIndex, sideA: false);

    private int PaneSideIndexToCanonical(int paneIndex, bool sideA)
    {
        if (_cachedInterleaved == null || _showGaps) return paneIndex; // gaps shown → pane list IS the interleaved list
        var list = sideA ? _cachedInterleaved.Value.A : _cachedInterleaved.Value.B;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Status != DiffStatus.Gap)
            {
                if (paneIndex <= 0) return i;
                paneIndex--;
            }
        }
        return Math.Max(0, list.Count - 1); // out of range → last row
    }

    /// <summary>Unified list index → canonical interleaved index of the row that owns it.</summary>
    public int UnifiedIndexToCanonical(int unifiedIndex)
    {
        if (_cachedInterleaved == null) return unifiedIndex;
        var a = _cachedInterleaved.Value.A;
        int u = 0;
        for (int i = 0; i < a.Count; i++)
        {
            int rows = a[i].Status == DiffStatus.Modified ? 2 : 1; // Modified expands to a -/+ pair
            if (unifiedIndex < u + rows) return i;
            u += rows;
        }
        return Math.Max(0, a.Count - 1);
    }

    /// <summary>Canonical interleaved index → first unified list index of that row.</summary>
    public int CanonicalToUnifiedIndex(int canonical)
    {
        if (_cachedInterleaved == null) return canonical;
        var a = _cachedInterleaved.Value.A;
        canonical = Math.Clamp(canonical, 0, Math.Max(0, a.Count - 1));
        int u = 0;
        for (int i = 0; i < canonical; i++)
            u += a[i].Status == DiffStatus.Modified ? 2 : 1;
        return u;
    }

    /// <summary>Canonical interleaved index → (left-pane, right-pane) displayed indices.</summary>
    public (int Left, int Right) CanonicalToPaneIndices(int canonical)
    {
        if (_cachedInterleaved == null) return (canonical, canonical);
        var (a, b) = _cachedInterleaved.Value;
        canonical = Math.Clamp(canonical, 0, Math.Max(0, a.Count - 1));
        if (_showGaps) return (canonical, canonical); // gaps shown → pane indices ARE interleaved indices
        int l = 0, r = 0;
        for (int i = 0; i < canonical; i++)
        {
            if (a[i].Status != DiffStatus.Gap) l++;
            if (b[i].Status != DiffStatus.Gap) r++;
        }
        return (l, r);
    }

    private (IReadOnlyList<DiffLine> A, IReadOnlyList<DiffLine> B) GetCachedLists()
    {
        if (_showGaps && _cachedInterleaved.HasValue)
            return (_cachedInterleaved.Value.A, _cachedInterleaved.Value.B);
        return (_cachedDiff!.Value.A, _cachedDiff!.Value.B);
    }

    private void InvalidateCache(bool resetWorkingCopy = true)
    {
        _cachedDiff = null;
        _cachedInterleaved = null;
        _cachedUnified = null;
        _cachedFindings = null;
        // Hex cache too — sync-point and file changes alter the anchors/bytes even when the
        // RawBytes references (and thus the fingerprint) are unchanged.
        _hexCache = null;
        _cachedDiffCountA = 0;
        _cachedDiffCountB = 0;
        // Only reset the working copy when files are reloaded/swapped; preserve it
        // for sync point and setting changes so the diff still reflects applied edits.
        if (resetWorkingCopy)
        {
            _rightWorkingLines = null;
            _rightWorkingBytes = null;
            _pendingByteRestore = null;
            HasUnsavedChanges = false;
        }
        // Clear sync point display lines and pending state
        SyncPointLinesA = Array.Empty<int>();
        SyncPointLinesB = Array.Empty<int>();
        PendingSyncLineA = -1;
        PendingSyncLineB = -1;
    }

    /// <summary>
    /// Clear structural notes on every cached row and re-stamp them from _cachedFindings when
    /// analysis is enabled. Stamping keys on (IsFromFileA, OriginalLineNumber), which is
    /// invariant under SwapFiles' flip+swap — so a swap needs no re-analysis or re-keying.
    /// All five lists are stamped separately: BuildUnified creates new DiffLine instances for
    /// Modified pairs (copying IsFromFileA/OriginalLineNumber, so the key still works).
    /// </summary>
    private void ApplyStructuralStamp()
    {
        var lists = new List<IReadOnlyList<DiffLine>>();
        if (_cachedDiff.HasValue)
        {
            lists.Add(_cachedDiff.Value.A);
            lists.Add(_cachedDiff.Value.B);
        }
        if (_cachedInterleaved.HasValue)
        {
            lists.Add(_cachedInterleaved.Value.A);
            lists.Add(_cachedInterleaved.Value.B);
        }
        if (_cachedUnified != null)
            lists.Add(_cachedUnified);

        foreach (var list in lists)
            foreach (var row in list)
            {
                row.StructuralNote = null;
                row.StructuralFlag = null;
            }

        if (!_useStructuralAnalysis || _isHexView || _cachedFindings == null) return;

        // (side, 1-based original line) → joined message + first finding's kind
        var byKey = new Dictionary<(bool, int), (string Message, StructuralKind Kind)>();
        foreach (var f in _cachedFindings)
        {
            if (f.LineA > 0) AddFinding(byKey, (true, f.LineA), f);
            if (f.LineB > 0) AddFinding(byKey, (false, f.LineB), f);
        }

        foreach (var list in lists)
            foreach (var row in list)
            {
                // A hint must align to a diff: Unchanged rows are never stamped — a finding that
                // touches only unchanged lines is dropped rather than shown on code that didn't change.
                if (row.Status == DiffStatus.Unchanged || row.Status == DiffStatus.Gap) continue;
                if (byKey.TryGetValue((row.IsFromFileA, row.OriginalLineNumber), out var hit))
                {
                    row.StructuralNote = hit.Message;
                    row.StructuralFlag = hit.Kind;
                }
            }

        static void AddFinding(Dictionary<(bool, int), (string Message, StructuralKind Kind)> map, (bool, int) key, StructuralFinding f)
        {
            if (map.TryGetValue(key, out var existing))
                map[key] = (existing.Message + "; " + f.Message, existing.Kind);
            else
                map[key] = (f.Message, f.Kind);
        }
    }

    /// <summary>
    /// Cross-file alignment from the row-aligned interleaved pair: aToB[i] = 0-based index of
    /// the B line aligned with A line i (-1 unpaired), bToA symmetric. Non-Gap rows enumerate
    /// each plain list in order; paired rows are Unchanged matches plus positionally-paired
    /// Modified lines.
    /// </summary>
    private static (int[] aToB, int[] bToA) BuildAlignmentMaps(IReadOnlyList<DiffLine> intA, IReadOnlyList<DiffLine> intB)
    {
        int nA = 0, nB = 0;
        foreach (var l in intA) if (l.Status != DiffStatus.Gap) nA++;
        foreach (var l in intB) if (l.Status != DiffStatus.Gap) nB++;
        var aToB = new int[nA]; Array.Fill(aToB, -1);
        var bToA = new int[nB]; Array.Fill(bToA, -1);
        int ai = 0, bi = 0;
        for (int i = 0; i < intA.Count; i++)
        {
            bool aReal = intA[i].Status != DiffStatus.Gap, bReal = intB[i].Status != DiffStatus.Gap;
            if (aReal && bReal) { aToB[ai] = bi; bToA[bi] = ai; }
            if (aReal) ai++;
            if (bReal) bi++;
        }
        return (aToB, bToA);
    }

    // ── Status Bar Update ──────────────────────────────────────────────────────

   private void UpdateTitleBar()
    {
        if (LeftFile != null && RightFile != null)
            TitleFileInfo = $"{LeftFile.DisplayName}  ⟩⟨  {RightFile.DisplayName}{(_hasUnsavedChanges ? " ●" : "")}";
        else if (LeftFile != null)
            TitleFileInfo = LeftFile.DisplayName;
        else if (RightFile != null)
            TitleFileInfo = RightFile.DisplayName;
        else
            TitleFileInfo = "";

        if (LeftFile != null && RightFile != null)
        {
            int diffCountA = _cachedDiff.HasValue ? _cachedDiffCountA : 0;
            int diffCountB = _cachedDiff.HasValue ? _cachedDiffCountB : 0;
            TitleSubtitle = $"| Δ {diffCountA + diffCountB} changes";
        }
        else
            TitleSubtitle = "";
    }

    private void UpdateStatus()
    {
        UpdateTitleBar();

        if (LeftFile != null)
        {
            StatusLeft = $"◈ {LeftFile.DisplayName}  " +
                $"{LeftFile.Lines.Count} lines  " +
                $"{LeftFile.FormattedSize}  " +
                $"{LeftFile.FormattedTime}";
        }
        else
        {
            StatusLeft = "◈ (no left file)";
        }

        if (RightFile != null)
        {
            StatusRight = $"◈ {RightFile.DisplayName}  " +
                $"{RightFile.Lines.Count} lines  " +
                $"{RightFile.FormattedSize}  " +
                $"{RightFile.FormattedTime}";
        }
        else
        {
            StatusRight = "◈ (no right file)";
        }

        string diffInfo;
        if (LeftFile != null && RightFile == null)
            diffInfo = $"◈ 1 file loaded ({LeftFile.Lines.Count} lines)";
        else if (LeftFile == null && RightFile != null)
            diffInfo = $"◈ 1 file loaded ({RightFile.Lines.Count} lines)";
        else
        {
            int diffCountA = _cachedDiff.HasValue ? _cachedDiffCountA : 0;
            int diffCountB = _cachedDiff.HasValue ? _cachedDiffCountB : 0;
            diffInfo = (diffCountA + diffCountB) == 0 && _cachedDiff.HasValue
                ? "✓ Files are identical"
                : $"Δ {diffCountA + diffCountB} changes";
        }

        if (string.IsNullOrEmpty(StatusInfo))
        {
            string syncInfo = _syncPointCount > 0
                ? $"  |  🔗 {_syncPointCount} sync point(s)"
                : "";
            StatusInfo = $"{diffInfo}  |  " +
                $"Layout: {LayoutName}  |  " +
                $"Scroll: {(_syncScroll ? "Synced" : "Independent")}  |  " +
                $"[Ctrl+L] Left  [Ctrl+Shift+R] Right  [Ctrl+R] Refresh  [Ctrl+S] Swap  [F11] Layout" +
                syncInfo;
            _statusLayoutName = LayoutName;
        }
        else
        {
            // Update the non-progress part of status info — replace against the layout name
            // that was actually written last time (tracked, since three modes can't be inverted).
            StatusInfo = StatusInfo.Replace(
                $"Layout: {_statusLayoutName}  |  " +
                $"Scroll: {(_syncScroll ? "Independent" : "Synced")}  |  " +
                $"[Ctrl+L] Left  [Ctrl+Shift+R] Right  [Ctrl+R] Refresh  [Ctrl+S] Swap  [F11] Layout",
                $"Layout: {LayoutName}  |  " +
                $"Scroll: {(_syncScroll ? "Synced" : "Independent")}  |  " +
                $"[Ctrl+L] Left  [Ctrl+Shift+R] Right  [Ctrl+R] Refresh  [Ctrl+S] Swap  [F11] Layout");
            _statusLayoutName = LayoutName;
        }
    }

    // ── INotifyPropertyChanged ─────────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name ?? nameof(field));
        return true;
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// RelayCommand — Lightweight ICommand implementation
// ═══════════════════════════════════════════════════════════════════════════════

internal class RelayCommand : ICommand
{
    private readonly Action _execute;
    public RelayCommand(Action execute) => _execute = execute;
#pragma warning disable CS0067 // Event is never used — required by ICommand interface, CanExecute always returns true
    public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute();
}

internal class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    public RelayCommand(Action<T?> execute) => _execute = execute;
#pragma warning disable CS0067 // Event is never used — required by ICommand interface, CanExecute always returns true
    public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute((T?)parameter);
}
