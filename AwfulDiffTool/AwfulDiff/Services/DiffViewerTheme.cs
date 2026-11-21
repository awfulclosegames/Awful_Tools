using AwfulDiff.Models;
using System.Windows.Media;

namespace AwfulDiff.Services;

/// <summary>
/// Holds all brush definitions used by FastDiffViewer for a single theme.
/// Instances are created once per theme and shared across all viewer instances.
/// </summary>
public class DiffViewerTheme
{
    // Backgrounds
    public SolidColorBrush BgPanel { get; set; } = null!;
    public SolidColorBrush BgGutter { get; set; } = null!;
    public SolidColorBrush BgUnchanged { get; set; } = null!;
    public SolidColorBrush BgAdded { get; set; } = null!;
    public SolidColorBrush BgRemoved { get; set; } = null!;
    public SolidColorBrush BgModified { get; set; } = null!;
    public SolidColorBrush BgGap { get; set; } = null!;

    // 3-way merge — A cyan, B green (reuses Added), conflict purple
    public SolidColorBrush BgChangeA { get; set; } = null!;
    public SolidColorBrush BgChangeB { get; set; } = null!;
    public SolidColorBrush BgConflict { get; set; } = null!;

    // Gap stripe
    public SolidColorBrush GapStripePaint { get; set; } = null!;

    // Foregrounds
    public SolidColorBrush FgUnchanged { get; set; } = null!;
    public SolidColorBrush FgAdded { get; set; } = null!;
    public SolidColorBrush FgRemoved { get; set; } = null!;
    public SolidColorBrush FgModified { get; set; } = null!;
    public SolidColorBrush FgGap { get; set; } = null!;
    public SolidColorBrush FgChangeA { get; set; } = null!;
    public SolidColorBrush FgChangeB { get; set; } = null!;
    public SolidColorBrush FgConflict { get; set; } = null!;
    /// <summary>✓ gutter glyph for winner rows of resolved conflict slots (green — "decided").</summary>
    public SolidColorBrush FgConflictResolved { get; set; } = null!;
    public SolidColorBrush FgTitle { get; set; } = null!;
    public SolidColorBrush FgMeta { get; set; } = null!;
    public SolidColorBrush FgLineNumber { get; set; } = null!;

    // Syntax highlighting (Visual Studio-style, desaturated) — deliberately muted so the
    // diff status block colours (add/remove/modified/conflict) stay visually dominant.
    public SolidColorBrush SyntaxKeyword { get; set; } = null!;
    public SolidColorBrush SyntaxString { get; set; } = null!;
    public SolidColorBrush SyntaxComment { get; set; } = null!;
    public SolidColorBrush SyntaxNumber { get; set; } = null!;
    public SolidColorBrush SyntaxPreprocessor { get; set; } = null!;
    public SolidColorBrush SyntaxFunction { get; set; } = null!;
    public SolidColorBrush SyntaxType { get; set; } = null!;

    // Structural analysis indicators (advisory gutter triangles) — error red for parse/brace
    // problems, amber for modifications, green for additions, desaturated rose for removals.
    public SolidColorBrush StructuralError { get; set; } = null!;
    public SolidColorBrush StructuralChanged { get; set; } = null!;
    public SolidColorBrush StructuralAdded { get; set; } = null!;
    public SolidColorBrush StructuralRemoved { get; set; } = null!;

    // Accent bar colors
    public SolidColorBrush AccentUnchanged { get; set; } = null!;
    public SolidColorBrush AccentAdded { get; set; } = null!;
    public SolidColorBrush AccentRemoved { get; set; } = null!;
    public SolidColorBrush AccentModified { get; set; } = null!;
    public SolidColorBrush AccentGap { get; set; } = null!;
    public SolidColorBrush AccentChangeA { get; set; } = null!;
    public SolidColorBrush AccentChangeB { get; set; } = null!;
    public SolidColorBrush AccentConflict { get; set; } = null!;
    /// <summary>Left accent bar for winner rows of resolved conflict slots.</summary>
    public SolidColorBrush AccentConflictResolved { get; set; } = null!;

    // Diff map overview bar
    public SolidColorBrush DiffMapBg { get; set; } = null!;
    public SolidColorBrush DiffMapAdded { get; set; } = null!;
    public SolidColorBrush DiffMapRemoved { get; set; } = null!;
    public SolidColorBrush DiffMapModified { get; set; } = null!;
    public SolidColorBrush DiffMapGap { get; set; } = null!;
    public SolidColorBrush DiffMapChangeA { get; set; } = null!;
    public SolidColorBrush DiffMapChangeB { get; set; } = null!;
    public SolidColorBrush DiffMapConflict { get; set; } = null!;
    /// <summary>Diff-map marker for blocks of winner rows from resolved conflict slots.</summary>
    public SolidColorBrush DiffMapConflictResolved { get; set; } = null!;
    public SolidColorBrush DiffMapDefault { get; set; } = null!;
    public SolidColorBrush DiffMapScrollIndicator { get; set; } = null!;

    // Scrollbar and grid lines
    public SolidColorBrush BorderLine { get; set; } = null!;
    public SolidColorBrush GutterDivider { get; set; } = null!;
    public SolidColorBrush ScrollTrack { get; set; } = null!;
    public SolidColorBrush ScrollThumb { get; set; } = null!;
    public SolidColorBrush ScrollThumbHover { get; set; } = null!;

    // Selection and search
    public SolidColorBrush SelectionHighlight { get; set; } = null!;
    public SolidColorBrush SelectionTextHighlight { get; set; } = null!;
    public SolidColorBrush SearchMatchHighlight { get; set; } = null!;
    public SolidColorBrush SearchCurrentHighlight { get; set; } = null!;

    // Drag-over
    public SolidColorBrush DragOverOverlay { get; set; } = null!;
    public SolidColorBrush DragOverBorder { get; set; } = null!;

    // Byte-level highlight (hex view inspector)
    public SolidColorBrush ByteHighlightFill { get; set; } = null!;

    // Inline diff change highlight
    public SolidColorBrush InlineChangedHighlight { get; set; } = null!;

    // Sync point visuals
    public SolidColorBrush PendingSyncHighlight { get; set; } = null!;
    public SolidColorBrush PendingSyncBorderBrush { get; set; } = null!;
    public SolidColorBrush SyncPointLineHighlight { get; set; } = null!;
    public SolidColorBrush SyncPointAccentBar { get; set; } = null!;
    public SolidColorBrush SyncPointGutterBg { get; set; } = null!;
    public SolidColorBrush SyncPointMarkerText { get; set; } = null!;

    // No-differences overlay
    public SolidColorBrush NoDiffOverlayBg { get; set; } = null!;
    public SolidColorBrush NoDiffTitleBrush { get; set; } = null!;
    public SolidColorBrush NoDiffSubBrush { get; set; } = null!;
    public SolidColorBrush NoDiffAccentBrush { get; set; } = null!;

    // Scanline brush (CRT effect — only used in Awful theme)
    public System.Windows.Media.DrawingBrush? ScanlineBrush { get; set; }

    // Pen helpers (derived from brushes above)
    public System.Windows.Media.Pen GapStripePen { get; set; } = null!;
    public System.Windows.Media.Pen BorderPen { get; set; } = null!;
    public System.Windows.Media.Pen ByteHighlightPen { get; set; } = null!;
    public System.Windows.Media.Pen PendingSyncPen { get; set; } = null!;
    public System.Windows.Media.Pen SyncPointDashedLine { get; set; } = null!;

    /// <summary>
    /// The base font size for content text in FastDiffViewer (scaled by FontScale).
    /// </summary>
    public double BaseFontSize { get; set; } = 13.0;

    /// <summary>
    /// The base font size for gutter text in FastDiffViewer (scaled by FontScale).
    /// </summary>
    public double GutterFontSize { get; set; } = 12.0;

    /// <summary>
    /// The base font size for title/meta text in FastDiffViewer (scaled by FontScale).
    /// </summary>
    public double TitleFontSize { get; set; } = 13.0;

    // ── Factory Methods ──────────────────────────────────────────────────────

    /// <summary>
    /// Create the base theme for `name` ("Awful", "SystemLight", "SystemDark") with any
    /// per-theme user colour overrides applied on top. Single entry point so every
    /// construction site (theme switch, new viewer instance) stays consistent.
    /// </summary>
    public static DiffViewerTheme Create(string name, ThemeColorOverrides? overrides = null)
    {
        var t = name switch
        {
            "SystemLight" => CreateSystemLight(),
            "SystemDark"  => CreateSystemDark(),
            _             => CreateAwful()
        };
        ApplyOverrides(t, overrides);
        return t;
    }

    /// <summary>
    /// Overlay the user's custom colours onto a freshly created theme. Each enabled role
    /// recolours its whole brush family — text, accent bar and diff-map marker take the
    /// chosen colour, and the row background becomes a translucent tint of it, so one
    /// picker gives a coherent look. Disabled or invalid roles leave the built-in brushes
    /// untouched (the theme's normal colours are used).
    /// </summary>
    public static void ApplyOverrides(DiffViewerTheme t, ThemeColorOverrides? o)
    {
        if (o == null) return;

        // True when the role is enabled and its hex parses — caller then replaces the
        // whole family with the chosen colour.
        bool Recolor(ColorOverride ov, out Color c)
        {
            c = Colors.Black;
            return ov.Enabled && TryParseHex(ov.Hex, out c);
        }

        if (Recolor(o.Added, out var a))
        {
            // The resolved-conflict family is derived from the Added green — keep it in step.
            t.FgAdded = t.AccentAdded = t.DiffMapAdded = new SolidColorBrush(a);
            t.FgConflictResolved = t.AccentConflictResolved = t.DiffMapConflictResolved = new SolidColorBrush(a);
            t.BgAdded = new SolidColorBrush(Color.FromArgb(0x30, a.R, a.G, a.B));
        }
        if (Recolor(o.Removed, out var r))
        {
            t.FgRemoved = t.AccentRemoved = t.DiffMapRemoved = new SolidColorBrush(r);
            t.BgRemoved = new SolidColorBrush(Color.FromArgb(0x30, r.R, r.G, r.B));
        }
        if (Recolor(o.Modified, out var m))
        {
            t.FgModified = t.AccentModified = t.DiffMapModified = new SolidColorBrush(m);
            t.BgModified = new SolidColorBrush(Color.FromArgb(0x30, m.R, m.G, m.B));
        }

        // Merge candidates colour both change families (A and B read as one role to the user).
        if (Recolor(o.MergeCandidates, out var mc))
        {
            t.FgChangeA = t.FgChangeB = new SolidColorBrush(mc);
            t.AccentChangeA = t.AccentChangeB = new SolidColorBrush(mc);
            t.DiffMapChangeA = t.DiffMapChangeB = new SolidColorBrush(mc);
            t.BgChangeA = t.BgChangeB = new SolidColorBrush(Color.FromArgb(0x30, mc.R, mc.G, mc.B));
        }

        if (Recolor(o.Conflicts, out var cf))
        {
            t.FgConflict = t.AccentConflict = t.DiffMapConflict = new SolidColorBrush(cf);
            t.BgConflict = new SolidColorBrush(Color.FromArgb(0x30, cf.R, cf.G, cf.B));
        }

        // Standard text — content foreground only (no accent/background family).
        if (Recolor(o.Text, out var tc))
            t.FgUnchanged = new SolidColorBrush(tc);
    }

    /// <summary>
    /// Strict hex parse: "#RRGGBB" or "#AARRGGBB" (leading # optional, case-insensitive).
    /// Named colours and any other format are rejected.
    /// </summary>
    public static bool TryParseHex(string? hex, out Color color)
    {
        color = Colors.Black;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.Trim().TrimStart('#');
        if (s.Length is not (6 or 8)) return false;
        if (!uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v)) return false;
        color = s.Length == 8
            ? Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public static DiffViewerTheme CreateAwful()
    {
        var t = new DiffViewerTheme();

        // Backgrounds — deep space blue palette
        t.BgPanel = Solid(0x13, 0x1A, 0x2B);
        t.BgGutter = Solid(0x0A, 0x0E, 0x18);
        t.BgUnchanged = Brushes.Transparent;
        t.BgAdded = SolidA(0x24, 0x00, 0xFF, 0x9D);
        t.BgRemoved = SolidA(0x24, 0xFF, 0x40, 0x50);
        t.BgModified = SolidA(0x24, 0xFF, 0xB0, 0x40);
        t.BgGap = SolidA(0x38, 0x1A, 0x25, 0x40);

        // 3-way merge — A cyan, B reuses Added green, conflict purple
        t.BgChangeA = SolidA(0x24, 0x00, 0xD4, 0xFF);
        t.BgChangeB = t.BgAdded;
        t.BgConflict = SolidA(0x28, 0xB0, 0x40, 0xFF);

        // Gap stripe
        t.GapStripePaint = Solid(0x00, 0xE0, 0xD0);

        // Foregrounds — brighter, more contrast for sci-fi feel
        t.FgUnchanged = Solid(0x7A, 0x8B, 0xA8);
        t.FgAdded = Solid(0x60, 0xFF, 0xB0);
        t.FgRemoved = Solid(0xFF, 0x60, 0x70);
        t.FgModified = Solid(0xFF, 0xC0, 0x50);
        t.FgGap = Solid(0x00, 0xD0, 0xC0);
        t.FgChangeA = Solid(0x50, 0xE0, 0xFF);
        t.FgChangeB = t.FgAdded;
        t.FgConflict = Solid(0xD0, 0x80, 0xFF);
        t.FgConflictResolved = t.FgAdded;   // resolved-conflict ✓ glyph — Added green
        t.FgTitle = Solid(0x00, 0xE5, 0xFF);
        t.FgMeta = Solid(0x3A, 0x45, 0x58);
        t.FgLineNumber = Solid(0x5A, 0x6A, 0x80);

        // Syntax highlighting — muted pastels that recede against the neon status colours
        t.SyntaxKeyword = Solid(0x6C, 0x8C, 0xD5);
        t.SyntaxString = Solid(0xCE, 0x9B, 0x8A);
        t.SyntaxComment = Solid(0x6A, 0x7D, 0x70);
        t.SyntaxNumber = Solid(0x5C, 0x9E, 0x94);
        t.SyntaxPreprocessor = Solid(0x8F, 0x7B, 0xA8);
        t.SyntaxFunction = Solid(0xD4, 0xC4, 0x74);   // muted gold — VS-style method/call hue
        t.SyntaxType = Solid(0x64, 0xC4, 0xD4);       // cyan — distinct from the teal numbers

        // Accent bar colors — neon-bright
        t.AccentUnchanged = Solid(0x1E, 0x2A, 0x3A);
        t.AccentAdded = Solid(0x60, 0xFF, 0xB0);
        t.AccentRemoved = Solid(0xFF, 0x50, 0x60);
        t.AccentModified = Solid(0xFF, 0xC0, 0x50);
        t.AccentGap = Solid(0x00, 0xE0, 0xD0);
        t.AccentChangeA = Solid(0x00, 0xD4, 0xFF);
        t.AccentChangeB = t.AccentAdded;
        t.AccentConflict = Solid(0xC0, 0x70, 0xFF);
        t.AccentConflictResolved = t.AccentAdded;   // resolved-conflict bar — Added green

        // Structural analysis indicators — status hues reused; rose stays distinct from error red
        t.StructuralError = t.AccentRemoved;
        t.StructuralChanged = t.AccentModified;
        t.StructuralAdded = t.AccentAdded;
        t.StructuralRemoved = Solid(0xD0, 0x88, 0x9C);

        // Diff map overview bar
        t.DiffMapBg = Solid(0x0A, 0x0E, 0x18);
        t.DiffMapAdded = Solid(0x60, 0xFF, 0xB0);
        t.DiffMapRemoved = Solid(0xFF, 0x50, 0x60);
        t.DiffMapModified = Solid(0xFF, 0xC0, 0x50);
        t.DiffMapGap = Solid(0x00, 0xE0, 0xD0);
        t.DiffMapChangeA = t.AccentChangeA;
        t.DiffMapChangeB = t.AccentChangeB;
        t.DiffMapConflict = t.AccentConflict;
        t.DiffMapConflictResolved = t.DiffMapAdded;   // resolved-conflict marker — Added green
        t.DiffMapDefault = Solid(0x80, 0x90, 0xA0);
        t.DiffMapScrollIndicator = Solid(0xFF, 0xFF, 0xFF);

        // Scrollbar & grid lines
        t.BorderLine = Solid(0x1A, 0x25, 0x38);
        t.GutterDivider = Solid(0x1E, 0x30, 0x48);
        t.ScrollTrack = Solid(0x0A, 0x0E, 0x18);
        t.ScrollThumb = Solid(0x1E, 0x30, 0x48);
        t.ScrollThumbHover = Solid(0x00, 0xE5, 0xFF);

        // Selection highlight — cyan holographic
        t.SelectionHighlight = SolidA(0x38, 0x00, 0xE5, 0xFF);
        t.SelectionTextHighlight = SolidA(0x58, 0x00, 0xE5, 0xFF);

        // Search match highlights — gold for matches, cyan for current
        t.SearchMatchHighlight = SolidA(0x30, 0xFF, 0xC0, 0x40);
        t.SearchCurrentHighlight = SolidA(0x58, 0x00, 0xE5, 0xFF);

        // Drag-over highlight — cyan glow
        t.DragOverOverlay = SolidA(0x18, 0x00, 0xE5, 0xFF);
        t.DragOverBorder = SolidA(0xCC, 0x00, 0xE5, 0xFF);

        // Byte-level highlight for hex inspector
        t.ByteHighlightFill = SolidA(0x40, 0x00, 0xE5, 0xFF);

        // Inline diff change highlight
        t.InlineChangedHighlight = SolidA(0x48, 0xFF, 0xFF, 0xFF);

        // Sync point visuals — bright cyan / electric blue
        t.PendingSyncHighlight = SolidA(0x60, 0x00, 0xD0, 0xFF);
        t.PendingSyncBorderBrush = Solid(0x00, 0xD0, 0xFF);
        t.SyncPointLineHighlight = SolidA(0x48, 0x00, 0xD0, 0xFF);
        t.SyncPointAccentBar = Solid(0x00, 0xD4, 0xFF);
        t.SyncPointGutterBg = Solid(0x00, 0x80, 0x90);
        t.SyncPointMarkerText = Solid(0x00, 0xE5, 0xFF);

        // No-differences overlay
        t.NoDiffOverlayBg = SolidA(0xE0, 0x08, 0x0C, 0x14);
        t.NoDiffTitleBrush = Solid(0x80, 0xFF, 0xD0);
        t.NoDiffSubBrush = Solid(0x40, 0xCC, 0x8A);
        t.NoDiffAccentBrush = Solid(0x60, 0xFF, 0xB0);

        // Scanline brush (CRT effect)
        var scanlineDrawing = new System.Windows.Media.DrawingGroup();
        scanlineDrawing.Children.Add(new System.Windows.Media.GeometryDrawing(
            SolidA(0x18, 0x00, 0xE5, 0xFF), null,
            new System.Windows.Media.RectangleGeometry(new System.Windows.Rect(0, 0, 1, 1))));
        scanlineDrawing.Freeze();
        t.ScanlineBrush = new System.Windows.Media.DrawingBrush(scanlineDrawing)
        {
            TileMode = System.Windows.Media.TileMode.Tile,
            Viewport = new System.Windows.Rect(0, 0, 1, 4),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = System.Windows.Media.Stretch.None
        };
        t.ScanlineBrush.Freeze();

        // Pens
        t.GapStripePen = CreatePen(t.GapStripePaint, 5);
        t.BorderPen = CreatePen(t.BgGutter, 1);
        t.ByteHighlightPen = CreatePen(Solid(0x00, 0xE5, 0xFF), 1);
        t.PendingSyncPen = new System.Windows.Media.Pen(t.PendingSyncBorderBrush, 2.0) { DashStyle = System.Windows.Media.DashStyles.Dash };
        t.SyncPointDashedLine = new System.Windows.Media.Pen(SolidA(0xDD, 0x00, 0xD4, 0xFF), 1.5) { DashStyle = System.Windows.Media.DashStyles.Dash };

        return t;
    }

    public static DiffViewerTheme CreateSystemDark()
    {
        var t = new DiffViewerTheme();

        // Backgrounds — VS Code dark-inspired
        t.BgPanel = Solid(0x1E, 0x1E, 0x1E);
        t.BgGutter = Solid(0x25, 0x25, 0x25);
        t.BgUnchanged = Brushes.Transparent;
        t.BgAdded = SolidA(0x30, 0x00, 0x80, 0x00);
        t.BgRemoved = SolidA(0x30, 0x80, 0x00, 0x00);
        t.BgModified = SolidA(0x30, 0x80, 0x80, 0x00);
        t.BgGap = SolidA(0x20, 0x60, 0x60, 0x60);

        // 3-way merge — A blue/cyan, B reuses Added green, conflict purple
        t.BgChangeA = SolidA(0x30, 0x26, 0x4F, 0x78);
        t.BgChangeB = t.BgAdded;
        t.BgConflict = SolidA(0x30, 0x8B, 0x5C, 0xF6);

        // Gap stripe
        t.GapStripePaint = Solid(0x50, 0x60, 0x60);

        // Foregrounds
        t.FgUnchanged = Solid(0xD4, 0xD4, 0xD4);
        t.FgAdded = Solid(0xB5, 0xFD, 0x22);
        t.FgRemoved = Solid(0xF1, 0x4C, 0x4C);
        t.FgModified = Solid(0xD7, 0xBA, 0x7D);
        t.FgGap = Solid(0x80, 0x80, 0x80);
        t.FgChangeA = Solid(0x7F, 0xBF, 0xE8);
        t.FgChangeB = t.FgAdded;
        t.FgConflict = Solid(0xC5, 0x86, 0xC0);
        t.FgConflictResolved = t.FgAdded;   // resolved-conflict ✓ glyph — Added green
        t.FgTitle = Solid(0xCC, 0xCC, 0xCC);
        t.FgMeta = Solid(0x60, 0x60, 0x60);
        t.FgLineNumber = Solid(0x85, 0x85, 0x85);

        // Syntax highlighting — desaturated VS dark palette (recedes from status colours)
        t.SyntaxKeyword = Solid(0x7A, 0x9B, 0xC8);
        t.SyntaxString = Solid(0xC0, 0x9B, 0x82);
        t.SyntaxComment = Solid(0x7C, 0x91, 0x73);
        t.SyntaxNumber = Solid(0x6E, 0x9C, 0x94);
        t.SyntaxPreprocessor = Solid(0xA5, 0x7E, 0xA3);
        t.SyntaxFunction = Solid(0xC4, 0xB2, 0x74);   // muted gold — VS-style method/call hue
        t.SyntaxType = Solid(0x74, 0xBC, 0xC8);       // cyan — distinct from the teal numbers

        // Accent bar colors
        t.AccentUnchanged = Solid(0x30, 0x30, 0x30);
        t.AccentAdded = Solid(0x60, 0xD0, 0x60);
        t.AccentRemoved = Solid(0xE0, 0x50, 0x50);
        t.AccentModified = Solid(0xD0, 0xA0, 0x50);
        t.AccentGap = Solid(0x70, 0x70, 0x70);
        t.AccentChangeA = Solid(0x3F, 0xA9, 0xF5);
        t.AccentChangeB = t.AccentAdded;
        t.AccentConflict = Solid(0xA3, 0x71, 0xF7);
        t.AccentConflictResolved = t.AccentAdded;   // resolved-conflict bar — Added green

        // Structural analysis indicators — status hues reused; rose stays distinct from error red
        t.StructuralError = t.AccentRemoved;
        t.StructuralChanged = t.AccentModified;
        t.StructuralAdded = t.AccentAdded;
        t.StructuralRemoved = Solid(0xC0, 0x80, 0x90);

        // Diff map overview bar
        t.DiffMapBg = Solid(0x1E, 0x1E, 0x1E);
        t.DiffMapAdded = Solid(0x60, 0xD0, 0x60);
        t.DiffMapRemoved = Solid(0xE0, 0x50, 0x50);
        t.DiffMapModified = Solid(0xD0, 0xA0, 0x50);
        t.DiffMapGap = Solid(0x70, 0x70, 0x70);
        t.DiffMapChangeA = t.AccentChangeA;
        t.DiffMapChangeB = t.AccentChangeB;
        t.DiffMapConflict = t.AccentConflict;
        t.DiffMapConflictResolved = t.DiffMapAdded;   // resolved-conflict marker — Added green
        t.DiffMapDefault = Solid(0x60, 0x60, 0x60);
        t.DiffMapScrollIndicator = Solid(0xCC, 0xCC, 0xCC);

        // Scrollbar & grid lines
        t.BorderLine = Solid(0x30, 0x30, 0x30);
        t.GutterDivider = Solid(0x40, 0x40, 0x40);
        t.ScrollTrack = Solid(0x1E, 0x1E, 0x1E);
        t.ScrollThumb = Solid(0x50, 0x50, 0x50);
        t.ScrollThumbHover = Solid(0x70, 0x70, 0x70);

        // Selection highlight
        t.SelectionHighlight = SolidA(0x40, 0x00, 0x7F, 0xD6);
        t.SelectionTextHighlight = SolidA(0x58, 0x00, 0x7F, 0xD6);

        // Search match highlights
        t.SearchMatchHighlight = SolidA(0x40, 0xFF, 0xCC, 0x00);
        t.SearchCurrentHighlight = SolidA(0x60, 0x00, 0x7F, 0xD6);

        // Drag-over highlight
        t.DragOverOverlay = SolidA(0x18, 0x00, 0x7F, 0xD6);
        t.DragOverBorder = SolidA(0xCC, 0x00, 0x7F, 0xD6);

        // Byte-level highlight for hex inspector
        t.ByteHighlightFill = SolidA(0x40, 0x00, 0x7F, 0xD6);

        // Inline diff change highlight
        t.InlineChangedHighlight = SolidA(0x30, 0xFF, 0xFF, 0xFF);

        // Sync point visuals
        t.PendingSyncHighlight = SolidA(0x40, 0x00, 0x7F, 0xD6);
        t.PendingSyncBorderBrush = Solid(0x00, 0x7F, 0xD6);
        t.SyncPointLineHighlight = SolidA(0x30, 0x00, 0x7F, 0xD6);
        t.SyncPointAccentBar = Solid(0x00, 0x7F, 0xD6);
        t.SyncPointGutterBg = Solid(0x00, 0x5A, 0x9E);
        t.SyncPointMarkerText = Solid(0x00, 0x9F, 0xFF);

        // No-differences overlay
        t.NoDiffOverlayBg = SolidA(0xC0, 0x1E, 0x1E, 0x1E);
        t.NoDiffTitleBrush = Solid(0xB5, 0xFD, 0x22);
        t.NoDiffSubBrush = Solid(0x80, 0xD0, 0x60);
        t.NoDiffAccentBrush = Solid(0x60, 0xD0, 0x60);

        // No scanline brush for system themes
        t.ScanlineBrush = null;

        // Pens
        t.GapStripePen = CreatePen(t.GapStripePaint, 5);
        t.BorderPen = CreatePen(t.BgGutter, 1);
        t.ByteHighlightPen = CreatePen(Solid(0x00, 0x7F, 0xD6), 1);
        t.PendingSyncPen = new System.Windows.Media.Pen(t.PendingSyncBorderBrush, 2.0) { DashStyle = System.Windows.Media.DashStyles.Dash };
        t.SyncPointDashedLine = new System.Windows.Media.Pen(SolidA(0xDD, 0x00, 0x7F, 0xD6), 1.5) { DashStyle = System.Windows.Media.DashStyles.Dash };

        return t;
    }

    public static DiffViewerTheme CreateSystemLight()
    {
        var t = new DiffViewerTheme();

        // Backgrounds — VS Code light-inspired
        t.BgPanel = Solid(0xFF, 0xFF, 0xFF);
        t.BgGutter = Solid(0xF0, 0xF0, 0xF0);
        t.BgUnchanged = Brushes.Transparent;
        t.BgAdded = SolidA(0x20, 0x00, 0x80, 0x00);
        t.BgRemoved = SolidA(0x20, 0xDC, 0x35, 0x45);
        t.BgModified = SolidA(0x20, 0xCC, 0xA0, 0x40);
        t.BgGap = SolidA(0x18, 0xA0, 0xA0, 0xA0);

        // 3-way merge — A blue/cyan, B reuses Added green, conflict purple
        t.BgChangeA = SolidA(0x28, 0x26, 0x4F, 0x78);
        t.BgChangeB = t.BgAdded;
        t.BgConflict = SolidA(0x28, 0x8B, 0x5C, 0xF6);

        // Gap stripe
        t.GapStripePaint = Solid(0xB0, 0xB0, 0xB0);

        // Foregrounds
        t.FgUnchanged = Solid(0x33, 0x33, 0x33);
        t.FgAdded = Solid(0x00, 0x80, 0x00);
        t.FgRemoved = Solid(0xC0, 0x20, 0x20);
        t.FgModified = Solid(0xB0, 0x7D, 0x00);
        t.FgGap = Solid(0x80, 0x80, 0x80);
        t.FgChangeA = Solid(0x0B, 0x5C, 0xA8);
        t.FgChangeB = t.FgAdded;
        t.FgConflict = Solid(0x6B, 0x21, 0xA8);
        t.FgConflictResolved = t.FgAdded;   // resolved-conflict ✓ glyph — Added green
        t.FgTitle = Solid(0x33, 0x33, 0x33);
        t.FgMeta = Solid(0x80, 0x80, 0x80);
        t.FgLineNumber = Solid(0x85, 0x85, 0x85);

        // Syntax highlighting — desaturated VS light palette (recedes from status colours)
        t.SyntaxKeyword = Solid(0x4A, 0x5A, 0xAE);
        t.SyntaxString = Solid(0xA6, 0x5D, 0x50);
        t.SyntaxComment = Solid(0x4F, 0x7D, 0x63);
        t.SyntaxNumber = Solid(0x4A, 0x7F, 0x76);
        t.SyntaxPreprocessor = Solid(0x7C, 0x5C, 0x8F);
        t.SyntaxFunction = Solid(0xB4, 0xA6, 0x62);   // muted gold — VS-style method/call hue
        t.SyntaxType = Solid(0x5E, 0xA8, 0xB8);       // cyan — distinct from the teal numbers

        // Accent bar colors
        t.AccentUnchanged = Solid(0xD0, 0xD0, 0xD0);
        t.AccentAdded = Solid(0x40, 0xC0, 0x40);
        t.AccentRemoved = Solid(0xE0, 0x50, 0x50);
        t.AccentModified = Solid(0xD0, 0xA0, 0x50);
        t.AccentGap = Solid(0xA0, 0xA0, 0xA0);
        t.AccentChangeA = Solid(0x1E, 0x88, 0xE5);
        t.AccentChangeB = t.AccentAdded;
        t.AccentConflict = Solid(0x8E, 0x24, 0xAA);
        t.AccentConflictResolved = t.AccentAdded;   // resolved-conflict bar — Added green

        // Structural analysis indicators — status hues reused; rose stays distinct from error red
        t.StructuralError = t.AccentRemoved;
        t.StructuralChanged = t.AccentModified;
        t.StructuralAdded = t.AccentAdded;
        t.StructuralRemoved = Solid(0xA8, 0x62, 0x74);

        // Diff map overview bar
        t.DiffMapBg = Solid(0xF0, 0xF0, 0xF0);
        t.DiffMapAdded = Solid(0x40, 0xC0, 0x40);
        t.DiffMapRemoved = Solid(0xE0, 0x50, 0x50);
        t.DiffMapModified = Solid(0xD0, 0xA0, 0x50);
        t.DiffMapGap = Solid(0xA0, 0xA0, 0xA0);
        t.DiffMapChangeA = t.AccentChangeA;
        t.DiffMapChangeB = t.AccentChangeB;
        t.DiffMapConflict = t.AccentConflict;
        t.DiffMapConflictResolved = t.DiffMapAdded;   // resolved-conflict marker — Added green
        t.DiffMapDefault = Solid(0xA0, 0xA0, 0xA0);
        t.DiffMapScrollIndicator = Solid(0x60, 0x60, 0x60);

        // Scrollbar & grid lines
        t.BorderLine = Solid(0xE0, 0xE0, 0xE0);
        t.GutterDivider = Solid(0xD0, 0xD0, 0xD0);
        t.ScrollTrack = Solid(0xF0, 0xF0, 0xF0);
        t.ScrollThumb = Solid(0xC0, 0xC0, 0xC0);
        t.ScrollThumbHover = Solid(0xA0, 0xA0, 0xA0);

        // Selection highlight
        t.SelectionHighlight = SolidA(0x40, 0x00, 0x7F, 0xD6);
        t.SelectionTextHighlight = SolidA(0x58, 0x00, 0x7F, 0xD6);

        // Search match highlights
        t.SearchMatchHighlight = SolidA(0x40, 0xFF, 0xCC, 0x00);
        t.SearchCurrentHighlight = SolidA(0x60, 0x00, 0x7F, 0xD6);

        // Drag-over highlight
        t.DragOverOverlay = SolidA(0x18, 0x00, 0x7F, 0xD6);
        t.DragOverBorder = SolidA(0xCC, 0x00, 0x7F, 0xD6);

        // Byte-level highlight for hex inspector
        t.ByteHighlightFill = SolidA(0x40, 0x00, 0x7F, 0xD6);

        // Inline diff change highlight
        t.InlineChangedHighlight = SolidA(0x28, 0xFF, 0xFF, 0xFF);

        // Sync point visuals
        t.PendingSyncHighlight = SolidA(0x40, 0x00, 0x7F, 0xD6);
        t.PendingSyncBorderBrush = Solid(0x00, 0x7F, 0xD6);
        t.SyncPointLineHighlight = SolidA(0x30, 0x00, 0x7F, 0xD6);
        t.SyncPointAccentBar = Solid(0x00, 0x7F, 0xD6);
        t.SyncPointGutterBg = Solid(0x00, 0x5A, 0x9E);
        t.SyncPointMarkerText = Solid(0xFF, 0xFF, 0xFF);

        // No-differences overlay
        t.NoDiffOverlayBg = SolidA(0xC8, 0xFF, 0xFF, 0xFF);
        t.NoDiffTitleBrush = Solid(0x00, 0x80, 0x00);
        t.NoDiffSubBrush = Solid(0x40, 0xA0, 0x40);
        t.NoDiffAccentBrush = Solid(0x40, 0xC0, 0x40);

        // No scanline brush for system themes
        t.ScanlineBrush = null;

        // Pens
        t.GapStripePen = CreatePen(t.GapStripePaint, 5);
        t.BorderPen = CreatePen(t.BgGutter, 1);
        t.ByteHighlightPen = CreatePen(Solid(0x00, 0x7F, 0xD6), 1);
        t.PendingSyncPen = new System.Windows.Media.Pen(t.PendingSyncBorderBrush, 2.0) { DashStyle = System.Windows.Media.DashStyles.Dash };
        t.SyncPointDashedLine = new System.Windows.Media.Pen(SolidA(0xDD, 0x00, 0x7F, 0xD6), 1.5) { DashStyle = System.Windows.Media.DashStyles.Dash };

        return t;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static SolidColorBrush Solid(byte r, byte g, byte b)
        => new(Color.FromRgb(r, g, b));

    private static SolidColorBrush SolidA(byte a, byte r, byte g, byte b)
        => new(Color.FromArgb(a, r, g, b));

    private static System.Windows.Media.Pen CreatePen(SolidColorBrush brush, double thickness)
    {
        var p = new System.Windows.Media.Pen(brush, thickness);
        return p;
    }
}
