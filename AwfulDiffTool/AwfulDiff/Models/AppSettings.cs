using System.Text.Json;
using System.Text.Json.Serialization;

namespace AwfulDiff.Models;

/// <summary>
/// Persisted application settings — saved to %APPDATA%\AwfulDiff\settings.json on exit.
/// </summary>
public class AppSettings
{
    public double WindowWidth { get; set; } = 1400;
    public double WindowHeight { get; set; } = 850;
    public double WindowLeft { get; set; } = double.NaN;   // NaN = center on startup
    public double WindowTop { get; set; } = double.NaN;
    public string WindowState { get; set; } = "Normal";     // "Normal" or "Maximized"

    /// <summary>
    /// 3-way merge setup dialog size (0 = first run — the dialog sizes to its content and
    /// remembers the result from then on).
    /// </summary>
    public double MergeDialogWidth { get; set; }
    public double MergeDialogHeight { get; set; }

    public bool IsSideBySide { get; set; } = true;

    /// <summary>
    /// Layout: "SideBySide", "TopBottom", or "Unified". Stored as a string (like DiffAlgorithm)
    /// so settings.json stays human-readable. Null in legacy files that predate this setting —
    /// the caller then falls back to IsSideBySide. Written out only when non-null, so saving
    /// never emits a null entry for other properties either.
    /// </summary>
    public string? LayoutMode { get; set; }
    // Legacy shared toggle values (pre per-mode settings). Still written on save for older
    // builds, and read as the fallback seed when a per-mode value below is missing from an
    // old settings.json (deserializes to null).
    public bool SyncScroll { get; set; } = true;
    public bool ShowGaps { get; set; } = true;
    public bool IgnoreWhitespace { get; set; } = false;
    public bool TimestampMode { get; set; } = false;
    public bool HexView { get; set; } = false;
    public bool ShowInlineDiffs { get; set; } = true;

    /// <summary>
    /// Per-view-mode toggle values (text vs hex) for the four mode-independent checkboxes.
    /// Nullable so a missing entry in a legacy file reads as null and the caller falls back
    /// to the legacy shared value above; always written non-null on save. IgnoreWhitespace is
    /// deliberately not per-mode — it only applies to text diffs.
    /// </summary>
    public bool? TextSyncScroll { get; set; }
    public bool? TextShowGaps { get; set; }
    public bool? TextShowInlineDiffs { get; set; }
    public bool? TextTimestampMode { get; set; }
    public bool? HexSyncScroll { get; set; }
    public bool? HexShowGaps { get; set; }
    public bool? HexShowInlineDiffs { get; set; }
    public bool? HexTimestampMode { get; set; }

    /// <summary>
    /// Hex view only: auto-resync + per-byte display. When on, a hex segment that is too large to
    /// byte-align in one pass is adaptively subdivided instead of falling back to positional rows
    /// (which cascades every following row into one giant Modified block), and Modified rows shade
    /// only the bytes that actually differ rather than the whole row. Default off.
    /// </summary>
    public bool HexPerByte { get; set; } = false;

    /// <summary>
    /// Visual Studio-style syntax coloring (keywords, strings, comments, numbers) overlaid on
    /// the diff text. Colours are desaturated so the add/remove/modified/conflict block colours
    /// stay visually dominant. Default on.
    /// </summary>
    public bool UseSyntaxColoring { get; set; } = true;

    /// <summary>
    /// Structural analysis: gutter triangles + hover tooltips for structural changes (functions,
    /// loops/ifs, brace nesting in C-family; keys/value types in JSON; elements/attributes in XML).
    /// Advisory only. Default on.
    /// </summary>
    public bool UseStructuralAnalysis { get; set; } = true;

    /// <summary>
    /// Hex inspector: last selected data type ("int8" … "double"). Stored as a string (like
    /// DiffAlgorithm) so settings.json stays human-readable; unknown values fall back to int32.
    /// </summary>
    public string HexInspectorType { get; set; } = "int32";

    /// <summary>
    /// Custom hex inspector struct patterns (name, endianness, ordered fields). Persisted by the
    /// struct manager dialog on save/delete. The name stored in HexInspectorType may refer to one
    /// of these; startup falls back to int32 when no pattern matches.
    /// </summary>
    public List<HexStructPattern> HexStructPatterns { get; set; } = new();

    /// <summary>
    /// Hex inspector panel height in pixels (default 180). Set by dragging the separator or the
    /// panes' bottom edge; persisted like window geometry. Loaded clamped to [60, 1200].
    /// </summary>
    public double HexInspectorHeight { get; set; } = 180;

    /// <summary>
    /// 3-way merge window: when true the base pane previews the merged output instead of the
    /// original base text. Default on; remembered between runs.
    /// </summary>
    public bool MergeShowMergedOutput { get; set; } = true;

    /// <summary>
    /// Directory-diff window: show files that are identical. Default off (differences only).
    /// </summary>
    public bool DirDiffShowUnchangedFiles { get; set; } = false;

    /// <summary>
    /// Directory-diff window: show folders without differences. Default off (differences only).
    /// </summary>
    public bool DirDiffShowUnchangedDirs { get; set; } = false;

    /// <summary>
    /// Directory-diff window geometry, remembered between launches. NaN position = center on open.
    /// </summary>
    public double DirDiffWindowWidth { get; set; } = 1000;
    public double DirDiffWindowHeight { get; set; } = 700;
    public double DirDiffWindowLeft { get; set; } = double.NaN;
    public double DirDiffWindowTop { get; set; } = double.NaN;

    /// <summary>Directory-diff window maximized state ("Normal" or "Maximized"), like MainWindow's WindowState.</summary>
    public string DirDiffWindowState { get; set; } = "Normal";

    /// <summary>
    /// 3-way merge window geometry, remembered between launches. NaN position = center on open.
    /// </summary>
    public double MergeWindowWidth { get; set; } = 1400;
    public double MergeWindowHeight { get; set; } = 850;
    public double MergeWindowLeft { get; set; } = double.NaN;
    public double MergeWindowTop { get; set; } = double.NaN;

    /// <summary>3-way merge window maximized state ("Normal" or "Maximized"), like MainWindow's WindowState.</summary>
    public string MergeWindowState { get; set; } = "Normal";

    /// <summary>
    /// Recently compared directory pairs (newest first, capped at 8). Prefills the directory-diff
    /// window when it opens with no other source for the paths.
    /// </summary>
    public List<DirDiffRecentEntry> DirDiffRecentDirs { get; set; } = new();

    /// <summary>
    /// Theme name: "Awful" (default sci-fi), "SystemLight", or "SystemDark".
    /// </summary>
    public string ThemeName { get; set; } = "Awful";

    /// <summary>
    /// Diff engine: "Patience" (default) or "Standard" (Myers, faster). Stored as a string
    /// (like ThemeName) so settings.json stays human-readable and unknown values fall back
    /// to Patience.
    /// </summary>
    public string DiffAlgorithm { get; set; } = "Patience";

     /// <summary>
    /// Font scale multiplier (0.5 to 2.0, default 1.0).
    /// </summary>
    public double FontScale { get; set; } = 1.0;

    /// <summary>
    /// Per-theme custom diff colours, keyed by theme name ("Awful", "SystemLight",
    /// "SystemDark"). A missing or all-disabled entry means that theme's built-in colours
    /// are used. Stored as a dictionary so settings.json stays human-readable (keys = themes).
    /// </summary>
    public Dictionary<string, ThemeColorOverrides> CustomColors { get; set; } = new();

    /// <summary>
    /// Create default settings.
    /// </summary>
    public static AppSettings Default => new();

    /// <summary>
    /// Serialize to JSON string.
    /// </summary>
    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <summary>
    /// Deserialize from JSON string, falling back to defaults on corruption.
    /// </summary>
    public static AppSettings FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? Default;
        }
        catch
        {
            return Default;
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Only LayoutMode can be null (legacy files); omitting it keeps old-format saves clean.
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new DoubleConverter() }
    };

    /// <summary>
    /// JSON has no NaN/Infinity literals and System.Text.Json throws when writing them. The
    /// window-geometry fields use double.NaN to mean "center on startup", so serialize any
    /// non-finite value as null and read null back as NaN — the file stays valid JSON, round-trips
    /// cleanly, and without this every Save() threw and settings.json was never written.
    /// </summary>
    private sealed class DoubleConverter : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.TokenType == JsonTokenType.Null ? double.NaN : reader.GetDouble();

        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        {
            if (!double.IsNaN(value) && !double.IsInfinity(value)) writer.WriteNumberValue(value);
            else writer.WriteNullValue();
        }
    }
}

/// <summary>
/// User-chosen overrides for one theme's diff colours. Each role is opt-in: when Enabled is
/// false (or the hex is invalid) the theme's built-in colour is used for that role.
/// </summary>
public class ThemeColorOverrides
{
    public ColorOverride Added { get; set; } = new();
    public ColorOverride Removed { get; set; } = new();
    public ColorOverride Modified { get; set; } = new();

    /// <summary>Merge candidates — colours both the A and B change families.</summary>
    public ColorOverride MergeCandidates { get; set; } = new();
    public ColorOverride Conflicts { get; set; } = new();

    /// <summary>Standard (unchanged) text colour.</summary>
    public ColorOverride Text { get; set; } = new();

    public bool HasAnyEnabled() => Added.Enabled || Removed.Enabled || Modified.Enabled
        || MergeCandidates.Enabled || Conflicts.Enabled || Text.Enabled;
}

/// <summary>
/// One opt-in colour: enabled flag plus a strict hex string ("#RRGGBB" or "#AARRGGBB").
/// </summary>
public class ColorOverride
{
    public bool Enabled { get; set; } = false;
    public string Hex { get; set; } = "";
}

/// <summary>One recently compared directory pair (directory-diff window).</summary>
public class DirDiffRecentEntry
{
    public string Left { get; set; } = "";
    public string Right { get; set; } = "";
}
