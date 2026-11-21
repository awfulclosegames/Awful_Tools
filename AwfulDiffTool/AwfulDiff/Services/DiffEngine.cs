using System.Runtime.CompilerServices;
using AwfulDiff.Models;

namespace AwfulDiff.Services;

/// <summary>
/// Reports progress from the diff computation back to the UI.
/// </summary>
public delegate void DiffProgress(double percentage, string statusText);

/// <summary>
/// Computes diffs between two sets of lines. Two engines are available (DiffAlgorithm):
/// Standard — Myers' O(N + D^2) bidirectional diff where D is the number of differences;
/// Patience — anchors on lines unique to both files (largest non-crossing set via LIS),
/// recursing between anchors with Myers as fallback for ranges without unique lines.
/// Both use precomputed integer hashes so that every line comparison is an O(1) int-equals
/// with a full string fallback only on hash collisions, and both canonicalize argument order
/// (IsCanonicalFirst) so Compute(A,B) and Compute(B,A) are always exact mirrors.
/// </summary>
public static class DiffEngine
{
    public static async Task<(List<DiffLine> FileA, List<DiffLine> FileB, List<DiffLine> InterleavedA, List<DiffLine> InterleavedB)> ComputeAsync(
        FileModel? fileA, FileModel? fileB, bool ignoreWhitespace = false, bool useTimestamps = false,
        DiffProgress? reportProgress = null,
        List<(int aIdx, int bIdx)>? syncPoints = null,
        bool computeInlineDiffs = true,
        DiffAlgorithm algorithm = DiffAlgorithm.Standard)
    {
        return await Task.Run(() => Compute(fileA, fileB, ignoreWhitespace, useTimestamps, reportProgress, syncPoints, computeInlineDiffs, algorithm));
    }

    public static (List<DiffLine> FileA, List<DiffLine> FileB, List<DiffLine> InterleavedA, List<DiffLine> InterleavedB) Compute(
        FileModel? fileA, FileModel? fileB, bool ignoreWhitespace = false, bool useTimestamps = false,
        DiffProgress? reportProgress = null,
        List<(int aIdx, int bIdx)>? syncPoints = null,
        bool computeInlineDiffs = true,
        DiffAlgorithm algorithm = DiffAlgorithm.Standard)
    {
        // In timestamp mode, the older file is treated as the "base" (before).
        // If fileA (left panel) is newer than fileB (right panel), swap inputs so
        // the diff algorithm sees older→newer, then assign results back correctly.
        bool swapped = false;
        if (useTimestamps
            && fileA?.LastWriteTime.HasValue == true
            && fileB?.LastWriteTime.HasValue == true)
        {
            if (fileA.LastWriteTime.Value > fileB.LastWriteTime.Value)
            {
                // Swap so older file is always treated as File A (base/before)
                (fileA, fileB) = (fileB, fileA);
                swapped = true;
            }
        }

        var aLines = fileA?.Lines ?? new List<string>();
        var bLines = fileB?.Lines ?? new List<string>();

        // When sync points are provided, partition the diff into independent segments.
        if (syncPoints != null && syncPoints.Count > 0)
        {
            return ComputeWithSyncPoints(aLines, bLines, ignoreWhitespace, useTimestamps,
                reportProgress, syncPoints, fileA, fileB, computeInlineDiffs, algorithm);
        }

        string[] aKeys = ignoreWhitespace ? aLines.Select(CharDiff.StripAllWhitespace).ToArray() : aLines.ToArray();
        string[] bKeys = ignoreWhitespace ? bLines.Select(CharDiff.StripAllWhitespace).ToArray() : bLines.ToArray();

        // Precompute 32-bit hashes for every line so comparisons become O(1) int-equals.
        // Full string comparison is the fallback on hash collisions only.
        int[] aHashes = new int[aKeys.Length];
        int[] bHashes = new int[bKeys.Length];
        for (int i = 0; i < aKeys.Length; i++) aHashes[i] = aKeys[i].GetHashCode();
        for (int i = 0; i < bKeys.Length; i++) bHashes[i] = bKeys[i].GetHashCode();

        int totalLines = aKeys.Length + bKeys.Length;

        var matches = new List<(int, int)>();
        FindMatches(aKeys, aHashes, bKeys, bHashes, matches, reportProgress, totalLines, algorithm);

        // Sort by file-A index so BuildResult can correctly pair adjacent unmatched blocks
        matches.Sort((a, b) => a.Item1.CompareTo(b.Item1));

        var resultA = BuildResult(aLines, matches, true, bLines, computeInlineDiffs, ignoreWhitespace);
        var resultB = BuildResult(bLines, matches, false, aLines, computeInlineDiffs, ignoreWhitespace);

        var (intA, intB) = BuildInterleaved(aLines, bLines, matches, computeInlineDiffs, ignoreWhitespace);

        AnnotateAnchors(resultA, resultB, intA, intB);

        // If we swapped inputs, return results in the original file order
        if (swapped)
            return (resultB, resultA, intB, intA);

        return (resultA, resultB, intA, intB);
    }

    /// <summary>
    /// Stamps OtherSideAnchor onto every Added/Removed line: the count of real (non-Gap)
    /// lines in the opposite file before this row. Walks the row-aligned interleaved pair —
    /// its non-Gap rows are exactly the plain list's lines in order, so pa/pb track each
    /// plain list while aCount/bCount count real lines seen so far on each side. Both the
    /// interleaved and plain instances are stamped (they are separate objects).
    ///
    /// The value survives SwapFiles' flip+swap without re-annotation: flipping only swaps
    /// Removed↔Added, so every row stays "real" or Gap exactly as before, and the count of
    /// other-side real lines preceding this row is unchanged.
    /// </summary>
    static void AnnotateAnchors(List<DiffLine> plainA, List<DiffLine> plainB, List<DiffLine> intA, List<DiffLine> intB)
    {
        int pa = 0, pb = 0;
        int aCount = 0, bCount = 0;

        for (int r = 0; r < Math.Min(intA.Count, intB.Count); r++)
        {
            var la = intA[r];
            var lb = intB[r];

            if (la.Status != DiffStatus.Gap)
            {
                if (la.Status is DiffStatus.Removed or DiffStatus.Added && pa < plainA.Count)
                {
                    la.OtherSideAnchor = bCount;
                    plainA[pa].OtherSideAnchor = bCount;
                }
                aCount++;
                pa++;
            }

            if (lb.Status != DiffStatus.Gap)
            {
                if (lb.Status is DiffStatus.Removed or DiffStatus.Added && pb < plainB.Count)
                {
                    lb.OtherSideAnchor = aCount;
                    plainB[pb].OtherSideAnchor = aCount;
                }
                bCount++;
                pb++;
            }
        }
    }

    /// <summary>
    /// Synthesizes a single git-style list from the row-aligned interleaved pair returned by
    /// Compute: Unchanged→1 line; Removed/Gap→1 Removed line (A's text and number);
    /// Gap/Added→1 Added line (B's text and number); Modified/Modified→a Removed+Added pair,
    /// each carrying its own side's ChangedRegions so the viewer can highlight intra-line
    /// changes on both rows. Never call this on hex-view output — that pair is null-padded,
    /// not row-aligned.
    /// </summary>
    public static IReadOnlyList<DiffLine> BuildUnified(
        IReadOnlyList<DiffLine> interleavedA, IReadOnlyList<DiffLine> interleavedB)
    {
        var unified = new List<DiffLine>(interleavedA.Count); // upper bound; Modified rows expand to 2
        for (int i = 0; i < interleavedA.Count; i++)
        {
            var la = interleavedA[i];
            var lb = interleavedB[i];

            switch ((la.Status, lb.Status))
            {
                case (DiffStatus.Unchanged, DiffStatus.Unchanged):
                    unified.Add(la);
                    break;

                case (DiffStatus.Removed, DiffStatus.Gap):
                    unified.Add(la);
                    break;

                case (DiffStatus.Gap, DiffStatus.Added):
                    unified.Add(lb);
                    break;

                case (DiffStatus.Modified, DiffStatus.Modified):
                    unified.Add(new DiffLine
                    {
                        OriginalLineNumber = la.OriginalLineNumber,
                        Text = la.Text,
                        Status = DiffStatus.Removed,
                        IsFromFileA = true,
                        Rejected = la.Rejected,
                        ChangedRegions = la.ChangedRegions
                    });
                    unified.Add(new DiffLine
                    {
                        OriginalLineNumber = lb.OriginalLineNumber,
                        Text = lb.Text,
                        Status = DiffStatus.Added,
                        IsFromFileA = false,
                        Rejected = lb.Rejected,
                        ChangedRegions = lb.ChangedRegions
                    });
                    break;

                // Defensive: unexpected pairing — emit whichever side isn't a Gap so no row is lost.
                default:
                    if (la.Status != DiffStatus.Gap) unified.Add(la);
                    if (lb.Status != DiffStatus.Gap && !ReferenceEquals(lb, la)) unified.Add(lb);
                    break;
            }
        }

        return unified;
    }

    /* ═══════════════ Sync-Point Partitioned Diff ═══════════════ */

    /// <summary>
    /// Computes the diff by partitioning into segments anchored at user-defined sync points.
    /// Each segment is diffed independently so that lines after a sync point are compared
    /// relative to that anchor, giving the user full control over alignment.
    /// </summary>
    private static (List<DiffLine> FileA, List<DiffLine> FileB, List<DiffLine> InterleavedA, List<DiffLine> InterleavedB)
    ComputeWithSyncPoints(
        List<string> aLines, List<string> bLines,
        bool ignoreWhitespace, bool useTimestamps,
        DiffProgress? reportProgress,
        List<(int aIdx, int bIdx)> syncPoints,
        FileModel? fileA, FileModel? fileB,
        bool computeInlineDiffs, DiffAlgorithm algorithm)
    {
        // In timestamp mode, if fileA is newer than fileB, swap so the older file
        // is treated as base. Also swap sync point indices to match.
        bool swapped = false;
        if (useTimestamps
            && fileA?.LastWriteTime.HasValue == true
            && fileB?.LastWriteTime.HasValue == true
            && fileA.LastWriteTime.Value > fileB.LastWriteTime.Value)
        {
            (fileA, fileB) = (fileB, fileA);
            (aLines, bLines) = (bLines, aLines);
            // Swap sync point indices: (aIdx, bIdx) → (bIdx, aIdx)
            syncPoints = syncPoints.Select(p => (p.bIdx, p.aIdx)).ToList();
            swapped = true;
        }

        // Sort sync points by file-A index and deduplicate
        var sorted = syncPoints
            .Distinct()
            .OrderBy(p => p.aIdx)
            .ThenBy(p => p.bIdx)
            .ToList();

        int aCount = aLines.Count;
        int bCount = bLines.Count;

        // Build segments: each segment is (aStart, aEnd, bStart, bEnd) — inclusive on both ends.
        // Segment i spans from just after sync point i-1 to sync point i (inclusive).
        // The trailing segment spans from just after the last sync point to EOF.
        var segments = new List<(int aStart, int aEnd, int bStart, int bEnd)>();

        for (int i = 0; i < sorted.Count; i++)
        {
            int aStart = (i == 0) ? 0 : sorted[i - 1].aIdx + 1;
            int bStart = (i == 0) ? 0 : sorted[i - 1].bIdx + 1;
            segments.Add((aStart, sorted[i].aIdx, bStart, sorted[i].bIdx));
        }

        // Trailing segment after last sync point (exclusive of the last sync point)
        {
            int aStart = sorted[^1].aIdx + 1;
            int bStart = sorted[^1].bIdx + 1;
            if (aStart < aCount || bStart < bCount)
                segments.Add((aStart, aCount - 1, bStart, bCount - 1));
        }

        // Precompute comparison keys and hashes once for the full arrays
        string[] aKeys = ignoreWhitespace ? aLines.Select(CharDiff.StripAllWhitespace).ToArray() : aLines.ToArray();
        string[] bKeys = ignoreWhitespace ? bLines.Select(CharDiff.StripAllWhitespace).ToArray() : bLines.ToArray();
        int[] aHashes = new int[aKeys.Length];
        int[] bHashes = new int[bKeys.Length];
        for (int i = 0; i < aKeys.Length; i++) aHashes[i] = aKeys[i].GetHashCode();
        for (int i = 0; i < bKeys.Length; i++) bHashes[i] = bKeys[i].GetHashCode();

        int totalLines = aKeys.Length + bKeys.Length;

        // Diff each segment independently and collect results
        var allResultA = new List<DiffLine>();
        var allResultB = new List<DiffLine>();
        var allInterleavedA = new List<DiffLine>();
        var allInterleavedB = new List<DiffLine>();

        for (int si = 0; si < segments.Count; si++)
        {
            var (aStart, aEnd, bStart, bEnd) = segments[si];
            int aLen = aEnd - aStart + 1;
            int bLen = bEnd - bStart + 1;

            // Extract subslices for this segment
            string[] segAKeys = new string[aLen];
            int[] segAHashes = new int[aLen];
            string[] segBKeys = new string[bLen];
            int[] segBHashes = new int[bLen];
            List<string> segALines = new(aLen);
            List<string> segBLines = new(bLen);

            for (int i = 0; i < aLen; i++)
            {
                segAKeys[i] = aKeys[aStart + i];
                segAHashes[i] = aHashes[aStart + i];
                segALines.Add(aLines[aStart + i]);
            }
            for (int i = 0; i < bLen; i++)
            {
                segBKeys[i] = bKeys[bStart + i];
                segBHashes[i] = bHashes[bStart + i];
                segBLines.Add(bLines[bStart + i]);
            }

            // Find matches within this segment
            var segMatches = new List<(int, int)>();
            FindMatches(segAKeys, segAHashes, segBKeys, segBHashes, segMatches, null, aLen + bLen, algorithm);
            segMatches.Sort((a, b) => a.Item1.CompareTo(b.Item1));

            // Build results with local indices, then offset to global
            var resA = BuildResult(segALines, segMatches, true, segBLines, computeInlineDiffs, ignoreWhitespace);
            var resB = BuildResult(segBLines, segMatches, false, segALines, computeInlineDiffs, ignoreWhitespace);
            var (intA, intB) = BuildInterleaved(segALines, segBLines, segMatches, computeInlineDiffs, ignoreWhitespace);

            // Offset all OriginalLineNumber values to global positions
            foreach (var l in resA) l.OriginalLineNumber += aStart;
            foreach (var l in resB) l.OriginalLineNumber += bStart;
            foreach (var l in intA) l.OriginalLineNumber += aStart;
            foreach (var l in intB) l.OriginalLineNumber += bStart;

            allResultA.AddRange(resA);
            allResultB.AddRange(resB);
            allInterleavedA.AddRange(intA);
            allInterleavedB.AddRange(intB);
        }

        // Report progress
        reportProgress?.Invoke(100.0, "Finalizing...");

        // Segments were diffed independently, but the concatenated interleaved pair is still
        // row-aligned across segment boundaries (each segment's lists are equal-length), so a
        // single full walk yields globally correct anchors.
        AnnotateAnchors(allResultA, allResultB, allInterleavedA, allInterleavedB);

        // If we swapped inputs for timestamp mode, return results in the original file order
        if (swapped)
            return (allResultB, allResultA, allInterleavedB, allInterleavedA);

        return (allResultA, allResultB, allInterleavedA, allInterleavedB);
    }

    /* ═══════════════ Match Finding — Standard (Myers) & Patience ═══════════════ */

    static void FindMatches(
        string[] a, int[] aHashes,
        string[] b, int[] bHashes,
        List<(int,int)> matches,
        DiffProgress? reportProgress, int totalLines,
        DiffAlgorithm algorithm = DiffAlgorithm.Standard)
    {
        int N = a.Length, M = b.Length;
        if (N == 0 || M == 0) return;

        // Canonicalize argument order so FindMatches(a,b) and FindMatches(b,a) always produce
        // transposed match sets. The core algorithms have tie-breaks that are not symmetric under
        // argument swap (Myers' first-overlapping-k selection, FallbackLCS backtrace preferring
        // "up", patience LIS reconstruction); without this, swapping sides could detect a different
        // alignment than the mirror of the original diff, making refresh-after-swap change the
        // displayed results. Both engines run on the same canonical order, so Compute(A,B) and
        // Compute(B,A) are exact mirrors for any input and engine.
        bool transposed = false;
        if (!IsCanonicalFirst(a, b))
        {
            (a, b) = (b, a);
            (aHashes, bHashes) = (bHashes, aHashes);
            transposed = true;
            N = a.Length; M = b.Length;
        }

        var stack = new ValueStack(64);
        stack.Push(0, N, 0, M);

        // Shared buffers — allocated once and reused across all subproblems. fwdM/bwdM are the
        // depth-monotone envelopes: fwdM[k] = max over depths e <= current of V_e[k]. By the DPA
        // structure (both predecessor intervals share bottom max(0,k), so their union stays
        // contiguous), every point on diagonal k with A-position t in [max(0,k), fwdM[k]] is
        // reachable from (0,0) within the current depth's budget — and mirror for bwdM.
        int[] fwd = null!, fwdM = null!;
        int[] bwd = null!, bwdM = null!;

        // Patience: shared counting dictionaries + growable anchor arrays, reused per subproblem.
        var countA = new Dictionary<string, int>();
        var posB = new Dictionary<string, int>();
        int[] anchorA = Array.Empty<int>();
        int[] anchorB = Array.Empty<int>();

        // Patience work budget: total line-visits across all patience steps. Bounds the worst case
        // (e.g. fully reversed files, where anchors peel one line per level) to O(k·(N+M)) dict
        // work before remaining subproblems fall back to Myers.
        long patienceBudget = (long)(N + M) * 16;

        int resolvedLines = 0;
        int frameCount = 0;

        while (stack.TryPop(out int aS, out int aE, out int bS, out int bE))
        {
            int n = aE - aS;
            int m = bE - bS;

            if (n == 0 || m == 0) continue;

            // ── Strip common prefix and suffix (hash-first comparison) ──
            int p = 0;
            while (p < Math.Min(n, m) && Eq(aHashes[aS + p], bHashes[bS + p], a[aS + p], b[bS + p])) p++;

            int s = 0;
            while (s < Math.Min(n - p, m - p) && Eq(aHashes[aE - 1 - s], bHashes[bE - 1 - s], a[aE - 1 - s], b[bE - 1 - s])) s++;

            // Record prefix and suffix matches (sorted later in Compute())
            for (int i = 0; i < p; i++) matches.Add((aS + i, bS + i));
            for (int i = 0; i < s; i++) matches.Add((aE - s + i, bE - s + i));

            // Adjust subproblem bounds
            aS += p; aE -= s; bS += p; bE -= s;
            n = aE - aS; m = bE - bS;

            if (n == 0 || m == 0) continue;

            // ── Fast path: identical sequences (hash-first) ──
            if (n == m)
            {
                bool allMatch = true;
                for (int i = 0; i < n; i++)
                    if (!Eq(aHashes[aS + i], bHashes[bS + i], a[aS + i], b[bS + i])) { allMatch = false; break; }
                if (allMatch)
                {
                    for (int i = 0; i < n; i++) matches.Add((aS + i, bS + i));
                    continue;
                }
            }

            // ── Fast path: single-line subproblems (hash-first) ──
            if (n == 1)
            {
                for (int j = 0; j < m; j++)
                    if (Eq(aHashes[aS], bHashes[bS + j], a[aS], b[bS + j])) { matches.Add((aS, bS + j)); goto next; }
                next: continue;
            }

            if (m == 1)
            {
                for (int i = 0; i < n; i++)
                    if (Eq(aHashes[aS + i], bHashes[bS], a[aS + i], b[bS])) { matches.Add((aS + i, bS)); goto next2; }
                next2: continue;
            }

            // ── Algorithm dispatch ──
            // Patience anchors on lines unique to both sub-ranges (guaranteed match points) and
            // recurses between them; Myers handles ranges without unique lines, tiny ranges, and
            // anything left once the patience budget is spent.
            bool handled = false;
            if (algorithm == DiffAlgorithm.Patience && n + m > 8 && patienceBudget >= n + m)
            {
                patienceBudget -= n + m;
                handled = PatienceStep(a, aHashes, b, bHashes, aS, aE, bS, bE, ref stack, matches, countA, posB, ref anchorA, ref anchorB);
            }
            if (!handled)
                MyersCore(a, aHashes, b, bHashes, aS, aE, bS, bE, ref stack, matches, ref fwd, ref fwdM, ref bwd, ref bwdM);

            // Track progress: count lines resolved from this subproblem
            resolvedLines += n + m;
            frameCount++;

            // Report progress every 8 frames to avoid excessive UI updates
            if (frameCount % 8 == 0 && reportProgress != null)
            {
                double pct = Math.Min(95.0, (double)resolvedLines / totalLines * 100.0);
                reportProgress(pct, $"Computing diff... ({pct:F0}%)");
            }
        }

        if (reportProgress != null)
            reportProgress(100.0, "Finalizing...");

        // Map matches back from canonical coordinates to the caller's argument order.
        if (transposed)
            for (int i = 0; i < matches.Count; i++)
            {
                var (x, y) = matches[i];
                matches[i] = (y, x);
            }
    }

    /// <summary>
    /// Myers' bidirectional search for the middle snake of one sub-range (hash-first).
    /// On overlap, splits into two smaller sub-problems pushed onto <paramref name="stack"/>.
    /// Falls back to O(N×M) LCS if no overlap is found within maxD steps.
    /// </summary>
    static void MyersCore(
        string[] a, int[] aHashes,
        string[] b, int[] bHashes,
        int aS, int aE, int bS, int bE,
        ref ValueStack stack, List<(int,int)> matches,
        ref int[] fwd, ref int[] fwdM, ref int[] bwd, ref int[] bwdM)
    {
        int n = aE - aS;
        int m = bE - bS;

        int maxD = n + m;
        int off = maxD + 2;
        int bufSize = off * 2;

        // Grow shared buffers if needed
        if (fwd == null || fwd.Length < bufSize) { fwd = new int[bufSize]; fwdM = new int[bufSize]; }
        if (bwd == null || bwd.Length < bufSize) { bwd = new int[bufSize]; bwdM = new int[bufSize]; }

        // The envelopes are per-call state: reset to "unreachable" before this subproblem's
        // depth loop starts accumulating.
        Array.Fill(fwdM, -1);
        Array.Fill(bwdM, -1);

        bool found = false;
        for (int d = 0; d < maxD && !found; d++)
        {
            // ── Forward step ──
            for (int k = -d; k <= d; k += 2)
            {
                // Boundary diagonal k == -d is reached via an insert from k+1 at depth d-1, so x
                // carries over unchanged (0 only at d=0). Forcing x = 0 here drops every match on
                // the edge diagonal and silently loses matches whenever the optimal path hugs it.
                int x = (k == -d) ? (d == 0 ? 0 : fwd[k + 1 + off]) :
                        (k ==  d) ? fwd[k - 1 + off] + 1 :
                                    Math.Max(fwd[k - 1 + off] + 1, fwd[k + 1 + off]);

                int y = x - k;
                while (x < n && y < m && Eq(aHashes[aS + x], bHashes[bS + y], a[aS + x], b[bS + y])) { x++; y++; }
                fwd[k + off] = x;
                if (x > fwdM[k + off]) fwdM[k + off] = x; // depth-monotone envelope
            }

            // ── Odd-total overlap check: forward depth d vs backward depth d-1 (total 2d-1).
            // Must run BEFORE this iteration's backward step, while bwd still holds depth d-1.
            // Checking the odd total first pins the edit distance exactly (L = 2d-1) before the
            // even check (L = 2d) can fire; splitting on a 2d meeting when L is 2d-1 loses a match.
            // Only meaningful when n+m is odd (then every edit distance of this sub-range is odd).
            if (d >= 1 && ((n + m) & 1) == 1 &&
                TrySplit(aS, aE, bS, bE, n, m, d, d - 1, off, ref stack, fwdM, bwdM))
                found = true;

            // ── Backward step ──
            for (int k = -d; k <= d; k += 2)
            {
                // Mirror of the forward boundary fix: k == -d carries x over from k+1 at depth d-1.
                int x = (k == -d) ? (d == 0 ? 0 : bwd[k + 1 + off]) :
                        (k ==  d) ? bwd[k - 1 + off] + 1 :
                                    Math.Max(bwd[k - 1 + off] + 1, bwd[k + 1 + off]);

                int y = x - k;
                while (x < n && y < m && Eq(aHashes[aS + n - 1 - x], bHashes[bS + m - 1 - y], a[aS + n - 1 - x], b[bS + m - 1 - y])) { x++; y++; }
                bwd[k + off] = x;
                if (x > bwdM[k + off]) bwdM[k + off] = x; // depth-monotone envelope
            }

            // ── Even-total overlap check: forward depth d vs backward depth d (total 2d) ──
            if (!found && TrySplit(aS, aE, bS, bE, n, m, d, d, off, ref stack, fwdM, bwdM))
                found = true;
        }

        if (!found)
            FallbackLCS(a, aHashes, aS, aE, b, bHashes, bS, bE, matches);
    }

    /// <summary>
    /// Scans the forward diagonals at depth d for a meeting point with the backward search at
    /// depth backD (total edits d + backD). A forward index k sits on actual diagonal k; a
    /// backward index kb sits on actual diagonal (n - m) - kb, so they can only meet at
    /// kb = n - m - k.
    ///
    /// The points reachable from an end within its edit budget form one interval per diagonal:
    /// forward [max(0,k), fwdM[k]] in A-position t (point (t, t-k)), backward
    /// [n - bwdM[kb], n - max(0,kb)]. Proof sketch: at depth e the reachable set on diagonal k
    /// is the union of the two predecessor-diagonal intervals shifted by one edit plus this
    /// depth's final snake; every piece has bottom max(0,k) (the edit-only corner path), so the
    /// union is exactly [max(0,k), V_e[k]] — and fwdM/bwdM take the running max over depths,
    /// which keeps it exact under "within budget" (≤ d) semantics. Intersecting the two
    /// intervals, clamped to the in-grid part of diagonal k, gives the valid split region: any
    /// t in it has dist_f(t) ≤ d and dist_b(t) ≤ backD, so dist(whole) ≤ d + backD = total.
    /// Because totals are checked in increasing order (0, 1, 2, ...), the first non-empty
    /// region sits on a shortest path — total = D*, and both halves then solve to exactly their
    /// budgets (a balanced split point of any optimal path is always within budget at total
    /// D*, so no smaller total can be the first fire). Returns true if a split was pushed.
    /// </summary>
    static bool TrySplit(
        int aS, int aE, int bS, int bE, int n, int m,
        int d, int backD, int off,
        ref ValueStack stack,
        int[] fwdM, int[] bwdM)
    {
        for (int k = -d; k <= d; k += 2)
        {
            int kb = n - m - k;
            if (kb < -backD || kb > backD) continue; // backward search hasn't reached this diagonal yet

            int lo = Math.Max(Math.Max(0, k), n - bwdM[kb + off]);
            int hi = Math.Min(fwdM[k + off], n - Math.Max(0, kb));

            // Clamp to the in-grid part of diagonal k (t ∈ [max(0,k), min(n, m+k)]); this also
            // skips frontier values that overshot out of the grid.
            if (hi > n) hi = n;
            if (hi > m + k) hi = m + k;
            if (lo > hi) continue; // no within-budget meeting point on this diagonal

            int xm = (lo + hi) / 2;
            int ym = xm - k;

            // Boundary splits are fine (the empty half is skipped by the caller); only reject a
            // split that would make no progress at all.
            if ((xm == 0 && ym == 0) || (xm == n && ym == m)) continue;

            stack.Push(aS + xm, aE, bS + ym, bE);
            stack.Push(aS, aS + xm, bS, bS + ym);
            return true;
        }
        return false;
    }

    /// <summary>
    /// One patience step on a sub-range: find lines that occur exactly once in BOTH the A and B
    /// sub-ranges (such a line must match its counterpart in any alignment), keep the largest
    /// non-crossing subset via LIS over their B positions, emit those matches, and push the
    /// segments between consecutive anchors back onto <paramref name="stack"/> for recursive
    /// processing. Returns false when no unique common lines exist — the caller then falls back
    /// to Myers on the whole range. All emitted matches are monotone (non-crossing).
    /// </summary>
    static bool PatienceStep(
        string[] a, int[] aHashes,
        string[] b, int[] bHashes,
        int aS, int aE, int bS, int bE,
        ref ValueStack stack, List<(int,int)> matches,
        Dictionary<string, int> countA, Dictionary<string, int> posB,
        ref int[] anchorA, ref int[] anchorB)
    {
        // Lines that occur exactly once in the A sub-range (with occurrence counts).
        countA.Clear();
        for (int i = aS; i < aE; i++)
        {
            string k = a[i];
            countA[k] = countA.TryGetValue(k, out int c) ? c + 1 : 1;
        }

        // Lines that occur exactly once in the B sub-range → their position.
        // First occurrence records its index; any later occurrence marks the key -1 so only
        // truly unique lines (exactly one occurrence) survive. (Insert-then-remove would leave
        // values with an odd count ≥ 3 behind as "unique".)
        posB.Clear();
        for (int j = bS; j < bE; j++)
        {
            string k = b[j];
            if (!posB.ContainsKey(k)) posB[k] = j;
            else posB[k] = -1;
        }

        // Collect anchors in A order: unique-in-A lines that are also unique in B.
        int kCount = 0;
        for (int i = aS; i < aE; i++)
        {
            string k = a[i];
            // countA[k] == 1: unique in A. posB value >= 0: unique in B (-1 marks duplicates).
            if (countA[k] == 1 && posB.TryGetValue(k, out int bj) && bj >= 0)
            {
                if (kCount >= anchorA.Length)
                {
                    int newLen = Math.Max(8, anchorA.Length * 2);
                    var grownA = new int[newLen];
                    Array.Copy(anchorA, grownA, anchorA.Length);
                    anchorA = grownA;
                    var grownB = new int[newLen];
                    Array.Copy(anchorB, grownB, anchorB.Length);
                    anchorB = grownB;
                }
                anchorA[kCount] = i;
                anchorB[kCount] = bj;
                kCount++;
            }
        }

        if (kCount == 0) return false;

        // LIS over B positions (anchors are already in A order) → largest non-crossing set.
        // tails[len] = smallest B position ending an increasing run of length len+1.
        var tails = new int[kCount];
        var tailIdx = new int[kCount];
        var prev = new int[kCount];
        int tLen = 0;
        for (int i = 0; i < kCount; i++)
        {
            int bp = anchorB[i];
            int lo = 0, hi = tLen;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (tails[mid] < bp) lo = mid + 1;
                else hi = mid;
            }
            tails[lo] = bp;
            tailIdx[lo] = i;
            prev[i] = lo > 0 ? tailIdx[lo - 1] : -1;
            if (lo == tLen) tLen++;
        }

        // Reconstruct the chosen anchors (walk predecessors back from the longest run's tail).
        var chosen = new int[tLen];
        for (int idx = tailIdx[tLen - 1], c = tLen - 1; idx >= 0; idx = prev[idx])
            chosen[c--] = idx;

        // Emit anchor matches and push the segments between consecutive anchors.
        int segA = aS, segB = bS;
        for (int c = 0; c < tLen; c++)
        {
            int ca = anchorA[chosen[c]];
            int cb = anchorB[chosen[c]];
            if (ca > segA || cb > segB) stack.Push(segA, ca, segB, cb);
            matches.Add((ca, cb));
            segA = ca + 1;
            segB = cb + 1;
        }
        if (segA < aE || segB < bE) stack.Push(segA, aE, segB, bE);

        return true;
    }

    /// <summary>
    /// Returns true when <paramref name="a"/> should be the first argument in canonical order.
    /// Length first, then ordinal lexicographic — a total order, so exactly one of
    /// IsCanonicalFirst(a,b) / IsCanonicalFirst(b,a) is false for distinct sequences.
    /// </summary>
    static bool IsCanonicalFirst(string[] a, string[] b)
    {
        if (a.Length != b.Length) return a.Length < b.Length;
        for (int i = 0; i < a.Length; i++)
        {
            int c = string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return c < 0;
        }
        return true; // identical sequences — either order is canonical
    }

    /// <summary>
    /// Hash-first equality: int comparison first, full string fallback only on collision.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool Eq(int ha, int hb, string sa, string sb) => ha == hb && sa == sb;

    struct Frame
    {
        public int AStart, AEnd, BStart, BEnd;
        public Frame(int aS, int aE, int bS, int bE) => (AStart, AEnd, BStart, BEnd) = (aS, aE, bS, bE);
    }

    struct ValueStack
    {
        private Frame[] _data;
        private int _top;

        public ValueStack(int capacity) { _data = new Frame[capacity]; _top = 0; }

        public void Push(int aS, int aE, int bS, int bE)
        {
            if (_top >= _data.Length)
            {
                var n = new Frame[_data.Length * 2];
                Array.Copy(_data, n, _data.Length);
                _data = n;
            }
            _data[_top++] = new Frame(aS, aE, bS, bE);
        }

        public bool TryPop(out int aS, out int aE, out int bS, out int bE)
        {
            if (_top == 0) { aS = aE = bS = bE = 0; return false; }
            var f = _data[--_top];
            aS = f.AStart; aE = f.AEnd; bS = f.BStart; bE = f.BEnd;
            return true;
        }
    }

    /// <summary>
    /// Exact O(N×M) LCS via Hirschberg's divide and conquer — O(min(N,M)) space, so no size cap.
    /// (The old DP table silently dropped ALL matches above 250k cells, which then made
    /// BuildResult pair the entire file positionally and report nearly every line as Modified.)
    /// Only reached when MyersCore finds no valid interior split — essentially inputs with no
    /// interior common structure.
    /// </summary>
    static void FallbackLCS(string[] a, int[] aHashes, int aS, int aE, string[] b, int[] bHashes, int bS, int bE, List<(int,int)> matches)
    {
        int N = aE - aS, M = bE - bS;
        if (N == 0 || M == 0) return;

        // Run the DP along the longer side so the score arrays are sized by the shorter one.
        bool flip = false;
        if (M > N)
        {
            (a, b) = (b, a);
            (aHashes, bHashes) = (bHashes, aHashes);
            (aS, aE, bS, bE) = (bS, bE, aS, aE);
            (N, M) = (M, N);
            flip = true;
        }

        HirschbergRec(a, aHashes, aS, aE, b, bHashes, bS, bE, matches, flip);
    }

    static void HirschbergRec(
        string[] a, int[] aHashes, int aS, int aE,
        string[] b, int[] bHashes, int bS, int bE,
        List<(int,int)> matches, bool flip)
    {
        int N = aE - aS, M = bE - bS;

        if (N == 0 || M == 0) return;

        // Single-line base case: match the first equal line in B (deterministic tie-break).
        if (N == 1)
        {
            for (int j = 0; j < M; j++)
                if (Eq(aHashes[aS], bHashes[bS + j], a[aS], b[bS + j]))
                {
                    matches.Add(flip ? (bS + j, aS) : (aS, bS + j));
                    return;
                }
            return;
        }

        int mid = N / 2;

        // fwd[j] = LCS length of A[aS..aS+mid) vs B[bS..bS+j).
        var fwd = new int[M + 1];
        for (int i = 0; i < mid; i++)
        {
            int diag = 0;
            for (int j = 1; j <= M; j++)
            {
                int up = fwd[j];
                if (Eq(aHashes[aS + i], bHashes[bS + j - 1], a[aS + i], b[bS + j - 1]))
                    fwd[j] = diag + 1;
                else if (fwd[j - 1] > up)
                    fwd[j] = fwd[j - 1];
                diag = up;
            }
        }

        // bwd[j] = LCS length of A[aS+mid..aE) vs B[bS+j..bE).
        var bwd = new int[M + 1];
        for (int i = N - 1; i >= mid; i--)
        {
            int diag = 0;
            for (int j = M - 1; j >= 0; j--)
            {
                int up = bwd[j];
                if (Eq(aHashes[aS + i], bHashes[bS + j], a[aS + i], b[bS + j]))
                    bwd[j] = diag + 1;
                else if (bwd[j + 1] > up)
                    bwd[j] = bwd[j + 1];
                diag = up;
            }
        }

        // Split at the B position maximizing fwd[j] + bwd[j] (smallest j on ties).
        int bestJ = 0, bestScore = -1;
        for (int j = 0; j <= M; j++)
        {
            int score = fwd[j] + bwd[j];
            if (score > bestScore) { bestScore = score; bestJ = j; }
        }

        bool onDiag = bestJ > 0 && Eq(aHashes[aS + mid - 1], bHashes[bS + bestJ - 1], a[aS + mid - 1], b[bS + bestJ - 1]);
        if (onDiag) matches.Add(flip ? (bS + bestJ - 1, aS + mid - 1) : (aS + mid - 1, bS + bestJ - 1));

        HirschbergRec(a, aHashes, aS, aS + mid - (onDiag ? 1 : 0), b, bHashes, bS, bS + bestJ - (onDiag ? 1 : 0), matches, flip);
        HirschbergRec(a, aHashes, aS + mid, aE, b, bHashes, bS + bestJ, bE, matches, flip);
    }

    /* ═══════════════ Build Interleaved (Gap) Results ═══════════════ */

    /// <summary>
    /// Walk the sorted match pairs and produce two equal-length, row-aligned lists.
    /// Where a line exists in one file but not the other, the opposite side gets a Gap entry
    /// so both panels display synchronized rows with visually distinct gap placeholders.
    /// </summary>
    private static (List<DiffLine> A, List<DiffLine> B) BuildInterleaved(List<string> aLines, List<string> bLines, List<(int,int)> matches, bool computeInlineDiffs, bool ignoreWhitespace = false)
    {
        var listA = new List<DiffLine>();
        var listB = new List<DiffLine>();

        int aIdx = 0, bIdx = 0;

        // Helper: emit unmatched block between positions (aFrom..aTo) and (bFrom..bTo)
        void EmitBlock(int aFrom, int aTo, int bFrom, int bTo)
        {
            int uA = aTo - aFrom; // lines only in A (or paired as mods)
            int uB = bTo - bFrom; // lines only in B (or paired as mods)

            // Pair up as many modifications as possible
            int mods = Math.Min(uA, uB);
            for (int i = 0; i < mods; i++)
            {
                string textA = aLines[aFrom + i];
                string textB = bLines[bFrom + i];

                // If lines are actually identical (or only differ by whitespace when ignoring), mark as Unchanged
                DiffStatus status;
                if (textA == textB)
                    status = DiffStatus.Unchanged;
                else if (ignoreWhitespace && CharDiff.StripAllWhitespace(textA) == CharDiff.StripAllWhitespace(textB))
                    status = DiffStatus.Unchanged;
                else
                    status = DiffStatus.Modified;

                var lineA = new DiffLine
                {
                    OriginalLineNumber = aFrom + i + 1,
                    Text = textA,
                    Status = status,
                    IsFromFileA = true,
                    OriginalText = textB
                };
                var lineB = new DiffLine
                {
                    OriginalLineNumber = bFrom + i + 1,
                    Text = textB,
                    Status = status,
                    IsFromFileA = false,
                    OriginalText = textA
                };

                if (status == DiffStatus.Modified && computeInlineDiffs)
                {
                    lineA.ChangedRegions = CharDiff.ComputeChangedRegions(textB, textA, ignoreWhitespace);
                    lineB.ChangedRegions = CharDiff.ComputeChangedRegions(textA, textB, ignoreWhitespace);
                }

                listA.Add(lineA);
                listB.Add(lineB);
            }

            // Remaining A-only lines → Removed on A, Gap on B
            for (int i = mods; i < uA; i++)
            {
                listA.Add(new DiffLine { OriginalLineNumber = aFrom + i + 1, Text = aLines[aFrom + i], Status = DiffStatus.Removed, IsFromFileA = true });
                listB.Add(new DiffLine { OriginalLineNumber = 0, Text = "", Status = DiffStatus.Gap, IsFromFileA = false });
            }

            // Remaining B-only lines → Gap on A, Added on B
            for (int i = mods; i < uB; i++)
            {
                listA.Add(new DiffLine { OriginalLineNumber = 0, Text = "", Status = DiffStatus.Gap, IsFromFileA = true });
                listB.Add(new DiffLine { OriginalLineNumber = bFrom + i + 1, Text = bLines[bFrom + i], Status = DiffStatus.Added, IsFromFileA = false });
            }
        }

        foreach (var (mA, mB) in matches)
        {
            // Emit unmatched block before this match
            if (aIdx < mA || bIdx < mB)
                EmitBlock(aIdx, mA, bIdx, mB);

            // Emit the matched line as unchanged on both sides
            listA.Add(new DiffLine { OriginalLineNumber = mA + 1, Text = aLines[mA], Status = DiffStatus.Unchanged, IsFromFileA = true });
            listB.Add(new DiffLine { OriginalLineNumber = mB + 1, Text = bLines[mB], Status = DiffStatus.Unchanged, IsFromFileA = false });

            aIdx = mA + 1;
            bIdx = mB + 1;
        }

        // Emit trailing unmatched block after last match
        int aCount = aLines.Count;
        int bCount = bLines.Count;
        if (aIdx < aCount || bIdx < bCount)
            EmitBlock(aIdx, aCount, bIdx, bCount);

        return (listA, listB);
    }

    /* ═══════════════ Build Results ═══════════════ */

    private static List<DiffLine> BuildResult(List<string> lines, List<(int,int)> matches, bool isFileA, List<string>? otherLines = null, bool computeInlineDiffs = true, bool ignoreWhitespace = false)
    {
        int count = lines.Count;
        var result = new DiffLine[count];

        for (int i = 0; i < count; i++)
            result[i] = new DiffLine { OriginalLineNumber = i + 1, Text = lines[i], Status = DiffStatus.Unchanged, IsFromFileA = isFileA };

        // bool[] marking — no boxing, no hashing, direct O(1) index access
        bool[] matched = new bool[count];
        foreach (var m in matches)
        {
            int idx = isFileA ? m.Item1 : m.Item2;
            if (idx >= 0 && idx < count) matched[idx] = true;
        }

        DiffStatus def = isFileA ? DiffStatus.Removed : DiffStatus.Added;
        for (int i = 0; i < count; i++)
            if (!matched[i]) result[i].Status = def;

        int aCount = isFileA ? count : otherLines?.Count ?? 0;
        int bCount = isFileA ? otherLines?.Count ?? 0 : count;

        // Pair an unmatched block as modifications (requires sorted matches). Leading, middle
        // and trailing blocks are all paired so the plain lists classify every line identically
        // to the interleaved (gap) lists — toggling ShowGaps must not change the diff.
        void PairBlock(int aLo, int bLo, int uA, int uB)
        {
            if (otherLines == null || uA <= 0 || uB <= 0) return;
            int pairs = Math.Min(uA, uB);
            for (int p = 0; p < pairs; p++)
            {
                int idxA = aLo + p;
                int idxB = bLo + p;
                int idx = isFileA ? idxA : idxB;
                if (idx < 0 || idx >= count) continue;
                result[idx].Status = DiffStatus.Modified;

                // Store paired text and compute changed regions if other file's lines are available.
                // The paired index into the OTHER file is idxA when building B, idxB when building A —
                // using idxB for both corrupts file B's OriginalText/ChangedRegions whenever the
                // blocks are offset (a0 != b0), which then breaks side swaps and refreshes.
                int theirIdx = isFileA ? idxB : idxA;
                if (theirIdx < otherLines.Count)
                {
                    string ourText = lines[idx];
                    string theirText = otherLines[theirIdx];
                    result[idx].OriginalText = theirText;

                    // If actually identical (or only differ by whitespace when ignoring), demote to Unchanged
                    if (ourText == theirText || (ignoreWhitespace && CharDiff.StripAllWhitespace(ourText) == CharDiff.StripAllWhitespace(theirText)))
                    {
                        result[idx].Status = DiffStatus.Unchanged;
                    }
                    else if (computeInlineDiffs)
                    {
                        result[idx].ChangedRegions = CharDiff.ComputeChangedRegions(theirText, ourText, ignoreWhitespace);
                    }
                }
            }
        }

        if (matches.Count == 0)
        {
            // Whole file is one unmatched block
            PairBlock(0, 0, aCount, bCount);
        }
        else
        {
            // Leading block before first match
            var (la, lb) = matches[0];
            PairBlock(0, 0, la, lb);

            // Middle blocks between consecutive matches
            for (int g = 0; g < matches.Count - 1; g++)
            {
                var (a0, b0) = matches[g];
                var (a1, b1) = matches[g + 1];
                PairBlock(a0 + 1, b0 + 1, a1 - a0 - 1, b1 - b0 - 1);
            }

            // Trailing block after last match
            var (ta, tb) = matches[^1];
            PairBlock(ta + 1, tb + 1, aCount - ta - 1, bCount - tb - 1);
        }

        return result.ToList();
    }

 }
