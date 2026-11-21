using AwfulDiff.Models;

namespace AwfulDiff.Services;

/// <summary>
/// Resolution options for a 3-way merge conflict.
/// </summary>
public enum MergeResolution
{
    Unresolved,
    TakeA,
    TakeB,
    OrderAB,
    OrderBA,
    /// <summary>
    /// Keep whatever the merged base currently shows at this location: the original base line
    /// for a line conflict (both sides' changes discarded), or nothing for an insertion
    /// conflict (both sides' insertions discarded). Lets a block be resolved per line as a mix
    /// of TakeA / TakeB / KeepBase.
    /// </summary>
    KeepBase
}

/// <summary>
/// Classification of one merge item (a base line or an insertion anchor) after combining
/// the base→A and base→B diffs.
/// </summary>
internal enum MergeClass
{
    None,              // neither side touched it
    ChangeA,           // only A modified/deleted this line, or inserted at this anchor
    ChangeB,           // only B did
    BothSame,          // both sides made the identical change (applied once)
    DeleteAgreed,      // both sides deleted the same base line
    ConflictLine,      // both sides changed the same base line differently
    ConflictInsertion  // both sides inserted different content at the same anchor
}

/// <summary>
/// One auto-mergeable change unit: a single-side (or agreed) modification/deletion of a base
/// line, or an inserted block at one anchor. Units default to accepted (auto-merge) and can
/// be rejected individually — either as a whole or line by line (multi-line insertions).
/// </summary>
public class MergeChange
{
    public int ItemIndex { get; }
    /// <summary>Which side's content lands in the merged file (arbitrary for DeleteAgreed).</summary>
    public bool FromA { get; }
    public bool IsInsertion { get; }
    public bool IsDeletion { get; }

    /// <summary>Number of display rows this unit spans (1 for line units, block size for insertions).</summary>
    public int LineCount { get; }

    private readonly HashSet<int> _rejectedLines = new();

    /// <summary>
    /// True when no line of the unit is rejected. Setting it applies to every line at once
    /// (whole-unit accept/reject); individual lines go through SetLineAccepted.
    /// </summary>
    public bool Accepted
    {
        get => _rejectedLines.Count == 0;
        set
        {
            _rejectedLines.Clear();
            if (!value) for (int i = 0; i < LineCount; i++) _rejectedLines.Add(i);
        }
    }

    internal bool IsLineAccepted(int line) => line >= 0 && line < LineCount && !_rejectedLines.Contains(line);

    internal void SetLineAccepted(int line, bool accepted)
    {
        if (line < 0 || line >= LineCount) return;
        if (accepted) _rejectedLines.Remove(line);
        else _rejectedLines.Add(line);
    }

    /// <summary>Copies the rejected-line set for undo snapshots (whole-unit state ≡ this set).</summary>
    internal int[] CaptureRejected() => _rejectedLines.ToArray();

    internal void RestoreRejected(int[] lines)
    {
        _rejectedLines.Clear();
        foreach (int l in lines) _rejectedLines.Add(l);
    }

    internal MergeChange(int itemIndex, bool fromA, bool isInsertion, bool isDeletion, int lineCount = 1)
    {
        ItemIndex = itemIndex;
        FromA = fromA;
        IsInsertion = isInsertion;
        IsDeletion = isDeletion;
        LineCount = Math.Max(1, lineCount);
    }
}

/// <summary>
/// A maximal contiguous run of conflict items (line conflicts and adjacent insertion
/// conflicts). Resolved per conflict line: each line (slot) carries its own resolution, so a
/// block can be resolved as a whole or line by line.
/// </summary>
public class ConflictBlock
{
    public int FirstItem { get; }
    public int LastItem { get; }

    internal MergeResolution[] Resolutions { get; }   // one per conflict line (slot)
    internal bool[] SlotAsymmetric { get; }           // delete-vs-modify flag per slot

    /// <summary>
    /// True when at least one item is a delete-vs-modify conflict. Ordering has no sensible
    /// semantics there, so the UI hides the OrderAB/OrderBA options for this block.
    /// </summary>
    public bool IsAsymmetric { get; }

    /// <summary>Display artifact: first triple-list row of this block (set by RebuildDisplay).</summary>
    public int StartRow { get; set; }

    /// <summary>
    /// Display artifact: first triple-list row of this block carrying a ResolvedConflict mark,
    /// −1 when the block has no resolved slots with visible winner rows (set by RebuildDisplay).
    /// Target of Shift+F3 resolved-conflict navigation.
    /// </summary>
    public int FirstResolvedRow { get; set; } = -1;

    internal ConflictBlock(int firstItem, int lastItem, MergeResolution[] resolutions, bool[] slotAsymmetric)
    {
        FirstItem = firstItem;
        LastItem = lastItem;
        Resolutions = resolutions;
        SlotAsymmetric = slotAsymmetric;
        IsAsymmetric = false;
        foreach (var a in slotAsymmetric) if (a) { IsAsymmetric = true; break; }
    }

    /// <summary>Current resolution of one conflict line (slot).</summary>
    public MergeResolution ResolutionAt(int slot) => Resolutions[slot];

    /// <summary>True when the given slot is a delete-vs-modify conflict (ordering meaningless there).</summary>
    public bool IsSlotAsymmetric(int slot) => SlotAsymmetric[slot];

    /// <summary>True when every conflict line in the block is resolved.</summary>
    public bool IsFullyResolved
    {
        get { foreach (var r in Resolutions) if (r == MergeResolution.Unresolved) return false; return true; }
    }

    /// <summary>True when no conflict line in the block is resolved yet.</summary>
    public bool IsFullyUnresolved
    {
        get { foreach (var r in Resolutions) if (r != MergeResolution.Unresolved) return false; return true; }
    }

    /// <summary>True when every conflict line carries the given resolution.</summary>
    public bool AllResolvedAs(MergeResolution resolution)
    {
        foreach (var r in Resolutions) if (r != resolution) return false;
        return true;
    }
}

/// <summary>
/// One merge item in document order: a base line (Pos = 0-based base line index) or an
/// insertion anchor (Pos = anchor 0..n, "before base line Pos").
/// </summary>
internal class MergeItem
{
    public int Index;
    public bool IsInsertion;
    public int Pos;

    // Line data (statuses as seen in the base→side interleaved diff)
    public DiffStatus StatusA = DiffStatus.Unchanged; // Unchanged / Modified / Removed
    public DiffStatus StatusB = DiffStatus.Unchanged;
    public string? TextA;   // A's text for this line (null when A removed it)
    public string? TextB;
    public List<(int start, int length)>? RegionsA; // char regions in TextA vs base (Modified only)
    public List<(int start, int length)>? RegionsB;

    // Insertion data at this anchor
    public List<string> InsA = new();
    public List<int> InsANumbers = new();
    public List<string> InsB = new();
    public List<int> InsBNumbers = new();

    public MergeClass Class = MergeClass.None;
    public int ChangeIndex = -1;   // index into MergeState.Changes (units)
    public int ConflictIndex = -1; // index into MergeState.Conflicts (conflict items)
    public int ConflictSlotStart = -1; // first resolution slot of this item within its block
}

/// <summary>
/// Full state of a 3-way merge: the three files, the classified items, the change units and
/// conflict blocks, plus the regenerated triple display lists. All mutations go through
/// MergeEngine methods; after any mutation call RebuildDisplay to refresh the triple lists.
/// </summary>
public class MergeState
{
    public FileModel BaseFile { get; }
    public FileModel FileA { get; }
    public FileModel FileB { get; }

    public List<string> BaseLines { get; }
    internal List<MergeItem> Items { get; } = new();
    public List<MergeChange> Changes { get; } = new();
    public List<ConflictBlock> Conflicts { get; } = new();

    /// <summary>Triple display lists (equal length, row-aligned) — regenerated by RebuildDisplay.</summary>
    public List<DiffLine> TripleA { get; set; } = new();
    public List<DiffLine> TripleBase { get; set; } = new();
    public List<DiffLine> TripleB { get; set; } = new();

    /// <summary>Base content as last loaded/saved (lines joined with "\n").</summary>
    public string SavedSnapshot { get; set; } = "";

    /// <summary>
    /// When true, RebuildDisplay renders the base pane as a preview of the merged output —
    /// each row shows the text that lands in the merged file there (deleted lines ghosted
    /// dimmed, unresolved conflicts stay flagged) — instead of the original base text.
    /// The A/B panes are unaffected. Row alignment is preserved either way.
    /// </summary>
    public bool ShowMergedOutput { get; set; }

    /// <summary>
    /// Free-text override of the merged output typed into the base-pane editor — the user's
    /// "Local Edits" (a hidden third source, "File C"). When non-null it takes precedence over
    /// BuildMergedContent for dirty/save/exit-code purposes. Unchecking the edit box keeps it
    /// pending (like a working copy), and it is RETAINED across conflict reopens: reopening a
    /// conflict only drops the base pane back to the viewer so the reopened conflict is visible,
    /// without discarding the edits. The display shows Local Edits only when every conflict is
    /// resolved (see RebuildDisplay).
    /// </summary>
    public string? ManualMergedOverride { get; set; }

    /// <summary>
    /// The computed merged content that <see cref="ManualMergedOverride"/> was captured against —
    /// snapshotted when base-pane editing begins (all conflicts resolved at that point). It is the
    /// common ancestor for rebasing the edits onto a recomputed merge after resolutions change
    /// (see RebaseManualEdits), so it stays in lockstep with the override's reference frame. Null
    /// until the user has edited; only meaningful while <see cref="ManualMergedOverride"/> is set.
    /// </summary>
    public string? ManualEditBaseline { get; set; }

    internal int[] RowToItem = Array.Empty<int>();
    internal int[] ItemFirstRow = Array.Empty<int>();   // first triple-list row of each item (set by RebuildDisplay)
    internal int[] RowToSlot = Array.Empty<int>();      // conflict slot per row, -1 outside conflicts

    /// <summary>Number of conflict lines still unresolved (across all blocks).</summary>
    public int UnresolvedCount
    {
        get
        {
            int c = 0;
            foreach (var b in Conflicts)
                foreach (var r in b.Resolutions)
                    if (r == MergeResolution.Unresolved) c++;
            return c;
        }
    }
    /// <summary>Number of conflict lines resolved (across all blocks).</summary>
    public int ResolvedCount
    {
        get
        {
            int c = 0;
            foreach (var b in Conflicts)
                foreach (var r in b.Resolutions)
                    if (r != MergeResolution.Unresolved) c++;
            return c;
        }
    }

    public bool IsSaveable => UnresolvedCount == 0;

    /// <summary>True when the (fully resolved) merged content differs from what was last saved.</summary>
    public bool IsDirty => MergeEngine.EffectiveContent(this) != SavedSnapshot;

    internal MergeState(FileModel baseFile, FileModel fileA, FileModel fileB)
    {
        BaseFile = baseFile;
        FileA = fileA;
        FileB = fileB;
        BaseLines = baseFile.Lines;
    }

    /// <summary>Maps a triple-list row to the change unit owning it (null for unchanged/conflict rows).</summary>
    public MergeChange? ChangeAtRow(int row)
    {
        if (row < 0 || row >= RowToItem.Length) return null;
        var item = Items[RowToItem[row]];
        return item.ChangeIndex >= 0 ? Changes[item.ChangeIndex] : null;
    }

    /// <summary>
    /// Maps a triple-list row to the (change unit, local line) owning it — local line is 0-based
    /// within the unit's rows. Null for rows with no change unit (unchanged/conflict rows).
    /// </summary>
    public (MergeChange change, int line)? ChangeLineAtRow(int row)
    {
        if (row < 0 || row >= RowToItem.Length) return null;
        int itemIndex = RowToItem[row];
        var item = Items[itemIndex];
        if (item.ChangeIndex < 0) return null;
        int first = ItemFirstRow.Length > itemIndex ? ItemFirstRow[itemIndex] : -1;
        return (Changes[item.ChangeIndex], first >= 0 ? row - first : 0);
    }

    /// <summary>Maps a triple-list row to the conflict block owning it (null outside conflicts).</summary>
    public ConflictBlock? ConflictAtRow(int row)
    {
        if (row < 0 || row >= RowToItem.Length) return null;
        var item = Items[RowToItem[row]];
        return item.ConflictIndex >= 0 ? Conflicts[item.ConflictIndex] : null;
    }

    /// <summary>
    /// Maps a triple-list row to the (conflict block, resolution slot) owning it — slot is
    /// 0-based within the block's conflict lines. Null for rows outside conflicts.
    /// </summary>
    public (ConflictBlock block, int slot)? ConflictLineAtRow(int row)
    {
        if (row < 0 || row >= RowToItem.Length || row >= RowToSlot.Length) return null;
        int slot = RowToSlot[row];
        if (slot < 0) return null;
        int itemIndex = RowToItem[row];
        var item = Items[itemIndex];
        if (item.ConflictIndex < 0) return null;
        return (Conflicts[item.ConflictIndex], slot);
    }
}

/// <summary>
/// Pure 3-way merge logic. Diffs each change file against the base, projects both pairwise
/// alignments onto base-line coordinates, classifies every base line and insertion anchor,
/// groups conflicts into blocks, and builds the merged content plus the triple display lists.
/// No WPF dependencies — unit-testable from the console test project.
/// </summary>
public static class MergeEngine
{
    /* ── Projection of one pairwise interleaved diff onto base coordinates ─────── */

    private class LineProj
    {
        public DiffStatus Status = DiffStatus.Unchanged; // Unchanged / Modified / Removed
        public string? SideText;                         // null when the side removed the line
        public List<(int start, int length)>? Regions;   // char regions in SideText vs base
    }

    private class InsertProj
    {
        public List<string> Texts = new();
        public List<int> Numbers = new(); // 1-based line numbers in the change file
    }

    /// <summary>
    /// Walks one interleaved pair (baseSide, side) and projects it onto base coordinates:
    /// one LineProj per base line (in order — every base line appears exactly once as a
    /// non-Gap row on the base side) plus one InsertProj per maximal run of Gap-base rows,
    /// anchored at the count of base lines consumed so far (0..n). The lists are equal
    /// length and row-aligned (BuildInterleaved), so both sides index with the same i.
    /// </summary>
    private static (List<LineProj> lines, List<(int anchor, InsertProj ins)> inserts)
        ProjectInterleaved(List<DiffLine> baseSide, List<DiffLine> side)
    {
        var lines = new List<LineProj>(baseSide.Count);
        var inserts = new List<(int, InsertProj)>();

        int baseIdx = 0;   // base lines consumed so far (== next base line index / anchor)
        int i = 0;
        while (i < baseSide.Count)
        {
            if (baseSide[i].Status != DiffStatus.Gap)
            {
                var s = side[i];
                bool sideGap = s.Status == DiffStatus.Gap;
                lines.Add(new LineProj
                {
                    Status = baseSide[i].Status,
                    SideText = sideGap ? null : s.Text,
                    Regions = sideGap ? null : s.ChangedRegions
                });
                baseIdx++;
                i++;
            }
            else
            {
                int start = i;
                while (i < baseSide.Count && baseSide[i].Status == DiffStatus.Gap) i++;
                var ins = new InsertProj();
                for (int j = start; j < i; j++)   // gap run is [start, i)
                {
                    ins.Texts.Add(side[j].Text ?? "");
                    ins.Numbers.Add(side[j].OriginalLineNumber);
                }
                inserts.Add((baseIdx, ins));
            }
        }

        return (lines, inserts);
    }

    /* ── Entry point ────────────────────────────────────────────────────────────── */

    /// <summary>
    /// Runs the two pairwise diffs (base→A, base→B), projects them to base coordinates,
    /// classifies every line/anchor, groups conflicts, and builds the initial triple lists.
    /// </summary>
    public static MergeState CreateMerge(FileModel baseFile, FileModel fileA, FileModel fileB,
        bool ignoreWhitespace = false, DiffAlgorithm algorithm = DiffAlgorithm.Standard)
    {
        var r1 = DiffEngine.Compute(baseFile, fileA, ignoreWhitespace, useTimestamps: false,
            computeInlineDiffs: true, algorithm: algorithm);
        var r2 = DiffEngine.Compute(baseFile, fileB, ignoreWhitespace, useTimestamps: false,
            computeInlineDiffs: true, algorithm: algorithm);

        var (linesA, insA) = ProjectInterleaved(r1.InterleavedA, r1.InterleavedB);
        var (linesB, insB) = ProjectInterleaved(r2.InterleavedA, r2.InterleavedB);

        int n = baseFile.Lines.Count;
        if (linesA.Count != n || linesB.Count != n)
            throw new InvalidOperationException($"Projection mismatch: base has {n} lines but projections produced {linesA.Count}/{linesB.Count}.");

        var state = new MergeState(baseFile, fileA, fileB);
        ClassifyAndBuild(state, linesA, insA, linesB, insB);
        state.SavedSnapshot = string.Join("\n", baseFile.Lines);
        RebuildDisplay(state);
        return state;
    }

    /* ── Classification ─────────────────────────────────────────────────────────── */

    private static void ClassifyAndBuild(MergeState state,
        List<LineProj> linesA, List<(int anchor, InsertProj ins)> insA,
        List<LineProj> linesB, List<(int anchor, InsertProj ins)> insB)
    {
        int n = state.BaseLines.Count;
        int ia = 0, ib = 0;

        MergeItem? MakeInsertion(int anchor)
        {
            var a = ia < insA.Count && insA[ia].anchor == anchor ? insA[ia++].ins : null;
            var b = ib < insB.Count && insB[ib].anchor == anchor ? insB[ib++].ins : null;
            bool aHasAny = a is { Texts.Count: > 0 }, bHasAny = b is { Texts.Count: > 0 };
            if (!aHasAny && !bHasAny) return null;   // nothing inserted at this anchor on either side

            var item = new MergeItem { IsInsertion = true, Pos = anchor };
            if (a != null) { item.InsA = a.Texts; item.InsANumbers = a.Numbers; }
            if (b != null) { item.InsB = b.Texts; item.InsBNumbers = b.Numbers; }

            bool aHas = item.InsA.Count > 0, bHas = item.InsB.Count > 0;
            if (aHas && !bHas) item.Class = MergeClass.ChangeA;
            else if (!aHas && bHas) item.Class = MergeClass.ChangeB;
            else if (item.InsA.SequenceEqual(item.InsB)) item.Class = MergeClass.BothSame;
            else item.Class = MergeClass.ConflictInsertion;
            return item;
        }

        for (int p = 0; p < n; p++)
        {
            var ins = MakeInsertion(p);
            if (ins != null) state.Items.Add(ins);

            var la = linesA[p];
            var lb = linesB[p];
            var line = new MergeItem { Pos = p };
            line.StatusA = la.Status;
            line.StatusB = lb.Status;
            line.TextA = la.SideText;
            line.TextB = lb.SideText;
            line.RegionsA = la.Regions;
            line.RegionsB = lb.Regions;

            bool aChanged = la.Status != DiffStatus.Unchanged;
            bool bChanged = lb.Status != DiffStatus.Unchanged;
            if (!aChanged && !bChanged)
                line.Class = MergeClass.None;
            else if (aChanged && !bChanged)
                line.Class = MergeClass.ChangeA;
            else if (!aChanged && bChanged)
                line.Class = MergeClass.ChangeB;
            else if (la.Status == DiffStatus.Removed && lb.Status == DiffStatus.Removed)
                line.Class = MergeClass.DeleteAgreed;
            else if (la.Status == DiffStatus.Modified && lb.Status == DiffStatus.Modified
                     && la.SideText == lb.SideText)
                line.Class = MergeClass.BothSame;
            else
                line.Class = MergeClass.ConflictLine;

            state.Items.Add(line);
        }

        var trailing = MakeInsertion(n);
        if (trailing != null) state.Items.Add(trailing);

        for (int i = 0; i < state.Items.Count; i++) state.Items[i].Index = i;

        // Create change units for non-conflict classes.
        foreach (var item in state.Items)
        {
            switch (item.Class)
            {
                case MergeClass.ChangeA:
                    item.ChangeIndex = state.Changes.Count;
                    state.Changes.Add(new MergeChange(item.Index, fromA: true, item.IsInsertion,
                        isDeletion: !item.IsInsertion && item.StatusA == DiffStatus.Removed,
                        lineCount: item.IsInsertion ? item.InsA.Count : 1));
                    break;
                case MergeClass.ChangeB:
                    item.ChangeIndex = state.Changes.Count;
                    state.Changes.Add(new MergeChange(item.Index, fromA: false, item.IsInsertion,
                        isDeletion: !item.IsInsertion && item.StatusB == DiffStatus.Removed,
                        lineCount: item.IsInsertion ? item.InsB.Count : 1));
                    break;
                case MergeClass.BothSame:
                    item.ChangeIndex = state.Changes.Count;
                    state.Changes.Add(new MergeChange(item.Index, fromA: true, item.IsInsertion, isDeletion: false,
                        lineCount: item.IsInsertion ? item.InsA.Count : 1));
                    break;
                case MergeClass.DeleteAgreed:
                    item.ChangeIndex = state.Changes.Count;
                    state.Changes.Add(new MergeChange(item.Index, fromA: true, isInsertion: false, isDeletion: true));
                    break;
            }
        }

        // Group maximal contiguous runs of conflict items into blocks. Each conflict line
        // (slot) gets its own resolution: line items contribute one slot, insertion items one
        // per aligned position (max of the two sides' line counts).
        int runStart = -1;
        for (int i = 0; i <= state.Items.Count; i++)
        {
            bool inConflict = i < state.Items.Count
                && state.Items[i].Class is MergeClass.ConflictLine or MergeClass.ConflictInsertion;
            if (inConflict)
            {
                if (runStart < 0) runStart = i;
            }
            else if (runStart >= 0)
            {
                int blockIndex = state.Conflicts.Count;
                var resolutions = new List<MergeResolution>();
                var asymmetric = new List<bool>();
                for (int j = runStart; j < i; j++)
                {
                    var it = state.Items[j];
                    it.ConflictIndex = blockIndex;
                    int slots = it.IsInsertion ? Math.Max(it.InsA.Count, it.InsB.Count) : 1;
                    it.ConflictSlotStart = resolutions.Count;
                    bool slotAsym = it.Class == MergeClass.ConflictLine
                        && (it.StatusA == DiffStatus.Removed) != (it.StatusB == DiffStatus.Removed);
                    for (int s2 = 0; s2 < slots; s2++)
                    {
                        resolutions.Add(MergeResolution.Unresolved);
                        asymmetric.Add(slotAsym); // delete-vs-modify: ordering is meaningless
                    }
                }
                state.Conflicts.Add(new ConflictBlock(runStart, i - 1, resolutions.ToArray(), asymmetric.ToArray()));
                runStart = -1;
            }
        }
    }

    /* ── Mutations ──────────────────────────────────────────────────────────────── */

    /// <summary>Resolves every conflict line of a block at once. Unresolved resets the block.</summary>
    public static void ResolveConflict(MergeState state, ConflictBlock block, MergeResolution resolution)
    {
        Array.Fill(block.Resolutions, resolution);
    }

    /// <summary>
    /// Resolves specific conflict lines — targets are (block, slot) pairs as returned by
    /// MergeState.ConflictLineAtRow. Whole-block ops stay on ResolveConflict.
    /// </summary>
    public static void ResolveConflictLines(MergeState state, IEnumerable<(ConflictBlock block, int slot)> targets, MergeResolution resolution)
    {
        foreach (var (block, slot) in targets)
            if (slot >= 0 && slot < block.Resolutions.Length)
                block.Resolutions[slot] = resolution;
    }

    public static void SetChangeAccepted(MergeState state, MergeChange change, bool accepted)
        => change.Accepted = accepted;

    /// <summary>
    /// Accepts (or rejects) specific lines of change units — targets are (unit, 0-based local
    /// line) pairs, as returned by MergeState.ChangeLineAtRow. Whole-unit ops stay on SetChangeAccepted.
    /// </summary>
    public static void SetChangeLinesAccepted(MergeState state, IEnumerable<(MergeChange change, int line)> targets, bool accepted)
    {
        foreach (var (change, line) in targets)
            change.SetLineAccepted(line, accepted);
    }

    /// <summary>Accepts (or rejects) every change unit. Pass fromA to target one side only.</summary>
    public static void AcceptAll(MergeState state, bool accepted = true, bool? fromA = null)
    {
        foreach (var c in state.Changes)
            if (fromA == null || c.FromA == fromA) c.Accepted = accepted;
    }

    /* ── Undo snapshots ─────────────────────────────────────────────────────────── */

    /// <summary>
    /// Captures the mutable surface of a merge session for undo (see MergeSnapshot). The
    /// Conflicts/Changes lists are fixed after ClassifyAndBuild, so index alignment holds for
    /// the session lifetime. Everything else is derived and re-derived by RebuildDisplay.
    /// </summary>
    public static MergeSnapshot CaptureSnapshot(MergeState state) => new()
    {
        BlockResolutions = state.Conflicts.Select(b => (MergeResolution[])b.Resolutions.Clone()).ToArray(),
        ChangeRejectedLines = state.Changes.Select(c => c.CaptureRejected()).ToArray(),
        ManualMergedOverride = state.ManualMergedOverride,
        ManualEditBaseline = state.ManualEditBaseline,
    };

    /// <summary>Restores a captured snapshot onto the state; call RebuildDisplay afterwards.</summary>
    public static void RestoreSnapshot(MergeState state, MergeSnapshot snap)
    {
        for (int i = 0; i < snap.BlockResolutions.Length && i < state.Conflicts.Count; i++)
            Array.Copy(snap.BlockResolutions[i], state.Conflicts[i].Resolutions, snap.BlockResolutions[i].Length);
        for (int i = 0; i < snap.ChangeRejectedLines.Length && i < state.Changes.Count; i++)
            state.Changes[i].RestoreRejected(snap.ChangeRejectedLines[i]);
        state.ManualMergedOverride = snap.ManualMergedOverride;
        state.ManualEditBaseline = snap.ManualEditBaseline;
    }

    /* ── Merged content ─────────────────────────────────────────────────────────── */

    private enum ConflictEmitKind { Unresolved, Single, Side, KeepBase }

    /// <summary>
    /// Orders one block's conflict slots into an emission sequence. TakeA/TakeB slots emit
    /// their single line in place; a maximal run of consecutive same-direction OrderAB/OrderBA
    /// slots emits all of the first side's lines followed by all of the second side's — "take
    /// one side's changes, then the other's" (a whole block or just a set of selected lines).
    /// Side entries carry one side's row for that slot; callers skip sides without content.
    /// Used by both BuildMergedContent and RebuildDisplay so the preview matches the output.
    /// </summary>
    private static List<(ConflictEmitKind kind, int slot, bool fromA)> BuildConflictSequence(ConflictBlock block)
    {
        var seq = new List<(ConflictEmitKind, int, bool)>();
        int runStart = -1, runEnd = -1;
        bool runBA = false;

        void FlushRun()
        {
            if (runStart < 0) return;
            for (int pass = 0; pass < 2; pass++)
            {
                bool fromA = runBA ? pass == 1 : pass == 0;
                for (int j = runStart; j <= runEnd; j++)
                    seq.Add((ConflictEmitKind.Side, j, fromA));
            }
            runStart = -1;
        }

        for (int j = 0; j < block.Resolutions.Length; j++)
        {
            switch (block.Resolutions[j])
            {
                case MergeResolution.OrderAB:
                case MergeResolution.OrderBA:
                    bool ba = block.Resolutions[j] == MergeResolution.OrderBA;
                    if (runStart < 0) { runStart = j; runBA = ba; }
                    else if (ba != runBA) { FlushRun(); runStart = j; runBA = ba; }
                    runEnd = j;
                    break;
                case MergeResolution.TakeA:
                    FlushRun(); seq.Add((ConflictEmitKind.Single, j, true)); break;
                case MergeResolution.TakeB:
                    FlushRun(); seq.Add((ConflictEmitKind.Single, j, false)); break;
                case MergeResolution.KeepBase:
                    // In-place like TakeA/TakeB (breaks ordering runs); emits the base line, not a side.
                    FlushRun(); seq.Add((ConflictEmitKind.KeepBase, j, false)); break;
                default:
                    FlushRun(); seq.Add((ConflictEmitKind.Unresolved, j, false)); break;
            }
        }
        FlushRun();
        return seq;
    }

    /// <summary>Maps a block slot to the (item, 0-based local line) it belongs to.</summary>
    private static (int itemIndex, int local) SlotToItem(MergeState state, ConflictBlock block, int slot)
    {
        for (int i = block.FirstItem; i <= block.LastItem; i++)
        {
            var it = state.Items[i];
            int slots = it.IsInsertion ? Math.Max(it.InsA.Count, it.InsB.Count) : 1;
            if (slot < it.ConflictSlotStart + slots) return (i, slot - it.ConflictSlotStart);
        }
        throw new InvalidOperationException($"conflict slot {slot} is not inside its block's items");
    }

    /// <summary>True when the side has content at that slot/line of the item.</summary>
    private static bool SideHas(MergeItem item, bool fromA, int local)
        => item.IsInsertion
            ? (fromA ? local < item.InsA.Count : local < item.InsB.Count)
            : fromA
                ? item.StatusA != DiffStatus.Removed && item.TextA != null
                : item.StatusB != DiffStatus.Removed && item.TextB != null;

    private static string? SideText(MergeItem item, bool fromA, int local)
        => !SideHas(item, fromA, local) ? null
         : item.IsInsertion ? (fromA ? item.InsA[local] : item.InsB[local])
                            : (fromA ? item.TextA! : item.TextB!);

    /// <summary>The merged content that would be written now — the manual editor override when set, else the computed merge.</summary>
    public static string? EffectiveContent(MergeState state)
        => state.ManualMergedOverride ?? BuildMergedContent(state);

    /// <summary>
    /// Builds the merged file content (lines joined with "\n"), or null while any conflict is
    /// unresolved. Rejected units keep the base content (restoring deleted lines); resolved
    /// conflicts emit per their resolution — an "A before B" / "B before A" run emits one
    /// side's whole set of lines followed by the other's (see BuildConflictSequence).
    /// </summary>
    public static string? BuildMergedContent(MergeState state)
    {
        if (state.UnresolvedCount > 0) return null;

        var outLines = new List<string>(state.BaseLines.Count);
        int i = 0;
        while (i < state.Items.Count)
        {
            var item = state.Items[i];
            if (item.ConflictIndex >= 0 && state.Conflicts[item.ConflictIndex].FirstItem == i)
            {
                // Emit the whole block at once so ordered runs group across their items.
                var block = state.Conflicts[item.ConflictIndex];
                foreach (var (kind, slot, fromA) in BuildConflictSequence(block))
                {
                    if (kind == ConflictEmitKind.Unresolved) continue;   // unreachable: checked above
                    if (kind == ConflictEmitKind.KeepBase)
                    {
                        var it = state.Items[SlotToItem(state, block, slot).itemIndex];
                        if (!it.IsInsertion) outLines.Add(state.BaseLines[it.Pos]);   // insertions drop
                        continue;
                    }
                    var (itemIndex, local) = SlotToItem(state, block, slot);
                    var text = SideText(state.Items[itemIndex], fromA, local);
                    if (text != null) outLines.Add(text);
                }
                i = block.LastItem + 1;
            }
            else if (!item.IsInsertion)
            {
                string baseText = state.BaseLines[item.Pos];
                switch (item.Class)
                {
                    case MergeClass.None:
                        outLines.Add(baseText);
                        break;

                    case MergeClass.ChangeA or MergeClass.ChangeB or MergeClass.BothSame or MergeClass.DeleteAgreed:
                        var ch = state.Changes[item.ChangeIndex];
                        if (!ch.Accepted)
                            outLines.Add(baseText);              // rejected → keep base (restores deletions)
                        else if (ch.IsDeletion)
                            break;                               // accepted deletion → omit
                        else
                            outLines.Add(item.Class == MergeClass.ChangeB ? item.TextB! : item.TextA!);
                        break;
                }
                i++;
            }
            else
            {
                switch (item.Class)
                {
                    case MergeClass.ChangeA or MergeClass.ChangeB or MergeClass.BothSame:
                        var ch = state.Changes[item.ChangeIndex];
                        var insTexts = item.Class == MergeClass.ChangeB ? item.InsB : item.InsA;
                        for (int j = 0; j < insTexts.Count; j++)
                            if (ch.IsLineAccepted(j)) outLines.Add(insTexts[j]);   // per-line accept/reject
                        break;
                }
                i++;
            }
        }

        return string.Join("\n", outLines);
    }

    /// <summary>
    /// Reconciles pending Local Edits (<see cref="MergeState.ManualMergedOverride"/>) against a
    /// recomputed merge after resolutions or accept flags changed. The edits were captured relative
    /// to <see cref="MergeState.ManualEditBaseline"/>; this performs a nested 3-way merge of
    /// (baseline, edited text, freshly computed merge) and resolves every conflict toward the edited
    /// side, so the user's typed changes survive while new resolutions flow in — instead of the stale
    /// frozen snapshot masking the re-resolution. Where an edit and a re-resolution both changed the
    /// same lines differently, the typed edit is kept: at this point the base pane is a plain editor
    /// with no place to render a fresh conflict. Returns true if the override changed. No-ops while
    /// any conflict is unresolved, when there are no edits to reconcile, or when the computed merge is
    /// unchanged from the baseline.
    /// </summary>
    public static bool RebaseManualEdits(MergeState state)
    {
        if (state.UnresolvedCount > 0) return false;
        if (state.ManualMergedOverride == null || state.ManualEditBaseline == null) return false;

        string? fresh = BuildMergedContent(state);
        if (fresh == null) return false;
        if (fresh == state.ManualEditBaseline) return false;   // computed merge unchanged — nothing to rebase

        var baselineModel = new FileModel { Lines = new List<string>(state.ManualEditBaseline.Split('\n')) };
        var editedModel   = new FileModel { Lines = new List<string>(state.ManualMergedOverride.Split('\n')) };
        var freshModel    = new FileModel { Lines = new List<string>(fresh.Split('\n')) };

        // A side = the user's edits, B side = the re-resolutions. Conflicts resolve toward A so a
        // colliding edit is never clobbered by a re-resolution.
        var sub = CreateMerge(baselineModel, editedModel, freshModel);
        foreach (var block in sub.Conflicts)
            ResolveConflict(sub, block, MergeResolution.TakeA);

        string? result = BuildMergedContent(sub);
        if (result == null || result == state.ManualMergedOverride) return false;

        state.ManualMergedOverride = result;
        state.ManualEditBaseline = fresh;   // rebased onto the new computed merge
        return true;
    }

    /// <summary>
    /// 0-based index in the merged output where the given (1-based) base line lands — or would
    /// land if it is deleted from the output. Walks the same emission order as BuildMergedContent
    /// so ordered conflict runs and per-line accepts are accounted for; -1 if the base line does
    /// not exist. Used to open the merged-output editor at the base pane's scroll position.
    /// </summary>
    public static int MergedIndexOfBaseLine(MergeState state, int baseLine)
    {
        int target = baseLine - 1;
        int emitted = 0;
        int i = 0;
        while (i < state.Items.Count)
        {
            var item = state.Items[i];
            if (item.ConflictIndex >= 0 && state.Conflicts[item.ConflictIndex].FirstItem == i)
            {
                var block = state.Conflicts[item.ConflictIndex];
                foreach (var (kind, slot, fromA) in BuildConflictSequence(block))
                {
                    if (kind == ConflictEmitKind.Unresolved) continue;   // unreachable when fully resolved
                    var (itemIndex, local) = SlotToItem(state, block, slot);
                    var it = state.Items[itemIndex];
                    if (!it.IsInsertion && it.Pos == target) return emitted;   // first occurrence wins
                    if (kind == ConflictEmitKind.KeepBase)
                    {
                        if (!it.IsInsertion) emitted++;   // the kept base line lands
                        continue;
                    }
                    if (SideText(it, fromA, local) != null) emitted++;
                }
                i = block.LastItem + 1;
            }
            else if (!item.IsInsertion)
            {
                if (item.Pos == target) return emitted;
                switch (item.Class)
                {
                    case MergeClass.None:
                        emitted++;
                        break;
                    case MergeClass.ChangeA or MergeClass.ChangeB or MergeClass.BothSame or MergeClass.DeleteAgreed:
                        var ch = state.Changes[item.ChangeIndex];
                        if (!ch.Accepted || !ch.IsDeletion) emitted++;
                        break;
                }
                i++;
            }
            else
            {
                switch (item.Class)
                {
                    case MergeClass.ChangeA or MergeClass.ChangeB or MergeClass.BothSame:
                        var ch = state.Changes[item.ChangeIndex];
                        var insTexts = item.Class == MergeClass.ChangeB ? item.InsB : item.InsA;
                        for (int j = 0; j < insTexts.Count; j++)
                            if (ch.IsLineAccepted(j)) emitted++;
                        break;
                }
                i++;
            }
        }
        return -1;
    }

    /* ── Triple display lists ───────────────────────────────────────────────────── */

    private static DiffLine GapCell(bool isFileA)
        => new() { OriginalLineNumber = 0, Text = "", Status = DiffStatus.Gap, IsFromFileA = isFileA };

    /// <summary>
    /// Regenerates TripleA/TripleBase/TripleB from the current resolutions and accept flags.
    /// All three lists are equal length (row-aligned) so panes can scroll-sync by raw ratio.
    /// </summary>
    public static void RebuildDisplay(MergeState state)
    {
        var tA = new List<DiffLine>();
        var tB = new List<DiffLine>();
        var tBase = new List<DiffLine>();
        var rowToItem = new List<int>();
        var rowToSlot = new List<int>();   // conflict slot per row (-1 outside conflicts)
        var itemFirstRow = new int[state.Items.Count];
        Array.Fill(itemFirstRow, -1);

        void AddRow(DiffLine a, DiffLine b, DiffLine baseCell, int itemIndex, int slot = -1)
        {
            tA.Add(a); tB.Add(b); tBase.Add(baseCell);
            rowToItem.Add(itemIndex);
            rowToSlot.Add(slot);
            if (itemFirstRow[itemIndex] < 0) itemFirstRow[itemIndex] = tA.Count - 1;
        }

        int i = 0;
        while (i < state.Items.Count)
        {
            var item = state.Items[i];
            if (item.ConflictIndex >= 0 && state.Conflicts[item.ConflictIndex].FirstItem == i)
            {
                // Emit the whole block at once so ordered runs group across their items.
                var block = state.Conflicts[item.ConflictIndex];
                block.StartRow = tA.Count;
                EmitConflictBlock(state, block, AddRow);
                // First row carrying a resolved-conflict mark (Shift+F3 navigation target).
                // Reset first: re-resolving/reopening moves the marks on every rebuild.
                block.FirstResolvedRow = -1;
                for (int r = block.StartRow; r < tA.Count && block.FirstResolvedRow < 0; r++)
                    if (tA[r].ResolvedConflict || tBase[r].ResolvedConflict || tB[r].ResolvedConflict)
                        block.FirstResolvedRow = r;
                i = block.LastItem + 1;
            }
            else
            {
                if (!item.IsInsertion)
                    EmitLineRows(state, item, AddRow);
                else
                    EmitInsertionRows(state, item, AddRow);
                i++;
            }
        }

        state.TripleA = tA;
        state.TripleBase = tBase;
        state.TripleB = tB;
        state.RowToItem = rowToItem.ToArray();
        state.ItemFirstRow = itemFirstRow;
        state.RowToSlot = rowToSlot.ToArray();

        // A pending manual edit IS the merged output (merged-output mode): show that exact text
        // in the base pane instead of the computed preview. A/B keep the row-aligned triple, so
        // when the edited line count differs the three lists are no longer equal length — ratio
        // scroll-sync degrades gracefully and every row lookup is bounds-checked against the
        // computed triple (RowToItem.Length), so nothing else needs to change.
        if (state.ManualMergedOverride != null && state.UnresolvedCount == 0 && state.ShowMergedOutput)
        {
            var edited = new List<DiffLine>();
            int n = 1;
            foreach (var text in state.ManualMergedOverride.Split('\n'))
                edited.Add(new() { OriginalLineNumber = n++, Text = text, Status = DiffStatus.Unchanged, IsFromFileA = true });
            state.TripleBase = edited;
        }
    }

    /// <summary>
    /// Renders one conflict block's rows via BuildConflictSequence, so ordered runs show all of
    /// the first side's rows followed by the second side's (matching BuildMergedContent).
    /// </summary>
    private static void EmitConflictBlock(MergeState state, ConflictBlock block, Action<DiffLine, DiffLine, DiffLine, int, int> add)
    {
        bool mv = state.ShowMergedOutput;
        var slotFirstRowUsed = new bool[block.Resolutions.Length];   // base cell goes on each slot's first row

        foreach (var (kind, slot, fromA) in BuildConflictSequence(block))
        {
            var (itemIndex, local) = SlotToItem(state, block, slot);
            var item = state.Items[itemIndex];
            if (item.IsInsertion)
                EmitConflictInsertionRows(item, kind, local, fromA, mv, add);
            else
                EmitConflictLineRows(state, item, kind, fromA, mv, slotFirstRowUsed, add);
        }
    }

    /// <summary>
    /// Rows for one conflict base line (slot). Unresolved → one purple row with both sides;
    /// Single (TakeA/TakeB) → the taken side's row plus the other side dimmed when present;
    /// Side → one row for that pass's side (ordered runs group all first-side rows, then the
    /// second side's). The base line cell rides on the slot's first rendered row.
    /// </summary>
    private static void EmitConflictLineRows(MergeState state, MergeItem item, ConflictEmitKind kind, bool fromA,
        bool mv, bool[] slotFirstRowUsed, Action<DiffLine, DiffLine, DiffLine, int, int> add)
    {
        int slot = item.ConflictSlotStart;   // line items own exactly one slot
        string baseText = state.BaseLines[item.Pos];
        int baseNum = item.Pos + 1;
        bool aHas = SideHas(item, true, 0);
        bool bHas = SideHas(item, false, 0);

        DiffLine BaseCell(DiffStatus status, bool rejected = false)
            => new() { OriginalLineNumber = baseNum, Text = baseText, Status = status, IsFromFileA = true, Rejected = rejected };

        // Merged-output-view base cell: what lands in the merged file at this row (neutral),
        // or the original line ghosted dimmed when it does not.
        DiffLine MergedBaseCell(string? shown, bool ghost, int num)
            => new() { OriginalLineNumber = num, Text = shown ?? baseText, Status = DiffStatus.Unchanged, IsFromFileA = true, Rejected = ghost };

        // Change pane cell: the file's own text (or Gap when that side removed the line).
        DiffLine SideCell(bool isA, string? text, bool has, DiffStatus status, bool rejected)
            => has
                ? new() { OriginalLineNumber = 0, Text = text!, Status = status, IsFromFileA = isA, Rejected = rejected }
                : GapCell(isA);

        if (kind == ConflictEmitKind.Unresolved)
        {
            add(SideCell(true, item.TextA, aHas, DiffStatus.Conflict, false),
                SideCell(false, item.TextB, bHas, DiffStatus.Conflict, false),
                BaseCell(DiffStatus.Conflict), item.Index, slot);
            return;
        }

        if (kind == ConflictEmitKind.KeepBase)
        {
            // Both sides' changes discarded — the original base line is what lands, so it is the
            // winner in BOTH base-pane modes: marked there so Shift+F3 reaches it (the dimmed
            // A/B cells carry no mark).
            bool firstRow = !slotFirstRowUsed[slot];
            slotFirstRowUsed[slot] = true;
            var aCell = SideCell(true, item.TextA, aHas, DiffStatus.ChangeA, rejected: true);
            var bCell = SideCell(false, item.TextB, bHas, DiffStatus.ChangeB, rejected: true);
            DiffLine baseCell = mv
                ? MergedBaseCell(baseText, ghost: false, firstRow ? baseNum : 0)
                : BaseCell(DiffStatus.Unchanged);
            if (firstRow) baseCell.ResolvedConflict = true;
            add(aCell, bCell, baseCell, item.Index, slot);
            return;
        }

        // Rows this slot renders, in order: (isA, includedInMerge).
        var rows = new List<(bool isA, bool included)>();
        if (kind == ConflictEmitKind.Single)
        {
            if (fromA ? aHas : bHas) rows.Add((fromA, true));          // taken side active
            if (!fromA ? aHas : bHas) rows.Add((!fromA, false));       // other side dimmed when present
        }
        else   // Side: this pass's side only (ordered runs include both sides).
        {
            if (!(fromA ? aHas : bHas)) return;                        // no content here → no row
            rows.Add((fromA, true));
        }

        bool anyIncluded = rows.Exists(r => r.included);
        bool first = !slotFirstRowUsed[slot];
        slotFirstRowUsed[slot] = true;

        for (int r = 0; r < rows.Count; r++)
        {
            var (isA, included) = rows[r];
            string? text = isA ? item.TextA : item.TextB;
            var aCell = isA ? SideCell(true, item.TextA, true, DiffStatus.ChangeA, !included) : GapCell(true);
            var bCell = !isA ? SideCell(false, item.TextB, true, DiffStatus.ChangeB, !included) : GapCell(false);

            bool firstRow = first && r == 0;
            DiffLine baseCell = mv
                ? MergedBaseCell(text, !included, firstRow ? baseNum : 0)
                : firstRow
                    ? (anyIncluded ? BaseCell(isA ? DiffStatus.ChangeA : DiffStatus.ChangeB)
                                   : BaseCell(DiffStatus.Conflict, rejected: true))   // resolved to deletion
                    : GapCell(true);

            // Mark the winner rows of a resolved slot (dimmed losers stay unmarked). The base
            // pane joins in only when it carries the winner's change cell — not in merged-output
            // mode (neutral preview) and not for resolutions that land nothing.
            if (included)
            {
                if (isA) aCell.ResolvedConflict = true; else bCell.ResolvedConflict = true;
                if (!mv && firstRow) baseCell.ResolvedConflict = true;
            }

            add(aCell, bCell, baseCell, item.Index, slot);
        }
    }

    /// <summary>
    /// Rows for one aligned position of a conflict insertion. Unresolved → one purple row with
    /// both sides; Single → one row, taken side active, other dimmed when present; Side → one
    /// row for that pass's side when it has a line here (ordered runs group per side).
    /// </summary>
    private static void EmitConflictInsertionRows(MergeItem item, ConflictEmitKind kind, int local, bool fromA,
        bool mv, Action<DiffLine, DiffLine, DiffLine, int, int> add)
    {
        int slot = item.ConflictSlotStart + local;
        var emptyBase = new DiffLine { OriginalLineNumber = 0, Text = "", Status = DiffStatus.Gap, IsFromFileA = true };

        // Merged-output-view base cell for an insertion row: the text that lands there.
        DiffLine MergedCell(string? text) => text != null
            ? new DiffLine { OriginalLineNumber = 0, Text = text, Status = DiffStatus.Unchanged, IsFromFileA = true }
            : emptyBase;

        int kA = item.InsA.Count, kB = item.InsB.Count;
        DiffLine ACell(DiffStatus status, bool rejected) => local < kA
            ? new() { OriginalLineNumber = item.InsANumbers[local], Text = item.InsA[local], Status = status, IsFromFileA = true, Rejected = rejected }
            : GapCell(true);
        DiffLine BCell(DiffStatus status, bool rejected) => local < kB
            ? new() { OriginalLineNumber = item.InsBNumbers[local], Text = item.InsB[local], Status = status, IsFromFileA = false, Rejected = rejected }
            : GapCell(false);

        switch (kind)
        {
            case ConflictEmitKind.Unresolved:
                add(ACell(DiffStatus.Conflict, false), BCell(DiffStatus.Conflict, false),
                    new DiffLine { OriginalLineNumber = 0, Text = "", Status = DiffStatus.Conflict, IsFromFileA = true },
                    item.Index, slot);
                break;

            case ConflictEmitKind.Single:
            {
                bool takeA = fromA;
                string? landed = takeA ? (local < kA ? item.InsA[local] : null) : (local < kB ? item.InsB[local] : null);
                var aCell = ACell(DiffStatus.ChangeA, rejected: !takeA);
                var bCell = BCell(DiffStatus.ChangeB, rejected: takeA);
                // Mark the taken side's cell when it has content (the GapCell fallback is no winner).
                if (takeA && local < kA) aCell.ResolvedConflict = true;
                else if (!takeA && local < kB) bCell.ResolvedConflict = true;
                add(aCell, bCell, mv ? MergedCell(landed) : emptyBase, item.Index, slot);
                break;
            }

            case ConflictEmitKind.KeepBase:
            {
                // Both sides' insertions discarded — nothing lands at this anchor, but the decision
                // still needs a mark + Shift+F3 target: the (empty) base cell carries it, so the row
                // shows ✓/green bar between the two dimmed insertions.
                var baseCell = new DiffLine { OriginalLineNumber = 0, Text = "", Status = DiffStatus.Gap, IsFromFileA = true };
                baseCell.ResolvedConflict = true;
                add(ACell(DiffStatus.ChangeA, rejected: true), BCell(DiffStatus.ChangeB, rejected: true),
                    baseCell, item.Index, slot);
                break;
            }

            default:   // Side — one row for this pass's side when it has a line here.
            {
                if (fromA)
                {
                    if (local >= kA) return;
                    var aCell = ACell(DiffStatus.ChangeA, false);
                    aCell.ResolvedConflict = true;   // ordered runs include both sides — all winners
                    add(aCell, GapCell(false), mv ? MergedCell(item.InsA[local]) : emptyBase, item.Index, slot);
                }
                else
                {
                    if (local >= kB) return;
                    var bCell = BCell(DiffStatus.ChangeB, false);
                    bCell.ResolvedConflict = true;
                    add(GapCell(true), bCell, mv ? MergedCell(item.InsB[local]) : emptyBase, item.Index, slot);
                }
                break;
            }
        }
    }

    private static void EmitLineRows(MergeState state, MergeItem item, Action<DiffLine, DiffLine, DiffLine, int, int> add)
    {
        bool mv = state.ShowMergedOutput;   // base pane previews the merged output
        string baseText = state.BaseLines[item.Pos];
        int baseNum = item.Pos + 1;
        bool aHas = item.StatusA != DiffStatus.Removed && item.TextA != null;
        bool bHas = item.StatusB != DiffStatus.Removed && item.TextB != null;

        // Base cell always shows the original base line.
        DiffLine BaseCell(DiffStatus status, bool rejected = false)
            => new() { OriginalLineNumber = baseNum, Text = baseText, Status = status, IsFromFileA = true, Rejected = rejected };

        // Merged-output-view base cell: what lands in the merged file at this row (neutral),
        // or the original line ghosted dimmed when it is deleted from the output.
        DiffLine MergedBaseCell(string? shown, bool ghost)
            => new() { OriginalLineNumber = baseNum, Text = shown ?? baseText, Status = DiffStatus.Unchanged, IsFromFileA = true, Rejected = ghost };

        // Change pane cell: the file's own text (or Gap when that side removed the line).
        DiffLine SideCell(bool fromA, string? text, bool has, DiffStatus status, bool rejected)
            => has
                ? new() { OriginalLineNumber = 0, Text = text!, Status = status, IsFromFileA = fromA, Rejected = rejected }
                : GapCell(fromA);

        switch (item.Class)
        {
            case MergeClass.None:
                add(new() { OriginalLineNumber = 0, Text = baseText, Status = DiffStatus.Unchanged, IsFromFileA = true },
                    new() { OriginalLineNumber = 0, Text = baseText, Status = DiffStatus.Unchanged, IsFromFileA = false },
                    BaseCell(DiffStatus.Unchanged), item.Index, -1);
                break;

            case MergeClass.ChangeA or MergeClass.ChangeB or MergeClass.BothSame or MergeClass.DeleteAgreed:
            {
                var ch = state.Changes[item.ChangeIndex];
                bool accepted = ch.Accepted;
                bool isA = item.Class != MergeClass.ChangeB;
                DiffStatus changeStatus = isA ? DiffStatus.ChangeA : DiffStatus.ChangeB;

                // What lands in the merged output at this row (null → the line is deleted).
                bool deletesLine = item.Class == MergeClass.DeleteAgreed
                    || (item.Class == MergeClass.ChangeA && !aHas)
                    || (item.Class == MergeClass.ChangeB && !bHas);
                string? landedText = deletesLine ? null : (item.Class == MergeClass.ChangeB ? item.TextB! : item.TextA!);
                DiffLine MergedCell() => MergedBaseCell(accepted ? landedText : null, accepted && deletesLine);

                if (item.Class == MergeClass.BothSame)
                {
                    // Both panes show their own (identical) text, each in its own color.
                    add(SideCell(true, item.TextA, aHas, DiffStatus.ChangeA, !accepted),
                        SideCell(false, item.TextB, bHas, DiffStatus.ChangeB, !accepted),
                        mv ? MergedCell() : BaseCell(accepted ? DiffStatus.ChangeA : DiffStatus.Unchanged), item.Index, -1);
                }
                else if (item.Class == MergeClass.DeleteAgreed)
                {
                    add(GapCell(true), GapCell(false),
                        mv ? MergedCell() : BaseCell(accepted ? DiffStatus.ChangeA : DiffStatus.Unchanged), item.Index, -1);
                }
                else // single-side change: changed side shows its text (dimmed when rejected),
                     // unchanged side keeps the base line as Unchanged.
                {
                    bool aChanged = isA;
                    var aCell = aChanged
                        ? SideCell(true, item.TextA, aHas, DiffStatus.ChangeA, !accepted)
                        : new() { OriginalLineNumber = 0, Text = baseText, Status = DiffStatus.Unchanged, IsFromFileA = true };
                    var bCell = !aChanged
                        ? SideCell(false, item.TextB, bHas, DiffStatus.ChangeB, !accepted)
                        : new() { OriginalLineNumber = 0, Text = baseText, Status = DiffStatus.Unchanged, IsFromFileA = false };
                    add(aCell, bCell, mv ? MergedCell() : BaseCell(accepted ? changeStatus : DiffStatus.Unchanged), item.Index, -1);
                }
                break;
            }

            // MergeClass.ConflictLine rows are emitted by EmitConflictBlock (whole-block, so
            // ordered runs group across their items).
        }
    }

    private static void EmitInsertionRows(MergeState state, MergeItem item, Action<DiffLine, DiffLine, DiffLine, int, int> add)
    {
        bool mv = state.ShowMergedOutput;   // base pane previews the merged output
        var emptyBase = new DiffLine { OriginalLineNumber = 0, Text = "", Status = DiffStatus.Gap, IsFromFileA = true };

        // Merged-output-view base cell for an insertion row: the text that lands there.
        DiffLine MergedCell(string? text) => text != null
            ? new DiffLine { OriginalLineNumber = 0, Text = text, Status = DiffStatus.Unchanged, IsFromFileA = true }
            : emptyBase;

        // change == null → no per-line state (conflict rows): every row renders active.
        void EmitCellPair(List<string> textsA, List<int> numsA, bool aActive, DiffStatus aStatus,
                          List<string> textsB, List<int> numsB, bool bActive, DiffStatus bStatus,
                          MergeChange? change, bool baseShowsMergedText)
        {
            int kA = aActive ? textsA.Count : 0;
            int kB = bActive ? textsB.Count : 0;
            for (int j = 0; j < Math.Max(kA, kB); j++)
            {
                bool lineAccepted = change == null || change.IsLineAccepted(j);
                var aCell = j < kA
                    ? new DiffLine { OriginalLineNumber = numsA[j], Text = textsA[j], Status = aStatus, IsFromFileA = true, Rejected = aActive && !lineAccepted }
                    : GapCell(true);
                var bCell = j < kB
                    ? new DiffLine { OriginalLineNumber = numsB[j], Text = textsB[j], Status = bStatus, IsFromFileA = false, Rejected = bActive && !lineAccepted }
                    : GapCell(false);

                string? landed = null;
                if (baseShowsMergedText && lineAccepted)
                    landed = (aActive && j < kA) ? textsA[j] : (bActive && j < kB) ? textsB[j] : null;
                add(aCell, bCell, baseShowsMergedText ? MergedCell(landed) : emptyBase, item.Index, -1);
            }
        }

        switch (item.Class)
        {
            case MergeClass.ChangeA or MergeClass.ChangeB or MergeClass.BothSame:
            {
                var ch = state.Changes[item.ChangeIndex];

                if (item.Class == MergeClass.BothSame)
                    EmitCellPair(item.InsA, item.InsANumbers, true, DiffStatus.ChangeA,
                                 item.InsB, item.InsBNumbers, true, DiffStatus.ChangeB, ch, mv);
                else if (item.Class == MergeClass.ChangeA)
                    EmitCellPair(item.InsA, item.InsANumbers, true, DiffStatus.ChangeA,
                                 new(), new(), false, DiffStatus.Unchanged, ch, mv);
                else
                    EmitCellPair(new(), new(), false, DiffStatus.Unchanged,
                                 item.InsB, item.InsBNumbers, true, DiffStatus.ChangeB, ch, mv);
                break;
            }

            // MergeClass.ConflictInsertion rows are emitted by EmitConflictBlock.
        }
    }
}
