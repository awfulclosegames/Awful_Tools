using AwfulDiff.Services;

namespace AwfulDiff.Models;

/// <summary>
/// Represents the diff status of a single line.
/// </summary>
public enum DiffStatus
{
    Unchanged,
    Added,
    Removed,
    Modified,
    Gap,

    // 3-way merge statuses (see MergeEngine)
    /// <summary>Line changed by File A relative to the base.</summary>
    ChangeA,
    /// <summary>Line changed by File B relative to the base.</summary>
    ChangeB,
    /// <summary>Both change files modified the same region of the base — needs resolution.</summary>
    Conflict
}

/// <summary>
/// A line from either file with its diff classification.
/// </summary>
public class DiffLine
{
    public int OriginalLineNumber { get; set; }
    public string? Text { get; set; }
    public DiffStatus Status { get; set; }
    public bool IsFromFileA { get; set; }

    /// <summary>
    /// For Modified lines, the paired text from the other file used to compute character-level diffs.
    /// </summary>
    public string? OriginalText { get; set; }

    /// <summary>
    /// For Modified lines, list of (startCharacterIndex, length) tuples indicating which
    /// character ranges in Text are different from OriginalText. Null or empty means the whole
    /// line is considered changed (or char-level diff was not computed / failed).
    /// </summary>
    public List<(int start, int length)>? ChangedRegions { get; set; }

    /// <summary>
    /// 3-way merge: change was rejected by the user. Row stays in place but renders dimmed
    /// and its contribution is excluded from the merged content.
    /// </summary>
    public bool Rejected { get; set; }

    /// <summary>
    /// 3-way merge: row is the winner of a resolved conflict slot — gets a green accent bar
    /// and a ✓ gutter glyph so resolved conflicts stay findable after resolution. Stamped by
    /// MergeEngine.RebuildDisplay, re-derived from resolutions on every rebuild (reopening a
    /// conflict clears it). Never set together with Rejected.
    /// </summary>
    public bool ResolvedConflict { get; set; }

    /// <summary>
    /// For Added/Removed lines (two-file diffs): how many real (non-Gap) lines of the OTHER
    /// file precede this line's position — i.e., where the block sits on the other side
    /// (0 = top of file). Null for all other statuses. Stamped by DiffEngine.AnnotateAnchors
    /// and used by FastDiffViewer to draw cross-pane anchor markers when gaps are hidden.
    /// Stable under SwapFiles' flip+swap: flipping preserves which rows are real, so the
    /// count of other-side lines before this row is unchanged — no re-annotation needed.
    /// </summary>
    public int? OtherSideAnchor { get; set; }

    /// <summary>
    /// Advisory structural-analysis message for this line; null = none. Stamped by the VM from
    /// cached findings, cleared when analysis is disabled or hex view is active. Advisory only —
    /// never affects diff alignment/status. FastDiffViewer draws a gutter dot + hover tooltip.
    /// </summary>
    public string? StructuralNote { get; set; }

    /// <summary>
    /// Category of the finding on this line (first kind wins) — selects the glyph brush.
    /// </summary>
    public StructuralKind? StructuralFlag { get; set; }

    // ── Hex view (byte-aligned rows) ──────────────────────────────────────────

    /// <summary>
    /// Hex rows only: file offset of the first occupied byte on this side. Null = no bytes
    /// (text-mode row or a Gap filler row).
    /// </summary>
    public int? ByteStart { get; set; }

    /// <summary>
    /// Hex rows only: bit c set ⇔ slot c holds a byte on this side (0 for text rows).
    /// File byte of slot c = ByteStart + popcount(HexSlotMask &amp; ((1&lt;&lt;c)-1)) — occupied
    /// slots always form a contiguous run in file space, so start+mask is exact even with
    /// mid-row holes (e.g. the other side inserted bytes between this side's bytes).
    /// </summary>
    public int HexSlotMask { get; set; }

    // Per-byte colour class (hex rows only). Bit c set ⇔ slot c is that class on this side.
    // Never set for matched or empty slots, so all three are 0 for Unchanged rows and text rows.
    // Stamped by both the aligned and positional rowizers; stable under SwapFiles' flip+swap
    // (the per-slot classification is symmetric per side). The hex per-byte display shades exactly
    // these slots — red/green/yellow — instead of the whole Modified row.

    /// <summary>Bit c ⇔ slot c is an ADDED byte (B-side KIns; always 0 on A rows).</summary>
    public int HexAddedSlots { get; set; }

    /// <summary>Bit c ⇔ slot c is a REMOVED byte (A-side KDel; always 0 on B rows).</summary>
    public int HexRemovedSlots { get; set; }

    /// <summary>Bit c ⇔ slot c is a substituted (MODIFIED) byte — KX, set on both sides.</summary>
    public int HexModifiedSlots { get; set; }

    /// <summary>
    /// Hex rows only: bit c set ⇔ slot c is empty on THIS side but occupied on the other — a
    /// genuine add/remove hole (the partner has a byte here that this side lacks). 0 for dense
    /// fixed-address rows (no mid-row holes) and text rows. The renderer draws diagonal stripes on
    /// exactly these slots (Show Gaps ON); whole-row Gap rows are striped separately by status.
    /// </summary>
    public int HexGapSlots { get; set; }
}
