using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using AwfulDiff.Models;

namespace AwfulDiff.Services;

/// <summary>
/// Byte-level diff engine for hex view.
///
/// TryCompute produces a maximal, non-crossing match pairing between the raw bytes of two
/// files (bidirectional middle-snake Myers with recursive splitting — ported from
/// DiffEngine.FindMatches/MyersCore and specialized to byte equality). A long work counter
/// bounds time: when the running total exceeds the budget the call returns false and the
/// caller falls back to positional 16-byte-row comparison for that segment. No per-D V-slice
/// storage — memory stays O(n+m); the budget bounds time only.
///
/// BuildAlignedRows turns a match pairing into display rows of 16 slots, where slot c pairs
/// an A-byte-or-empty with a B-byte-or-empty (see DiffLine.ByteStart/HexSlotMask). Unchanged
/// regions stay Unchanged no matter where edits fall — the property positional comparison
/// lacks. ComputeWithSyncPoints splits the byte arrays at user-placed byte anchors, mirroring
/// DiffEngine.ComputeWithSyncPoints (inclusive segments + trailing segment to EOF).
///
/// Argument order is canonicalized (shorter first, then lexicographic) so TryCompute(a,b) and
/// TryCompute(b,a) run the identical computation — this is what makes SwapFiles' flip path
/// equivalent to a recompute.
/// </summary>
public static class ByteDiff
{
    /// <summary>Work-cell budget per segment (~10⁸ cells ≈ well under a second).</summary>
    public const long MaxWorkCells = 100_000_000L;

    /// <summary>
    /// Auto-resync leaf cap: max number of byte-Myers frames the adaptive subdivision may run
    /// before giving up and using positional rows for the remainder. Real files with a handful
    /// of edits produce only a few leaves (unchanged halves align in one cheap pass); the cap
    /// bounds adversarial input where every sub-frame is genuinely hard.
    /// </summary>
    private const int MaxResyncLeaves = 1024;

    // Rowizer slot kinds: what occupies the slot on this side.
    private const byte KEmpty = 0, KMatch = 1, KX = 2, KDel = 3, KIns = 4;

    /* ═══════════ Public API ═══════════ */

    /// <summary>
    /// Compute the maximal match pairing between two whole byte arrays.
    /// matchB[ia] = ib (or −1) pairs a[ia] with b[ib]; matchA is the inverse.
    /// Returns false when the running work total exceeds cellBudget — callers fall back to
    /// positional comparison; the arrays may then hold partial results and must not be used.
    /// </summary>
    public static bool TryCompute(byte[] a, byte[] b, long cellBudget, out int[] matchB, out int[] matchA)
    {
        matchB = new int[a.Length];
        matchA = new int[b.Length];
        return TryComputeRange(a, 0, a.Length, b, 0, b.Length, cellBudget, matchB, matchA);
    }

    /// <summary>
    /// True when the anchor set is strictly co-monotone: sorted by ByteA ascending, every
    /// later anchor also has a strictly larger ByteB. Rejects duplicates and crossing pairs.
    /// Order-independent — sorts internally.
    /// </summary>
    public static bool IsAnchorSetOrdered(IReadOnlyList<(int ByteA, int ByteB)> anchors)
    {
        var sorted = anchors.OrderBy(p => p.ByteA).ThenBy(p => p.ByteB).ToList();
        int prevA = -1, prevB = -1;
        foreach (var (a, b) in sorted)
        {
            if (a <= prevA || b <= prevB) return false;
            prevA = a; prevB = b;
        }
        return true;
    }

    /// <summary>
    /// Turn a match pairing over the range [aOff, aOff+lenA) × [bOff, bOff+lenB) into aligned
    /// 16-slot display rows. matchB/matchA are indexed by absolute file offset (−1 = unmatched).
    /// Both returned lists have equal length; rows carry local OriginalLineNumber (row index + 1,
    /// gaps = 0) which the caller offsets to global positions when stitching segments.
    /// </summary>
    public static (List<DiffLine> RowsA, List<DiffLine> RowsB) BuildAlignedRows(
        byte[] a, int aOff, int lenA, byte[] b, int bOff, int lenB,
        int[] matchB, int[] matchA, bool computeInlineDiffs)
    {
        var rowsA = new List<DiffLine>();
        var rowsB = new List<DiffLine>();

        int endA = aOff + lenA, endB = bOff + lenB;
        int ia = aOff, ib = bOff;

        // Phase 1: walk the pairing into flat cell arrays. Each cell consumes ≥1 total byte, so
        // lenA+lenB cells is a safe upper bound on the cell count. aByteOff/bByteOff record the
        // file offset of each side's byte in the cell (−1 when that side is empty) — this is what
        // lets ByteStart stay exact after the shuffle re-slots bytes below.
        int cap = lenA + lenB;
        var kA = new byte[cap];
        var kB = new byte[cap];
        var aByteOff = new int[cap];
        var bByteOff = new int[cap];
        Array.Fill(aByteOff, -1);
        Array.Fill(bByteOff, -1);

        int cells = 0;

        // Deterministic walk. At any non-matched position the pairing is forced: if a[ia] is
        // unpaired, b[ib] cannot pair with it (its own match at i' > ia would cross), and vice
        // versa. Only when BOTH are unpaired do we emit an X slot pairing them.
        while (ia < endA || ib < endB)
        {
            if (ia < endA && ib < endB && matchB[ia] == ib)
            {
                kA[cells] = KMatch; kB[cells] = KMatch;
                aByteOff[cells] = ia; bByteOff[cells] = ib;
                ia++; ib++;
            }
            else if (ia >= endA)
            {
                kB[cells] = KIns; bByteOff[cells] = ib;
                ib++;
            }
            else if (ib >= endB)
            {
                kA[cells] = KDel; aByteOff[cells] = ia;
                ia++;
            }
            else if (matchB[ia] == -1 && matchA[ib] == -1)
            {
                // Neither byte is paired with anything: pair them as an X slot. Demote to Match
                // when the values happen to be equal (Myers tie-breaking can leave equal bytes
                // unpaired) — mirrors text mode's demotion in BuildInterleaved.
                byte k = a[ia] == b[ib] ? KMatch : KX;
                kA[cells] = k; kB[cells] = k;
                aByteOff[cells] = ia; bByteOff[cells] = ib;
                ia++; ib++;
            }
            else if (matchB[ia] == -1)
            {
                // A's byte is unpaired. B's byte is matched later (i' > ia), so it waits.
                kA[cells] = KDel; aByteOff[cells] = ia;
                ia++;
            }
            else
            {
                // A's byte is matched later (j' > ib), so B's byte is unpaired and goes now.
                kB[cells] = KIns; bByteOff[cells] = ib;
                ib++;
            }

            cells++;
        }

        // Phase 2: shuffle — re-pair every net-balanced indel excursion positionally so a same-length
        // replacement reads as per-byte Modified (yellow) instead of staggered add/remove holes. This
        // generalizes the classic diff "shuffle": it also reunites isolated KDel/KIns cells that the raw
        // walk separated by match/substitute cells when a repeated byte got matched non-locally (the
        // user's "nothing was added or removed" case). Genuine one-sided shifts that never return to
        // baseline keep their real holes. See ShuffleSubstitutions for the order-preservation argument.
        cells = ShuffleSubstitutions(a, b, kA, kB, aByteOff, bByteOff, cells);

        // Phase 3: chunk the shuffled cells into 16-slot rows.
        for (int start = 0; start < cells; start += HexFormat.BytesPerLine)
            FlushRowRange(rowsA, rowsB, a, b, kA, kB, aByteOff, bByteOff, start,
                Math.Min(cells, start + HexFormat.BytesPerLine), computeInlineDiffs);

        return (rowsA, rowsB);
    }

    /// <summary>
    /// Generalized diff "shuffle": re-pair every net-balanced indel excursion positionally so a
    /// same-length byte replacement renders as per-byte Modified (yellow) rather than add/remove
    /// holes — the user's "nothing was added or removed" case.
    ///
    /// The raw walk emits staggered KDel/KIns cells whenever the LCS matches a repeated byte value
    /// non-locally: a local substitution region then gets one side marked KDel and the other KIns,
    /// even though both files have data at every position. The old shuffle only fixed *contiguous*
    /// indel runs; here we flatten any region where the running A-minus-B shift departs from its
    /// baseline and returns to it (equal byte counts on each side), re-pairing the i-th A-byte with
    /// the i-th B-byte. Genuine one-sided shifts that never return (a real net length change) keep
    /// their holes.
    ///
    /// Order preservation: within a span, A-byte offsets are collected in file order (the walk
    /// advances ia monotonically) and likewise for B, so positional pairing keeps each side's bytes
    /// a contiguous in-file sequence — the invariant FormatAlignedRow's sequential read relies on.
    /// Returns the new (≤ input) cell count; arrays are rewritten in place.
    /// </summary>
    private static int ShuffleSubstitutions(byte[] a, byte[] b, byte[] kA, byte[] kB, int[] aByteOff, int[] bByteOff, int cells)
    {
        if (cells <= 1) return cells;

        // Pass 1 — find the outermost net-balanced excursions. Track the running A-minus-B shift:
        // KDel raises it by 1, KIns lowers it by 1, match/substitute leave it unchanged. An
        // excursion is a maximal range where the shift departs from its baseline and returns to it —
        // equal byte counts on each side, so it can be re-paired with no holes. Because the shift
        // moves only ±1 per cell, it must pass through any level it crosses, so "first return to the
        // current baseline after having left it" cleanly delimits one outermost excursion; reset the
        // baseline there and continue. This is direction-agnostic (handles both up-then-down and
        // down-then-up) and never nests: a trailing shift that never returns leaves no span, so its
        // cells stay as real holes.
        var spanStart = new int[cells];
        var spanEnd = new int[cells];
        int spanCount = 0;

        int level = 0;          // shift after the cell just before `basePos`
        int basePos = 0;        // cell index where the current baseline was last established
        bool departed = false;  // have we left the baseline since basePos?
        for (int c = 0; c < cells; c++)
        {
            level += (kA[c] == KDel ? 1 : 0) - (kB[c] == KIns ? 1 : 0);
            if (level != 0 && !departed) departed = true;
            else if (level == 0 && departed)
            {
                spanStart[spanCount] = basePos; spanEnd[spanCount] = c + 1; spanCount++;
                basePos = c + 1; departed = false;
            }
        }

        // Pass 2 — emit: re-pair each span, copy every other cell verbatim.
        var nA = new byte[cells];
        var nB = new byte[cells];
        var naOff = new int[cells];
        var nbOff = new int[cells];

        int w = 0, i = 0, si = 0;
        while (i < cells)
        {
            if (si < spanCount && spanStart[si] == i)
            {
                w = RepairSpan(a, b, kA, kB, aByteOff, bByteOff, nA, nB, naOff, nbOff, i, spanEnd[si], w);
                i = spanEnd[si]; si++;
            }
            else
            {
                nA[w] = kA[i]; nB[w] = kB[i];
                naOff[w] = aByteOff[i]; nbOff[w] = bByteOff[i];
                w++; i++;
            }
        }

        Array.Copy(nA, 0, kA, 0, w);
        Array.Copy(nB, 0, kB, 0, w);
        Array.Copy(naOff, 0, aByteOff, 0, w);
        Array.Copy(nbOff, 0, bByteOff, 0, w);
        return w;
    }

    /// <summary>
    /// Re-pair one net-balanced cell span [s,e) positionally: the i-th A-byte pairs with the i-th
    /// B-byte (KMatch when equal, else KX), any surplus bytes stay as genuine holes. Each side's
    /// offsets are collected in file order (ascending through the walk), so no pairing crosses and
    /// each side stays a contiguous in-file sequence. Returns the updated write index.
    /// </summary>
    private static int RepairSpan(byte[] a, byte[] b, byte[] kA, byte[] kB, int[] aByteOff, int[] bByteOff,
        byte[] nA, byte[] nB, int[] naOff, int[] nbOff, int s, int e, int w)
    {
        int nDel = 0, nIns = 0;
        for (int c = s; c < e; c++)
        {
            if (kA[c] != KEmpty) nDel++;
            if (kB[c] != KEmpty) nIns++;
        }

        var delOffs = new int[nDel];
        var insOffs = new int[nIns];
        int di = 0, ii = 0;
        for (int c = s; c < e; c++)
        {
            if (kA[c] != KEmpty) delOffs[di++] = aByteOff[c];
            if (kB[c] != KEmpty) insOffs[ii++] = bByteOff[c];
        }

        int pairs = Math.Min(nDel, nIns);
        for (int p = 0; p < pairs; p++)
        {
            // Demote to Match when the values are equal — an equal byte is not a change (R3: colour
            // only changed bytes), so it must not carry the yellow Modified class.
            byte k = a[delOffs[p]] == b[insOffs[p]] ? KMatch : KX;
            nA[w] = k; nB[w] = k;
            naOff[w] = delOffs[p]; nbOff[w] = insOffs[p];
            w++;
        }
        for (int d = pairs; d < nDel; d++)
        {
            nA[w] = KDel; nB[w] = KEmpty;
            naOff[w] = delOffs[d]; nbOff[w] = -1;
            w++;
        }
        for (int s2 = pairs; s2 < nIns; s2++)
        {
            nA[w] = KEmpty; nB[w] = KIns;
            naOff[w] = -1; nbOff[w] = insOffs[s2];
            w++;
        }
        return w;
    }

    /// <summary>
    /// Build the two DiffLines for cells [start,end) — a slice of ≤16 shuffled cells. Mirrors the
    /// old streaming FlushRow exactly (status, per-class masks, HexGapSlots, OriginalText), but
    /// derives ByteStart from the recorded byte offsets instead of a running pointer.
    /// </summary>
    private static void FlushRowRange(
        List<DiffLine> rowsA, List<DiffLine> rowsB,
        byte[] a, byte[] b,
        byte[] kA, byte[] kB, int[] aByteOff, int[] bByteOff,
        int start, int end, bool computeInlineDiffs)
    {
        int rowIdx = rowsA.Count;      // both lists stay equal-length

        var kindA = new byte[HexFormat.BytesPerLine];
        var kindB = new byte[HexFormat.BytesPerLine];
        int maskA = 0, maskB = 0;          // bit c set ⇔ slot c holds a byte on this side
        int aStart = -1, bStart = -1;      // file offset of the first occupied byte on each side

        for (int c = start; c < end; c++)
        {
            int slot = c - start;
            kindA[slot] = kA[c];
            kindB[slot] = kB[c];
            if (kA[c] != KEmpty) { maskA |= 1 << slot; if (aStart < 0) aStart = aByteOff[c]; }
            if (kB[c] != KEmpty) { maskB |= 1 << slot; if (bStart < 0) bStart = bByteOff[c]; }
        }

        // ── Side A: kinds KMatch/KX/KDel ─────────────────────────────────
        DiffLine lineA;
        if (maskA == 0)
        {
            lineA = new DiffLine { OriginalLineNumber = 0, Text = "", Status = DiffStatus.Gap, IsFromFileA = true };
        }
        else
        {
            bool anyM = false, anyX = false, anyD = false;
            int removedA = 0, modifiedA = 0;
            for (int c = 0; c < HexFormat.BytesPerLine; c++)
            {
                switch (kindA[c])
                {
                    case KMatch: anyM = true; break;
                    case KX: anyX = true; modifiedA |= 1 << c; break;
                    case KDel: anyD = true; removedA |= 1 << c; break;
                }
            }

            var statusA = (!anyX && !anyD) ? DiffStatus.Unchanged
                         : (!anyM && !anyX) ? DiffStatus.Removed
                         : DiffStatus.Modified;

            lineA = new DiffLine
            {
                OriginalLineNumber = rowIdx + 1,
                Text = HexFormat.FormatAlignedRow(aStart, a, aStart, maskA),
                Status = statusA,
                IsFromFileA = true,
                ByteStart = aStart,
                HexSlotMask = maskA,
                HexRemovedSlots = removedA,
                HexModifiedSlots = modifiedA,
                HexGapSlots = maskB & ~maskA   // slots empty here but present on B (add holes)
            };

            if (statusA == DiffStatus.Modified && computeInlineDiffs)
                lineA.ChangedRegions = BuildChangedRegions(kindA, KX, KDel);
        }

        // ── Side B: symmetric — KIns plays the role of KDel, Added replaces Removed ──
        DiffLine lineB;
        if (maskB == 0)
        {
            lineB = new DiffLine { OriginalLineNumber = 0, Text = "", Status = DiffStatus.Gap, IsFromFileA = false };
        }
        else
        {
            bool anyM = false, anyX = false, anyI = false;
            int addedB = 0, modifiedB = 0;
            for (int c = 0; c < HexFormat.BytesPerLine; c++)
            {
                switch (kindB[c])
                {
                    case KMatch: anyM = true; break;
                    case KX: anyX = true; modifiedB |= 1 << c; break;
                    case KIns: anyI = true; addedB |= 1 << c; break;
                }
            }

            var statusB = (!anyX && !anyI) ? DiffStatus.Unchanged
                         : (!anyM && !anyX) ? DiffStatus.Added
                         : DiffStatus.Modified;

            lineB = new DiffLine
            {
                OriginalLineNumber = rowIdx + 1,
                Text = HexFormat.FormatAlignedRow(bStart, b, bStart, maskB),
                Status = statusB,
                IsFromFileA = false,
                ByteStart = bStart,
                HexSlotMask = maskB,
                HexAddedSlots = addedB,
                HexModifiedSlots = modifiedB,
                HexGapSlots = maskA & ~maskB   // slots empty here but present on A (remove holes)
            };

            if (statusB == DiffStatus.Modified && computeInlineDiffs)
                lineB.ChangedRegions = BuildChangedRegions(kindB, KX, KIns);
        }

        // Modified rows carry the partner's text so the pair travels correctly through a swap.
        if (lineA.Status == DiffStatus.Modified) lineA.OriginalText = lineB.Text;
        if (lineB.Status == DiffStatus.Modified) lineB.OriginalText = lineA.Text;

        rowsA.Add(lineA);
        rowsB.Add(lineB);
    }

    /// <summary>
    /// Positional fallback: row i of A vs row i of B (the pre-alignment hex behavior), used for
    /// segments where the byte diff exceeded its work budget. Stamps ByteStart/HexSlotMask so
    /// hit-testing and the inspector work identically to the aligned path.
    /// </summary>
    public static (List<DiffLine> RowsA, List<DiffLine> RowsB) BuildPositionalRows(
        byte[] a, int aOff, int lenA, byte[] b, int bOff, int lenB, bool computeInlineDiffs)
    {
        int linesA = HexFormat.BytesToHexLines(lenA);
        int linesB = HexFormat.BytesToHexLines(lenB);
        int maxLines = Math.Max(linesA, linesB);

        var rowsA = new List<DiffLine>(maxLines);
        var rowsB = new List<DiffLine>(maxLines);
        var kindA = new byte[HexFormat.BytesPerLine];
        var kindB = new byte[HexFormat.BytesPerLine];

        for (int i = 0; i < maxLines; i++)
        {
            int aStart = aOff + i * HexFormat.BytesPerLine;
            int bStart = bOff + i * HexFormat.BytesPerLine;
            int countA = i < linesA ? Math.Min(HexFormat.BytesPerLine, lenA - i * HexFormat.BytesPerLine) : 0;
            int countB = i < linesB ? Math.Min(HexFormat.BytesPerLine, lenB - i * HexFormat.BytesPerLine) : 0;

            Array.Clear(kindA, 0, HexFormat.BytesPerLine);
            Array.Clear(kindB, 0, HexFormat.BytesPerLine);

            bool anyDiff = false;
            int removedA = 0, modifiedA = 0, addedB = 0, modifiedB = 0;
            for (int c = 0; c < HexFormat.BytesPerLine; c++)
            {
                bool pa = c < countA, pb = c < countB;
                if (!pa && !pb) continue;
                bool differ = pa && pb && a[aStart + c] != b[bStart + c];
                bool diff = pa != pb || differ;
                if (diff) anyDiff = true;
                // kind arrays keep the whole "differing" set as KX for inline ChangedRegions;
                // the per-class masks split it into removed/added (one side only) vs modified (both).
                if (pa) { kindA[c] = diff ? KX : KMatch; if (!pb) removedA |= 1 << c; else if (differ) modifiedA |= 1 << c; }
                if (pb) { kindB[c] = diff ? KX : KMatch; if (!pa) addedB |= 1 << c; else if (differ) modifiedB |= 1 << c; }
            }

            int maskA = countA == 0 ? 0 : (1 << countA) - 1;
            int maskB = countB == 0 ? 0 : (1 << countB) - 1;

            // statusA == Modified ⇔ both sides present and anyDiff (then statusB is too).
            var statusA = countA == 0 ? DiffStatus.Gap
                         : countB == 0 ? DiffStatus.Removed
                         : anyDiff ? DiffStatus.Modified
                         : DiffStatus.Unchanged;
            var statusB = countB == 0 ? DiffStatus.Gap
                         : countA == 0 ? DiffStatus.Added
                         : anyDiff ? DiffStatus.Modified
                         : DiffStatus.Unchanged;

            string textA = countA > 0 ? HexFormat.FormatAlignedRow(aStart, a, aStart, maskA) : "";
            string textB = countB > 0 ? HexFormat.FormatAlignedRow(bStart, b, bStart, maskB) : "";

            var lineA = new DiffLine
            {
                OriginalLineNumber = countA == 0 ? 0 : i + 1,
                Text = textA,
                Status = statusA,
                IsFromFileA = true,
                ByteStart = countA == 0 ? null : aStart,
                HexSlotMask = maskA,
                HexRemovedSlots = removedA,
                HexModifiedSlots = modifiedA,
                HexGapSlots = maskB & ~maskA
            };
            var lineB = new DiffLine
            {
                OriginalLineNumber = countB == 0 ? 0 : i + 1,
                Text = textB,
                Status = statusB,
                IsFromFileA = false,
                ByteStart = countB == 0 ? null : bStart,
                HexSlotMask = maskB,
                HexAddedSlots = addedB,
                HexModifiedSlots = modifiedB,
                HexGapSlots = maskA & ~maskB
            };

            if (statusA == DiffStatus.Modified)
            {
                lineA.OriginalText = textB;
                lineB.OriginalText = textA;
                if (computeInlineDiffs)
                {
                    lineA.ChangedRegions = BuildChangedRegions(kindA, KX, KX);
                    lineB.ChangedRegions = BuildChangedRegions(kindB, KX, KX);
                }
            }

            rowsA.Add(lineA);
            rowsB.Add(lineB);
        }

        return (rowsA, rowsB);
    }

    /// <summary>
    /// Repack the aligned rows into dense fixed-address display rows (Show Gaps OFF): row k of
    /// each pane is that file's own bytes at offsets [k*16, k*16+15] — no drift, no mid-row holes.
    /// The two lists are equal length (maxLines = BytesToHexLines(max(lenA,lenB))); the shorter
    /// file's trailing rows are Gap rows so the panes stay row-aligned. Row status is positional
    /// (mirrors BuildPositionalRows: a row shifted by an earlier add/remove reads Modified even
    /// when its own bytes are unchanged — the R5 cascade the user resolves with Per-Byte or a Sync
    /// Point); the per-byte colour masks are carried over from the aligned classification, repacked
    /// to file addresses, so Per-Byte shading still marks only the bytes that genuinely changed.
    /// No second diff pass: every byte's class is recovered from its aligned row's ByteStart +
    /// HexSlotMask (occupied slots are contiguous in file space, so a running index is exact).
    /// </summary>
    public static (IReadOnlyList<DiffLine> FixedA, IReadOnlyList<DiffLine> FixedB) BuildFixedAddressRows(
        byte[] a, int lenA, byte[] b, int lenB,
        IReadOnlyList<DiffLine> alignedA, IReadOnlyList<DiffLine> alignedB)
    {
        int maxLines = HexFormat.BytesToHexLines(Math.Max(lenA, lenB));

        var fixedRemovedA = new int[maxLines];
        var fixedModifiedA = new int[maxLines];
        var fixedAddedB = new int[maxLines];
        var fixedModifiedB = new int[maxLines];

        // Repack A-side colour masks to file addresses.
        foreach (var line in alignedA)
        {
            int mask = line.HexSlotMask;
            if (mask == 0) continue;
            int removed = line.HexRemovedSlots, modified = line.HexModifiedSlots;
            int fileIdx = line.ByteStart ?? 0;
            for (int c = 0; c < HexFormat.BytesPerLine; c++)
            {
                if ((mask & (1 << c)) == 0) continue;   // empty slot — no byte here
                int rowK = fileIdx / HexFormat.BytesPerLine, colC = fileIdx % HexFormat.BytesPerLine;
                if ((removed & (1 << c)) != 0) fixedRemovedA[rowK] |= 1 << colC;
                if ((modified & (1 << c)) != 0) fixedModifiedA[rowK] |= 1 << colC;
                fileIdx++;
            }
        }

        // Repack B-side colour masks to file addresses.
        foreach (var line in alignedB)
        {
            int mask = line.HexSlotMask;
            if (mask == 0) continue;
            int added = line.HexAddedSlots, modified = line.HexModifiedSlots;
            int fileIdx = line.ByteStart ?? 0;
            for (int c = 0; c < HexFormat.BytesPerLine; c++)
            {
                if ((mask & (1 << c)) == 0) continue;   // empty slot — no byte here
                int rowK = fileIdx / HexFormat.BytesPerLine, colC = fileIdx % HexFormat.BytesPerLine;
                if ((added & (1 << c)) != 0) fixedAddedB[rowK] |= 1 << colC;
                if ((modified & (1 << c)) != 0) fixedModifiedB[rowK] |= 1 << colC;
                fileIdx++;
            }
        }

        var rowsA = new List<DiffLine>(maxLines);
        var rowsB = new List<DiffLine>(maxLines);

        for (int k = 0; k < maxLines; k++)
        {
            int aStart = k * HexFormat.BytesPerLine;
            int bStart = k * HexFormat.BytesPerLine;
            int countA = aStart < lenA ? Math.Min(HexFormat.BytesPerLine, lenA - aStart) : 0;
            int countB = bStart < lenB ? Math.Min(HexFormat.BytesPerLine, lenB - bStart) : 0;

            bool anyDiff = false;
            for (int c = 0; c < HexFormat.BytesPerLine; c++)
            {
                bool pa = c < countA, pb = c < countB;
                if (!pa && !pb) continue;
                if (pa != pb || a[aStart + c] != b[bStart + c]) { anyDiff = true; break; }
            }

            var statusA = countA == 0 ? DiffStatus.Gap
                         : countB == 0 ? DiffStatus.Removed
                         : anyDiff ? DiffStatus.Modified
                         : DiffStatus.Unchanged;
            var statusB = countB == 0 ? DiffStatus.Gap
                         : countA == 0 ? DiffStatus.Added
                         : anyDiff ? DiffStatus.Modified
                         : DiffStatus.Unchanged;

            int maskA = countA == 0 ? 0 : (1 << countA) - 1;
            int maskB = countB == 0 ? 0 : (1 << countB) - 1;

            var lineA = new DiffLine
            {
                OriginalLineNumber = countA == 0 ? 0 : k + 1,
                Text = countA > 0 ? HexFormat.FormatAlignedRow(aStart, a, aStart, maskA) : "",
                Status = statusA,
                IsFromFileA = true,
                ByteStart = countA == 0 ? null : aStart,
                HexSlotMask = maskA,
                HexRemovedSlots = fixedRemovedA[k],
                HexModifiedSlots = fixedModifiedA[k]
            };
            var lineB = new DiffLine
            {
                OriginalLineNumber = countB == 0 ? 0 : k + 1,
                Text = countB > 0 ? HexFormat.FormatAlignedRow(bStart, b, bStart, maskB) : "",
                Status = statusB,
                IsFromFileA = false,
                ByteStart = countB == 0 ? null : bStart,
                HexSlotMask = maskB,
                HexAddedSlots = fixedAddedB[k],
                HexModifiedSlots = fixedModifiedB[k]
            };

            if (statusA == DiffStatus.Modified)
            {
                lineA.OriginalText = lineB.Text;
                lineB.OriginalText = lineA.Text;
            }

            rowsA.Add(lineA);
            rowsB.Add(lineB);
        }

        return (rowsA, rowsB);
    }

    /// <summary>
    /// Progress/cancellation state shared by the segment loop and the resync recursion.
    /// Progress is measured in consumed side-A bytes: segments and resync leaves partition
    /// side A exactly, so DoneBytes reaches TotalBytes precisely at the end (hence the 99%
    /// cap per update — Finish reports the final 100%).
    /// </summary>
    private sealed class SegmentContext
    {
        public long TotalBytes;
        public long DoneBytes;
        public Action<double, string>? Report;
        public CancellationToken Token = default;

        public void Completed(int lenA)
        {
            if (Report == null || TotalBytes <= 0) return;
            DoneBytes += lenA;
            double pct = Math.Min(99.0, 100.0 * DoneBytes / TotalBytes);
            Report(pct, $"Aligning bytes... ({pct:F0}%)");
        }

        public void Finish()
        {
            if (Report != null) Report(100.0, "Aligning bytes... (100%)");
        }

        public void ThrowIfCancelled()
        {
            if (Token.IsCancellationRequested) throw new OperationCanceledException(Token);
        }
    }

    /// <summary>
    /// Diff the two files with byte sync points, mirroring DiffEngine.ComputeWithSyncPoints:
    /// segment i spans [prev+1 .. anchor_i] × [prev+1 .. anchor_i] inclusive (the anchor belongs
    /// to the segment ending at it), plus a trailing segment to EOF. Each segment gets the FULL
    /// budget — sync points exist to partition pathological files, so the budget is not divided.
    /// On success the anchor pair is forced into the match arrays (it is the segment's max index
    /// on both axes, so it cannot cross anything); on budget failure just that segment falls back
    /// to positional rows and FallbackSegments is incremented.
    ///
    /// When autoResync is true a budget-exceeded segment is NOT dropped to positional rows (which
    /// would turn every following row into one giant Modified block). Instead it is adaptively
    /// subdivided — see BuildResyncSegment — so unchanged regions still align and only genuinely
    /// different sub-regions degrade. FallbackSegments then counts the sub-frames that ultimately
    /// needed positional rows (0 in the common case).
    ///
    /// Anchors must be co-monotone (validate with IsAnchorSetOrdered first). Rows carry global
    /// OriginalLineNumber.
    ///
    /// reportProgress (optional) receives (percent 0–100, status text) as side-A bytes are
    /// consumed — one call per completed segment / resync leaf; cancellation throws
    /// OperationCanceledException at segment and engine-loop boundaries.
    /// </summary>
    public static (List<DiffLine> RowsA, List<DiffLine> RowsB, int FallbackSegments) ComputeWithSyncPoints(
        byte[] a, byte[] b, IReadOnlyList<(int ByteA, int ByteB)> anchors,
        bool computeInlineDiffs, long cellBudget = MaxWorkCells, bool autoResync = false,
        Action<double, string>? reportProgress = null, CancellationToken ct = default)
    {
        var sorted = anchors
            .Where(p => p.ByteA >= 0 && p.ByteA < a.Length && p.ByteB >= 0 && p.ByteB < b.Length)
            .Distinct()
            .OrderBy(p => p.ByteA)
            .ThenBy(p => p.ByteB)
            .ToList();

        var ctx = new SegmentContext { TotalBytes = a.Length, Report = reportProgress, Token = ct };

        var matchB = new int[a.Length]; Array.Fill(matchB, -1);
        var matchA = new int[b.Length]; Array.Fill(matchA, -1);

        var rowsA = new List<DiffLine>();
        var rowsB = new List<DiffLine>();
        int fallbackSegments = 0;

        void AppendSegment((List<DiffLine>, List<DiffLine>) seg, int aStart, int bStart)
        {
            foreach (var l in seg.Item1) l.OriginalLineNumber += aStart;
            foreach (var l in seg.Item2) l.OriginalLineNumber += bStart;
            rowsA.AddRange(seg.Item1);
            rowsB.AddRange(seg.Item2);
        }

        int prevA = -1, prevB = -1;
        foreach (var (anchorA, anchorB) in sorted)
        {
            ctx.ThrowIfCancelled();
            int segAStart = prevA + 1, segALen = anchorA - prevA;
            int segBStart = prevB + 1, segBLen = anchorB - prevB;

            if (autoResync)
            {
                // Subdivide on budget failure instead of positional — unchanged regions keep
                // aligning, so no cascade. The anchor pin is skipped here: subdivision re-derives
                // the pairing and the segment's last byte still lands at the end of its rows.
                // Progress is reported by the leaves (they partition this segment's bytes).
                AppendSegment(BuildResyncSegment(a, segAStart, segALen, b, segBStart, segBLen,
                    matchB, matchA, cellBudget, computeInlineDiffs, ref fallbackSegments, ctx),
                    segAStart, segBStart);
            }
            else
            {
                bool ok = TryComputeRange(a, segAStart, segALen, b, segBStart, segBLen, cellBudget, matchB, matchA, ct);
                if (ok)
                {
                    // Force the anchor pair: clear whatever the engine paired instead, then pin it.
                    if (matchB[anchorA] >= 0) matchA[matchB[anchorA]] = -1;
                    if (matchA[anchorB] >= 0) matchB[matchA[anchorB]] = -1;
                    matchB[anchorA] = anchorB;
                    matchA[anchorB] = anchorA;
                }
                else
                    fallbackSegments++;

                AppendSegment(ok
                    ? BuildAlignedRows(a, segAStart, segALen, b, segBStart, segBLen, matchB, matchA, computeInlineDiffs)
                    : BuildPositionalRows(a, segAStart, segALen, b, segBStart, segBLen, computeInlineDiffs),
                    segAStart, segBStart);
                ctx.Completed(segALen);
            }

            prevA = anchorA; prevB = anchorB;
        }

        // Trailing segment after the last sync point (exclusive of it) — mirrors DiffEngine.
        int tailAStart = prevA + 1, tailALen = a.Length - tailAStart;
        int tailBStart = prevB + 1, tailBLen = b.Length - tailBStart;
        if (tailALen > 0 || tailBLen > 0)
        {
            ctx.ThrowIfCancelled();
            if (autoResync)
                AppendSegment(BuildResyncSegment(a, tailAStart, tailALen, b, tailBStart, tailBLen,
                    matchB, matchA, cellBudget, computeInlineDiffs, ref fallbackSegments, ctx),
                    tailAStart, tailBStart);
            else
            {
                bool ok = TryComputeRange(a, tailAStart, tailALen, b, tailBStart, tailBLen, cellBudget, matchB, matchA, ct);
                if (!ok) fallbackSegments++;
                AppendSegment(ok
                    ? BuildAlignedRows(a, tailAStart, tailALen, b, tailBStart, tailBLen, matchB, matchA, computeInlineDiffs)
                    : BuildPositionalRows(a, tailAStart, tailALen, b, tailBStart, tailBLen, computeInlineDiffs),
                    tailAStart, tailBStart);
                ctx.Completed(tailALen);
            }
        }

        ctx.Finish();
        return (rowsA, rowsB, fallbackSegments);
    }

    /// <summary>
    /// Auto-resync segment builder. Tries to byte-align the whole range; when that exceeds the
    /// work budget it recursively splits BOTH sides at their midpoints and aligns each half
    /// instead of falling back to positional rows (which would turn every subsequent row into one
    /// giant Modified block). Unchanged regions align cleanly inside any sub-frame — a[i] ==
    /// b[i+delta] throughout, so no spurious changes appear at split boundaries; only genuinely
    /// different regions keep subdividing. Each leaf gets the full budget (mirroring how sync-point
    /// segments are not divided). Terminates in success once a side drops to one byte (the
    /// single-byte fast path is O(other-side) and always fits); degrades to positional rows for the
    /// current sub-frame when <see cref="MaxResyncLeaves"/> is reached (adversarial input).
    /// Returns rows with local 1-based OriginalLineNumber, like BuildAlignedRows. Sub-frames are
    /// disjoint in both coordinates, so their match-array writes never cross and stitching is safe.
    /// </summary>
    private static (List<DiffLine> RowsA, List<DiffLine> RowsB) BuildResyncSegment(
        byte[] a, int aOff, int lenA, byte[] b, int bOff, int lenB,
        int[] matchB, int[] matchA, long cellBudget, bool computeInlineDiffs, ref int fallbackLeaves,
        SegmentContext ctx)
    {
        ctx.ThrowIfCancelled();

        if (TryComputeRange(a, aOff, lenA, b, bOff, lenB, cellBudget, matchB, matchA, ctx.Token))
        {
            // Leaf — this frame's side-A bytes are done being consumed.
            var rows = BuildAlignedRows(a, aOff, lenA, b, bOff, lenB, matchB, matchA, computeInlineDiffs);
            ctx.Completed(lenA);
            return rows;
        }

        // Budget exceeded. Subdivide unless one side is already a single byte (the fast path should
        // have succeeded — guard anyway) or the leaf cap is reached (bounded degradation).
        if ((lenA <= 1 || lenB <= 1) || fallbackLeaves >= MaxResyncLeaves)
        {
            fallbackLeaves++;
            var rows = BuildPositionalRows(a, aOff, lenA, b, bOff, lenB, computeInlineDiffs);
            ctx.Completed(lenA);
            return rows;
        }

        int aMid = aOff + lenA / 2;
        int bMid = bOff + lenB / 2;

        var (leftA, leftB) = BuildResyncSegment(a, aOff, aMid - aOff, b, bOff, bMid - bOff,
            matchB, matchA, cellBudget, computeInlineDiffs, ref fallbackLeaves, ctx);
        var (rightA, rightB) = BuildResyncSegment(a, aMid, lenA - (aMid - aOff), b, bMid, lenB - (bMid - bOff),
            matchB, matchA, cellBudget, computeInlineDiffs, ref fallbackLeaves, ctx);

        // Stitch: offset the right half's local line numbers by the left half's row count.
        foreach (var l in rightA) l.OriginalLineNumber += leftA.Count;
        foreach (var l in rightB) l.OriginalLineNumber += leftB.Count;
        leftA.AddRange(rightA);
        leftB.AddRange(rightB);
        return (leftA, leftB);
    }

    /* ═══════════ Engine internals — byte port of DiffEngine.FindMatches/MyersCore/Hirschberg ═══════════ */

    private static bool TryComputeRange(
        byte[] a, int aOff, int lenA, byte[] b, int bOff, int lenB,
        long cellBudget, int[] matchB, int[] matchA, CancellationToken ct = default)
    {
        for (int i = aOff; i < aOff + lenA; i++) matchB[i] = -1;
        for (int i = bOff; i < bOff + lenB; i++) matchA[i] = -1;

        if (lenA == 0 || lenB == 0) return true;

        // Canonicalize argument order so TryCompute(a,b) / TryCompute(b,a) are exact mirrors.
        bool transposed = false;
        byte[] ca = a, cb = b;
        int cAO = aOff, cBO = bOff, n = lenA, m = lenB;
        if (!IsCanonicalFirst(ca, cAO, n, cb, cBO, m))
        {
            (ca, cb) = (cb, ca);
            (cAO, cBO) = (cBO, cAO);
            (n, m) = (m, n);
            transposed = true;
        }

        long work = 0;
        var stack = new FrameStack(64);
        int[] fwd = Array.Empty<int>(), bwd = Array.Empty<int>();   // shared growable Myers buffers
        stack.Push(cAO, cAO + n, cBO, cBO + m);

        while (stack.TryPop(out int aS, out int aE, out int bS, out int bE))
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            int lA = aE - aS, lB = bE - bS;
            if (lA == 0 || lB == 0) continue;

            // Strip common prefix / suffix and record those matches directly.
            int p = 0;
            while (p < Math.Min(lA, lB) && ca[aS + p] == cb[bS + p]) { p++; work++; }
            if (work > cellBudget) return false;

            int s = 0;
            while (s < Math.Min(lA - p, lB - p) && ca[aE - 1 - s] == cb[bE - 1 - s]) { s++; work++; }
            if (work > cellBudget) return false;

            for (int i = 0; i < p; i++) EmitMatch(matchB, matchA, transposed, aS + i, bS + i);
            for (int i = 0; i < s; i++) EmitMatch(matchB, matchA, transposed, aE - s + i, bE - s + i);

            aS += p; aE -= s; bS += p; bE -= s;
            lA = aE - aS; lB = bE - bS;
            if (lA == 0 || lB == 0) continue;

            // Identical fast path.
            if (lA == lB)
            {
                bool allEq = true;
                for (int i = 0; i < lA; i++)
                {
                    work++;
                    if (ca[aS + i] != cb[bS + i]) { allEq = false; break; }
                }
                if (work > cellBudget) return false;
                if (allEq)
                {
                    for (int i = 0; i < lA; i++) EmitMatch(matchB, matchA, transposed, aS + i, bS + i);
                    continue;
                }
            }

            // Single-byte fast paths — pair with the first equal counterpart. Safe: this byte is
            // the only one of its side in the frame, so no other match can cross it.
            if (lA == 1)
            {
                for (int j = 0; j < lB; j++)
                {
                    work++;
                    if (ca[aS] == cb[bS + j]) { EmitMatch(matchB, matchA, transposed, aS, bS + j); break; }
                }
                if (work > cellBudget) return false;
                continue;
            }
            if (lB == 1)
            {
                for (int i = 0; i < lA; i++)
                {
                    work++;
                    if (ca[aS + i] == cb[bS]) { EmitMatch(matchB, matchA, transposed, aS + i, bS); break; }
                }
                if (work > cellBudget) return false;
                continue;
            }

            if (!MyersCore(ca, aS, cb, bS, lA, lB, matchB, matchA, transposed, ref stack, ref fwd, ref bwd, cellBudget, ref work, ct))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Bidirectional middle-snake Myers on one frame. Splits at the meeting point and pushes the
    /// two halves back onto the caller's stack (which must be passed by ref — Push can grow the
    /// backing array). Returns false when the work budget is exceeded, or falls through to
    /// Hirschberg if no usable meeting point is found.
    /// </summary>
    private static bool MyersCore(
        byte[] ca, int aS, byte[] cb, int bS, int n, int m,
        int[] matchB, int[] matchA, bool transposed,
        ref FrameStack stack, ref int[] fwd, ref int[] bwd, long cellBudget, ref long work, CancellationToken ct)
    {
        int maxD = n + m;
        int off = maxD + 2;
        int bufSize = off * 2;

        if (fwd.Length < bufSize)
        {
            fwd = new int[bufSize];
            bwd = new int[bufSize];
        }

        bool found = false;
        for (int d = 0; d < maxD && !found; d++)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            // Forward step: x on diagonal k, y = x − k.
            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d ? 0 : k == d ? fwd[k - 1 + off] + 1
                      : Math.Max(fwd[k - 1 + off] + 1, fwd[k + 1 + off]);
                int y = x - k;
                while (x < n && y < m && ca[aS + x] == cb[bS + y]) { x++; y++; work++; }
                fwd[k + off] = x;
            }

            // Backward step from the ends, in reversed coordinates (x' bytes consumed from the end).
            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d ? 0 : k == d ? bwd[k - 1 + off] + 1
                      : Math.Max(bwd[k - 1 + off] + 1, bwd[k + 1 + off]);
                int y = x - k;
                while (x < n && y < m && ca[aS + n - 1 - x] == cb[bS + m - 1 - y]) { x++; y++; work++; }
                bwd[k + off] = x;
            }

            work += 2L * (d + 1);

            // Overlap check: the backward frontier on reversed diagonal kb = n−m−k sits on the
            // same original diagonal as forward k.
            for (int k = -d; k <= d; k += 2)
            {
                int kb = n - m - k;
                if (kb < -d || kb > d) continue;
                if (fwd[k + off] >= n - bwd[kb + off])
                {
                    int xm0 = n - bwd[kb + off];
                    int ym0 = m - bwd[kb + off] + kb;
                    if (xm0 < 0) xm0 = 0; else if (xm0 > n) xm0 = n;
                    if (ym0 < 0) ym0 = 0; else if (ym0 > m) ym0 = m;

                    // Recover the full snake through the meeting point.
                    int sx = xm0, sy = ym0;
                    while (sx > 0 && sy > 0 && ca[aS + sx - 1] == cb[bS + sy - 1]) { sx--; sy--; work++; }
                    int ex = xm0, ey = ym0;
                    while (ex < n && ey < m && ca[aS + ex] == cb[bS + ey]) { ex++; ey++; work++; }

                    int xm = (sx + ex) / 2;
                    int ym = (sy + ey) / 2;
                    if (xm <= 0 || ym <= 0 || xm >= n || ym >= m) continue;   // degenerate — try larger d

                    stack.Push(aS + xm, aS + n, bS + ym, bS + m);            // upper-right half
                    stack.Push(aS, aS + xm, bS, bS + ym);                    // lower-left half
                    found = true;
                    break;
                }
            }

            if (work > cellBudget) return false;
        }

        if (!found)
        {
            // Hirschberg with DP arrays sized by the shorter side. Flipped when the shorter
            // array is b so the int[M+1] DP buffer tracks the shorter dimension.
            if (n <= m)
                return HirschbergRec(ca, aS, n, cb, bS, m, matchB, matchA, transposed, flipped: false, cellBudget, ref work, ct);
            return HirschbergRec(cb, bS, m, ca, aS, n, matchB, matchA, transposed, flipped: true, cellBudget, ref work, ct);
        }
        return true;
    }

    /* ═══════════ Hirschberg fallback ═══════════ */

    /// <summary>
    /// Linear-space LCS (Hirschberg) over one frame. Emits matches directly into the caller's
    /// arrays — matchB/matchA/transposed are threaded from TryComputeRange; when called as a
    /// Myers fallback for the whole frame they cover exactly this frame's byte ranges.
    /// Returns false when the work budget is exceeded.
    /// </summary>
    private static bool HirschbergRec(
        byte[] x, int xOff, int N, byte[] y, int yOff, int M,
        int[] matchB, int[] matchA, bool transposed, bool flipped, long cellBudget, ref long work, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
        if (N == 0 || M == 0) return true;

        // Base case: one byte on the x side — pair it with the first equal byte on the y side.
        if (N == 1)
        {
            for (int j = 0; j < M; j++)
            {
                if (++work > cellBudget) return false;
                if (x[xOff] == y[yOff + j])
                {
                    if (flipped) EmitMatch(matchB, matchA, transposed, yOff + j, xOff);
                    else         EmitMatch(matchB, matchA, transposed, xOff, yOff + j);
                    return true;
                }
            }
            return true;   // no equal counterpart — this byte stays unmatched
        }

        int mid = N / 2;

        // Forward: fwdScore[j] = LCS(x[xOff..xOff+mid), y[yOff..yOff+j))
        var dp = new int[M + 1];
        for (int i = 0; i < mid; i++)
        {
            int diag = 0;
            for (int j = 1; j <= M; j++)
            {
                if (++work > cellBudget) return false;
                int up = dp[j];
                dp[j] = x[xOff + i] == y[yOff + j - 1] ? diag + 1 : Math.Max(up, dp[j - 1]);
                diag = up;
            }
        }
        var fwdScore = (int[])dp.Clone();

        // Backward: bwd[j] = LCS(x[xOff+mid..xOff+N), y[yOff+j..yOff+M))
        Array.Clear(dp, 0, M + 1);
        for (int i = N - 1; i >= mid; i--)
        {
            int diag = 0;
            for (int j = M - 1; j >= 0; j--)
            {
                if (++work > cellBudget) return false;
                int up = dp[j];
                dp[j] = x[xOff + i] == y[yOff + j] ? diag + 1 : Math.Max(up, dp[j + 1]);
                diag = up;
            }
        }

        // Split at the smallest j maximizing fwdScore[j] + bwd[j].
        int bestJ = 0, bestScore = -1;
        for (int j = 0; j <= M; j++)
        {
            int score = fwdScore[j] + dp[j];
            if (score > bestScore) { bestScore = score; bestJ = j; }
        }

        // x[mid−1] pairs with y[bestJ−1] iff the diagonal through it is on an optimal path.
        bool onDiag = bestJ > 0 && bestJ < M
            && fwdScore[bestJ - 1] + dp[bestJ + 1] + 1 == bestScore
            && x[xOff + mid - 1] == y[yOff + bestJ - 1];
        if (onDiag)
        {
            if (flipped) EmitMatch(matchB, matchA, transposed, yOff + bestJ - 1, xOff + mid - 1);
            else         EmitMatch(matchB, matchA, transposed, xOff + mid - 1, yOff + bestJ - 1);
        }

        return HirschbergRec(x, xOff, mid, y, yOff, bestJ, matchB, matchA, transposed, flipped, cellBudget, ref work, ct)
            && HirschbergRec(x, xOff + mid, N - mid, y, yOff + bestJ, M - bestJ, matchB, matchA, transposed, flipped, cellBudget, ref work, ct);
    }

    /* ═══════════ Helpers ═══════════ */

    /// <summary>Record a canonical-coordinate match into the caller's arrays (undoing any transpose).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitMatch(int[] matchB, int[] matchA, bool transposed, int ia, int ib)
    {
        if (transposed) { matchA[ia] = ib; matchB[ib] = ia; }
        else            { matchB[ia] = ib; matchA[ib] = ia; }
    }

    /// <summary>Canonical first argument: shorter length wins, then ordinal lexicographic byte order.</summary>
    private static bool IsCanonicalFirst(byte[] a, int aOff, int lenA, byte[] b, int bOff, int lenB)
    {
        if (lenA != lenB) return lenA < lenB;
        for (int i = 0; i < lenA; i++)
            if (a[aOff + i] != b[bOff + i]) return a[aOff + i] < b[bOff + i];
        return true;
    }

    /// <summary>
    /// (startColumn, length) runs covering the hex pair + ascii char of every changed slot,
    /// sorted and merged into contiguous runs — same shape as CharDiff.ComputeHexChangedRegions.
    /// </summary>
    private static List<(int start, int length)> BuildChangedRegions(byte[] kinds, byte k1, byte k2)
    {
        var regions = new List<(int, int)>();
        for (int c = 0; c < HexFormat.BytesPerLine; c++)
        {
            if (kinds[c] != k1 && kinds[c] != k2) continue;
            regions.Add((HexFormat.HexSlotColumns(c).Col, 2));
            regions.Add((HexFormat.AsciiColumn(c), 1));
        }

        regions.Sort((r, s) => r.Item1.CompareTo(s.Item1));
        var merged = new List<(int start, int length)>();
        foreach (var (start, length) in regions)
        {
            if (merged.Count > 0 && start <= merged[^1].start + merged[^1].length)
                merged[^1] = (merged[^1].start, Math.Max(merged[^1].start + merged[^1].length, start + length) - merged[^1].start);
            else
                merged.Add((start, length));
        }
        return merged;
    }

    /// <summary>
    /// Growable LIFO of (aS, aE, bS, bE) frames. Must be passed by ref — Push can grow the
    /// backing array and a by-value copy would silently drop the split frames (see
    /// DiffEngine.ValueStack).
    /// </summary>
    private struct FrameStack
    {
        private int[] _items;   // 4 ints per frame
        private int _count;

        public FrameStack(int capacity) => _items = new int[Math.Max(1, capacity) * 4];

        public void Push(int aS, int aE, int bS, int bE)
        {
            if (_count * 4 + 4 > _items.Length)
            {
                var grown = new int[_items.Length * 2];
                Buffer.BlockCopy(_items, 0, grown, 0, _items.Length * sizeof(int));
                _items = grown;
            }
            int o = _count++ * 4;
            _items[o] = aS; _items[o + 1] = aE; _items[o + 2] = bS; _items[o + 3] = bE;
        }

        public bool TryPop(out int aS, out int aE, out int bS, out int bE)
        {
            if (_count == 0) { aS = aE = bS = bE = 0; return false; }
            int o = --_count * 4;
            aS = _items[o]; aE = _items[o + 1]; bS = _items[o + 2]; bE = _items[o + 3];
            return true;
        }
    }
}
