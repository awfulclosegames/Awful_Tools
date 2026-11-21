using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using AwfulDiff.Models;
using AwfulDiff.Services;

namespace AwfulDiff.Controls;

/// <summary>
/// Ultra-fast diff viewer that bypasses the WPF visual tree entirely.
///
/// Renders all visible lines directly in OnRender(DrawingContext) using fixed geometry —
/// no measure/arrange passes, no containers, no bindings, no converters.
/// Visual tree cost: exactly 1 element per viewer.
/// </summary>
public class FastDiffViewer : Control
{
    // ── Constants ──────────────────────────────────────────────────────────────

    private const int LineHeight = 20;
    private const int TextGutterWidth = 56;   // Line number column width for text mode
    private const int HexGutterWidth = 84;    // Wider gutter for hex offsets (8 chars + padding)
    private const int AccentWidth = 3;        // Left status bar width
    private const int DiffMapWidth = 4;      // Right-side diff overview map width
    private const int ScrollbarWidth = 14;
    private const int MinThumbHeight = 20;    // Minimum vertical thumb height
    private const int HScrollbarHeight = 14;  // Horizontal scrollbar height
    private const int MinThumbWidth = 20;     // Minimum horizontal thumb width

    private bool _isHexView;
    public bool IsHexView
    {
        get => _isHexView;
        set
        {
            if (_isHexView != value)
            {
                _isHexView = value;
                if (!_isHexView)
                {
                    _byteSelStart = null;
                    _byteSelEnd = null;
                    RecomputeSyntaxTokens();   // back to text view — re-tokenize lines
                }
                else
                {
                    _syntaxTokens = null;     // hex rows are not source text
                }
                InvalidateVisual();
            }
        }
    }

    /// <summary>
    /// Hex per-byte display: when true (and in hex view) a Modified row shades only its changed
    /// byte slots — red for removed, green for added, yellow for modified (see DiffLine's three
    /// Hex*Slots masks) — instead of the whole row. Added/Removed rows keep their full-row highlight
    /// — every byte on them is genuinely new/gone, so there is nothing to distinguish within the row.
    /// No-op in text view.
    /// </summary>
    private bool _hexPerByte;
    public bool HexPerByte
    {
        get => _hexPerByte;
        set
        {
            if (_hexPerByte != value)
            {
                _hexPerByte = value;
                InvalidateVisual();
            }
        }
    }

    private int GutterWidth => _isHexView ? HexGutterWidth : TextGutterWidth;

    // ── Theme Support ──────────────────────────────────────────────────────────

    /// <summary>The current theme applied to this viewer instance.</summary>
    private DiffViewerTheme _theme;

    /// <summary>All live instances — used to propagate theme changes.</summary>
    private static readonly List<WeakReference<FastDiffViewer>> _instances = new();

    /// <summary>Current font scale factor (applied to all font sizes in viewer).</summary>
    private double _fontScale = 1.0;

    // ── Syntax coloring state ──────────────────────────────────────────────────

    /// <summary>Whether Visual Studio-style syntax coloring is enabled (app-wide toggle).</summary>
    private bool _syntaxEnabled;

    /// <summary>
    /// Per-line coloured tokens, parallel to <see cref="Lines"/>. A null entry means that
    /// line has no coloured tokens (rendered entirely in the diff status foreground).
    /// A null array means not computed (disabled, hex view, unsupported extension, or the
    /// file is above the line-count guard).
    /// </summary>
    private List<SyntaxToken>?[]? _syntaxTokens;

    /// <summary>
    /// Apply a new theme to all existing FastDiffViewer instances and set it as
    /// the default for future instances.
    /// </summary>
    public static void ApplyTheme(string themeName, double fontScale = 1.0)
    {
        // Create routes through the named factories and overlays any registered
        // per-theme custom colours (ThemeManager keeps those in sync with settings).
        var newTheme = DiffViewerTheme.Create(themeName, ThemeManager.GetCustomColors(themeName));

        lock (_instances)
        {
            foreach (var weak in _instances)
            {
                if (weak.TryGetTarget(out var viewer))
                {
                    viewer.AssignTheme(newTheme, fontScale);
                }
            }
        }

        // Clean up dead weak references
        lock (_instances)
        {
            _instances.RemoveAll(w => w.TryGetTarget(out _) == false);
        }
    }

    /// <summary>
    /// Enable or disable syntax coloring on all existing FastDiffViewer instances.
    /// Each instance recomputes (or drops) its token table.
    /// </summary>
    public static void SetSyntaxColoring(bool enabled)
    {
        lock (_instances)
        {
            foreach (var weak in _instances)
            {
                if (weak.TryGetTarget(out var viewer))
                    viewer.AssignSyntaxColoring(enabled);
            }

            // Clean up dead weak references
            _instances.RemoveAll(w => w.TryGetTarget(out _) == false);
        }
    }

    /// <summary>
    /// Binary search for a value in a sorted list.
    /// </summary>
    private static bool BinarySearch(IReadOnlyList<int> sorted, int value)
    {
        int lo = 0, hi = sorted.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (sorted[mid] == value) return true;
            if (sorted[mid] < value) lo = mid + 1;
            else hi = mid - 1;
        }
        return false;
    }

    // ── Text Formats (reused for all text rendering) ───────────────────────────

    private Typeface? _gutterTypeface;
    private Typeface? _contentTypeface;
    private Typeface? _titleTypeface;
    private TextFormattingMode _textFormatting = TextFormattingMode.Display;

    // ── Precomputed character advance width (monospace) ────────────────────────
    // Measured once from the content typeface, used for arithmetic position calculation
    // instead of per-frame BuildHighlightGeometry calls.
    private double _charAdvance = 0;

    // ── Cached FormattedText for static labels (avoid per-frame allocation) ────
    private FormattedText? _syncPointMarker;
    private FormattedText? _pendingLabel;

    // ── Text Cache (bounded LRU via ordered dictionary) ────────────────────────

    /// <summary>
    /// Cached FormattedText objects for line content. Bounded to prevent memory growth
    /// with very large files — evicts oldest entries when exceeding the limit.
    /// </summary>
    private readonly LruFormattedTextCache _textCache = new(maxEntries: 10_000);

    // Separate LRU for gutter text — it uses the SemiBold gutter typeface, which the
    // content cache's key doesn't distinguish, so the two must not share entries.
    private readonly LruFormattedTextCache _gutterCache = new(maxEntries: 4096);

    // Hex-view text tone variants: normal hex text is brightened slightly, 0x00 bytes are drawn
    // darker than their neighbours — so null runs read as clearly dimmer in every theme. Keyed
    // by brush instance; theme switches replace the instances, so stale entries are just unused.
    private readonly Dictionary<Brush, Brush> _hexBrightBrushCache = new();
    private readonly Dictionary<Brush, Brush> _hexDimBrushCache = new();

    // ── Gap stripe brush (tiled 45° stripes for gap lines) ─────────────────────
    // A DrawingBrush that tiles a single diagonal from a 10×10 tile. One DrawRectangle
    // per gap line replaces ~200 individual DrawLine calls in fullscreen. Not frozen:
    // ViewportOrigin is updated each frame so the pattern stays locked to content
    // (not the viewport) while scrolling.
    private DrawingBrush? _gapStripeBrush;

    // ── Dependency Properties ──────────────────────────────────────────────────

    public static readonly DependencyProperty LinesProperty =
        DependencyProperty.Register(nameof(Lines), typeof(IReadOnlyList<DiffLine>), typeof(FastDiffViewer),
            new PropertyMetadata(null, OnLinesChanged));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(FastDiffViewer),
            new PropertyMetadata("(no file loaded)"));

    /// <summary>
    /// File path the displayed lines came from. Used only for syntax language detection
    /// (by extension) — changing it re-tokenizes when syntax coloring is enabled.
    /// </summary>
    public static readonly DependencyProperty FilePathProperty =
        DependencyProperty.Register(nameof(FilePath), typeof(string), typeof(FastDiffViewer),
            new PropertyMetadata((string?)null, (s, e) =>
            {
                if (s is FastDiffViewer v && e.OldValue as string != e.NewValue as string)
                    v.RecomputeSyntaxTokens();
            }));

    public static readonly DependencyProperty MetaProperty =
        DependencyProperty.Register(nameof(Meta), typeof(string), typeof(FastDiffViewer),
            new PropertyMetadata(""));

    public static readonly DependencyProperty ShowNoDifferencesProperty =
        DependencyProperty.Register(nameof(ShowNoDifferences), typeof(bool), typeof(FastDiffViewer),
            new PropertyMetadata(false, (s, e) =>
            {
                if (s is FastDiffViewer v)
                {
                    v._diffMapDirty = true;
                    v.InvalidateVisual();
                }
            }));

    /// <summary>
    /// Git-style change glyphs for the unified layout: Removed rows render a red "-n" in the
    /// gutter, Added rows a green "+m". Default false so existing two-pane viewers are unaffected.
    /// </summary>
    public static readonly DependencyProperty ShowChangeGlyphsProperty =
        DependencyProperty.Register(nameof(ShowChangeGlyphs), typeof(bool), typeof(FastDiffViewer),
            new PropertyMetadata(false, (s, e) =>
            {
                if (s is FastDiffViewer v)
                    v.InvalidateVisual();
            }));

    /// <summary>
    /// Set from outside to sync scroll position with the partner viewer.
    /// When this changes, scroll to match and re-render immediately.
    /// </summary>
    public static readonly DependencyProperty SyncScrollRatioProperty =
        DependencyProperty.Register(nameof(SyncScrollRatio), typeof(double?), typeof(FastDiffViewer),
            new PropertyMetadata(null, OnSyncScrollRatioChanged));

    /// <summary>
    /// Set from outside to sync horizontal scroll position with the partner viewer.
    /// </summary>
    public static readonly DependencyProperty SyncHorizontalScrollRatioProperty =
        DependencyProperty.Register(nameof(SyncHorizontalScrollRatio), typeof(double?), typeof(FastDiffViewer),
            new PropertyMetadata(null, OnSyncHorizontalScrollRatioChanged));

    /// <summary>
    /// Index of the currently selected/hilited diff line. -1 means no selection.
    /// </summary>
    public static readonly DependencyProperty SelectedLineIndexProperty =
        DependencyProperty.Register(nameof(SelectedLineIndex), typeof(int), typeof(FastDiffViewer),
            new PropertyMetadata(-1, (s, e) =>
            {
                if (s is FastDiffViewer v && e.NewValue is int newVal && e.OldValue is int oldVal && newVal != oldVal)
                {
                    v.InvalidateVisual();
                    v.SelectionIndexChanged?.Invoke(v, newVal);
                }
            }));

    /// <summary>
    /// Raised when the selected line index changes (for partner viewer sync).
    /// </summary>
    public event EventHandler<int>? SelectionIndexChanged;

    /// <summary>
    /// Current search query text. When set, matching lines are highlighted in OnRender.
    /// </summary>
    public static readonly DependencyProperty SearchQueryProperty =
        DependencyProperty.Register(nameof(SearchQuery), typeof(string), typeof(FastDiffViewer),
            new PropertyMetadata((string?)null, (s, e) =>
            {
                if (s is FastDiffViewer v)
                {
                    // Reset to the first match when query changes
                    if (!string.IsNullOrEmpty(e.NewValue as string))
                        v.CurrentSearchMatchIndex = -1;
                    v.InvalidateVisual();
                }
            }));

    /// <summary>
    /// Index of the currently active search match line. -1 means no current match.
    /// </summary>
    public static readonly DependencyProperty CurrentSearchMatchIndexProperty =
        DependencyProperty.Register(nameof(CurrentSearchMatchIndex), typeof(int), typeof(FastDiffViewer),
            new PropertyMetadata(-1, (s, e) =>
            {
                if (s is FastDiffViewer v)
                    v.InvalidateVisual();
            }));

    /// <summary>
    /// When true, a visual overlay border is drawn to indicate the viewer is the drop target.
    /// </summary>
    public static readonly DependencyProperty IsDragOverProperty =
        DependencyProperty.Register(nameof(IsDragOver), typeof(bool), typeof(FastDiffViewer),
            new PropertyMetadata(false, (s, e) =>
            {
                if (s is FastDiffViewer v)
                    v.InvalidateVisual();
            }));

    // ── Sync Point DPs ────────────────────────────────────────────────────────

    /// <summary>
    /// Display-line index for a pending sync point placement (-1 = none).
    /// When ≥ 0, the line is highlighted with a cyan indicator showing where
    /// a sync point will be placed upon confirmation.
    /// </summary>
    public static readonly DependencyProperty PendingSyncLineProperty =
        DependencyProperty.Register(nameof(PendingSyncLine), typeof(int), typeof(FastDiffViewer),
            new PropertyMetadata(-1, (s, e) =>
            {
                if (s is FastDiffViewer v)
                    v.InvalidateVisual();
            }));

    /// <summary>
    /// Collection of display-line indices that are active sync points.
    /// These lines receive a small link marker in the gutter.
    /// </summary>
    public static readonly DependencyProperty SyncPointLinesProperty =
        DependencyProperty.Register(nameof(SyncPointLines), typeof(IReadOnlyList<int>), typeof(FastDiffViewer),
            new PropertyMetadata(null, (s, e) =>
            {
                if (s is FastDiffViewer v)
                {
                    v._diffMapDirty = true;
                    v.InvalidateVisual();
                }
            }));

    public int PendingSyncLine
    {
        get => (int)GetValue(PendingSyncLineProperty);
        set => SetValue(PendingSyncLineProperty, value);
    }

    public IReadOnlyList<int>? SyncPointLines
    {
        get => (IReadOnlyList<int>?)GetValue(SyncPointLinesProperty);
        set => SetValue(SyncPointLinesProperty, value);
    }

    public IReadOnlyList<DiffLine>? Lines
    {
        get => (IReadOnlyList<DiffLine>?)GetValue(LinesProperty);
        set => SetValue(LinesProperty, value);
    }

    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? FilePath
    {
        get => (string?)GetValue(FilePathProperty);
        set => SetValue(FilePathProperty, value);
    }

    public string? Meta
    {
        get => (string?)GetValue(MetaProperty);
        set => SetValue(MetaProperty, value);
    }

    public bool ShowNoDifferences
    {
        get => (bool)GetValue(ShowNoDifferencesProperty);
        set => SetValue(ShowNoDifferencesProperty, value);
    }

    public bool ShowChangeGlyphs
    {
        get => (bool)GetValue(ShowChangeGlyphsProperty);
        set => SetValue(ShowChangeGlyphsProperty, value);
    }

    public double? SyncScrollRatio
    {
        get => (double?)GetValue(SyncScrollRatioProperty);
        set => SetValue(SyncScrollRatioProperty, value);
    }

    public double? SyncHorizontalScrollRatio
    {
        get => (double?)GetValue(SyncHorizontalScrollRatioProperty);
        set => SetValue(SyncHorizontalScrollRatioProperty, value);
    }

    public int SelectedLineIndex
    {
        get => (int)GetValue(SelectedLineIndexProperty);
        set => SetValue(SelectedLineIndexProperty, value);
    }

    public string? SearchQuery
    {
        get => (string?)GetValue(SearchQueryProperty);
        set => SetValue(SearchQueryProperty, value);
    }

    public int CurrentSearchMatchIndex
    {
        get => (int)GetValue(CurrentSearchMatchIndexProperty);
        set => SetValue(CurrentSearchMatchIndexProperty, value);
    }

    public bool IsDragOver
    {
        get => (bool)GetValue(IsDragOverProperty);
        set => SetValue(IsDragOverProperty, value);
    }

    private static void OnLinesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FastDiffViewer viewer)
        {
            // Preserve the current scroll ratio so that when lines are reassigned
            // (e.g., after toggling layout, settings, or re-diff), the viewer
            // stays at roughly the same position instead of jumping to the top.
            double previousRatio = viewer.GetScrollRatio();

            viewer.SelectedLineIndex = -1;
            viewer.SearchQuery = null;
            viewer.CurrentSearchMatchIndex = -1;
            viewer._selectionStart = -1;
            viewer._selectionEnd = -1;
            viewer._byteSelStart = null;
            viewer._byteSelEnd = null;
            viewer._anchorSlot = null; // Highlight refers to the old file's line positions
            viewer.HideStructuralTooltip();   // rows no longer match an open tooltip
            viewer.RecalculateExtent();
            // ScanDiffBlocks is deferred to OnRender (line 1079) so that collapsed
            // viewers never scan — they don't render, so the scan is skipped entirely.
            // RecalculateExtent() sets _diffMapDirty = true, ensuring OnRender knows.

            // Restore the scroll ratio against the new content extent
            double maxOffset = Math.Max(0, viewer._scrollExtent - (viewer._viewportHeight - viewer._titleBarHeight));
            viewer._scrollOffset = previousRatio * maxOffset;
            viewer.RecomputeSyntaxTokens();
            viewer.InvalidateVisual();
            viewer.RaiseSelectionChanged();
        }
    }

    private static void OnSyncScrollRatioChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FastDiffViewer viewer && e.NewValue is double ratio)
        {
            // Prevent feedback loop: only apply if it differs from current position
            double currentRatio = viewer.GetScrollRatio();
            if (Math.Abs(currentRatio - ratio) > 0.001)
            {
                viewer._scrollOffset = ratio * (viewer._scrollExtent - viewer._viewportHeight);
                viewer._scrollOffset = Math.Clamp(viewer._scrollOffset, 0, Math.Max(0, viewer._scrollExtent - viewer._viewportHeight));
                viewer.InvalidateVisual();
            }
        }
    }

    private static void OnSyncHorizontalScrollRatioChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FastDiffViewer viewer && e.NewValue is double ratio)
        {
            double currentRatio = viewer.GetHorizontalScrollRatio();
            if (Math.Abs(currentRatio - ratio) > 0.001)
            {
                double hTrack = viewer._hScrollTrackWidth();
                double scrollable = viewer._contentMaxWidth - hTrack;
                viewer._hScrollOffset = ratio * scrollable;
                viewer._hScrollOffset = Math.Clamp(viewer._hScrollOffset, 0, Math.Max(0, scrollable));
                viewer.InvalidateVisual();
            }
        }
    }

    // ── Scroll State ───────────────────────────────────────────────────────────

    private double _scrollOffset;         // Current vertical scroll in pixels
    private double _scrollExtent;         // Total content height in pixels

    // ── Horizontal Scroll State ────────────────────────────────────────────────

    private double _hScrollOffset;        // Pixels scrolled to the right
    private double _contentMaxWidth;      // Widest content line in px (excluding gutter)
    private Rect _hScrollThumbRect;       // Hit region for horizontal thumb
    private Rect _hScrollTrackRect;       // Track region for horizontal scrollbar

    // ── Diff Map (overview mini-map showing where diffs are) ────────────────────

    /// <summary>A contiguous block of diffs in the file.</summary>
    private readonly struct DiffBlock(int startLine, int endLine, DiffStatus status, bool resolved)
    {
        public readonly int StartLine = startLine;  // List index (first line of block)
        public readonly int EndLine = endLine;      // List index (last line of block)
        public readonly DiffStatus Status = status;
        public readonly bool Resolved = resolved;   // winner rows of a resolved conflict slot
    }

    private List<DiffBlock> _diffBlocks = new();
    private Rect _diffMapRect = Rect.Empty; // Hit region for diff map bar
    private bool _diffMapDirty;          // True when blocks need re-scanning
    private int _lastScannedLineCount;   // Line count of last scan — detects stale data
    private bool _hasGapLines;           // Lines contain Gap rows (gap mode) — suppresses anchor markers
    private (int Slot, DiffStatus Status)? _anchorSlot; // Partner-gap highlight: row boundary where a gap would sit

    // ── Layout State ───────────────────────────────────────────────────────────

    private int _viewportHeight;          // Available rendering height (minus title bar)
    private int _viewportWidth;           // Available rendering width
    private int _titleBarHeight = 30;     // Title + meta bar height
    private bool _hasContent;             // Whether there are lines to render

    // ── Scrollbar Interaction State ────────────────────────────────────────────

    private enum ScrollDragMode { None, Thumb, TrackUp, TrackDown, ContentDrag, SelectionDrag, HThumb }
    private ScrollDragMode _dragMode = ScrollDragMode.None;
    private double _dragStartY;           // Mouse Y at drag start (local coords)
    private double _dragStartOffset;      // Scroll offset at drag start
    private bool _isScrollThumbHovered;

    // ── Text Selection State ─────────────────────────────────────────────────────
    private int _selectionStart = -1;     // Anchor line of selection (-1 = no selection)
    private int _selectionEnd = -1;       // Current end of selection (follows mouse during drag)
    private static int s_selectionSeq;    // Global monotonic counter across all viewers
    private int _selectionSeq;            // This viewer's stamp — higher means more recent change

    // ── Byte-Level Selection State (hex view inspector) ───────────────────────
    // (line, slot): the row index and the 0-15 hex slot of the selected byte. Slots are
    // mask-aware — an aligned row may have holes, so a slot is only valid when its bit is
    // set in that row's HexSlotMask.
    private (int line, int slot)? _byteSelStart;
    private (int line, int slot)? _byteSelEnd;

    /// <summary>
    /// Type size in bytes from the hex inspector. Used to snap selections and highlights.
    /// </summary>
    public int InspectorTypeSize { get; set; } = 1;

    // ── Sync Scroll Event ──────────────────────────────────────────────────────

    /// <summary>
    /// Raised when the scroll position changes (for partner viewer sync).
    /// </summary>
    public event EventHandler<double>? ScrollRatioChanged;

    /// <summary>
    /// Raised when the horizontal scroll position changes (for partner viewer sync).
    /// </summary>
    public event EventHandler<double>? HorizontalScrollRatioChanged;

    /// <summary>
    /// Raised when the user right-clicks on a diff line, requesting an "Apply" action.
    /// The event argument contains all currently selected line indices.
    /// </summary>
    public event EventHandler<List<int>>? RightClickLineIndex;
    /// <summary>
    /// Raised when the user right-clicks any line (regardless of diff status).
    /// Used for sync point placement and other general line operations.
    /// </summary>
    public event EventHandler<int>? RightClickAnyLine;

    /// <summary>
    /// Raised when the user single-left-clicks a line (not a drag/selection).
    /// Used for confirming sync points and other single-click actions.
    /// </summary>
    public event EventHandler<int>? SingleClickedLine;

    /// <summary>
    /// Hex view only: raised when the user right-clicks any row of the hex content area,
    /// regardless of diff status. Payload: (row index within this viewer's Lines, slot 0-15) —
    /// slot is -1 when the click did not land on an occupied byte slot (Gap filler row or a hole
    /// in an aligned row). Used for placing byte-granularity sync points and selection-scoped
    /// actions like CRC-32 over the selected bytes.
    /// </summary>
    public event EventHandler<(int LineIndex, int Slot)>? HexRightClicked;

    /// <summary>
    /// Hex view only: raised when the user single-left-clicks an occupied byte slot (no drag).
    /// Payload: (row index, slot 0-15). Used for confirming pending hex sync points.
    /// </summary>
    public event EventHandler<(int LineIndex, int Slot)>? HexSlotSingleClicked;

    /// <summary>
    /// Raised when the user double-clicks an Added/Removed block while the cross-pane anchor
    /// markers are visible. Payload: (slot, status) — slot is the block's OtherSideAnchor
    /// (count of real lines in the partner file before the change point, i.e. the row index
    /// where a Gap would be inserted) and status picks the highlight colour.
    /// </summary>
    public event Action<int, DiffStatus>? AnchorJumpRequested;

    /// <summary>
    /// Raised when the selection range changes (on click, drag end, or clear).
    /// Provides the selected line indices for external consumers like hex inspectors.
    /// </summary>
    public event EventHandler<List<int>>? SelectionChanged;

    // ── Constructor ────────────────────────────────────────────────────────────

    public FastDiffViewer()
    {
        // Initialize with the app-wide theme state and register this instance. LoadSettings
        // runs before window XAML is parsed, so CurrentThemeName/FontScale/custom colours
        // already reflect saved settings at construction time.
        _theme = DiffViewerTheme.Create(ThemeManager.CurrentThemeName,
            ThemeManager.GetCustomColors(ThemeManager.CurrentThemeName));
        _fontScale = ThemeManager.FontScale;
        // Pick up the app-wide toggle state — a window opened after settings were
        // restored at startup must not start with syntax coloring off.
        _syntaxEnabled = ThemeManager.UseSyntaxColoring;
        lock (_instances)
        {
            _instances.Add(new WeakReference<FastDiffViewer>(this));
        }

        // Ensure typefaces are created on the UI thread
        CoerceTypefaces();
    }

    /// <summary>
    /// Assign a new theme to this viewer instance, recomputing cached text and typefaces.
    /// </summary>
    private void AssignTheme(DiffViewerTheme newTheme, double fontScale)
    {
        _theme = newTheme;
        _fontScale = fontScale;
        _textCache.Clear();
        _gutterCache.Clear();
        CoerceTypefaces();
        InvalidateVisual();
    }

    /// <summary>
    /// Update only the font scale for this instance (keeps current theme colors).
    /// </summary>
    private void AssignFontScale(double fontScale)
    {
        _fontScale = fontScale;
        _textCache.Clear();
        _gutterCache.Clear();
        CoerceTypefaces();
        InvalidateVisual();
    }

    /// <summary>
    /// Enable or disable syntax coloring on this instance. Toggling recomputes (or drops)
    /// the token table; no cache clear needed — coloured entries age out via LRU eviction.
    /// </summary>
    private void AssignSyntaxColoring(bool enabled)
    {
        if (_syntaxEnabled == enabled) return;
        _syntaxEnabled = enabled;
        RecomputeSyntaxTokens();
        InvalidateVisual();
    }

    /// <summary>
    /// (Re)builds the per-line syntax token table from the current Lines + FilePath.
    /// No-ops when coloring is disabled, in hex view, for unsupported extensions, or above
    /// the line-count guard. A fresh tokenizer resets block-comment state, so a re-tokenize
    /// always starts clean.
    /// </summary>
    private void RecomputeSyntaxTokens()
    {
        var lines = Lines;

        if (!_syntaxEnabled || IsHexView || lines == null || lines.Count == 0
            || lines.Count > SyntaxHighlighter.MaxLines)
        {
            _syntaxTokens = null;
            return;
        }

        var tokenizer = SyntaxHighlighter.Create(FilePath);
        if (tokenizer == null)
        {
            _syntaxTokens = null;
            return;
        }

        var tokens = new List<SyntaxToken>?[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            string? raw = lines[i].Text;
            if (string.IsNullOrEmpty(raw)) continue;   // null entry — nothing to color

            // Tokenize the tab-expanded render copy so token indices match the arithmetic
            // span positioning in OnRender (spaces are never tokens, so kinds are unchanged).
            var (text, _) = GetTabExpanded(raw);
            var lineTokens = tokenizer.TokenizeLine(text);
            tokens[i] = lineTokens.Count > 0 ? lineTokens : null;
        }

        _syntaxTokens = tokens;
    }

    // ── Tab expansion (rendering only) ─────────────────────────────────────────
    // FormattedText renders '\t' at its default tab stop — several character cells wide —
    // while every span/region in OnRender is positioned arithmetically at col * _charAdvance
    // (one cell per char). On a tab-indented line the early spans therefore overflow into
    // the later spans' positions and the text renders as an illegible overlap. The rendered
    // copy of each line expands tabs to fixed-width space runs so glyphs and arithmetic
    // agree. Original DiffLine.Text is never modified — diff/merge output keeps real tabs.

    private const int RenderTabWidth = 4;
    private readonly Dictionary<string, (string expanded, int[] map)> _tabExpansion = new();

    /// <summary>
    /// Render copy of <paramref name="text"/> with tabs expanded to spaces, plus a map from
    /// original char index → expanded index. The map is null when the text has no tabs —
    /// in that case the text is returned as-is and indices are identical (identity map).
    /// Keyed by content so repeated lines share one expansion; bounded for huge files.
    /// </summary>
    private (string text, int[]? map) GetTabExpanded(string text)
    {
        if (text.IndexOf('\t') < 0) return (text, null);
        if (_tabExpansion.TryGetValue(text, out var hit)) return (hit.expanded, hit.map);

        var sb = new StringBuilder(text.Length + RenderTabWidth * 4);
        // Boundary map: one entry per raw index PLUS a sentinel at text.Length holding the
        // total expanded length. ChangedRegions may end exactly at end-of-line (e.g. the
        // whole-line region (0, text.Length)), so callers index map[start + length] with
        // start + length == text.Length — without the sentinel that is an out-of-bounds read.
        var map = new int[text.Length + 1];
        for (int i = 0; i < text.Length; i++)
        {
            map[i] = sb.Length;
            if (text[i] == '\t') sb.Append(' ', RenderTabWidth);
            else sb.Append(text[i]);
        }
        map[text.Length] = sb.Length;

        if (_tabExpansion.Count > 20_000) _tabExpansion.Clear(); // bound memory on huge files
        var result = (sb.ToString(), map);
        _tabExpansion[text] = result;
        return result;
    }

    /// <summary>
    /// Draws one syntax span (a substring of a monospace line) at character column `col`
    /// using the shared text cache.
    /// </summary>
    /// <summary>
    /// Hex-view tone variants of a foreground brush, cached per brush instance. Brightening and
    /// dimming are pure luminance scales (toward white / toward black), so the result is correct
    /// in every theme without knowing the row background — blending toward BgModified on
    /// per-byte rows used to *brighten* null bytes against their grey neighbours.
    /// </summary>
    private Brush GetHexBrightBrush(Brush brush) => AdjustHexTone(brush, _hexBrightBrushCache, c => Color.FromArgb(c.A,
        (byte)Math.Min(255, c.R + (255 - c.R) * 0.15),
        (byte)Math.Min(255, c.G + (255 - c.G) * 0.15),
        (byte)Math.Min(255, c.B + (255 - c.B) * 0.15)));

    private Brush GetHexDimBrush(Brush brush) => AdjustHexTone(brush, _hexDimBrushCache, c => Color.FromArgb(c.A,
        (byte)(c.R * 0.6), (byte)(c.G * 0.6), (byte)(c.B * 0.6)));

    private static Brush AdjustHexTone(Brush brush, Dictionary<Brush, Brush> cache, Func<Color, Color> adjust)
    {
        if (cache.TryGetValue(brush, out var cached)) return cached;

        Brush result = brush;
        if (brush is SolidColorBrush sb && sb.Color.A > 0)
        {
            var adj = new SolidColorBrush(adjust(sb.Color));
            adj.Freeze();
            result = adj;
        }

        cache[brush] = result;
        return result;
    }

    private void DrawSpan(DrawingContext dc, string text, int col, int length, Brush brush, double hTextX, double y)
    {
        var ft = _textCache.GetOrCreate(text.Substring(col, length), brush, _contentTypeface!, _textFormatting, _theme.BaseFontSize * _fontScale);
        dc.DrawText(ft, new Point(hTextX + col * _charAdvance, y + 2));
    }

   private void CoerceTypefaces()
    {
        var defaultFamily = new FontFamily("Segoe UI");
        var monoFamily = new FontFamily("Consolas");
        _gutterTypeface = new Typeface(monoFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        _contentTypeface = new Typeface(monoFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _titleTypeface = new Typeface(defaultFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        double contentSize = _theme.BaseFontSize * _fontScale;

        // Precompute character advance width for monospace font (arithmetic positioning in OnRender)
        var sample = new FormattedText("X", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _contentTypeface, contentSize, _theme.BgPanel, 96.0);
        _charAdvance = sample.Width;

        // Cache static labels to avoid per-frame FormattedText allocation
        double gutterSize = _theme.GutterFontSize * _fontScale;
        _syncPointMarker = new FormattedText("◈ SYNC", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _gutterTypeface, gutterSize, _theme.SyncPointMarkerText, 96.0);
        _pendingLabel = new FormattedText("◀ PENDING", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _contentTypeface, contentSize, _theme.PendingSyncBorderBrush, 96.0);

        BuildGapStripeBrush(); // pen comes from the theme — rebuild on every change
    }

    /// <summary>
    /// Builds the tiled DrawingBrush used to paint gap-line stripes. The tile is one stripe
    /// spacing (10px) square containing a single corner-to-corner diagonal; the family of
    /// lines x − y ≡ c (mod 10) tiles seamlessly corner-to-corner, so one DrawRectangle per
    /// gap row replaces ~200 individual DrawLine calls in fullscreen.
    /// </summary>
    private void BuildGapStripeBrush()
    {
        const double spacing = 20; // stripe spacing (px) — fewer, thicker lines

        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
            dc.DrawLine(_theme.GapStripePen, new Point(0, 0), new Point(spacing, spacing));
        drawing.Freeze();

        _gapStripeBrush = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, spacing, spacing),
            Stretch = Stretch.None
        };
    }

    // ── MeasureOverride: Fixed geometry — no measurement needed ────────────────

    protected override Size MeasureOverride(Size availableSize)
    {
        // We need at least enough space for the title bar and a few lines
        return new Size(
            Math.Min(availableSize.Width, 10000),
            Math.Min(availableSize.Height, 10000));
    }

    // ── ArrangeOverride: Store viewport dimensions ─────────────────────────────

    protected override Size ArrangeOverride(Size arrangeBounds)
    {
        _viewportWidth = (int)arrangeBounds.Width;
        _viewportHeight = (int)arrangeBounds.Height;

        // If viewport changed significantly, re-render
        InvalidateVisual();
        return arrangeBounds;
    }

    // ── Recalculate total content height ───────────────────────────────────────

     private void RecalculateExtent()
    {
        var lines = Lines;
        int lineCount = lines?.Count ?? 0;
        _scrollExtent = lineCount * LineHeight;
        _hasContent = lineCount > 0;

        // Clamp vertical scroll offset
        double maxOffset = Math.Max(0, _scrollExtent - (_viewportHeight - _titleBarHeight));
        _scrollOffset = Math.Clamp(_scrollOffset, 0, maxOffset);

        // Width: do NOT measure lines here. RecalculateExtent runs on the UI thread
        // during swaps/layout changes — measuring visible lines (hundreds when fullscreen)
        // creates FormattedText objects synchronously, blocking the UI.
        // Instead, start with a rough estimate and let OnRender track widths lazily
        // as lines are drawn. The h-scrollbar will adapt on subsequent renders.
        if (_contentMaxWidth <= 0)
            _contentMaxWidth = 960; // Rough estimate (~120 chars at typical font size)

        // Clamp horizontal scroll offset
        double hScrollable = Math.Max(0, _contentMaxWidth - _hScrollTrackWidth());
        _hScrollOffset = Math.Clamp(_hScrollOffset, 0, hScrollable);

        // Mark diff map as needing re-scan (statuses may have changed even if line count didn't)
        _diffMapDirty = true;
    }

    /// <summary>
    /// Available width for the horizontal scroll track (viewport minus gutter and vertical scrollbar area).
    /// </summary>
    private double _hScrollTrackWidth()
    {
        return Math.Max(0, _viewportWidth - GutterWidth - ScrollbarWidth - DiffMapWidth);
    }

    // ── Diff Map: Scan for contiguous diff blocks ─────────────────────────────

    /// <summary>
    /// Scans the Lines list to find contiguous blocks of diffs and computes
    /// the max original line count (total lines in this file) for proportional scaling.
    /// </summary>
    private void ScanDiffBlocks()
    {
        int lineCount = Lines?.Count ?? 0;

        // Skip if nothing changed since last scan
        if (lineCount == _lastScannedLineCount && !_diffMapDirty) return;

        _diffBlocks.Clear();
        _diffMapDirty = false;
        _hasGapLines = false;

        if (lineCount == 0) return;

        // Single pass: group contiguous non-Unchanged lines into blocks, keyed by display
        // index. DrawDiffMap positions them proportionally to the displayed content without
        // re-scanning on each render frame. Gap rows occupy their own rows in the list, so
        // gap blocks naturally get a map region sized in proportion to the missing lines.
        int blockStart = -1;
        DiffStatus? blockStatus = null;
        bool? blockResolved = null;

        void CloseBlock(int endLine)
        {
            _diffBlocks.Add(new DiffBlock(blockStart, endLine, blockStatus!.Value, blockResolved ?? false));
            blockStart = -1;
            blockStatus = null;
            blockResolved = null;
        }

        var lines = Lines!;
        for (int i = 0; i < lineCount; i++)
        {
            if (lines[i].Status == DiffStatus.Gap) _hasGapLines = true;

            if (lines[i].Status == DiffStatus.Unchanged)
            {
                if (blockStart >= 0 && blockStatus.HasValue)
                    CloseBlock(i - 1);
            }
            else if (lines[i].Status != blockStatus || lines[i].ResolvedConflict != blockResolved)
            {
                // Resolved-conflict winner rows share a change status with plain changes but get
                // their own map marker, so the resolved mark is part of the block key.
                if (blockStart >= 0 && blockStatus.HasValue)
                    CloseBlock(i - 1);
                blockStart = i;
                blockStatus = lines[i].Status;
                blockResolved = lines[i].ResolvedConflict;
            }
        }

        if (blockStart >= 0 && blockStatus.HasValue)
            CloseBlock(lineCount - 1);

        _lastScannedLineCount = lineCount;
    }

    // ── Diff Map: Draw the overview bar ────────────────────────────────────────

    /// <summary>
    /// Draws a thin bar showing colored markers for diff blocks, positioned proportionally
    /// to their location in the displayed content (display index / total displayed lines).
    /// </summary>
    private void DrawDiffMap(DrawingContext dc, int contentTop, int contentHeight)
    {
        if (_diffBlocks.Count == 0 || Lines is null || Lines.Count == 0) return;

        double mapX = _viewportWidth - ScrollbarWidth - DiffMapWidth;
        double mapY = contentTop;
        double mapW = DiffMapWidth;
        double mapH = contentHeight;

        // Subtle background for the bar
        dc.DrawRectangle(_theme.DiffMapBg, null, new Rect(mapX, mapY, mapW, mapH));

        int lineCount = Lines.Count;

        // Map each diff block to a proportional position on the bar.
        // Position by display index, not original line number: in gap mode, gap rows
        // occupy their own rows, so gap regions are sized in proportion to the missing
        // lines and the whole map stays exactly proportional to scroll position.
        // (In non-gap mode display index == original line − 1, so this is unchanged.)
        foreach (var block in _diffBlocks)
        {
            double topRatio = block.StartLine / (double)lineCount;
            double bottomRatio = (block.EndLine + 1) / (double)lineCount;

            double markerTop = mapY + topRatio * mapH;
            double markerBottom = mapY + bottomRatio * mapH;
            double markerHeight = Math.Max(2, markerBottom - markerTop); // Minimum 2px visibility

           Brush color = block.Resolved
            ? _theme.DiffMapConflictResolved   // resolved-conflict winner rows — "decided" green
            : block.Status switch
            {
                DiffStatus.Added => _theme.DiffMapAdded,
                DiffStatus.Removed => _theme.DiffMapRemoved,
                DiffStatus.Modified => _theme.DiffMapModified,
                DiffStatus.Gap => _theme.DiffMapGap,
                DiffStatus.ChangeA => _theme.DiffMapChangeA,
                DiffStatus.ChangeB => _theme.DiffMapChangeB,
                DiffStatus.Conflict => _theme.DiffMapConflict,
                _ => _theme.DiffMapDefault
            };

            // Soft glow behind the marker (extends slightly beyond bar edges)
            dc.DrawRectangle(color, null,
                new Rect(mapX - 0.5, markerTop - 1, mapW + 1, markerHeight + 2));

            // Draw rounded marker filling the bar width
            dc.DrawRoundedRectangle(color, null,
                new Rect(mapX + 0.5, markerTop, mapW - 1, markerHeight), 1, 1);
        }

        // Scroll position indicator — drawn AFTER the markers so they never cover it.
        // Glow → contrasting ring → dot keeps it visible over any marker color.
        double scrollRatio = _scrollExtent > contentHeight
            ? _scrollOffset / (_scrollExtent - contentHeight)
            : 0;
        scrollRatio = Math.Clamp(scrollRatio, 0, 1);

        // Indicator size proportional to viewport fraction of total content
        double indicatorSize = _scrollExtent > 0
            ? Math.Max(3, Math.Min(contentHeight / _scrollExtent * mapH, mapH * 0.15))
            : Math.Max(3, mapH * 0.15);
        double indicatorY = mapY + scrollRatio * (mapH - indicatorSize);
        var indicatorCenter = new Point(mapX + mapW / 2, indicatorY + indicatorSize / 2);

        dc.DrawEllipse(_theme.DiffMapScrollIndicator, null, indicatorCenter, mapW / 2 + 2, indicatorSize / 2 + 2);
        dc.DrawEllipse(_theme.BgPanel, null, indicatorCenter, mapW / 2 + 1, indicatorSize / 2 + 1);
        dc.DrawEllipse(_theme.DiffMapScrollIndicator, null, indicatorCenter, mapW / 2, indicatorSize / 2);
    }

    // ── Cross-pane anchor markers (gapless two-pane mode) ─────────────────────

    /// <summary>
    /// Draws "≈N" chips for visible Added/Removed blocks when gaps are hidden: N is the
    /// block's OtherSideAnchor + 1, the approximate line number where the block sits on the
    /// other side. Chips pin to the right edge of the content area (viewport-sticky, not
    /// content-scrolled) so they stay visible while scrolling horizontally, and sit just
    /// left of the diff map bar so it never covers them.
    /// </summary>
    private void DrawAnchorMarkers(DrawingContext dc, int visibleStart, int visibleEnd, int contentTop, int contentHeight)
    {
        const double chipH = 14;
        double chipRight = _viewportWidth - ScrollbarWidth - DiffMapWidth - 2;

        foreach (var block in _diffBlocks)
        {
            if (block.Status is not (DiffStatus.Added or DiffStatus.Removed)) continue;
            if (block.EndLine < visibleStart || block.StartLine >= visibleEnd) continue;

            int? anchor = Lines![block.StartLine].OtherSideAnchor;
            if (anchor == null) continue;

            bool added = block.Status == DiffStatus.Added;
            var accentBrush = added ? _theme.AccentAdded : _theme.AccentRemoved;
            var fgBrush = added ? _theme.FgAdded : _theme.FgRemoved;

            // Same typeface/size as gutter line numbers — reads as a line-number annotation.
            // LRU-cached: the same anchors repeat on every frame and across both panes.
            var ft = CreateGutterText($"≈{anchor + 1}", fgBrush);

            double chipW = ft.Width + 8;
            if (chipW > chipRight) continue; // degenerate viewport — skip rather than overflow

            double y0 = contentTop + block.StartLine * LineHeight - _scrollOffset;
            // Keep the chip clear of the always-drawn horizontal scrollbar track at the bottom.
            // Max/Min instead of Clamp: on a very short viewport the upper bound can fall
            // below the lower one, and Clamp throws on inverted bounds.
            double chipY = Math.Max(contentTop + 1,
                Math.Min(y0 + (LineHeight - chipH) / 2, contentTop + contentHeight - HScrollbarHeight - chipH - 1));
            double chipX = chipRight - chipW;

            // Accent ring (outer rounded rect) with opaque gutter fill inside — no Pen needed.
            dc.DrawRoundedRectangle(accentBrush, null, new Rect(chipX, chipY, chipW, chipH), 3, 3);
            dc.DrawRoundedRectangle(_theme.BgGutter, null, new Rect(chipX + 1, chipY + 1, chipW - 2, chipH - 2), 2, 2);
            dc.DrawText(ft, new Point(chipX + 4, chipY + (chipH - ft.Height) / 2));
        }
    }

    /// <summary>
    /// Thin accent line at the row boundary where a partner pane's Added/Removed block would be
    /// inserted (set by ShowAnchorSlot). Full content width, 1px tall — reads as "a gap goes
    /// here". Drawn under the same gate as the anchor chips (gapless two-pane mode only).
    /// </summary>
    private void DrawAnchorSlotLine(DrawingContext dc, int contentTop, int contentHeight)
    {
        if (_anchorSlot is not { } slotInfo) return;

        // Boundary after `slot` real lines = top of the row where a Gap would be inserted.
        double y = contentTop + slotInfo.Slot * LineHeight - _scrollOffset;
        // Keep it inside the content area — the h-scrollbar track always draws over its strip.
        if (y < contentTop || y > contentTop + contentHeight - HScrollbarHeight) return;

        var brush = slotInfo.Status == DiffStatus.Added ? _theme.AccentAdded : _theme.AccentRemoved;
        dc.DrawRectangle(brush, null, new Rect(0, y, _viewportWidth - ScrollbarWidth - DiffMapWidth, 1));
    }

    // ── GetScrollRatio: Current position as 0..1 ──────────────────────────────

    /// <summary>
    /// Returns the current scroll position as a ratio from 0 (top) to 1 (bottom).
    /// </summary>
    public double GetScrollRatio()
    {
        int contentHeight = _viewportHeight - _titleBarHeight;
        double scrollable = _scrollExtent - contentHeight;
        if (scrollable <= 0) return 0;
        return _scrollOffset / scrollable;
    }

    /// <summary>
    /// Returns the current horizontal scroll position as a ratio from 0 (left) to 1 (right).
    /// </summary>
    public double GetHorizontalScrollRatio()
    {
        double hTrack = _hScrollTrackWidth();
        double scrollable = _contentMaxWidth - hTrack;
        if (scrollable <= 0) return 0;
        return _hScrollOffset / scrollable;
    }

    // ── OnRender: THE CORE — Direct drawing, zero visual children ──────────────

    protected override void OnRender(DrawingContext dc)
    {
        if (_viewportWidth <= 0 || _viewportHeight <= 0) return;

        int contentTop = _titleBarHeight;
        int contentHeight = _viewportHeight - contentTop;
        int contentWidth = _viewportWidth;

        // ── 1. Fill background ────────────────────────────────────────────────
        dc.DrawRectangle(_theme.BgPanel, null, new Rect(0, 0, _viewportWidth, _viewportHeight));

        if (!_hasContent || Lines is null)
        {
            DrawEmptyState(dc, contentTop, contentHeight, contentWidth);

            // Draw drag-over highlight even for empty panes
            if (IsDragOver)
                DrawDragOverHighlight(dc);
            return;
        }

        // ── 2. Calculate visible line range and horizontal clip ────────────────
        double textX = GutterWidth + AccentWidth + 4;
        double hClipLeft = GutterWidth;
        double hClipRight = _viewportWidth - ScrollbarWidth - DiffMapWidth;
        double hTextX = textX - _hScrollOffset;
        int visibleStart = (int)(_scrollOffset / LineHeight);
        int visibleLineCount = (contentHeight / LineHeight) + 1; // +1 for partial row at bottom
        int visibleEnd = Math.Min(Lines.Count, visibleStart + visibleLineCount);

        // Scan early so _diffBlocks/_hasGapLines are current for the anchor-marker pass below
        // (the later call before DrawDiffMap is a no-op once the scan is clean).
        ScanDiffBlocks();

        if (visibleStart >= Lines.Count)
        {
            // Scrolled past content — just draw empty space
            dc.DrawRectangle(_theme.BgPanel, null, new Rect(0, contentTop, contentWidth, contentHeight));
            DrawScrollbar(dc, contentTop, contentHeight, contentWidth);
            DrawHScrollbar(dc, contentTop, contentHeight, contentWidth);
            ScanDiffBlocks();
            DrawDiffMap(dc, contentTop, contentHeight);
            return;
        }

        // ── 3. Draw accent bars (batched by consecutive status) ───────────────
        DrawBatchedAccentBars(dc, visibleStart, visibleEnd, contentTop);

        // ── 4. Draw line backgrounds and borders ──────────────────────────────
        // Anchor the gap stripe pattern to content coordinates so it scrolls with the
        // lines instead of sliding over them (translating the brush shifts its tile grid).
        if (_gapStripeBrush != null)
            _gapStripeBrush.Transform = new TranslateTransform(-_hScrollOffset, -_scrollOffset);

        const double rejectedOpacity = 0.45; // dimming for rejected merge changes

        for (int i = visibleStart; i < visibleEnd; i++)
        {
            var line = Lines[i];
            double y = contentTop + i * LineHeight - _scrollOffset;

            if (line.Rejected) dc.PushOpacity(rejectedOpacity);

            // Background. In hex per-byte mode a Modified row is NOT shaded across the whole row —
            // only its changed byte slots are (drawn in step 6), so skip the full-row fill here.
            bool perByteModified = _hexPerByte && _isHexView
                && line.Status == DiffStatus.Modified;
            Brush bg = GetBackgroundBrush(line.Status);
            if (bg != _theme.BgUnchanged && !perByteModified)
            {
                dc.DrawRectangle(bg, null, new Rect(0, y, contentWidth, LineHeight));
            }

            // Diagonal stripe pattern for gap lines (content area only) — a single
            // tiled-brush fill replaces ~200 individual DrawLine calls in fullscreen
            if (line.Status == DiffStatus.Gap && _gapStripeBrush != null)
            {
                double left = GutterWidth;
                double right = contentWidth - ScrollbarWidth - DiffMapWidth;
                if (right > left)
                    dc.DrawRectangle(_gapStripeBrush, null, new Rect(left, y, right - left, LineHeight));
            }

            // Mid-row add/remove holes (hex, Show Gaps ON): a slot empty on this side but occupied
            // on the other — a genuine gap that isn't a whole-Gap row. Stripe exactly those slots
            // with diagonal lines so the hole is marked (R4). Fixed-address rows are dense, so
            // HexGapSlots is 0 there and this never fires for them; trailing EOF empties are never
            // in the mask either, so they stay unmarked.
            if (_isHexView && _gapStripeBrush != null && line.HexGapSlots != 0)
            {
                // Stripe each contiguous run of gap slots with two tight rectangles: one over the
                // hex digit pairs and one over the matching ascii characters. (A single rect from
                // hex col to ascii col would sweep across unrelated bytes in between.)
                int gapMask = line.HexGapSlots;
                int c = 0;
                while (c < HexFormat.BytesPerLine)
                {
                    if ((gapMask & (1 << c)) == 0) { c++; continue; }
                    int d = c;
                    while (d < HexFormat.BytesPerLine && (gapMask & (1 << d)) != 0) d++;

                    // Hex section: slot c's digit start → slot d-1's digit end (inclusive of the
                    // mid-row separator when a run crosses byte 7/8 — still gap space).
                    int hexLeft = HexFormat.HexSlotColumns(c).Col;
                    int hexRight = HexFormat.HexSlotColumns(d - 1).Col + 2;
                    dc.DrawRectangle(_gapStripeBrush, null,
                        new Rect(hTextX + hexLeft * _charAdvance, y, (hexRight - hexLeft) * _charAdvance, LineHeight));

                    // Ascii section: contiguous chars for the same run.
                    int ascLeft = HexFormat.AsciiColumn(c);
                    int ascRight = HexFormat.AsciiColumn(d - 1) + 1;
                    dc.DrawRectangle(_gapStripeBrush, null,
                        new Rect(hTextX + ascLeft * _charAdvance, y, (ascRight - ascLeft) * _charAdvance, LineHeight));

                    c = d;
                }
            }

            // Bottom border (subtle grid lines)
            dc.DrawRectangle(_theme.BorderLine, null, new Rect(0, y + LineHeight - 1, contentWidth, 1));

            if (line.Rejected) dc.Pop();
        }

        // ── 4b. Draw text selection highlight (range) ────────────────────────
        if (_selectionStart >= 0 && _selectionEnd >= 0)
        {
            int selBegin = Math.Min(_selectionStart, _selectionEnd);
            int selEnd = Math.Max(_selectionStart, _selectionEnd);

            for (int i = Math.Max(selBegin, visibleStart); i < Math.Min(selEnd + 1, visibleEnd); i++)
            {
                double y = contentTop + i * LineHeight - _scrollOffset;
                dc.DrawRectangle(_theme.SelectionHighlight, null, new Rect(0, y, contentWidth, LineHeight));
            }
        }

        // ── 4ba. Draw byte-level highlight (hex inspector) ────────────────────
        // Byte highlights are drawn in step 6 alongside text rendering,
        // where FormattedText is available for BuildHighlightGeometry.

        // ── 4c. Draw search match highlights ───────────────────────────────────
        string? searchQuery = SearchQuery;
        if (!string.IsNullOrEmpty(searchQuery))
        {
            int currentSearchIdx = CurrentSearchMatchIndex;
            for (int i = visibleStart; i < visibleEnd; i++)
            {
                var line = Lines[i];
                if (line.Text != null && line.Text.IndexOf(searchQuery, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    double y = contentTop + i * LineHeight - _scrollOffset;
                    Brush highlightBrush = i == currentSearchIdx ? _theme.SearchCurrentHighlight : _theme.SearchMatchHighlight;
                    dc.DrawRectangle(highlightBrush, null, new Rect(0, y, contentWidth, LineHeight));
                }
            }
        }

        // ── 5. Draw gutter background (before sync points so they draw on top) ──
        dc.DrawRectangle(_theme.BgGutter, null, new Rect(0, contentTop, GutterWidth, contentHeight));
        // Gutter right divider — 2px, clearly visible
        dc.DrawRectangle(_theme.GutterDivider, null, new Rect(GutterWidth - 1, contentTop, 2, contentHeight));

        // ── 4d. Draw active sync point line highlights ─────────────────────────
        IReadOnlyList<int>? syncPointLines = SyncPointLines;
        if (syncPointLines != null)
        {
            foreach (int spLine in syncPointLines)
            {
                if (spLine >= visibleStart && spLine < visibleEnd)
                {
                    double y = contentTop + spLine * LineHeight - _scrollOffset;

                    // Cyan-tinted background across full width
                    dc.DrawRectangle(_theme.SyncPointLineHighlight, null, new Rect(0, y, contentWidth, LineHeight));

                    // Solid accent bar on left edge (3px)
                    dc.DrawRectangle(_theme.SyncPointAccentBar, null, new Rect(0, y, 3, LineHeight));

                    // Dashed horizontal line at midpoint, spanning content area
                    double midY = y + LineHeight / 2;
                    dc.DrawLine(_theme.SyncPointDashedLine, new Point(GutterWidth + 4, midY), new Point(contentWidth - 4, midY));

                    // Colored gutter background for this line
                    dc.DrawRectangle(_theme.SyncPointGutterBg, null, new Rect(0, y, GutterWidth, LineHeight));

                    // "◈ SYNC" marker in gutter — bright and prominent
                    dc.DrawText(_syncPointMarker!, new Point(2, y + 1));
                }
            }
        }

        // ── 4e. Draw pending sync line highlight ───────────────────────────────
        int pendingSyncLine = PendingSyncLine;
        if (pendingSyncLine >= 0 && pendingSyncLine < Lines.Count)
        {
            double y = contentTop + pendingSyncLine * LineHeight - _scrollOffset;
            // Only draw if visible
            if (y + LineHeight >= contentTop && y <= contentTop + contentHeight)
            {
                dc.DrawRectangle(_theme.PendingSyncHighlight, null, new Rect(0, y, contentWidth, LineHeight));
                dc.DrawRectangle(null, _theme.PendingSyncPen, new Rect(1, y + 1, contentWidth - 2, LineHeight - 2));
            }
        }

         // ── 6. Draw line numbers and content text (with horizontal scroll) ─────────

        double hClipWidth = hClipRight - hClipLeft;
        double hClipHeight = contentHeight - HScrollbarHeight; // Exclude horizontal scrollbar area from content
        var hClipRect = new Rect(hClipLeft, contentTop, hClipWidth, Math.Max(0, hClipHeight));
        dc.PushClip(new RectangleGeometry(hClipRect));

        // Draw PENDING label (inside clip so it respects gutter/scollbar boundaries)
        if (pendingSyncLine >= 0 && pendingSyncLine < Lines!.Count)
        {
            double y = contentTop + pendingSyncLine * LineHeight - _scrollOffset;
            if (y + LineHeight >= contentTop && y <= contentTop + contentHeight)
            {
                double labelX = hClipRight - _pendingLabel!.Width - 4 - _hScrollOffset;
                dc.DrawText(_pendingLabel, new Point(labelX, y + 2));
            }
        }

        for (int i = visibleStart; i < visibleEnd; i++)
        {
            var line = Lines[i];
            double y = contentTop + i * LineHeight - _scrollOffset;

            if (line.Rejected) dc.PushOpacity(rejectedOpacity);

            // Content text (horizontally scrolled). Tabs are expanded for rendering — see
            // GetTabExpanded: FormattedText draws a tab several cells wide while every span
            // below is positioned at col * _charAdvance, so raw tabs make spans overlap.
            // `tabMap` converts raw-text indices (ChangedRegions) to expanded coordinates;
            // null = no tabs on this line, indices are identical.
            string? rawText = line.Text;
            if (!string.IsNullOrEmpty(rawText))
            {
                var (text, tabMap) = GetTabExpanded(rawText);

                List<SyntaxToken>? syntaxTokens = _syntaxEnabled ? _syntaxTokens?[i] : null;

                if (syntaxTokens != null)
                {
                    // Multi-span: gaps between coloured tokens use the diff status foreground,
                    // token spans use their theme brush. Width is tracked from the full line
                    // length (monospace), not the widest span, so the h-scrollbar extent stays correct.
                    Brush baseBrush = GetForegroundBrush(line.Status);

                    double lineWidth = text.Length * _charAdvance;
                    if (lineWidth > _contentMaxWidth)
                    {
                        _contentMaxWidth = lineWidth;
                        InvalidateVisual(); // h-scrollbar needs to grow
                    }

                    int pos = 0;
                    foreach (var t in syntaxTokens)
                    {
                        if (t.Start > pos)
                            DrawSpan(dc, text, pos, t.Start - pos, baseBrush, hTextX, y);

                        Brush kindBrush = t.Kind switch
                        {
                            SyntaxTokenKind.Keyword => _theme.SyntaxKeyword,
                            SyntaxTokenKind.String => _theme.SyntaxString,
                            SyntaxTokenKind.Comment => _theme.SyntaxComment,
                            SyntaxTokenKind.Number => _theme.SyntaxNumber,
                            SyntaxTokenKind.Function => _theme.SyntaxFunction,
                            SyntaxTokenKind.Type => _theme.SyntaxType,
                            _ => _theme.SyntaxPreprocessor
                        };
                        DrawSpan(dc, text, t.Start, t.Length, kindBrush, hTextX, y);
                        pos = t.Start + t.Length;
                    }
                    if (pos < text.Length)
                        DrawSpan(dc, text, pos, text.Length - pos, baseBrush, hTextX, y);
                }
                else
                {
                    // Per-Byte hex Modified rows: draw the whole row in the *unchanged* foreground so
                    // only the actually-changed bytes stand out (their per-slot background shading is
                    // drawn separately, and their text is re-drawn below in the class colour). Without
                    // this, GetForegroundBrush(Modified) would tint every byte's text as modified.
                    bool perByteHex = _hexPerByte && _isHexView && line.Status == DiffStatus.Modified;
                    Brush rowFg = perByteHex ? (Brush)_theme.FgUnchanged : GetForegroundBrush(line.Status);

                    // Hex view: brighten all non-null text slightly so 0x00 bytes (re-drawn darker
                    // below from the un-brightened base) read as clearly dimmer than their neighbours.
                    Brush displayFg = _isHexView ? GetHexBrightBrush(rowFg) : rowFg;

                    FormattedText formatted = _textCache.GetOrCreate(text, displayFg, _contentTypeface!, _textFormatting, _theme.BaseFontSize * _fontScale);

                    // Track content width lazily during render — avoids measuring all lines upfront.
                    // When a wider line is discovered, schedule a re-render so the h-scrollbar adjusts.
                    if (formatted.Width > _contentMaxWidth)
                    {
                        _contentMaxWidth = formatted.Width;
                        InvalidateVisual(); // h-scrollbar needs to grow
                    }

                    dc.DrawText(formatted, new Point(hTextX, y + 2));

                    // Per-Byte hex: re-draw each changed byte's hex pair + ascii char in its class
                    // foreground (red removed / green added / yellow modified) on top of the base text.
                    if (perByteHex)
                    {
                        for (int c = 0; c < HexFormat.BytesPerLine; c++)
                        {
                            Brush? spanBrush = null;
                            if ((line.HexRemovedSlots & (1 << c)) != 0) spanBrush = GetHexBrightBrush(_theme.FgRemoved);
                            else if ((line.HexAddedSlots & (1 << c)) != 0) spanBrush = GetHexBrightBrush(_theme.FgAdded);
                            else if ((line.HexModifiedSlots & (1 << c)) != 0) spanBrush = GetHexBrightBrush(_theme.FgModified);
                            if (spanBrush == null) continue;

                            var (hexCol, hexLen) = HexFormat.HexSlotColumns(c);
                            DrawSpan(dc, text, hexCol, hexLen, spanBrush, hTextX, y);
                            DrawSpan(dc, text, HexFormat.AsciiColumn(c), 1, spanBrush, hTextX, y);
                        }
                    }

                    // ── Null-byte dimming (hex view) ───────────────────────────
                    // 0x00 bytes are re-drawn darker than every other value so runs of zeros
                    // recede in binary data. The pair "00" can only appear at an occupied slot's
                    // hex columns (empty slots render as blanks), so a plain character test
                    // identifies null bytes without touching the row model. Dimming scales the
                    // slot's own foreground toward black — starting from the un-brightened base,
                    // so nulls land well below the brightened neighbours in every theme.
                    if (_isHexView)
                    {
                        for (int c = 0; c < HexFormat.BytesPerLine; c++)
                        {
                            var (hexCol, hexLen) = HexFormat.HexSlotColumns(c);
                            if (hexCol + hexLen > text.Length) break;
                            if (text[hexCol] != '0' || text[hexCol + 1] != '0') continue;

                            // Dim whatever class colour this slot's text was drawn in: the per-byte
                            // class fg on Modified rows, the row fg otherwise.
                            Brush baseBrush = rowFg;
                            if (perByteHex)
                            {
                                if ((line.HexRemovedSlots & (1 << c)) != 0) baseBrush = _theme.FgRemoved;
                                else if ((line.HexAddedSlots & (1 << c)) != 0) baseBrush = _theme.FgAdded;
                                else if ((line.HexModifiedSlots & (1 << c)) != 0) baseBrush = _theme.FgModified;
                            }

                            Brush dim = GetHexDimBrush(baseBrush);
                            DrawSpan(dc, text, hexCol, hexLen, dim, hTextX, y);
                            DrawSpan(dc, text, HexFormat.AsciiColumn(c), 1, dim, hTextX, y);
                        }
                    }
                }

                // ── Byte-level highlight (hex inspector) ─────────────────────
                // Row-local arithmetic: each displayed row in the selection range draws its
                // occupied slots from first→last — no global byte math, so the highlight stays
                // correct across Gap rows and holes in aligned rows.
                if (IsHexView && _byteSelStart.HasValue && _byteSelEnd.HasValue)
                {
                    var p1 = _byteSelStart.Value;
                    var p2 = _byteSelEnd.Value;
                    int minLine = Math.Min(p1.line, p2.line);
                    int maxLine = Math.Max(p1.line, p2.line);

                    if (i >= minLine && i <= maxLine && line.ByteStart.HasValue)
                    {
                        int slotAtMin = p1.line <= p2.line ? p1.slot : p2.slot;
                        int slotAtMax = p1.line <= p2.line ? p2.slot : p1.slot;
                        int mask = line.HexSlotMask;

                        int firstC, lastC;
                        if (minLine == maxLine)
                        {
                            firstC = Math.Min(slotAtMin, slotAtMax);
                            lastC = Math.Max(slotAtMin, slotAtMax);
                        }
                        else
                        {
                            firstC = i == minLine ? slotAtMin : 0;
                            lastC = i == maxLine ? slotAtMax : HexFormat.LastSlot(mask);
                        }

                        double inset = 1;
                        for (int c = firstC; c <= lastC; c++)
                        {
                            if (((mask >> c) & 1) == 0) continue; // Hole — no byte on this side

                            var (hexCol, hexLen) = HexFormat.HexSlotColumns(c);
                            double hexX = hTextX + hexCol * _charAdvance;
                            double hexW = hexLen * _charAdvance;
                            dc.DrawRectangle(_theme.ByteHighlightFill, null,
                                new Rect(hexX - inset, y + inset, hexW + 2 * inset, LineHeight - 2 * inset));
                            dc.DrawRectangle(null, _theme.ByteHighlightPen,
                                new Rect(hexX - inset, y + inset, hexW + 2 * inset, LineHeight - 2 * inset));

                            // Corresponding ASCII character at the slot's ascii column
                            double charX = hTextX + HexFormat.AsciiColumn(c) * _charAdvance;
                            dc.DrawRectangle(_theme.ByteHighlightFill, null,
                                new Rect(charX - inset, y + inset, _charAdvance + 2 * inset, LineHeight - 2 * inset));
                            dc.DrawRectangle(null, _theme.ByteHighlightPen,
                                new Rect(charX - inset, y + inset, _charAdvance + 2 * inset, LineHeight - 2 * inset));
                        }
                    }
                }

                // ── Inline diff change highlight (Modified / merge-change lines) ──
                // Removed/Added only carry ChangedRegions in the unified layout, where a
                // modified line is synthesized as a remove+add pair — two-pane rows never do.
                if (line.Status is DiffStatus.Modified or DiffStatus.ChangeA or DiffStatus.ChangeB
                    or DiffStatus.Removed or DiffStatus.Added
                    && line.ChangedRegions != null && line.ChangedRegions.Count > 0)
                {
                    double hlInset = 1;
                    foreach (var (start, length) in line.ChangedRegions)
                    {
                        // Regions are raw-text indices — map through the tab expansion.
                        if (length <= 0 || start >= rawText.Length) continue;
                        int ds = tabMap != null ? tabMap[start] : start;
                        int de = tabMap != null
                            ? tabMap[Math.Min(start + length, rawText.Length)]
                            : start + length;
                        {
                            double hlX = hTextX + ds * _charAdvance;
                            double hlW = (de - ds) * _charAdvance;
                            dc.DrawRectangle(_theme.InlineChangedHighlight, null,
                                new Rect(hlX - hlInset, y + hlInset, hlW + 2 * hlInset, LineHeight - 2 * hlInset));
                        }
                    }
                }

                // ── Hex per-byte change highlight (Modified rows only) ─────────────
                // Shade exactly the changed byte slots (hex pair + ascii char) instead of the
                // whole row — the visual half of the hex auto-resync option. The full-row fill was
                // skipped in step 4 for these rows, so this is what paints them. Each slot takes the
                // colour of its class: red = removed (A-side KDel), green = added (B-side KIns),
                // yellow = modified (KX, both sides). Within one side a slot is at most one class
                // (removed XOR modified on A, added XOR modified on B), so the first match wins.
                if (_hexPerByte && _isHexView && line.Status == DiffStatus.Modified)
                {
                    double inset = 1;
                    for (int c = 0; c < HexFormat.BytesPerLine; c++)
                    {
                        Brush? slotBrush = null;
                        if ((line.HexRemovedSlots & (1 << c)) != 0) slotBrush = _theme.BgRemoved;
                        else if ((line.HexAddedSlots & (1 << c)) != 0) slotBrush = _theme.BgAdded;
                        else if ((line.HexModifiedSlots & (1 << c)) != 0) slotBrush = _theme.BgModified;
                        if (slotBrush == null) continue;

                        var (hexCol, hexLen) = HexFormat.HexSlotColumns(c);
                        double hexX = hTextX + hexCol * _charAdvance;
                        dc.DrawRectangle(slotBrush, null,
                            new Rect(hexX - inset, y + inset, hexLen * _charAdvance + 2 * inset, LineHeight - 2 * inset));
                        double charX = hTextX + HexFormat.AsciiColumn(c) * _charAdvance;
                        dc.DrawRectangle(slotBrush, null,
                            new Rect(charX - inset, y + inset, _charAdvance + 2 * inset, LineHeight - 2 * inset));
                    }
                }
            }

            if (line.Rejected) dc.Pop();
        }

        dc.Pop();

        // Draw line numbers (NOT affected by horizontal scroll — always visible in gutter)
        for (int i = visibleStart; i < visibleEnd; i++)
        {
            var line = Lines[i];
            double y = contentTop + i * LineHeight - _scrollOffset;

            if (line.Rejected) dc.PushOpacity(rejectedOpacity);

            // Line number — right-aligned in gutter, clamped so it never overlaps content.
            // Gap rows show ✦; conflict rows show ⚠ (the merge conflict indicator); winner
            // rows of resolved conflicts show ✓ in green so they stay findable after resolution.
            Brush? glyphBrush = null;
            string lnText;
            if (line.ResolvedConflict)
            {
                lnText = "  ✓";
                glyphBrush = _theme.FgConflictResolved;
            }
            else
            {
                lnText = line.Status switch
                {
                    DiffStatus.Gap => "  ✦",
                    DiffStatus.Conflict => "  ⚠",
                    _ => line.OriginalLineNumber.ToString()
                };
            }
            if (ShowChangeGlyphs)
            {
                // Git-style glyphs for the unified layout: "-42" red, "+17" green.
                if (line.Status == DiffStatus.Removed)
                {
                    lnText = $"-{line.OriginalLineNumber}";
                    glyphBrush = _theme.FgRemoved;
                }
                else if (line.Status == DiffStatus.Added)
                {
                    lnText = $"+{line.OriginalLineNumber}";
                    glyphBrush = _theme.FgAdded;
                }
            }
            var lnFormatted = CreateGutterText(lnText, glyphBrush);
            double lnX = Math.Max(2, GutterWidth - 8 - lnFormatted.Width);
            dc.DrawText(lnFormatted, new Point(lnX, y + 2));

            // Structural finding — small coloured triangle in the gutter left of the line
            // number, deliberately a different shape from the solid diff bars so it reads as
            // advisory. lnX > 12 keeps it clear of long numbers (unified "-1234" glyphs).
            if (!IsHexView && line.StructuralNote != null && lnX > 12)
            {
                dc.PushTransform(new TranslateTransform(5, y + LineHeight / 2.0));
                dc.DrawGeometry(GetStructuralBrush(line.StructuralFlag!.Value), null, StructuralTriangleGeom);
                dc.Pop();
            }

            if (line.Rejected) dc.Pop();
        }

        // ── 6b. Cross-pane anchor markers (gapless two-pane mode) ─────────────
        // With gaps hidden, an Added/Removed block gives no hint where it sits on the
        // other side — exactly what's hard to find in long files under synced scrolling.
        // Each such line carries OtherSideAnchor (count of real lines on the opposite
        // side before this position), so draw a small "≈N" chip at the right edge of the
        // block's first row: N = anchor + 1, the approximate line number to look for in
        // the partner pane. Suppressed when gaps are shown (they already encode the
        // correspondence), in hex view, and in the unified layout (ShowChangeGlyphs)
        // where both sides share one list. Merge panes never contain Added/Removed rows,
        // so this is a no-op there without an extra gate.
        if (!IsHexView && !_hasGapLines && !ShowChangeGlyphs)
        {
            DrawAnchorMarkers(dc, visibleStart, visibleEnd, contentTop, contentHeight);
            DrawAnchorSlotLine(dc, contentTop, contentHeight);
        }

        // ── 7. Draw scrollbars ────────────────────────────────────────────────
        DrawScrollbar(dc, contentTop, contentHeight, contentWidth);
        DrawHScrollbar(dc, contentTop, contentHeight, contentWidth);

        // ── 7b. Draw diff map overview bar (on top so it's visible) ──────────
        ScanDiffBlocks(); // Ensure blocks are current — re-scans if dirty or line count changed
        DrawDiffMap(dc, contentTop, contentHeight);

        // ── 8. Draw "No differences" overlay if requested ─────────────────────
        if (ShowNoDifferences)
        {
            DrawNoDifferencesOverlay(dc, contentTop, contentHeight, contentWidth);
        }

        // ── 9. Draw drag-over highlight overlay ────────────────────────────────
        if (IsDragOver)
            DrawDragOverHighlight(dc);
    }

   // ── Drag-over highlight drawing ────────────────────────────────────────────

    private void DrawDragOverHighlight(DrawingContext dc)
    {
        // Semi-transparent overlay across entire viewer
        dc.DrawRectangle(_theme.DragOverOverlay, null, new Rect(0, 0, _viewportWidth, _viewportHeight));

        // Bright border outline (3px thick)
        double bw = 3;
        dc.DrawRectangle(_theme.DragOverBorder, null, new Rect(0, 0, _viewportWidth, bw));                       // top
        dc.DrawRectangle(_theme.DragOverBorder, null, new Rect(0, _viewportHeight - bw, _viewportWidth, bw));   // bottom
        dc.DrawRectangle(_theme.DragOverBorder, null, new Rect(0, 0, bw, _viewportHeight));                     // left
        dc.DrawRectangle(_theme.DragOverBorder, null, new Rect(_viewportWidth - bw, 0, bw, _viewportHeight));   // right

        // "[ DRAG FILE HERE ]" label — same size/position as empty state placeholder
        int contentTop = _titleBarHeight;
        int contentHeight = _viewportHeight - contentTop;
        double cx = _viewportWidth / 2;
        double cy = contentTop + contentHeight / 2;

        var dropLabel = CreateContentText("[ DRAG FILE HERE ]",
            _theme.DragOverBorder, _contentTypeface!, _textFormatting);
        dc.DrawText(dropLabel, new Point(cx - dropLabel.Width / 2, cy - 15));
    }

    // ── Batched accent bar drawing ─────────────────────────────────────────────

    private void DrawBatchedAccentBars(DrawingContext dc, int visibleStart, int visibleEnd, int contentTop)
    {
        if (visibleStart >= visibleEnd) return;

        int i = visibleStart;
        while (i < visibleEnd)
        {
            DiffStatus status = Lines![i].Status;
            bool resolved = Lines![i].ResolvedConflict;
            // Resolved-conflict winner rows get the "decided" green bar regardless of which side.
            Brush accentBrush = resolved ? _theme.AccentConflictResolved : GetAccentBrush(status);

            // Find end of consecutive run with same status + resolved mark
            int runEnd = i + 1;
            while (runEnd < visibleEnd && Lines![runEnd].Status == status && Lines![runEnd].ResolvedConflict == resolved)
                runEnd++;

            double startY = contentTop + i * LineHeight - _scrollOffset;
            double runHeight = (runEnd - i) * LineHeight;

            dc.DrawRectangle(accentBrush, null, new Rect(0, startY, AccentWidth, runHeight));

            i = runEnd;
        }
    }

    // ── Scrollbar drawing ─────────────────────────────────────────────────────

    private void DrawScrollbar(DrawingContext dc, int contentTop, int contentHeight, int contentWidth)
    {
        double trackX = contentWidth - ScrollbarWidth;
        double trackY = contentTop;
        double trackH = contentHeight;

        // Track background
        dc.DrawRectangle(_theme.ScrollTrack, null, new Rect(trackX, trackY, ScrollbarWidth, trackH));

        // Thumb
        int lineCount = Lines?.Count ?? 0;
        if (lineCount == 0) return;

        double totalLines = lineCount;
        double visibleLines = contentHeight / LineHeight;
        if (visibleLines >= totalLines) return; // No scrollbar needed

        double thumbRatio = visibleLines / totalLines;
        double thumbHeight = Math.Max(MinThumbHeight, trackH * thumbRatio);

        // Thumb position proportional to scroll offset
        double maxThumbOffset = trackH - thumbHeight;
        double scrollablePixels = _scrollExtent - contentHeight;
        double thumbY;
        if (scrollablePixels > 0)
            thumbY = trackY + (_scrollOffset / scrollablePixels) * maxThumbOffset;
        else
            thumbY = trackY;

        Brush thumbBrush = _isScrollThumbHovered ? _theme.ScrollThumbHover : _theme.ScrollThumb;

        dc.DrawRoundedRectangle(thumbBrush, null,
            new Rect(trackX + 2, thumbY, ScrollbarWidth - 4, thumbHeight), 3, 3);

        // Store thumb rect for hit testing
        _scrollThumbRect = new Rect(trackX, thumbY, ScrollbarWidth, thumbHeight);
        _scrollTrackRect = new Rect(trackX, trackY, ScrollbarWidth, trackH);
    }

    private Rect _scrollThumbRect = Rect.Empty;
    private Rect _scrollTrackRect = Rect.Empty;

    // ── Horizontal scrollbar drawing ─────────────────────────────────────────────

    private void DrawHScrollbar(DrawingContext dc, int contentTop, int contentHeight, int contentWidth)
    {
        double trackX = GutterWidth;
        double trackY = contentTop + contentHeight - HScrollbarHeight;
        double trackW = _hScrollTrackWidth();

        // Track background
        dc.DrawRectangle(_theme.ScrollTrack, null, new Rect(trackX, trackY, trackW, HScrollbarHeight));

        // Thumb
        if (_contentMaxWidth <= 0) return;

        double hTrack = _hScrollTrackWidth();
        if (hTrack <= 0) return;

        double thumbRatio = hTrack / _contentMaxWidth;
        if (thumbRatio >= 1.0) return; // No horizontal scrollbar needed

        double thumbWidth = Math.Max(MinThumbWidth, trackW * thumbRatio);

        // Thumb position proportional to horizontal scroll offset
        double maxThumbOffset = trackW - thumbWidth;
        double scrollablePixels = _contentMaxWidth - hTrack;
        double thumbX;
        if (scrollablePixels > 0)
            thumbX = trackX + (_hScrollOffset / scrollablePixels) * maxThumbOffset;
        else
            thumbX = trackX;

        Brush thumbBrush = _isHScrollThumbHovered ? _theme.ScrollThumbHover : _theme.ScrollThumb;

        dc.DrawRoundedRectangle(thumbBrush, null,
            new Rect(thumbX, trackY + 2, thumbWidth, HScrollbarHeight - 4), 3, 3);

        // Store thumb/track rects for hit testing
        _hScrollThumbRect = new Rect(thumbX, trackY, thumbWidth, HScrollbarHeight);
        _hScrollTrackRect = new Rect(trackX, trackY, trackW, HScrollbarHeight);
    }

    private bool _isHScrollThumbHovered;


    // ── Empty state placeholder ────────────────────────────────────────────────

    private void DrawEmptyState(DrawingContext dc, int contentTop, int contentHeight, int contentWidth)
    {
        double cx = contentWidth / 2;
        double cy = contentTop + contentHeight / 2;

        // CRT scanlines (subtle tiled brush)
        dc.DrawRectangle(_theme.ScanlineBrush, null, new Rect(0, contentTop, contentWidth, contentHeight));

        // "[ DRAG FILE HERE ]" with glow
        var cyanBright = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF));
        var cyanDim = new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0xE5, 0xFF));

        var titleGlow = CreateContentText("[ DRAG FILE HERE ]", cyanDim, _contentTypeface!, _textFormatting);
        dc.DrawText(titleGlow, new Point(cx - titleGlow.Width / 2 - 1, cy - 16));
        dc.DrawText(titleGlow, new Point(cx - titleGlow.Width / 2 + 1, cy - 16));
        dc.DrawText(titleGlow, new Point(cx - titleGlow.Width / 2, cy - 14));

        var text = CreateContentText("[ DRAG FILE HERE ]", _theme.FgTitle, _contentTypeface!, _textFormatting);
        dc.DrawText(text, new Point(cx - text.Width / 2, cy - 15));

        // Subtitle with glow
        var subGlow = CreateContentText("or use toolbar buttons to load files", cyanDim, _contentTypeface!, _textFormatting);
        dc.DrawText(subGlow, new Point(cx - subGlow.Width / 2, cy + 11));

        var sub = CreateContentText("or use toolbar buttons to load files", _theme.FgMeta, _contentTypeface!, _textFormatting);
        dc.DrawText(sub, new Point(cx - sub.Width / 2, cy + 10));
    }

    // ── "No differences" overlay ───────────────────────────────────────────────

    private void DrawNoDifferencesOverlay(DrawingContext dc, int contentTop, int contentHeight, int contentWidth)
    {
        double cx = contentWidth / 2;
        double cy = contentTop + contentHeight / 2;

        // ── Dark CRT background ──────────────────────────────────────────────
        var bgBrush = new SolidColorBrush(Color.FromArgb(0xE0, 0x08, 0x0C, 0x14));
        dc.DrawRectangle(bgBrush, null, new Rect(0, contentTop, contentWidth, contentHeight));

        // ── CRT scanlines (tiled brush) ───────────────────────────────────────
        dc.DrawRectangle(_theme.ScanlineBrush, null, new Rect(0, contentTop, contentWidth, contentHeight));

        // ── Subtle vignette (darker edges via gradient border rects) ─────────
        var vignetteTop = new SolidColorBrush(Color.FromArgb(0x60, 0x00, 0x00, 0x00));
        var vignetteBot = new SolidColorBrush(Color.FromArgb(0x60, 0x00, 0x00, 0x00));
        dc.DrawRectangle(vignetteTop, null, new Rect(0, contentTop, contentWidth, 40));
        dc.DrawRectangle(vignetteBot, null, new Rect(0, contentTop + contentHeight - 40, contentWidth, 40));

        // ── Text with bloom/glow (layered draws) ─────────────────────────────
        var greenBright = new SolidColorBrush(Color.FromRgb(0x60, 0xFF, 0xB0));
        var greenDim = new SolidColorBrush(Color.FromArgb(0x50, 0x60, 0xFF, 0xB0));

        // Bloom pass: wider, semi-transparent draws around the text
        var titleBloom = CreateContentText("FILES ARE IDENTICAL",
            greenBright, _titleTypeface!, _textFormatting, 28);
        double titleY = cy - 18;

        // Outer glow (4 offsets)
        dc.DrawText(titleBloom, new Point(cx - titleBloom.Width / 2 - 1, titleY + 1));
        dc.DrawText(titleBloom, new Point(cx - titleBloom.Width / 2 + 1, titleY + 1));
        dc.DrawText(titleBloom, new Point(cx - titleBloom.Width / 2, titleY - 1));
        dc.DrawText(titleBloom, new Point(cx - titleBloom.Width / 2, titleY + 1));

        // Main crisp text
        var titleMain = CreateContentText("FILES ARE IDENTICAL",
            new SolidColorBrush(Color.FromRgb(0x80, 0xFF, 0xD0)), _titleTypeface!, _textFormatting, 28);
        dc.DrawText(titleMain, new Point(cx - titleMain.Width / 2, titleY));

        // Sub-text with bloom
        var subBloom = CreateContentText("No differences found between the two files",
            greenDim, _contentTypeface!, _textFormatting);
        double subY = cy + 14;
        dc.DrawText(subBloom, new Point(cx - subBloom.Width / 2 - 1, subY));
        dc.DrawText(subBloom, new Point(cx - subBloom.Width / 2 + 1, subY));
        dc.DrawText(subBloom, new Point(cx - subBloom.Width / 2, subY + 1));

        var subMain = CreateContentText("No differences found between the two files",
            new SolidColorBrush(Color.FromRgb(0x40, 0xCC, 0x8A)), _contentTypeface!, _textFormatting);
        dc.DrawText(subMain, new Point(cx - subMain.Width / 2, subY));

        // ── HUD corner brackets around the text block ────────────────────────
        double panelLeft = cx - 200;
        double panelTop = cy - 50;
        double panelRight = cx + 200;
        double panelBottom = cy + 40;

        var bracketPen = new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0x60, 0xFF, 0xB0)), 2);
        var cornerPen = new Pen(new SolidColorBrush(Color.FromRgb(0x60, 0xFF, 0xB0)), 3);

        // Corner segments (L-shaped brackets)
        double cornerLen = 18;

        // Top-left corner
        dc.DrawLine(cornerPen, new Point(panelLeft, panelTop), new Point(panelLeft + cornerLen, panelTop));
        dc.DrawLine(cornerPen, new Point(panelLeft, panelTop), new Point(panelLeft, panelTop + cornerLen));

        // Top-right corner
        dc.DrawLine(cornerPen, new Point(panelRight - cornerLen, panelTop), new Point(panelRight, panelTop));
        dc.DrawLine(cornerPen, new Point(panelRight, panelTop), new Point(panelRight, panelTop + cornerLen));

        // Bottom-left corner
        dc.DrawLine(cornerPen, new Point(panelLeft, panelBottom - cornerLen), new Point(panelLeft, panelBottom));
        dc.DrawLine(cornerPen, new Point(panelLeft, panelBottom), new Point(panelLeft + cornerLen, panelBottom));

        // Bottom-right corner
        dc.DrawLine(cornerPen, new Point(panelRight - cornerLen, panelBottom), new Point(panelRight, panelBottom));
        dc.DrawLine(cornerPen, new Point(panelRight, panelBottom), new Point(panelRight, panelBottom - cornerLen));

        // Dashed connecting lines (thin, dimmer)
        var dashPen = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0x60, 0xFF, 0xB0)), 1);

        // Top edge dashes
        for (double dx = panelLeft + cornerLen; dx < panelRight - cornerLen; dx += 12)
            dc.DrawLine(dashPen, new Point(dx, panelTop), new Point(dx + 5, panelTop));
        // Bottom edge dashes
        for (double dx = panelLeft + cornerLen; dx < panelRight - cornerLen; dx += 12)
            dc.DrawLine(dashPen, new Point(dx, panelBottom), new Point(dx + 5, panelBottom));
        // Left edge dashes
        for (double dy = panelTop + cornerLen; dy < panelBottom - cornerLen; dy += 12)
            dc.DrawLine(dashPen, new Point(panelLeft, dy), new Point(panelLeft, dy + 5));
        // Right edge dashes
        for (double dy = panelTop + cornerLen; dy < panelBottom - cornerLen; dy += 12)
            dc.DrawLine(dashPen, new Point(panelRight, dy), new Point(panelRight, dy + 5));

        // ── Status indicator dot ─────────────────────────────────────────────
        var dotBrush = new SolidColorBrush(Color.FromRgb(0x60, 0xFF, 0xB0));
        dc.DrawEllipse(dotBrush, null, new Point(panelLeft - 10, panelTop + 5), 4, 4);
        var dotGlow = new SolidColorBrush(Color.FromArgb(0x40, 0x60, 0xFF, 0xB0));
        dc.DrawEllipse(dotGlow, null, new Point(panelLeft - 10, panelTop + 5), 7, 7);
    }

    // ── Text Creation Helpers ──────────────────────────────────────────────────

    private FormattedText CreateGutterText(string text, Brush? foreground = null)
    {
        // Cached: line numbers repeat heavily (and the "✦" gap marker appears on every
        // gap row), so this used to allocate a FormattedText per visible line per frame.
        // The cache key includes the brush color, so colored glyphs (-n / +m in unified
        // mode) are safe to mix with plain line numbers.
        return _gutterCache.GetOrCreate(text, foreground ?? _theme.FgLineNumber, _gutterTypeface!, _textFormatting, 13);
    }

    private FormattedText CreateContentText(string text, Brush foreground, Typeface typeface, TextFormattingMode formatting, double fontSize = 13)
    {
        // Trim long text to viewport width for performance
        double maxWidth = _viewportWidth - GutterWidth - 20;
        if (maxWidth < 10) maxWidth = 400;

        var ft = new FormattedText(
            text,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            foreground,
            96.0 // pixels per dip (standard DPI)
        )
        {
            MaxTextWidth = maxWidth
        };

        return ft;
    }

    // ── Brush Lookup Tables ────────────────────────────────────────────────────

    private SolidColorBrush GetBackgroundBrush(DiffStatus status) => status switch
    {
        DiffStatus.Added => _theme.BgAdded,
        DiffStatus.Removed => _theme.BgRemoved,
        DiffStatus.Modified => _theme.BgModified,
        DiffStatus.Gap => _theme.BgGap,
        DiffStatus.ChangeA => _theme.BgChangeA,
        DiffStatus.ChangeB => _theme.BgChangeB,
        DiffStatus.Conflict => _theme.BgConflict,
        _ => _theme.BgUnchanged
    };

    private SolidColorBrush GetForegroundBrush(DiffStatus status) => status switch
    {
        DiffStatus.Added => _theme.FgAdded,
        DiffStatus.Removed => _theme.FgRemoved,
        DiffStatus.Modified => _theme.FgModified,
        DiffStatus.Gap => _theme.FgGap,
        DiffStatus.ChangeA => _theme.FgChangeA,
        DiffStatus.ChangeB => _theme.FgChangeB,
        DiffStatus.Conflict => _theme.FgConflict,
        _ => _theme.FgUnchanged
    };

    private SolidColorBrush GetAccentBrush(DiffStatus status) => status switch
    {
        DiffStatus.Added => _theme.AccentAdded,
        DiffStatus.Removed => _theme.AccentRemoved,
        DiffStatus.Modified => _theme.AccentModified,
        DiffStatus.Gap => _theme.AccentGap,
        DiffStatus.ChangeA => _theme.AccentChangeA,
        DiffStatus.ChangeB => _theme.AccentChangeB,
        DiffStatus.Conflict => _theme.AccentConflict,
        _ => _theme.AccentUnchanged
    };

    /// <summary>Frozen upward triangle centred on the origin — the structural-finding gutter mark.</summary>
    private static readonly Geometry StructuralTriangleGeom = CreateTriangle();

    private static Geometry CreateTriangle()
    {
        var figure = new PathFigure { StartPoint = new Point(0, -4.8), IsClosed = true };
        figure.Segments.Add(new LineSegment(new Point(-4.6, 4.2), true));
        figure.Segments.Add(new LineSegment(new Point(4.6, 4.2), true));
        var geom = new PathGeometry(new[] { figure });
        geom.Freeze();
        return geom;
    }

    /// <summary>Gutter triangle colour for a structural finding — error red, amber, green or rose.</summary>
    private SolidColorBrush GetStructuralBrush(StructuralKind kind) => kind switch
    {
        StructuralKind.BraceImbalance or StructuralKind.InvalidDocument => _theme.StructuralError,
        StructuralKind.FunctionAdded or StructuralKind.ControlBlockAdded
            or StructuralKind.TypeAdded or StructuralKind.JsonKeyAdded
            or StructuralKind.XmlElementAdded => _theme.StructuralAdded,
        StructuralKind.FunctionRemoved or StructuralKind.ControlBlockRemoved
            or StructuralKind.TypeRemoved or StructuralKind.JsonKeyRemoved
            or StructuralKind.XmlElementRemoved => _theme.StructuralRemoved,
        _ => _theme.StructuralChanged
    };

    // ── Mouse Input Handling ───────────────────────────────────────────────────

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);

        int contentHeight = _viewportHeight - _titleBarHeight;

        // Shift+wheel = horizontal scroll
        if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
        {
            double hTrack = _hScrollTrackWidth();
            double hScrollable = _contentMaxWidth - hTrack;
            if (hScrollable > 0)
            {
                int hDelta = e.Delta * 3;
                double oldHOffset = _hScrollOffset;
                _hScrollOffset = Math.Clamp(_hScrollOffset - hDelta, 0, hScrollable);
                if (_hScrollOffset != oldHOffset)
                {
                    InvalidateVisual();
                    RaiseHorizontalScrollRatioChanged();
                }
            }
            e.Handled = true;
            return;
        }

        if (_scrollExtent <= contentHeight) return; // Nothing to scroll

        int delta = e.Delta * 3; // Sensitivity: 3px per wheel tick
        double maxOffset = _scrollExtent - contentHeight;
        double oldOffset = _scrollOffset;
        _scrollOffset = Math.Clamp(_scrollOffset - delta, 0, maxOffset);

        if (_scrollOffset != oldOffset)
        {
            InvalidateVisual();
            RaiseScrollRatioChanged();
        }

        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);

        var pos = e.GetPosition(this);

        // Check horizontal scrollbar first (it overlaps with vertical scrollbar corner area)
        if (_hScrollTrackRect.Contains(pos))
        {
            // Clear selection when clicking on scrollbar
            _selectionStart = -1;
            _selectionEnd = -1;
            RaiseSelectionChanged();

            double hTrack = _hScrollTrackWidth();
            // Max(0, …): the track/thumb rects are from the last render — content may have
            // shrunk since (async diff swap), making the raw value negative and Clamp throw.
            double hScrollable = Math.Max(0, _contentMaxWidth - hTrack);

            if (_hScrollThumbRect.Contains(pos))
            {
                // Drag the horizontal thumb
                _dragMode = ScrollDragMode.HThumb;
                _dragStartY = pos.X; // reuse _dragStartY to store start X for horizontal drag
                _dragStartOffset = _hScrollOffset;
                CaptureMouse();
            }
            else if (pos.X < _hScrollThumbRect.Left)
            {
                // Page left (click left of thumb)
                _hScrollOffset = Math.Max(0, _hScrollOffset - hTrack);
                InvalidateVisual();
                RaiseHorizontalScrollRatioChanged();
            }
            else
            {
                // Page right (click right of thumb)
                _hScrollOffset = Math.Clamp(_hScrollOffset + hTrack, 0, hScrollable);
                InvalidateVisual();
                RaiseHorizontalScrollRatioChanged();
            }

            e.Handled = true;
        }
        else if (_scrollTrackRect.Contains(pos))
        {
            // Clear selection when clicking on scrollbar
            _selectionStart = -1;
            _selectionEnd = -1;
            RaiseSelectionChanged();

            int contentHeight = _viewportHeight - _titleBarHeight;

            if (_scrollThumbRect.Contains(pos))
            {
                // Drag the thumb
                _dragMode = ScrollDragMode.Thumb;
                _dragStartY = pos.Y;
                _dragStartOffset = _scrollOffset;
                CaptureMouse();
            }
            else if (pos.Y < _scrollThumbRect.Top)
            {
                // Page up (click above thumb)
                _scrollOffset = Math.Max(0, _scrollOffset - contentHeight);
                InvalidateVisual();
                RaiseScrollRatioChanged();
            }
            else
            {
                // Page down (click below thumb) — Max(0, …): see hScrollable above
                double maxOffset = Math.Max(0, _scrollExtent - contentHeight);
                _scrollOffset = Math.Clamp(_scrollOffset + contentHeight, 0, maxOffset);
                InvalidateVisual();
                RaiseScrollRatioChanged();
            }

            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Middle)
        {
            // Middle mouse drag to scroll (like many editors)
            _dragMode = ScrollDragMode.ContentDrag;
            _dragStartY = pos.Y;
            _dragStartOffset = _scrollOffset;
            CaptureMouse();
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Left && pos.Y > _titleBarHeight)
        {
            // Left-click on content area — start text selection
            int lineIdx = GetLineAtY(pos.Y);
            if (lineIdx >= 0)
            {
                // Double-click on an Added/Removed block (anchor markers visible): ask the partner
                // pane to reveal where this block's gap would sit. Falls through to selection below.
                if (e.ClickCount == 2 && !IsHexView && !ShowChangeGlyphs && !_hasGapLines)
                {
                    var clicked = Lines?[lineIdx];
                    if (clicked is { Status: DiffStatus.Added or DiffStatus.Removed, OtherSideAnchor: not null })
                        AnchorJumpRequested?.Invoke(clicked.OtherSideAnchor.Value, clicked.Status);
                }

                bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);

                if (shift && _selectionStart >= 0)
                {
                    // Shift+click: extend selection from anchor to clicked line
                    _selectionEnd = lineIdx;
                }
                else
                {
                    // Normal click: set new anchor
                    _selectionStart = lineIdx;
                    _selectionEnd = lineIdx;
                }

                // Track byte-precise position for hex inspector (only in hex view mode).
                // Slots are mask-aware: holes in aligned rows can't start a selection.
                if (IsHexView)
                {
                    double textX = GutterWidth + AccentWidth + 4 - _hScrollOffset;
                    var clickedRow = Lines?[lineIdx];
                    int slotAtClick = clickedRow is { ByteStart: not null }
                        ? GetSlotAtX(pos.X, textX, clickedRow.HexSlotMask) : -1;

                    if (slotAtClick >= 0)
                    {
                        // Select clicked byte + next (typeSize-1) bytes; when the row runs out
                        // before the element completes, wrap the endpoint into subsequent rows.
                        var selEndPt = ExtendSlotWrapped(lineIdx, slotAtClick);

                        if (selEndPt.HasValue)
                        {
                            if (shift && _byteSelStart.HasValue)
                            {
                                _byteSelEnd = selEndPt.Value;
                            }
                            else
                            {
                                _byteSelStart = (lineIdx, slotAtClick);
                                _byteSelEnd = selEndPt.Value;
                            }
                            // Deterministic focus target for the pane-level Del shortcut — this
                            // control has no keyboard handling of its own.
                            Focus();
                        }
                    }
                    else if (!shift)
                    {
                        // New anchor click that didn't land on a byte — drop any stale byte selection.
                        _byteSelStart = null;
                        _byteSelEnd = null;
                    }
                }

                _dragMode = ScrollDragMode.SelectionDrag;
                _dragStartY = pos.Y;
                _dragStartOffset = _scrollOffset;
                CaptureMouse();
                InvalidateVisual();
                RaiseSelectionChanged();
                e.Handled = true;
            }
            else
            {
                // Clicked in content area but outside valid lines — clear selection
                _selectionStart = -1;
                _selectionEnd = -1;
                _byteSelStart = null;
                _byteSelEnd = null;
                InvalidateVisual();
                RaiseSelectionChanged();
            }
        }
        else if (e.ChangedButton == MouseButton.Left && pos.Y <= _titleBarHeight)
        {
            // Click on title bar — clear selection
            _selectionStart = -1;
            _selectionEnd = -1;
            _byteSelStart = null;
            _byteSelEnd = null;
            InvalidateVisual();
            RaiseSelectionChanged();
        }
        else if (e.ChangedButton == MouseButton.Right && pos.Y > _titleBarHeight)
        {
            // Right-click on content area — raise events for context menu / apply
            int lineIdx = GetLineAtY(pos.Y);
            if (lineIdx >= 0 && Lines != null && lineIdx < Lines.Count)
            {
                SelectedLineIndex = lineIdx;

                // Fire for any line (sync points, etc.)
                RightClickAnyLine?.Invoke(this, lineIdx);

                var line = Lines[lineIdx];

                // Hex view: report the exact byte slot so either panel can offer sync-point options
                // (slot -1 when the click missed an occupied slot — e.g. a Gap filler row — the menu
                // can still offer selection-scoped actions like CRC). Fires for all rows, unlike
                // RightClickLineIndex below, which is status-gated.
                if (IsHexView)
                {
                    double textX = GutterWidth + AccentWidth + 4 - _hScrollOffset;
                    int slotAtClick = line.ByteStart.HasValue ? GetSlotAtX(pos.X, textX, line.HexSlotMask) : -1;
                    HexRightClicked?.Invoke(this, (lineIdx, slotAtClick));
                }

                // Only fire actionable event for specific diff statuses
                // (merge statuses included so the 3-way merge context menus can fire)
                if (line.Status is DiffStatus.Modified or DiffStatus.Added or DiffStatus.Gap
                    or DiffStatus.ChangeA or DiffStatus.ChangeB or DiffStatus.Conflict)
                {
                    var selectedIndices = GetSelectedLineIndices();
                    if (selectedIndices.Count > 0)
                    {
                        RightClickLineIndex?.Invoke(this, selectedIndices);
                    }
                }

                // Handle the right-click to prevent default context menu
                e.Handled = true;
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var pos = e.GetPosition(this);

        // Update scrollbar hover state
        bool wasHovered = _isScrollThumbHovered;
        _isScrollThumbHovered = _scrollThumbRect.Contains(pos);
        bool wasHHovered = _isHScrollThumbHovered;
        _isHScrollThumbHovered = _hScrollThumbRect.Contains(pos);
        if (wasHovered != _isScrollThumbHovered || wasHHovered != _isHScrollThumbHovered)
            InvalidateVisual();

        // Structural tooltip follows the cursor; any active drag hides it.
        if (_dragMode != ScrollDragMode.None) HideStructuralTooltip();
        else UpdateStructuralTooltip(pos);

        if (_dragMode == ScrollDragMode.None) return;

        int contentHeight = _viewportHeight - _titleBarHeight;
        double deltaPixels = pos.Y - _dragStartY;

        if (_dragMode == ScrollDragMode.Thumb)
        {
            // Map thumb drag to scroll offset
            double trackH = _scrollTrackRect.Height;
            double thumbH = _scrollThumbRect.Height;
            // Max(0, …): content can shrink below the viewport mid-drag (an async diff result
            // swaps in shorter lines while the mouse is captured) — a negative max throws.
            double maxScroll = Math.Max(0, _scrollExtent - contentHeight);
            double maxThumbTravel = trackH - thumbH;

            if (maxThumbTravel > 0)
            {
                double newOffset = _dragStartOffset + (deltaPixels / maxThumbTravel) * maxScroll;
                _scrollOffset = Math.Clamp(newOffset, 0, maxScroll);
            }
        }
        else if (_dragMode == ScrollDragMode.ContentDrag)
        {
            // Direct pixel-to-pixel scroll (Max(0, …) — see Thumb branch above)
            double maxOffset = Math.Max(0, _scrollExtent - contentHeight);
            _scrollOffset = Math.Clamp(_dragStartOffset - deltaPixels, 0, maxOffset);
        }
        else if (_dragMode == ScrollDragMode.HThumb)
        {
            // Horizontal thumb drag
            double trackW = _hScrollTrackRect.Width;
            double thumbW = _hScrollThumbRect.Width;
            double hTrack = _hScrollTrackWidth();
            double maxHScroll = Math.Max(0, _contentMaxWidth - hTrack);
            double maxThumbTravel = trackW - thumbW;

            if (maxThumbTravel > 0)
            {
                double deltaX = pos.X - _dragStartY; // _dragStartY holds start X (reused for simplicity)
                double newOffset = _dragStartOffset + (deltaX / maxThumbTravel) * maxHScroll;
                _hScrollOffset = Math.Clamp(newOffset, 0, maxHScroll);
            }
        }
        else if (_dragMode == ScrollDragMode.SelectionDrag)
        {
            // Auto-scroll when mouse is near top/bottom edge of viewport
            int margin = 40; // pixels from edge to trigger auto-scroll
            if (pos.Y < margin)
            {
                double maxOffset = Math.Max(0, _scrollExtent - contentHeight);
                _scrollOffset = Math.Clamp(_scrollOffset - 4, 0, maxOffset);
            }
            else if (pos.Y > contentHeight + _titleBarHeight - margin)
            {
                double maxOffset = Math.Max(0, _scrollExtent - contentHeight);
                _scrollOffset = Math.Clamp(_scrollOffset + 4, 0, maxOffset);
            }

            // Update selection end based on current mouse Y
            int lineAtMouse = GetLineAtY(pos.Y);
            double textX = GutterWidth + AccentWidth + 4;
            double hTextX = textX - _hScrollOffset;
            var mouseRow = IsHexView && lineAtMouse >= 0 && Lines != null && lineAtMouse < Lines.Count ? Lines[lineAtMouse] : null;
            int slotAtMouse = mouseRow is { ByteStart: not null } ? GetSlotAtX(pos.X, hTextX, mouseRow.HexSlotMask) : -1;
            bool byteChanged = lineAtMouse != _selectionEnd || slotAtMouse != _byteSelEnd?.slot;
            if (lineAtMouse >= 0 && byteChanged)
            {
                _selectionEnd = lineAtMouse;
                // Drag end follows the exact slot under the mouse — holes never extend the selection.
                if (IsHexView && slotAtMouse >= 0 && _byteSelStart.HasValue)
                    _byteSelEnd = (lineAtMouse, slotAtMouse);
                RaiseScrollRatioChanged();
                RaiseSelectionChanged();
            }
        }

        InvalidateVisual();
        RaiseScrollRatioChanged();
        RaiseHorizontalScrollRatioChanged();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);

        if (_dragMode != ScrollDragMode.None)
        {
            if (_dragMode == ScrollDragMode.SelectionDrag)
            {
                // Normalize: ensure start <= end
                if (_selectionStart > _selectionEnd)
                {
                    int temp = _selectionStart;
                    _selectionStart = _selectionEnd;
                    _selectionEnd = temp;
                }
                InvalidateVisual();

                // Fire SingleClickedLine if this was a click on a single line (no drag range)
                if (_selectionStart >= 0 && _selectionStart == _selectionEnd)
                {
                    SingleClickedLine?.Invoke(this, _selectionStart);

                    // Hex view: also report the exact byte slot for pending sync-point confirmation.
                    if (IsHexView && _byteSelStart.HasValue && _byteSelStart.Value.line == _selectionStart)
                        HexSlotSingleClicked?.Invoke(this, (_selectionStart, _byteSelStart.Value.slot));
                }
            }

            _dragMode = ScrollDragMode.None;
            ReleaseMouseCapture();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _isScrollThumbHovered = false;
        _isHScrollThumbHovered = false;
        HideStructuralTooltip();
        InvalidateVisual();
    }

    // ── Text Selection Helpers ─────────────────────────────────────────────

    /// <summary>
    /// Convert a Y coordinate (local) to a line index. Returns -1 if outside content area.
    /// </summary>
    private int GetLineAtY(double y)
    {
        int contentTop = _titleBarHeight;
        if (y < contentTop) return -1;

        double scrolledY = y - contentTop + _scrollOffset;
        int lineIdx = (int)(scrolledY / LineHeight);

        var lines = Lines;
        if (lines == null || lineIdx < 0 || lineIdx >= lines.Count)
            return -1;

        return lineIdx;
    }

    // ── Structural finding tooltip (advisory — hover text for gutter triangles) ─

    private System.Windows.Controls.ToolTip? _structuralTooltip;
    private int _tooltipLineIdx = -1;

    /// <summary>
    /// Show the tooltip for the structural finding on the row under pos. A null note or hex
    /// view hides it; moving within the same row is a no-op so the tooltip doesn't flicker
    /// while the cursor lingers (Mouse placement tracks the cursor on its own).
    /// </summary>
    private void UpdateStructuralTooltip(Point pos)
    {
        int idx = GetLineAtY(pos.Y);   // -1 outside content or when no lines are set
        var lines = Lines;
        string? note = !IsHexView && lines != null && idx >= 0 ? lines[idx].StructuralNote : null;

        if (note == null)
        {
            HideStructuralTooltip();
            return;
        }
        if (idx == _tooltipLineIdx) return;   // same row — already showing the right text

        var tip = _structuralTooltip ??= new System.Windows.Controls.ToolTip
        {
            Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse,
            PlacementTarget = this,
            MaxWidth = 420,
            HasDropShadow = true
        };
        tip.Content = new TextBlock
        {
            Text = note,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4)
        };
        _tooltipLineIdx = idx;
        tip.IsOpen = true;
    }

    private void HideStructuralTooltip()
    {
        if (_structuralTooltip is { IsOpen: true }) _structuralTooltip.IsOpen = false;
        _tooltipLineIdx = -1;
    }

    /// <summary>
    /// Map an X coordinate to an occupied byte slot (0-15) within a hex row.
    /// Returns -1 if the X position doesn't fall on any occupied slot — holes in aligned
    /// rows are never hit, so selections and sync points can only start on real bytes.
    /// Computes column positions arithmetically from measured per-character advance.
    /// </summary>
    private int GetSlotAtX(double x, double textX, int slotMask)
    {
        // Use precomputed character advance width (monospace font — uniform advance)
        double charW = _charAdvance;
        if (charW <= 0 || slotMask == 0) return -1;

        for (int c = 0; c < HexFormat.BytesPerLine; c++)
        {
            if (((slotMask >> c) & 1) == 0) continue; // Hole — no byte on this side

            // Hex digit pair at the slot's hex column
            var (col, len) = HexFormat.HexSlotColumns(c);
            double byteStartX = textX + col * charW;
            if (x >= byteStartX && x < byteStartX + len * charW)
                return c;

            // ASCII character at the slot's ascii column
            int charCol = HexFormat.AsciiColumn(c);
            double charStartX = textX + charCol * charW;
            if (x >= charStartX && x < charStartX + charW)
                return c;
        }
        return -1;
    }

    /// <summary>
    /// Slot of the byte `steps` occupied slots ahead of slot c (file order), clamped to the
    /// row's last occupied slot. Used to snap click selections to the inspector's type size.
    /// Slot c must be occupied in slotMask.
    /// </summary>
    private static int ExtendSlot(int slotMask, int c, int steps)
    {
        if (steps <= 0) return c;
        int target = Math.Min(HexFormat.SlotOrdinal(slotMask, c) + steps, HexFormat.SlotCount(slotMask) - 1);
        for (int s = 0; s < HexFormat.BytesPerLine; s++)
        {
            if (((slotMask >> s) & 1) == 0) continue;
            if (HexFormat.SlotOrdinal(slotMask, s) == target) return s;
        }
        return c;
    }

    /// <summary>
    /// Endpoint of an InspectorTypeSize-byte selection starting at (lineIdx, slot), wrapping
    /// into subsequent rows when the clicked row runs out before the element completes. Gap
    /// filler rows (no bytes on this side) are skipped. Returns null if the start isn't on an
    /// occupied slot; covers fewer than InspectorTypeSize bytes only when the lines end first.
    /// </summary>
    private (int line, int slot)? ExtendSlotWrapped(int lineIdx, int slot)
    {
        var lines = Lines;
        if (lines == null || lineIdx < 0 || lineIdx >= lines.Count) return null;
        var row = lines[lineIdx];
        if (row.ByteStart == null || ((row.HexSlotMask >> slot) & 1) == 0) return null;

        int need = InspectorTypeSize - (HexFormat.SlotCount(row.HexSlotMask) - HexFormat.SlotOrdinal(row.HexSlotMask, slot));
        if (need <= 0)
            return (lineIdx, ExtendSlot(row.HexSlotMask, slot, InspectorTypeSize - 1));

        int fallbackLine = lineIdx, fallbackSlot = HexFormat.LastSlot(row.HexSlotMask);
        for (int i = lineIdx + 1; i < lines.Count && need > 0; i++)
        {
            var r = lines[i];
            if (r.ByteStart == null) continue; // Gap filler — no bytes on this side
            int count = HexFormat.SlotCount(r.HexSlotMask);
            if (count == 0) continue;
            fallbackLine = i;
            if (count >= need) return (i, SlotAtOrdinal(r.HexSlotMask, need - 1));
            fallbackSlot = HexFormat.LastSlot(r.HexSlotMask);
            need -= count;
        }
        return (fallbackLine, fallbackSlot); // lines ended before the element completed
    }

    /// <summary>
    /// Slot index of the row's `ordinal`-th occupied slot (0-based), or -1 if absent.
    /// </summary>
    private static int SlotAtOrdinal(int slotMask, int ordinal)
    {
        for (int s = 0; s < HexFormat.BytesPerLine; s++)
            if (((slotMask >> s) & 1) != 0 && HexFormat.SlotOrdinal(slotMask, s) == ordinal) return s;
        return -1;
    }

    /// <summary>
    /// Get the selected text as a string. Returns empty string if no selection.
    /// </summary>
    public string GetSelectedText()
    {
        var lines = Lines;
        if (lines == null || _selectionStart < 0 || _selectionEnd < 0)
            return "";

        int start = Math.Min(_selectionStart, _selectionEnd);
        int end = Math.Max(_selectionStart, _selectionEnd);

        var sb = new System.Text.StringBuilder();
        for (int i = start; i <= end; i++)
        {
            if (i > start) sb.AppendLine();
            sb.Append(lines[i].Text ?? "");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Returns true if there is an active text selection.
    /// </summary>
    public bool HasSelection => _selectionStart >= 0 && _selectionEnd >= 0;

    /// <summary>
    /// Monotonic stamp of this viewer's most recent selection change (higher = more recent,
    /// comparable across viewers). Used to route Ctrl+C to the pane the user last touched.
    /// </summary>
    public int SelectionSeq => _selectionSeq;

    /// <summary>
    /// Returns the byte-precise selection range for hex inspector purposes as (startByte,
    /// byteCount) in file space. Accurate under byte-aligned rows: offsets come from each
    /// row's ByteStart/HexSlotMask, and rows between the endpoints are walked so Gap filler
    /// rows (no bytes on this side) never inflate the count — an endByte-startByte span would
    /// over-count across them. Returns null if no byte-level selection is active or an
    /// endpoint no longer sits on an occupied slot (e.g. the lines were swapped out).
    /// </summary>
    public (int startByte, int byteCount)? GetByteSelectionRange()
    {
        var lines = Lines;
        if (!_byteSelStart.HasValue || !_byteSelEnd.HasValue || lines == null) return null;

        var (lineA, slotA) = _byteSelStart.Value;
        var (lineB, slotB) = _byteSelEnd.Value;
        if (lineA < 0 || lineA >= lines.Count || lineB < 0 || lineB >= lines.Count) return null;

        int minLine = Math.Min(lineA, lineB), maxLine = Math.Max(lineA, lineB);
        var rowA = lines[lineA];
        var rowB = lines[lineB];
        if (rowA.ByteStart == null || ((rowA.HexSlotMask >> slotA) & 1) == 0) return null;
        if (rowB.ByteStart == null || ((rowB.HexSlotMask >> slotB) & 1) == 0) return null;

        int startOrdinal = lineA == lineB
            ? Math.Min(HexFormat.SlotOrdinal(rowA.HexSlotMask, slotA), HexFormat.SlotOrdinal(rowB.HexSlotMask, slotB))
            : HexFormat.SlotOrdinal(rowA.HexSlotMask, slotA);
        int endOrdinal = lineA == lineB
            ? Math.Max(HexFormat.SlotOrdinal(rowA.HexSlotMask, slotA), HexFormat.SlotOrdinal(rowB.HexSlotMask, slotB))
            : HexFormat.SlotOrdinal(rowB.HexSlotMask, slotB);

        int startByte = -1;
        int byteCount = 0;
        for (int i = minLine; i <= maxLine; i++)
        {
            var r = lines[i];
            if (r.ByteStart == null) continue; // Gap filler — no bytes on this side

            int count;
            if (i == minLine && i == maxLine)
                count = endOrdinal - startOrdinal + 1;
            else if (i == minLine)
                count = HexFormat.SlotCount(r.HexSlotMask) - startOrdinal;
            else if (i == maxLine)
                count = endOrdinal + 1;
            else
                count = HexFormat.SlotCount(r.HexSlotMask);

            if (startByte < 0) startByte = r.ByteStart.Value + startOrdinal;
            byteCount += count;
        }
        if (startByte < 0) return null;

        return (startByte, byteCount);
    }

    /// <summary>
    /// Selects a byte range in file space — the inverse of GetByteSelectionRange: locates the row
    /// holding startByte (same ByteStart/SlotCount predicate), maps both endpoints to
    /// (line, slot) and paints the selection. Returns false when lines aren't assigned yet or the
    /// range doesn't fit in the rows. Raises the selection-changed event so the inspector refreshes
    /// onto the new bytes.
    /// </summary>
    public bool SetByteSelectionRange(int startByte, int byteCount)
    {
        var lines = Lines;
        if (lines == null || lines.Count == 0 || byteCount <= 0) return false;

        int startLine = -1, startOrdinal = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            var r = lines[i];
            if (r.ByteStart == null) continue;   // Gap filler — no bytes on this side
            int count = HexFormat.SlotCount(r.HexSlotMask);
            if (startByte >= r.ByteStart.Value && startByte < r.ByteStart.Value + count)
            {
                startLine = i;
                startOrdinal = startByte - r.ByteStart.Value;
                break;
            }
        }
        if (startLine < 0) return false;

        // Walk forward consuming whole rows until the endpoint lands (mirrors ExtendSlotWrapped).
        int endLine = -1, endOrdinal = -1;
        int remaining = byteCount;
        for (int i = startLine; i < lines.Count && remaining > 0; i++)
        {
            var r = lines[i];
            if (r.ByteStart == null) continue;   // Gap filler — no bytes on this side
            int count = HexFormat.SlotCount(r.HexSlotMask);
            if (i == startLine) count -= startOrdinal;
            if (count <= 0) continue;
            if (remaining <= count)
            {
                endLine = i;
                endOrdinal = (i == startLine ? startOrdinal : 0) + remaining - 1;
                remaining = 0;
            }
            else
                remaining -= count;
        }
        if (endLine < 0) return false;   // range ran off the end of the rows

        int slotStart = SlotAtOrdinal(lines[startLine].HexSlotMask, startOrdinal);
        int slotEnd = SlotAtOrdinal(lines[endLine].HexSlotMask, endOrdinal);
        if (slotStart < 0 || slotEnd < 0) return false;

        _byteSelStart = (startLine, slotStart);
        _byteSelEnd = (endLine, slotEnd);
        InvalidateVisual();
        RaiseSelectionChanged();
        return true;
    }

    /// <summary>
    /// Returns all currently selected line indices as a sorted list.
    /// If no selection is active, returns a single-element list containing the
    /// SelectedLineIndex (set by right-click), or an empty list if that's also invalid.
    /// </summary>
    public List<int> GetSelectedLineIndices()
    {
        var indices = new List<int>();

        int start = _selectionStart >= 0 && _selectionEnd >= 0 ? Math.Min(_selectionStart, _selectionEnd) : -1;
        int end = _selectionStart >= 0 && _selectionEnd >= 0 ? Math.Max(_selectionStart, _selectionEnd) : -1;

        if (start >= 0 && end >= 0 && Lines != null)
        {
            for (int i = start; i <= end && i < Lines.Count; i++)
                indices.Add(i);
        }
        else if (SelectedLineIndex >= 0 && Lines != null && SelectedLineIndex < Lines.Count)
        {
            indices.Add(SelectedLineIndex);
        }

        return indices;
    }

    // ── Keyboard Navigation ────────────────────────────────────────────────────

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        int contentHeight = _viewportHeight - _titleBarHeight;
        double maxOffset = Math.Max(0, _scrollExtent - contentHeight);
        bool changed = false;

        switch (e.Key)
        {
            case Key.Up:
                _scrollOffset = Math.Clamp(_scrollOffset - LineHeight, 0, maxOffset);
                changed = true;
                break;
            case Key.Down:
                _scrollOffset = Math.Clamp(_scrollOffset + LineHeight, 0, maxOffset);
                changed = true;
                break;
            case Key.PageUp:
                _scrollOffset = Math.Clamp(_scrollOffset - contentHeight + LineHeight, 0, maxOffset);
                changed = true;
                break;
            case Key.PageDown:
                _scrollOffset = Math.Clamp(_scrollOffset + contentHeight - LineHeight, 0, maxOffset);
                changed = true;
                break;
            case Key.Home:
                _scrollOffset = 0;
                changed = true;
                break;
            case Key.End:
                _scrollOffset = maxOffset;
                changed = true;
                break;
        }

        if (changed)
        {
            InvalidateVisual();
            RaiseScrollRatioChanged();
            e.Handled = true;
        }
    }

    // ── Diff Block Navigation ──────────────────────────────────────────────────

    /// <summary>
    /// Navigate to the next/previous diff block (contiguous region of non-Unchanged lines).
    /// Scrolls into view and updates SelectedLineIndex.
    /// </summary>
    public void NavigateToDiffBlock(int direction)
    {
        var lines = Lines;
        if (lines is null || lines.Count == 0) return;

        int current = SelectedLineIndex;

        // Find the current diff block boundaries
        int searchFrom;
        if (current >= 0 && current < lines.Count && lines[current].Status != DiffStatus.Unchanged)
        {
            // Currently on a diff line — if going forward, start from end of current block
            if (direction > 0)
            {
                int blockEnd = current + 1;
                while (blockEnd < lines.Count && lines[blockEnd].Status != DiffStatus.Unchanged)
                    blockEnd++;
                searchFrom = blockEnd;
            }
            else
            {
                // Going backward, start from beginning of current block
                int blockStart = current - 1;
                while (blockStart >= 0 && lines[blockStart].Status != DiffStatus.Unchanged)
                    blockStart--;
                searchFrom = blockStart;
            }
        }
        else
        {
            // Not on a diff line — start from the first visible unchanged line after scroll top
            searchFrom = direction > 0
                ? Math.Max(0, (int)(_scrollOffset / LineHeight))
                : (int)(_scrollOffset / LineHeight) + ((_viewportHeight - _titleBarHeight) / LineHeight);
        }

        // Search for the next diff block
        int found = -1;
        if (direction > 0)
        {
            for (int i = searchFrom; i < lines.Count; i++)
            {
                if (lines[i].Status != DiffStatus.Unchanged)
                {
                    found = i;
                    break;
                }
            }
        }
        else
        {
            for (int i = Math.Min(searchFrom, lines.Count - 1); i >= 0; i--)
            {
                if (lines[i].Status != DiffStatus.Unchanged)
                {
                    // Find the start of this block
                    found = i;
                    break;
                }
            }
        }

        if (found >= 0)
        {
            SelectedLineIndex = found;

            // Center the start of the block in the viewport
            ScrollToLine(found, center: true);
        }
    }

    // ── Sync Scroll Event Raising ──────────────────────────────────────────────

    private void RaiseScrollRatioChanged()
    {
        double ratio = GetScrollRatio();
        ScrollRatioChanged?.Invoke(this, ratio);
    }

    private void RaiseHorizontalScrollRatioChanged()
    {
        double ratio = GetHorizontalScrollRatio();
        HorizontalScrollRatioChanged?.Invoke(this, ratio);
    }

    // ── Selection Changed Event Raising ────────────────────────────────────────

    private void RaiseSelectionChanged()
    {
        _selectionSeq = Interlocked.Increment(ref s_selectionSeq);

        // Compute actual selection range (not falling back to SelectedLineIndex
        // which is for right-click context and unrelated to drag selection)
        var indices = new List<int>();
        if (_selectionStart >= 0 && _selectionEnd >= 0 && Lines != null)
        {
            int start = Math.Min(_selectionStart, _selectionEnd);
            int end = Math.Max(_selectionStart, _selectionEnd);
            for (int i = start; i <= end && i < Lines.Count; i++)
                indices.Add(i);
        }
        SelectionChanged?.Invoke(this, indices);
    }

    // ── Public API: Programmatic scroll sync from parent ───────────────────────

    /// <summary>
    /// Scroll to match the partner viewer's scroll ratio.
    /// Called by MainWindow when sync scroll is enabled.
    /// </summary>
    public void SyncScroll(double ratio)
    {
        int contentHeight = _viewportHeight - _titleBarHeight;
        double maxOffset = Math.Max(0, _scrollExtent - contentHeight);
        _scrollOffset = ratio * maxOffset;
        InvalidateVisual();
    }

    /// <summary>
    /// Returns the current scroll position as (topmost visible line index, sub-line pixel
    /// offset). Used by MainWindow to carry an exact position across layout switches.
    /// </summary>
    public (int Line, double SubLine) GetScrollAnchor()
    {
        int contentHeight = _viewportHeight - _titleBarHeight;
        if (_scrollExtent <= 0 || contentHeight <= 0) return (0, 0);
        int line = (int)(_scrollOffset / LineHeight);
        return (line, _scrollOffset - line * LineHeight);
    }

    /// <summary>
    /// Maps this viewer's topmost visible row to the line of a plain text editor showing the
    /// same content — gap rows (empty alignment cells) don't exist in plain text, so only
    /// non-gap rows count. Used when swapping a pane for an edit TextBox so the editor opens
    /// where the user was looking.
    /// </summary>
    public static int AnchorToPlainLine(FastDiffViewer viewer)
    {
        int line = viewer.GetScrollAnchor().Line;
        var lines = viewer.Lines;
        if (line <= 0 || lines == null) return 0;

        int n = 0;
        for (int i = 0; i < Math.Min(line, lines.Count); i++)
            if (lines[i].Status != DiffStatus.Gap) n++;
        return n;
    }

    /// <summary>
    /// Sets the scroll position to an exact line + sub-line pixel offset. Like SyncScroll,
    /// this does not raise ScrollRatioChanged, so it cannot trigger sync feedback.
    /// </summary>
    public void SetScrollAnchor(int line, double subLine)
    {
        int contentHeight = _viewportHeight - _titleBarHeight;
        double maxOffset = Math.Max(0, _scrollExtent - contentHeight);
        _scrollOffset = Math.Clamp(line * LineHeight + subLine, 0, maxOffset);
        InvalidateVisual();
    }

    /// <summary>
    /// Called by MainWindow when horizontal scroll sync is enabled.
    /// </summary>
    public void SyncHorizontalScroll(double ratio)
    {
        double hTrack = _hScrollTrackWidth();
        double scrollable = _contentMaxWidth - hTrack;
        _hScrollOffset = ratio * scrollable;
        _hScrollOffset = Math.Clamp(_hScrollOffset, 0, Math.Max(0, scrollable));
        InvalidateVisual();
    }

    /// <summary>
    /// Scroll to bring a specific line index into view.
    /// Called by MainWindow for search navigation.
    /// When center is true the line is placed in the middle of the viewport (used by
    /// diff-block/conflict navigation so the jumped-to block starts mid-screen).
    /// </summary>
    public void ScrollToLine(int lineIndex, bool center = false)
    {
        int contentHeight = _viewportHeight - _titleBarHeight;
        double maxScroll = Math.Max(0, _scrollExtent - contentHeight);
        double lineY = lineIndex * LineHeight;

        if (center)
        {
            _scrollOffset = Math.Clamp(lineY + LineHeight / 2 - contentHeight / 2, 0, maxScroll);
        }
        else if (lineY < _scrollOffset)
        {
            _scrollOffset = Math.Max(0, lineY - 2);
        }
        else if (lineY + LineHeight > _scrollOffset + contentHeight)
        {
            _scrollOffset = Math.Min(maxScroll, lineY - contentHeight + LineHeight + 2);
        }

        InvalidateVisual();
        RaiseScrollRatioChanged();
    }

    /// <summary>
    /// Reveal where a partner pane's Added/Removed block would sit in this file: mark the row
    /// boundary `slot` (count of real lines before it — exactly where a Gap row would be
    /// inserted) with a thin accent line and scroll to center it. Raising ScrollRatioChanged
    /// lets sync scroll bring the source pane along, so both panes converge on the change.
    /// The line persists until the lines change or another jump replaces it.
    /// </summary>
    public void ShowAnchorSlot(int slot, DiffStatus status)
    {
        _anchorSlot = (slot, status);
        ScrollToLine(slot, center: true);
    }

    // ── Zero Visual Children (no layout overhead) ──────────────────────────────

    protected override int VisualChildrenCount => 0;
    protected override System.Windows.Media.Visual? GetVisualChild(int index) => throw new IndexOutOfRangeException();
}

// ═══════════════════════════════════════════════════════════════════════════════
// LRU Cache for FormattedText — prevents unbounded memory growth with large files
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Simple LRU cache for FormattedText objects. Identical line content (common in
/// unchanged regions) shares one FormattedText instance, dramatically reducing
/// per-frame allocation during scroll.
/// </summary>
internal sealed class LruFormattedTextCache
{
    /// <summary>
    /// Self-referencing node in the LRU list. Each node carries its own key and direct
    /// neighbor pointers, so Unlink() is O(1) — unlike LinkedList&lt;T&gt;.Find(value), which
    /// does an O(N) equality scan per hit (catastrophic with thousands of entries).
    /// </summary>
    private sealed class Node
    {
        public (string text, string color, double fontSize) Key;
        public FormattedText? Value;
        public Node? Prev;
        public Node? Next;

        public void Unlink()
        {
            if (Prev != null) Prev.Next = Next;
            if (Next != null) Next.Prev = Prev;
        }
    }

    /// <summary>
    /// Dictionary for O(1) lookup by key.
    /// Key is (text, brush_color_hex, font_size) so that same text with different colors/sizes is cached separately.
    /// </summary>
    private readonly System.Collections.Generic.Dictionary<(string text, string color, double fontSize), Node> _cache = new();

    // Circular list sentinel: _sentinel.Next is the most-recently-used node,
    // _sentinel.Prev is the least-recently-used (evicted first).
    private readonly Node _sentinel = new();

    private readonly int _maxEntries;

    public LruFormattedTextCache(int maxEntries)
    {
        _maxEntries = maxEntries;
        _sentinel.Prev = _sentinel;
        _sentinel.Next = _sentinel;
    }

    public void Clear()
    {
        _cache.Clear();
        _sentinel.Prev = _sentinel;
        _sentinel.Next = _sentinel;
    }

    private void PushFront(Node node)
    {
        var head = _sentinel.Next!; // sentinel always links to itself when empty
        node.Prev = _sentinel;
        node.Next = head;
        head.Prev = node;
        _sentinel.Next = node;
    }

    public FormattedText GetOrCreate(string text, Brush foreground, Typeface typeface, TextFormattingMode formatting, double fontSize)
    {
        // Composite key: include brush color and font size so that the same text with
        // different colors or sizes is cached separately.
        var solid = foreground as SolidColorBrush;
        var key = (text, solid?.Color.ToString() ?? "N", fontSize);

        // Fast path: hit — promote to most-recently-used, O(1) pointer surgery
        if (_cache.TryGetValue(key, out var node))
        {
            node.Unlink();
            PushFront(node);
            return node.Value!;
        }

        // Miss: create new
        var ft = new FormattedText(
            text,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            foreground,
            96.0
        )
        {
            MaxTextWidth = 10000 // Large enough for horizontal scrolling
        };

        // Evict least-recently-used entries if at capacity — tail is O(1) via sentinel
        while (_cache.Count >= _maxEntries)
        {
            var lru = _sentinel.Prev!; // list is non-empty here (count >= maxEntries > 0)
            lru.Unlink();
            _cache.Remove(lru.Key);
        }

        node = new Node { Key = key, Value = ft };
        PushFront(node);
        _cache[key] = node;
        return ft;
    }
}