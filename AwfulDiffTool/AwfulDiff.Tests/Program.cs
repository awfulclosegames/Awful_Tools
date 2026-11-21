using System;
using System.Diagnostics;
using System.IO;   // not in this project's implicit usings (WPF) — needed by the directory-diff tests
using System.Threading;
using AwfulDiff.Models;
using AwfulDiff.Services;

// Quick smoke test for DiffEngine — runs various scenarios and reports results
class TestDiff
{
    static int passed = 0, failed = 0;

    static void Main()
    {
        Run("empty vs empty", () =>
        {
            var (a, b, _, _) = DiffEngine.Compute(null, null);
            if (a.Count != 0 || b.Count != 0) throw new Exception($"Expected 0,0 got {a.Count},{b.Count}");
        });

        Run("empty vs content", () =>
        {
            var right = new FileModel { FilePath = "b.txt", Lines = new System.Collections.Generic.List<string> { "line1", "line2", "line3" } };
            var (a, b, _, _) = DiffEngine.Compute(null, right);
            if (a.Count != 0 || b.Count != 3) throw new Exception($"Expected 0,3 got {a.Count},{b.Count}");
        });

        Run("content vs empty", () =>
        {
            var left = new FileModel { FilePath = "a.txt", Lines = new System.Collections.Generic.List<string> { "x", "y" } };
            var (a, b, _, _) = DiffEngine.Compute(left, null);
            if (a.Count != 2 || b.Count != 0) throw new Exception($"Expected 2,0 got {a.Count},{b.Count}");
        });

        Run("identical files", () =>
        {
            var lines = new System.Collections.Generic.List<string> { "same1", "same2", "same3" };
            var a = new FileModel { FilePath = "a.txt", Lines = new System.Collections.Generic.List<string>(lines) };
            var b = new FileModel { FilePath = "b.txt", Lines = new System.Collections.Generic.List<string>(lines) };
            var (ra, rb, _, _) = DiffEngine.Compute(a, b);
            bool ok = true;
            foreach (var l in ra) if (l.Status != DiffStatus.Unchanged) ok = false;
            foreach (var l in rb) if (l.Status != DiffStatus.Unchanged) ok = false;
            if (!ok) throw new Exception("Expected all Unchanged");
        });

        Run("completely different", () =>
        {
            var a = new FileModel { FilePath = "a.txt", Lines = new System.Collections.Generic.List<string> { "aaa", "bbb" } };
            var b = new FileModel { FilePath = "b.txt", Lines = new System.Collections.Generic.List<string> { "xxx", "yyy", "zzz" } };
            var (ra, rb, _, _) = DiffEngine.Compute(a, b);
            // No matches: the whole file is one unmatched block — min(2,3) lines pair as Modified,
            // the extra B line stays Added (same classification as the gap view)
            if (ra[0].Status != DiffStatus.Modified) throw new Exception($"ra[0] should be Modified, got {ra[0].Status}");
            if (ra[1].Status != DiffStatus.Modified) throw new Exception($"ra[1] should be Modified, got {ra[1].Status}");
            if (rb[0].Status != DiffStatus.Modified) throw new Exception($"rb[0] should be Modified, got {rb[0].Status}");
            if (rb[1].Status != DiffStatus.Modified) throw new Exception($"rb[1] should be Modified, got {rb[1].Status}");
            if (rb[2].Status != DiffStatus.Added) throw new Exception($"rb[2] should be Added, got {rb[2].Status}");
        });

        Run("partial match", () =>
        {
            var a = new FileModel { FilePath = "a.txt", Lines = new System.Collections.Generic.List<string> { "keep", "remove", "keep2" } };
            var b = new FileModel { FilePath = "b.txt", Lines = new System.Collections.Generic.List<string> { "keep", "add", "keep2" } };
            var (ra, rb, _, _) = DiffEngine.Compute(a, b);
            // keep/keep2 Unchanged; a 1:1 replacement pair is shown as Modified on both sides
            if (ra[0].Status != DiffStatus.Unchanged) throw new Exception("ra[0] should be Unchanged");
            if (ra[1].Status != DiffStatus.Modified) throw new Exception($"ra[1] should be Modified, got {ra[1].Status}");
            if (ra[2].Status != DiffStatus.Unchanged) throw new Exception("ra[2] should be Unchanged");
            if (rb[0].Status != DiffStatus.Unchanged) throw new Exception("rb[0] should be Unchanged");
            if (rb[1].Status != DiffStatus.Modified) throw new Exception($"rb[1] should be Modified, got {rb[1].Status}");
            if (rb[2].Status != DiffStatus.Unchanged) throw new Exception("rb[2] should be Unchanged");
        });

        Run("large identical (5000 lines)", () =>
        {
            var lines = new System.Collections.Generic.List<string>();
            for (int i = 0; i < 5000; i++) lines.Add($"line{i}");
            var a = new FileModel { FilePath = "a.txt", Lines = new System.Collections.Generic.List<string>(lines) };
            var b = new FileModel { FilePath = "b.txt", Lines = new System.Collections.Generic.List<string>(lines) };

            var sw = Stopwatch.StartNew();
            var (ra, rb, _, _) = DiffEngine.Compute(a, b);
            sw.Stop();

            bool ok = true;
            foreach (var l in ra) if (l.Status != DiffStatus.Unchanged) ok = false;
            if (!ok) throw new Exception("Expected all Unchanged");
            Console.WriteLine($"  ({sw.ElapsedMilliseconds}ms)");
        });

        Run("large different (5000 vs 5000, last line differs)", () =>
        {
            var linesA = new System.Collections.Generic.List<string>();
            var linesB = new System.Collections.Generic.List<string>();
            for (int i = 0; i < 5000; i++)
            {
                linesA.Add($"line{i}");
                linesB.Add(i == 4999 ? "CHANGED" : $"line{i}");
            }
            var a = new FileModel { FilePath = "a.txt", Lines = linesA };
            var b = new FileModel { FilePath = "b.txt", Lines = linesB };

            var sw = Stopwatch.StartNew();
            var (ra, rb, _, _) = DiffEngine.Compute(a, b);
            sw.Stop();

            int changedA = 0, changedB = 0;
            foreach (var l in ra) if (l.Status != DiffStatus.Unchanged) changedA++;
            foreach (var l in rb) if (l.Status != DiffStatus.Unchanged) changedB++;
            if (changedA != 1 || changedB != 1)
                throw new Exception($"Expected 1 change each, got {changedA},{changedB}");
            Console.WriteLine($"  ({sw.ElapsedMilliseconds}ms)");
        });

        Run("large no match (5000 vs 5000, all different)", () =>
        {
            var linesA = new System.Collections.Generic.List<string>();
            var linesB = new System.Collections.Generic.List<string>();
            for (int i = 0; i < 5000; i++)
            {
                linesA.Add($"A{i}");
                linesB.Add($"B{i}");
            }
            var a = new FileModel { FilePath = "a.txt", Lines = linesA };
            var b = new FileModel { FilePath = "b.txt", Lines = linesB };

            var sw = Stopwatch.StartNew();
            var (ra, rb, _, _) = DiffEngine.Compute(a, b);
            sw.Stop();

            // No matches: whole file is one unmatched block; equal lengths → every line pairs as Modified
            bool ok = true;
            foreach (var l in ra) if (l.Status != DiffStatus.Modified) ok = false;
            foreach (var l in rb) if (l.Status != DiffStatus.Modified) ok = false;
            if (!ok) throw new Exception("Expected all Modified");
            Console.WriteLine($"  ({sw.ElapsedMilliseconds}ms)");
        });

        // Regression: a line removed at the top and an unrelated line added at the bottom must
        // show as one Removed + one Added with everything else Unchanged — NOT the whole file
        // Modified. (The old capped DP fallback silently dropped all matches above 250k cells,
        // so BuildResult paired the files positionally and marked nearly every line Modified.)
        Run("standard: offset ends — removed at top, added at bottom", () =>
        {
            void Check(int shared)
            {
                var a = new List<string> { "HEADER-A" };
                var b = new List<string>();
                for (int i = 0; i < shared; i++) { a.Add($"line{i}"); b.Add($"line{i}"); }
                b.Add("FOOTER-B");
                var fa = new FileModel { FilePath = "a.txt", Lines = a };
                var fb = new FileModel { FilePath = "b.txt", Lines = b };

                var sw = Stopwatch.StartNew();
                var (ra, rb, _, _) = DiffEngine.Compute(fa, fb, computeInlineDiffs: true, algorithm: DiffAlgorithm.Standard);
                sw.Stop();

                int uncA = 0, modA = 0, remA = 0, addA = 0;
                foreach (var l in ra) switch (l.Status) { case DiffStatus.Unchanged: uncA++; break; case DiffStatus.Modified: modA++; break; case DiffStatus.Removed: remA++; break; case DiffStatus.Added: addA++; break; }
                int uncB = 0, modB = 0, remB = 0, addB = 0;
                foreach (var l in rb) switch (l.Status) { case DiffStatus.Unchanged: uncB++; break; case DiffStatus.Modified: modB++; break; case DiffStatus.Removed: remB++; break; case DiffStatus.Added: addB++; break; }

                if (uncA != shared || modA != 0 || remA != 1 || addA != 0)
                    throw new Exception($"A ({shared} shared): expected unc={shared} rem=1, got unc={uncA} mod={modA} rem={remA} add={addA}");
                if (uncB != shared || modB != 0 || remB != 0 || addB != 1)
                    throw new Exception($"B ({shared} shared): expected unc={shared} add=1, got unc={uncB} mod={modB} rem={remB} add={addB}");
                if (ra[0].Status != DiffStatus.Removed || ra[0].Text != "HEADER-A")
                    throw new Exception($"A: expected line 0 Removed 'HEADER-A', got {ra[0].Status} '{ra[0].Text}'");
                if (rb[^1].Status != DiffStatus.Added || rb[^1].Text != "FOOTER-B")
                    throw new Exception($"B: expected last line Added 'FOOTER-B', got {rb[^1].Status} '{rb[^1].Text}'");

                Console.WriteLine($"  shared={shared}: A unc/rem = {uncA}/{remA}, B unc/add = {uncB}/{addB} ({sw.ElapsedMilliseconds}ms)");
            }

            Check(18);   // small: exercises MyersCore split path
            Check(997);  // large (~996k cells): the reported bug — old fallback lost all matches here
        });

        Run("standard: scattered edits in 4500-line files", () =>
        {
            // The reported shape: ~4500 lines, a handful of scattered adds/removes.
            // True LCS = base minus the removed lines; expect localized Added/Removed only.
            var baseLines = new List<string>();
            for (int i = 0; i < 4500; i++) baseLines.Add($"line{i}");

            // A: remove one line every ~300th position (15 removals)
            var a = new List<string>();
            foreach (var l in baseLines)
                if ((int.Parse(l.AsSpan(4)) % 300) != 7) a.Add(l);

            // B: insert an unrelated line after every ~260th position (17 insertions)
            var b = new List<string>();
            for (int i = 0; i < baseLines.Count; i++)
            {
                b.Add(baseLines[i]);
                if (i % 260 == 259) b.Add($"inserted-{i}");
            }

            var fa = new FileModel { FilePath = "a.txt", Lines = a };
            var fb = new FileModel { FilePath = "b.txt", Lines = b };

            int removed = baseLines.Count - a.Count;
            int added = b.Count - baseLines.Count;

            var sw = Stopwatch.StartNew();
            var (ra, rb, _, _) = DiffEngine.Compute(fa, fb, computeInlineDiffs: true, algorithm: DiffAlgorithm.Standard);
            sw.Stop();

            int uncA = 0, modA = 0, remA = 0, addA = 0;
            foreach (var l in ra) switch (l.Status) { case DiffStatus.Unchanged: uncA++; break; case DiffStatus.Modified: modA++; break; case DiffStatus.Removed: remA++; break; case DiffStatus.Added: addA++; break; }
            int uncB = 0, modB = 0, remB = 0, addB = 0;
            foreach (var l in rb) switch (l.Status) { case DiffStatus.Unchanged: uncB++; break; case DiffStatus.Modified: modB++; break; case DiffStatus.Removed: remB++; break; case DiffStatus.Added: addB++; break; }

            Console.WriteLine($"  A: unc={uncA} mod={modA} rem={remA} add={addA}; B: unc={uncB} mod={modB} rem={remB} add={addB} ({sw.ElapsedMilliseconds}ms)");

            if (modA != 0 || modB != 0)
                throw new Exception($"Expected no Modified lines, got A mod={modA}, B mod={modB}");
            // Every line of A exists in B unchanged: all of A is Unchanged.
            if (uncA != a.Count || remA != 0)
                throw new Exception($"A: expected unc={a.Count} rem=0, got unc={uncA} rem={remA}");
            // B's unmatched lines = the inserted lines + the base lines A removed → all Added.
            if (uncB != b.Count - added - removed || addB != added + removed)
                throw new Exception($"B: expected unc={b.Count - added - removed} add={added + removed}, got unc={uncB} add={addB}");
        });

        // ── Swap equivalence: flip-in-place (SwapFiles fast path) must equal a fresh recompute ──

        Run("swap equivalence: mods after insertion", () =>
        {
            var a = new List<string>
            {
                "common 1", "alpha line", "beta line", "gamma line",
                "mod one: keep this part", "common 5", "mod two: x = 1;",
                "common 7", "tail A"
            };
            var b = new List<string>
            {
                "common 1", "INSERTED at top of block", "alpha line", "beta line", "gamma line",
                "mod one: keep THIS part", "common 5", "mod two: x = 2;",
                "extra B line", "tail A"
            };
            AssertSwapEquivalence(a, b);
        });

        Run("swap equivalence: leading and trailing mods", () =>
        {
            var c = new List<string> { "first line C", "shared", "shared2", "last C" };
            var d = new List<string> { "first line D", "shared", "shared2", "last D" };
            AssertSwapEquivalence(c, d);
        });

        Run("swap equivalence: multi-line block size mismatch", () =>
        {
            var a = new List<string> { "top", "a1", "a2", "a3", "bottom" };
            var b = new List<string> { "top", "b1", "b2", "bottom" };
            AssertSwapEquivalence(a, b);
        });

        // ── Gap vs non-gap: both builders must classify every real line identically ──

        Run("gap/non-gap consistency: leading replacement", () =>
        {
            var a = new List<string> { "first A", "shared1", "shared2" };
            var b = new List<string> { "first B", "shared1", "shared2" };
            AssertGapNonGapConsistency(a, b);
        });

        Run("gap/non-gap consistency: trailing replacement", () =>
        {
            var a = new List<string> { "shared1", "last A" };
            var b = new List<string> { "shared1", "last B" };
            AssertGapNonGapConsistency(a, b);
        });

        Run("gap/non-gap consistency: whole-file replacement", () =>
        {
            var a = new List<string> { "a1", "a2" };
            var b = new List<string> { "b1", "b2" };
            AssertGapNonGapConsistency(a, b);
        });

        // ── Fuzz: flip must equal recompute for ambiguous (repetitive) inputs ──

        Run("fuzz: swap equivalence on 500 random repetitive cases", () =>
        {
            var rng = new Random(12345);
            string[] alphabet = { "x", "y", "z", "w", "v" };
            for (int iter = 0; iter < 500; iter++)
            {
                int lenA = rng.Next(1, 31), lenB = rng.Next(1, 31);
                var a = new List<string>(); var b = new List<string>();
                // Start from a common base and mutate — guarantees ambiguity (repeats)
                var baseLines = new List<string>();
                int baseLen = rng.Next(1, 25);
                for (int i = 0; i < baseLen; i++) baseLines.Add(alphabet[rng.Next(alphabet.Length)]);
                a.AddRange(baseLines);
                b.AddRange(baseLines);
                for (int i = 0; i < rng.Next(0, 6); i++)
                {
                    int side = rng.Next(2);
                    var target = side == 0 ? a : b;
                    int pos = rng.Next(0, target.Count + 1);
                    if (rng.NextDouble() < 0.5 && target.Count > 0)
                        target[rng.Next(target.Count)] = alphabet[rng.Next(alphabet.Length)];
                    else
                        target.Insert(pos, alphabet[rng.Next(alphabet.Length)]);
                }
                try
                {
                    AssertSwapEquivalence(a, b);
                }
                catch (Exception ex)
                {
                    throw new Exception($"iter={iter} A=[{string.Join(",", a)}] B=[{string.Join(",", b)}] :: {ex.Message}");
                }
            }
        });

        // ── Patience engine ──

        Run("patience: anchors on unique lines", () =>
        {
            var a = new List<string> { "a", "b", "c", "d", "e", "f", "g", "h" };
            var b = new List<string> { "b", "a", "c", "d", "e", "f", "g", "X" };
            var fa = new FileModel { FilePath = "a.txt", Lines = a };
            var fb = new FileModel { FilePath = "b.txt", Lines = b };
            var (ra, rb, _, _) = DiffEngine.Compute(fa, fb, computeInlineDiffs: true, algorithm: DiffAlgorithm.Patience);
            // Patience anchors on lines unique to both files and keeps the largest non-crossing set
            // (LIS over B positions): b,c,d,e,f,g stay aligned; 'a' is unmatched (Removed/Added) and
            // h↔X pair as Modified.
            if (ra[0].Status != DiffStatus.Removed) throw new Exception($"ra[0] should be Removed, got {ra[0].Status}");
            for (int i = 1; i <= 6; i++)
                if (ra[i].Status != DiffStatus.Unchanged) throw new Exception($"ra[{i}] should be Unchanged, got {ra[i].Status}");
            if (ra[7].Status != DiffStatus.Modified) throw new Exception($"ra[7] should be Modified, got {ra[7].Status}");
            if (rb[0].Status != DiffStatus.Unchanged) throw new Exception($"rb[0] should be Unchanged, got {rb[0].Status}");
            if (rb[1].Status != DiffStatus.Added) throw new Exception($"rb[1] should be Added, got {rb[1].Status}");
            for (int i = 2; i <= 6; i++)
                if (rb[i].Status != DiffStatus.Unchanged) throw new Exception($"rb[{i}] should be Unchanged, got {rb[i].Status}");
            if (rb[7].Status != DiffStatus.Modified) throw new Exception($"rb[7] should be Modified, got {rb[7].Status}");
        });

        Run("patience: fully reversed 2000 unique lines", () =>
        {
            var a = new List<string>();
            for (int i = 0; i < 2000; i++) a.Add($"L{i}");
            var b = new List<string>(a);
            b.Reverse();

            var fa = new FileModel { FilePath = "a.txt", Lines = a };
            var fb = new FileModel { FilePath = "b.txt", Lines = b };
            var sw = Stopwatch.StartNew();
            var (ra, rb, _, _) = DiffEngine.Compute(fa, fb, computeInlineDiffs: true, algorithm: DiffAlgorithm.Patience);
            sw.Stop();

            // LCS is 1 (all lines distinct): patience anchors the pivot line L1999 ↔ L1999 and leaves
            // the rest unmatched. Guards against O(N²) anchor peeling on adversarial inputs.
            int uA = 0, uB = 0;
            for (int i = 0; i < ra.Count; i++)
                if (ra[i].Status == DiffStatus.Unchanged) { uA++; if (i != 1999) throw new Exception($"unexpected Unchanged at A[{i}]"); }
            for (int i = 0; i < rb.Count; i++)
                if (rb[i].Status == DiffStatus.Unchanged) { uB++; if (i != 0) throw new Exception($"unexpected Unchanged at B[{i}]"); }
            if (uA != 1 || uB != 1) throw new Exception($"Expected 1 Unchanged each side, got {uA},{uB}");
            Console.WriteLine($"  ({sw.ElapsedMilliseconds}ms)");
        });

        Run("patience: large identical (5000 lines)", () =>
        {
            var lines = new List<string>();
            for (int i = 0; i < 5000; i++) lines.Add($"line{i}");
            var a = new FileModel { FilePath = "a.txt", Lines = new List<string>(lines) };
            var b = new FileModel { FilePath = "b.txt", Lines = new List<string>(lines) };

            var sw = Stopwatch.StartNew();
            var (ra, rb, _, _) = DiffEngine.Compute(a, b, computeInlineDiffs: true, algorithm: DiffAlgorithm.Patience);
            sw.Stop();

            bool ok = true;
            foreach (var l in ra) if (l.Status != DiffStatus.Unchanged) ok = false;
            if (!ok) throw new Exception("Expected all Unchanged");
            Console.WriteLine($"  ({sw.ElapsedMilliseconds}ms)");
        });

        Run("patience: swap equivalence targeted cases", () =>
        {
            // Rotation: patience anchors via LIS and must mirror exactly when sides are flipped
            AssertSwapEquivalence(
                new List<string> { "x", "y", "z" },
                new List<string> { "z", "x", "y" },
                DiffAlgorithm.Patience);
            // Repetitive (no unique lines → Myers fallback inside the patience path)
            AssertSwapEquivalence(
                new List<string> { "a", "b", "a", "c", "b", "a" },
                new List<string> { "b", "a", "c", "a", "b" },
                DiffAlgorithm.Patience);
            // Same shapes as the Myers swap tests, through the patience engine
            AssertSwapEquivalence(
                new List<string> { "common 1", "alpha line", "beta line", "gamma line",
                                   "mod one: keep this part", "common 5", "mod two: x = 1;",
                                   "common 7", "tail A" },
                new List<string> { "common 1", "INSERTED at top of block", "alpha line", "beta line", "gamma line",
                                   "mod one: keep THIS part", "common 5", "mod two: x = 2;",
                                   "extra B line", "tail A" },
                DiffAlgorithm.Patience);
        });

        Run("patience: gap/non-gap consistency", () =>
        {
            AssertGapNonGapConsistency(
                new List<string> { "a", "b", "c", "d", "e", "f", "g", "h" },
                new List<string> { "b", "a", "c", "d", "e", "f", "g", "X" },
                DiffAlgorithm.Patience);
            // Repeated 't' exercises demotion-to-Unchanged block pairing under patience anchors
            AssertGapNonGapConsistency(
                new List<string> { "t", "u", "t" },
                new List<string> { "u", "t", "t" },
                DiffAlgorithm.Patience);
        });

        Run("fuzz: patience swap equivalence on 500 random cases", () =>
        {
            var rng = new Random(67890);
            string[] alphabet = { "x", "y", "z", "w", "v" };
            for (int iter = 0; iter < 500; iter++)
            {
                // Even iterations: repetitive alphabet (no unique lines → Myers fallback inside patience).
                // Odd iterations: unique line ids (exercises the anchor/LIS path).
                bool unique = iter % 2 == 1;
                var a = new List<string>(); var b = new List<string>();
                var baseLines = new List<string>();
                int baseLen = rng.Next(1, 35);
                for (int i = 0; i < baseLen; i++)
                    baseLines.Add(unique ? $"line{i}" : alphabet[rng.Next(alphabet.Length)]);
                a.AddRange(baseLines);
                b.AddRange(baseLines);
                for (int i = 0; i < rng.Next(0, 8); i++)
                {
                    int side = rng.Next(2);
                    var target = side == 0 ? a : b;
                    int pos = rng.Next(0, target.Count + 1);
                    if (rng.NextDouble() < 0.5 && target.Count > 0)
                        target[rng.Next(target.Count)] = unique ? $"mut{iter}-{i}" : alphabet[rng.Next(alphabet.Length)];
                    else
                        target.Insert(pos, unique ? $"ins{iter}-{i}" : alphabet[rng.Next(alphabet.Length)]);
                }
                try
                {
                    AssertSwapEquivalence(a, b, DiffAlgorithm.Patience);
                }
                catch (Exception ex)
                {
                    throw new Exception($"iter={iter} A=[{string.Join(",", a)}] B=[{string.Join(",", b)}] :: {ex.Message}");
                }
            }
        });

        // ── 3-way merge engine ──

        Run("merge: identical files → no units, merged == base", () =>
        {
            var lines = new List<string> { "one", "two", "three" };
            var s = MergeEngine.CreateMerge(MF("base.txt", lines), MF("a.txt", lines), MF("b.txt", lines));
            if (s.Changes.Count != 0 || s.Conflicts.Count != 0)
                throw new Exception($"expected 0 units/conflicts, got {s.Changes.Count}/{s.Conflicts.Count}");
            if (MergeEngine.BuildMergedContent(s) != string.Join("\n", lines))
                throw new Exception("merged content should equal base");
            if (s.IsDirty) throw new Exception("expected not dirty");
            AssertInvariants(s);
        });

        Run("merge: A-only modify auto-merges; reject restores base + dims row", () =>
        {
            var baseL = new List<string> { "alpha", "beta", "gamma" };
            var a = new List<string> { "alpha", "BETA-CHANGED", "gamma" };
            var s = MergeEngine.CreateMerge(MF("base.txt", baseL), MF("a.txt", a), MF("b.txt", baseL));
            if (s.Changes.Count != 1 || s.Conflicts.Count != 0)
                throw new Exception($"expected 1 unit/0 conflicts, got {s.Changes.Count}/{s.Conflicts.Count}");
            var ch = s.Changes[0];
            if (!ch.FromA || ch.IsInsertion || ch.IsDeletion)
                throw new Exception($"expected A modify unit, got FromA={ch.FromA} ins={ch.IsInsertion} del={ch.IsDeletion}");
            if (MergeEngine.BuildMergedContent(s) != "alpha\nBETA-CHANGED\ngamma")
                throw new Exception($"unexpected merged: {MergeEngine.BuildMergedContent(s)}");

            MergeEngine.SetChangeAccepted(s, ch, false);
            MergeEngine.RebuildDisplay(s);
            if (MergeEngine.BuildMergedContent(s) != string.Join("\n", baseL))
                throw new Exception("reject should restore base content");
            bool sawRejected = s.TripleA.Any(l => l.Status == DiffStatus.ChangeA && l.Rejected);
            if (!sawRejected) throw new Exception("expected dimmed (Rejected) A row after reject");
            AssertInvariants(s);
        });

        Run("merge: B-only insertion lands at correct anchor; reject removes it", () =>
        {
            var baseL = new List<string> { "one", "two", "three" };
            var b = new List<string> { "one", "INSERTED", "two", "three" };
            var s = MergeEngine.CreateMerge(MF("base.txt", baseL), MF("a.txt", baseL), MF("b.txt", b));
            if (s.Changes.Count != 1 || s.Conflicts.Count != 0)
                throw new Exception($"expected 1 unit/0 conflicts, got {s.Changes.Count}/{s.Conflicts.Count}");
            var ch = s.Changes[0];
            if (ch.FromA || !ch.IsInsertion)
                throw new Exception($"expected B insertion unit, got FromA={ch.FromA} ins={ch.IsInsertion}");
            if (MergeEngine.BuildMergedContent(s) != "one\nINSERTED\ntwo\nthree")
                throw new Exception($"unexpected merged: {MergeEngine.BuildMergedContent(s)}");

            MergeEngine.SetChangeAccepted(s, ch, false);
            if (MergeEngine.BuildMergedContent(s) != string.Join("\n", baseL))
                throw new Exception("reject should drop the insertion");
            AssertInvariants(s);
        });

        Run("merge: non-overlapping A+B changes both auto-merge, order preserved", () =>
        {
            var baseL = new List<string> { "l0", "l1", "l2", "l3", "l4", "l5", "l6", "l7" };
            var a = new List<string>(baseL); a[2] = "A-MOD";
            var b = new List<string>(baseL); b.Insert(4, "B-INS"); b[7] = "B-MOD"; // base l6 → index 7 after insert
            var s = MergeEngine.CreateMerge(MF("base.txt", baseL), MF("a.txt", a), MF("b.txt", b));
            // A: 1 modify. B: 1 insertion (anchor 4) + 1 modify (l6) → 3 units total.
            if (s.Changes.Count != 3 || s.Conflicts.Count != 0)
                throw new Exception($"expected 3 units/0 conflicts, got {s.Changes.Count}/{s.Conflicts.Count}");
            if (!s.Changes.Any(c => c.FromA) || !s.Changes.Any(c => !c.FromA))
                throw new Exception("expected one unit per side");
            const string expected = "l0\nl1\nA-MOD\nl3\nB-INS\nl4\nl5\nB-MOD\nl7";
            if (MergeEngine.BuildMergedContent(s) != expected)
                throw new Exception($"unexpected merged: {MergeEngine.BuildMergedContent(s)}");
            AssertInvariants(s);
        });

        Run("merge: conflicting modify → 1 conflict, null until resolved, all 4 resolutions exact", () =>
        {
            var baseL = new List<string> { "top", "shared", "bottom" };
            var a = new List<string> { "top", "A-VERSION", "bottom" };
            var b = new List<string> { "top", "B-VERSION", "bottom" };
            MergeState Mk() => MergeEngine.CreateMerge(MF("base.txt", baseL), MF("a.txt", a), MF("b.txt", b));
            void Expect(MergeState s, string expected)
            {
                var got = MergeEngine.BuildMergedContent(s);
                if (got != expected) throw new Exception($"expected [{expected}] got [{got}]");
            }

            var s0 = Mk();
            if (s0.Conflicts.Count != 1 || s0.Changes.Count != 0)
                throw new Exception($"expected 1 conflict/0 units, got {s0.Conflicts.Count}/{s0.Changes.Count}");
            if (s0.Conflicts[0].IsAsymmetric) throw new Exception("modify-vs-modify should be symmetric");
            if (MergeEngine.BuildMergedContent(s0) != null) throw new Exception("expected null while unresolved");
            if (s0.IsSaveable) throw new Exception("expected not saveable while unresolved");

            var sA = Mk(); MergeEngine.ResolveConflict(sA, sA.Conflicts[0], MergeResolution.TakeA);
            if (!sA.IsSaveable) throw new Exception("TakeA should make it saveable");
            Expect(sA, "top\nA-VERSION\nbottom");

            var sB = Mk(); MergeEngine.ResolveConflict(sB, sB.Conflicts[0], MergeResolution.TakeB);
            Expect(sB, "top\nB-VERSION\nbottom");

            var sAB = Mk(); MergeEngine.ResolveConflict(sAB, sAB.Conflicts[0], MergeResolution.OrderAB);
            Expect(sAB, "top\nA-VERSION\nB-VERSION\nbottom");

            var sBA = Mk(); MergeEngine.ResolveConflict(sBA, sBA.Conflicts[0], MergeResolution.OrderBA);
            Expect(sBA, "top\nB-VERSION\nA-VERSION\nbottom");

            AssertInvariants(sA); AssertInvariants(sAB);
        });

        Run("merge: agreed deletion omits line; reject restores it", () =>
        {
            var baseL = new List<string> { "one", "DROP", "three" };
            var del = new List<string> { "one", "three" };
            var s = MergeEngine.CreateMerge(MF("base.txt", baseL), MF("a.txt", del), MF("b.txt", del));
            if (s.Changes.Count != 1 || s.Conflicts.Count != 0)
                throw new Exception($"expected 1 unit/0 conflicts, got {s.Changes.Count}/{s.Conflicts.Count}");
            if (!s.Changes[0].IsDeletion) throw new Exception("expected a deletion unit");
            if (MergeEngine.BuildMergedContent(s) != "one\nthree")
                throw new Exception($"unexpected merged: {MergeEngine.BuildMergedContent(s)}");

            MergeEngine.SetChangeAccepted(s, s.Changes[0], false);
            if (MergeEngine.BuildMergedContent(s) != string.Join("\n", baseL))
                throw new Exception("reject should restore the deleted line");
            AssertInvariants(s);
        });

        Run("merge: replace (delete+insert at same spot) is one modify, no conflict", () =>
        {
            // EmitBlock pairs min(uA,uB) as Modified, so a 1:1 swap classifies as a single
            // modify unit — it must NOT become a delete-vs-nothing conflict.
            var baseL = new List<string> { "one", "TWO", "three" };
            var a = new List<string> { "one", "NEW", "three" };
            var s = MergeEngine.CreateMerge(MF("base.txt", baseL), MF("a.txt", a), MF("b.txt", baseL));
            if (s.Changes.Count != 1 || s.Conflicts.Count != 0)
                throw new Exception($"expected 1 unit/0 conflicts, got {s.Changes.Count}/{s.Conflicts.Count}");
            if (!s.Changes[0].FromA || s.Changes[0].IsInsertion || s.Changes[0].IsDeletion)
                throw new Exception("expected an A modify unit");
            if (MergeEngine.BuildMergedContent(s) != "one\nNEW\nthree")
                throw new Exception($"unexpected merged: {MergeEngine.BuildMergedContent(s)}");
            AssertInvariants(s);
        });

        Run("merge: delete + extra insert → modify + insert units, no conflict", () =>
        {
            // A replaces TWO with two lines: pair 1 is Modified (TWO→NEW1), the surplus line
            // projects as an insertion at the anchor after TWO.
            var baseL = new List<string> { "one", "TWO", "three" };
            var a = new List<string> { "one", "NEW1", "NEW2", "three" };
            var s = MergeEngine.CreateMerge(MF("base.txt", baseL), MF("a.txt", a), MF("b.txt", baseL));
            if (s.Conflicts.Count != 0) throw new Exception($"expected no conflicts, got {s.Conflicts.Count}");
            if (s.Changes.Count != 2 || !s.Changes.All(c => c.FromA))
                throw new Exception($"expected 2 A units, got {s.Changes.Count}");
            if (!s.Changes.Any(c => c.IsInsertion) || !s.Changes.Any(c => !c.IsInsertion && !c.IsDeletion))
                throw new Exception("expected one insertion + one modify unit");
            if (MergeEngine.BuildMergedContent(s) != "one\nNEW1\nNEW2\nthree")
                throw new Exception($"unexpected merged: {MergeEngine.BuildMergedContent(s)}");
            AssertInvariants(s);
        });

        Run("merge: whitespace-only diff honors ignoreWhitespace", () =>
        {
            var baseL = new List<string> { "hello world", "keep" };
            var a = new List<string> { "hello world ", "keep" }; // trailing space (engine trims when ignoring)
            var sIgnore = MergeEngine.CreateMerge(MF("base.txt", baseL), MF("a.txt", a), MF("b.txt", baseL), ignoreWhitespace: true);
            if (sIgnore.Changes.Count != 0) throw new Exception($"ignoreWhitespace should yield no units, got {sIgnore.Changes.Count}");
            if (MergeEngine.BuildMergedContent(sIgnore) != string.Join("\n", baseL))
                throw new Exception("merged should keep base text when ignoring whitespace");

            var sStrict = MergeEngine.CreateMerge(MF("base.txt", baseL), MF("a.txt", a), MF("b.txt", baseL), ignoreWhitespace: false);
            if (sStrict.Changes.Count != 1 || !sStrict.Changes[0].FromA)
                throw new Exception($"strict mode should yield 1 A unit, got {sStrict.Changes.Count}");
            AssertInvariants(sIgnore); AssertInvariants(sStrict);
        });

        Run("merge: empty base, both insert → anchor-0 insertion conflict, orderings exact", () =>
        {
            var a = new List<string> { "A1", "A2" };
            var b = new List<string> { "B1" };
            MergeState Mk() => MergeEngine.CreateMerge(MF("base.txt", new List<string>()), MF("a.txt", a), MF("b.txt", b));
            void Expect(MergeState s, string expected)
            {
                var got = MergeEngine.BuildMergedContent(s);
                if (got != expected) throw new Exception($"expected [{expected}] got [{got}]");
            }

            var s0 = Mk();
            if (s0.Conflicts.Count != 1 || s0.Changes.Count != 0)
                throw new Exception($"expected 1 conflict/0 units, got {s0.Conflicts.Count}/{s0.Changes.Count}");
            if (MergeEngine.BuildMergedContent(s0) != null) throw new Exception("expected null while unresolved");

            var sA = Mk(); MergeEngine.ResolveConflict(sA, sA.Conflicts[0], MergeResolution.TakeA);
            Expect(sA, "A1\nA2");
            var sB = Mk(); MergeEngine.ResolveConflict(sB, sB.Conflicts[0], MergeResolution.TakeB);
            Expect(sB, "B1");
            var sAB = Mk(); MergeEngine.ResolveConflict(sAB, sAB.Conflicts[0], MergeResolution.OrderAB);
            Expect(sAB, "A1\nA2\nB1");
            var sBA = Mk(); MergeEngine.ResolveConflict(sBA, sBA.Conflicts[0], MergeResolution.OrderBA);
            Expect(sBA, "B1\nA1\nA2");

            AssertInvariants(s0); AssertInvariants(sAB);
        });

        Run("merge: one side identical to base (both directions)", () =>
        {
            var baseL = new List<string> { "a", "b", "c" };
            var bMod = new List<string> { "a", "B-CHANGED", "c" };
            var aMod = new List<string> { "a", "A-CHANGED", "c" };

            var s1 = MergeEngine.CreateMerge(MF("base.txt", baseL), MF("a.txt", baseL), MF("b.txt", bMod));
            if (s1.Changes.Count != 1 || s1.Conflicts.Count != 0 || s1.Changes[0].FromA)
                throw new Exception($"expected a single B unit, got {s1.Changes.Count} units FromA={s1.Changes.FirstOrDefault()?.FromA}");
            if (MergeEngine.BuildMergedContent(s1) != "a\nB-CHANGED\nc")
                throw new Exception($"unexpected merged: {MergeEngine.BuildMergedContent(s1)}");

            var s2 = MergeEngine.CreateMerge(MF("base.txt", baseL), MF("a.txt", aMod), MF("b.txt", baseL));
            if (s2.Changes.Count != 1 || s2.Conflicts.Count != 0 || !s2.Changes[0].FromA)
                throw new Exception($"expected a single A unit, got {s2.Changes.Count}");
            if (MergeEngine.BuildMergedContent(s2) != "a\nA-CHANGED\nc")
                throw new Exception($"unexpected merged: {MergeEngine.BuildMergedContent(s2)}");

            AssertInvariants(s1); AssertInvariants(s2);
        });

        Run("merge: identical modification on both sides → single unit, no conflict", () =>
        {
            var baseL = new List<string> { "x", "y" };
            var a = new List<string> { "SAME", "y" };
            var b = new List<string> { "SAME", "y" };
            var s = MergeEngine.CreateMerge(MF("base.txt", baseL), MF("a.txt", a), MF("b.txt", b));
            if (s.Changes.Count != 1 || s.Conflicts.Count != 0)
                throw new Exception($"expected 1 unit/0 conflicts, got {s.Changes.Count}/{s.Conflicts.Count}");
            if (MergeEngine.BuildMergedContent(s) != "SAME\ny")
                throw new Exception($"unexpected merged: {MergeEngine.BuildMergedContent(s)}");

            MergeEngine.SetChangeAccepted(s, s.Changes[0], false);
            if (MergeEngine.BuildMergedContent(s) != string.Join("\n", baseL))
                throw new Exception("reject should restore base content");
            AssertInvariants(s);
        });

        Run("merge: delete-vs-modify is asymmetric; TakeA drops the line, ordering keeps only B", () =>
        {
            var baseL = new List<string> { "one", "TWO", "three" };
            var a = new List<string> { "one", "three" };              // A deletes TWO
            var b = new List<string> { "one", "B-TWO", "three" };    // B modifies TWO
            MergeState Mk() => MergeEngine.CreateMerge(MF("base.txt", baseL), MF("a.txt", a), MF("b.txt", b));

            var s0 = Mk();
            if (s0.Conflicts.Count != 1) throw new Exception($"expected 1 conflict, got {s0.Conflicts.Count}");
            if (!s0.Conflicts[0].IsAsymmetric) throw new Exception("delete-vs-modify should be asymmetric");

            var sA = Mk(); MergeEngine.ResolveConflict(sA, sA.Conflicts[0], MergeResolution.TakeA);
            if (MergeEngine.BuildMergedContent(sA) != "one\nthree")
                throw new Exception($"TakeA should drop the line: {MergeEngine.BuildMergedContent(sA)}");

            var sB = Mk(); MergeEngine.ResolveConflict(sB, sB.Conflicts[0], MergeResolution.TakeB);
            if (MergeEngine.BuildMergedContent(sB) != "one\nB-TWO\nthree")
                throw new Exception($"TakeB: {MergeEngine.BuildMergedContent(sB)}");

            var sAB = Mk(); MergeEngine.ResolveConflict(sAB, sAB.Conflicts[0], MergeResolution.OrderAB);
            // A has no content for this line → ordering degenerates to B alone
            if (MergeEngine.BuildMergedContent(sAB) != "one\nB-TWO\nthree")
                throw new Exception($"OrderAB: {MergeEngine.BuildMergedContent(sAB)}");

            AssertInvariants(s0); AssertInvariants(sA);
        });

        Run("merge: base pane can preview the merged output", () =>
        {
            var baseL = new List<string> { "a0", "a1", "DROP", "a3" };
            var a = new List<string> { "a0", "X", "a3" };   // A modifies line 2, deletes DROP
            var b = new List<string> { "a0", "Y", "a3" };   // B modifies line 2 differently (conflict), deletes DROP

            var s = MergeEngine.CreateMerge(MF("base.txt", baseL), MF("a.txt", a), MF("b.txt", b));
            if (s.Conflicts.Count != 1) throw new Exception($"expected 1 conflict, got {s.Conflicts.Count}");
            MergeEngine.ResolveConflict(s, s.Conflicts[0], MergeResolution.TakeA);

            // Normal view: the base pane keeps showing the original base text.
            MergeEngine.RebuildDisplay(s);
            if (!s.TripleBase.Any(l => l.Text == "a1" && l.OriginalLineNumber > 0))
                throw new Exception("normal view should show the original base text");

            // Merged-output view: the non-ghosted base rows reconstruct exactly the merged content.
            s.ShowMergedOutput = true;
            MergeEngine.RebuildDisplay(s);
            AssertInvariants(s);   // row alignment / numbering must survive the toggle

            var preview = new List<string>();
            foreach (var l in s.TripleBase)
                if (!l.Rejected && (l.OriginalLineNumber > 0 || l.Status != DiffStatus.Gap))
                {
                    if (l.Text == null) throw new Exception("preview row has null text");
                    preview.Add(l.Text);
                }
            string expected = "a0\nX\na3";
            if (string.Join("\n", preview) != expected)
                throw new Exception($"preview [{string.Join("|", preview)}] != [{expected}]");
            if (string.Join("\n", preview) != MergeEngine.BuildMergedContent(s))
                throw new Exception("preview must equal BuildMergedContent");

            // The agreed deletion is ghosted (original text, dimmed), not dropped from the rows.
            var ghost = s.TripleBase.FirstOrDefault(l => l.Text == "DROP");
            if (ghost == null || !ghost.Rejected)
                throw new Exception("deleted line should be ghosted dimmed in the preview");

            // Toggling back restores the original base text.
            s.ShowMergedOutput = false;
            MergeEngine.RebuildDisplay(s);
            if (!s.TripleBase.Any(l => l.Text == "a1" && l.OriginalLineNumber > 0))
                throw new Exception("toggling off should restore the original base text");

            AssertInvariants(s);
        });

        Run("merge: resolved conflicts can be re-resolved or reset", () =>
        {
            var baseL = new List<string> { "a0", "mid", "a2" };
            var a = new List<string> { "a0", "FROM_A", "a2" };
            var b = new List<string> { "a0", "FROM_B", "a2" };

            var s = MergeEngine.CreateMerge(MF("base.txt", baseL), MF("a.txt", a), MF("b.txt", b));
            if (s.Conflicts.Count != 1) throw new Exception($"expected 1 conflict, got {s.Conflicts.Count}");
            var block = s.Conflicts[0];

            MergeEngine.ResolveConflict(s, block, MergeResolution.TakeA);
            MergeEngine.RebuildDisplay(s);
            if (MergeEngine.BuildMergedContent(s) != "a0\nFROM_A\na2")
                throw new Exception($"TakeA: [{MergeEngine.BuildMergedContent(s)}]");

            // Switch the choice — TakeB must fully replace TakeA.
            MergeEngine.ResolveConflict(s, block, MergeResolution.TakeB);
            MergeEngine.RebuildDisplay(s);
            if (MergeEngine.BuildMergedContent(s) != "a0\nFROM_B\na2")
                throw new Exception($"re-resolve to TakeB: [{MergeEngine.BuildMergedContent(s)}]");

            // OrderAB keeps both sides.
            MergeEngine.ResolveConflict(s, block, MergeResolution.OrderAB);
            MergeEngine.RebuildDisplay(s);
            if (MergeEngine.BuildMergedContent(s) != "a0\nFROM_A\nFROM_B\na2")
                throw new Exception($"OrderAB: [{MergeEngine.BuildMergedContent(s)}]");

            // Reset back to unresolved: saveability drops again.
            MergeEngine.ResolveConflict(s, block, MergeResolution.Unresolved);
            MergeEngine.RebuildDisplay(s);
            if (MergeEngine.BuildMergedContent(s) != null)
                throw new Exception("reset should make the merge unsaveable");
            if (s.UnresolvedCount != 1)
                throw new Exception($"expected 1 unresolved after reset, got {s.UnresolvedCount}");

            // And resolving again after a reset still works.
            MergeEngine.ResolveConflict(s, block, MergeResolution.TakeB);
            MergeEngine.RebuildDisplay(s);
            if (MergeEngine.BuildMergedContent(s) != "a0\nFROM_B\na2")
                throw new Exception($"re-resolve after reset: [{MergeEngine.BuildMergedContent(s)}]");

            AssertInvariants(s);
        });

        Run("merge: selection-scoped accept/reject affects only the selected lines", () =>
        {
            // A inserts a 4-line block after base line 0; B is identical to base.
            var baseL = new List<string> { "b0", "b1", "b2" };
            var a = new List<string> { "b0", "i1", "i2", "i3", "i4", "b1", "b2" };
            var b = new List<string>(baseL);

            var s = MergeEngine.CreateMerge(MF("base.txt", baseL), MF("a.txt", a), MF("b.txt", b));
            if (s.Changes.Count != 1) throw new Exception($"expected 1 change unit, got {s.Changes.Count}");
            var ins = s.Changes[0];
            if (!ins.IsInsertion || ins.LineCount != 4)
                throw new Exception($"expected a 4-line insertion unit, got IsInsertion={ins.IsInsertion} LineCount={ins.LineCount}");

            // Whole-unit reject still drops the entire block.
            MergeEngine.SetChangeAccepted(s, ins, false);
            if (MergeEngine.BuildMergedContent(s) != "b0\nb1\nb2")
                throw new Exception($"whole-unit reject: [{MergeEngine.BuildMergedContent(s)}]");

            // Accept all lines, then reject only the two middle lines of the unit.
            MergeEngine.SetChangeAccepted(s, ins, true);
            MergeEngine.SetChangeLinesAccepted(s, new (MergeChange, int)[] { (ins, 1), (ins, 2) }, false);
            if (MergeEngine.BuildMergedContent(s) != "b0\ni1\ni4\nb1\nb2")
                throw new Exception($"per-line reject: [{MergeEngine.BuildMergedContent(s)}]");

            // The display dims exactly the rejected rows of the unit.
            MergeEngine.RebuildDisplay(s);
            AssertInvariants(s);
            var rejectedFlags = new List<bool>();
            for (int r = 0; r < s.TripleA.Count; r++)
                if (s.ChangeLineAtRow(r) is { change: var c } && ReferenceEquals(c, ins))
                    rejectedFlags.Add(s.TripleA[r].Rejected);
            if (rejectedFlags.Count != 4 || !rejectedFlags.SequenceEqual(new[] { false, true, true, false }))
                throw new Exception($"display Rejected flags: [{string.Join(",", rejectedFlags)}]");

            // ChangeLineAtRow maps the unit's rows to local line offsets 0..3.
            int firstRow = -1;
            for (int r = 0; r < s.TripleA.Count; r++)
                if (s.ChangeLineAtRow(r) is { } t && ReferenceEquals(t.change, ins)) { firstRow = r; break; }
            if (firstRow < 0 || s.ChangeLineAtRow(firstRow + 2) is not { line: 2 })
                throw new Exception("ChangeLineAtRow should map unit rows to local offsets");

            // Re-accepting one of the rejected lines restores just that line.
            MergeEngine.SetChangeLinesAccepted(s, new (MergeChange, int)[] { (ins, 2) }, true);
            if (MergeEngine.BuildMergedContent(s) != "b0\ni1\ni3\ni4\nb1\nb2")
                throw new Exception($"per-line re-accept: [{MergeEngine.BuildMergedContent(s)}]");

            // Accepting the whole unit again clears every per-line rejection.
            MergeEngine.SetChangeAccepted(s, ins, true);
            if (MergeEngine.BuildMergedContent(s) != "b0\ni1\ni2\ni3\ni4\nb1\nb2")
                throw new Exception($"whole-unit accept: [{MergeEngine.BuildMergedContent(s)}]");

            // Accept-all / reject-all still operate whole-unit.
            MergeEngine.AcceptAll(s, accepted: false);
            if (MergeEngine.BuildMergedContent(s) != "b0\nb1\nb2")
                throw new Exception($"reject all: [{MergeEngine.BuildMergedContent(s)}]");
            MergeEngine.AcceptAll(s, accepted: true);
            if (MergeEngine.BuildMergedContent(s) != "b0\ni1\ni2\ni3\ni4\nb1\nb2")
                throw new Exception($"accept all: [{MergeEngine.BuildMergedContent(s)}]");

            AssertInvariants(s);
        });

        Run("merge: selection-scoped conflict resolution affects only the selected lines", () =>
        {
            // Two adjacent line conflicts → one block with two slots.
            var s = MergeEngine.CreateMerge(
                MF("base.txt", new() { "b0", "b1", "b2", "b3" }),
                MF("a.txt",    new() { "b0", "a1", "a2", "b3" }),
                MF("b.txt",    new() { "b0", "x1", "x2", "b3" }));

            if (s.Conflicts.Count != 1) throw new Exception($"expected 1 conflict block, got {s.Conflicts.Count}");
            var block = s.Conflicts[0];
            if (!block.IsFullyUnresolved || s.UnresolvedCount != 2)
                throw new Exception($"expected 2 unresolved slots (count={s.UnresolvedCount})");

            // Find the block's slots via the row mapping (survives display rebuilds).
            var slotTargets = new List<(ConflictBlock, int)>();
            for (int r = 0; r < s.TripleA.Count; r++)
                if (s.ConflictLineAtRow(r) is { } t && !slotTargets.Contains(t)) slotTargets.Add(t);
            if (slotTargets.Count != 2) throw new Exception($"expected 2 slots, got {slotTargets.Count}");

            // Resolve only the first conflict line to TakeA — block stays partially unresolved.
            MergeEngine.ResolveConflictLines(s, new[] { slotTargets[0] }, MergeResolution.TakeA);
            MergeEngine.RebuildDisplay(s);
            if (MergeEngine.BuildMergedContent(s) != null)
                throw new Exception("still-unresolved slot must block save");
            if (s.UnresolvedCount != 1) throw new Exception($"UnresolvedCount={s.UnresolvedCount}, expected 1");
            AssertInvariants(s);

            // Resolve the second line to TakeB — fully resolved with mixed per-line winners.
            MergeEngine.ResolveConflictLines(s, new[] { slotTargets[1] }, MergeResolution.TakeB);
            MergeEngine.RebuildDisplay(s);
            if (MergeEngine.BuildMergedContent(s) != "b0\na1\nx2\nb3")
                throw new Exception($"mixed resolution: [{MergeEngine.BuildMergedContent(s)}]");
            AssertInvariants(s);

            // Scoped reset of one slot re-opens the block.
            MergeEngine.ResolveConflictLines(s, new[] { slotTargets[0] }, MergeResolution.Unresolved);
            MergeEngine.RebuildDisplay(s);
            if (MergeEngine.BuildMergedContent(s) != null || s.UnresolvedCount != 1)
                throw new Exception("scoped reset should leave exactly one unresolved slot");
            AssertInvariants(s);

            // Whole-block ops still fill every slot at once.
            MergeEngine.ResolveConflict(s, block, MergeResolution.OrderAB);
            if (!block.IsFullyResolved || !block.AllResolvedAs(MergeResolution.OrderAB))
                throw new Exception("whole-block resolve must fill every slot");
            // Order* groups the run: all of A's lines first, then all of B's (not interleaved).
            if (MergeEngine.BuildMergedContent(s) != "b0\na1\na2\nx1\nx2\nb3")
                throw new Exception($"OrderAB: [{MergeEngine.BuildMergedContent(s)}]");
            AssertInvariants(s);
        });

        Run("merge: Order* runs emit one side's whole set of lines, then the other's", () =>
        {
            // Two adjacent line conflicts → one block with two slots.
            var s = MergeEngine.CreateMerge(
                MF("base.txt", new() { "b0", "b1", "b2", "b3" }),
                MF("a.txt",    new() { "b0", "a1", "a2", "b3" }),
                MF("b.txt",    new() { "b0", "x1", "x2", "b3" }));

            var block = s.Conflicts[0];

            // B before A over the whole block: all of B's lines, then all of A's.
            MergeEngine.ResolveConflict(s, block, MergeResolution.OrderBA);
            if (MergeEngine.BuildMergedContent(s) != "b0\nx1\nx2\na1\na2\nb3")
                throw new Exception($"OrderBA: [{MergeEngine.BuildMergedContent(s)}]");

            // The display rows follow the same grouping: every B row precedes every A row.
            MergeEngine.RebuildDisplay(s);
            int firstA = -1, lastB = -1;
            for (int r = 0; r < s.TripleA.Count; r++)
            {
                if (s.TripleA[r].Status == DiffStatus.ChangeA) { if (firstA < 0) firstA = r; }
                if (s.TripleB[r].Status == DiffStatus.ChangeB) lastB = r;
            }
            if (firstA < 0 || lastB < 0 || firstA <= lastB)
                throw new Exception($"display not grouped: first A row {firstA}, last B row {lastB}");

            // Selection-scoped run: only slot 0 ordered (B before A), slot 1 takes A.
            var s2 = MergeEngine.CreateMerge(
                MF("base.txt", new() { "b0", "b1", "b2", "b3" }),
                MF("a.txt",    new() { "b0", "a1", "a2", "b3" }),
                MF("b.txt",    new() { "b0", "x1", "x2", "b3" }));
            var slotTargets = new List<(ConflictBlock, int)>();
            for (int r = 0; r < s2.TripleA.Count; r++)
                if (s2.ConflictLineAtRow(r) is { } t && !slotTargets.Contains(t)) slotTargets.Add(t);
            MergeEngine.ResolveConflictLines(s2, new[] { slotTargets[0] }, MergeResolution.OrderBA);
            MergeEngine.ResolveConflictLines(s2, new[] { slotTargets[1] }, MergeResolution.TakeA);
            if (MergeEngine.BuildMergedContent(s2) != "b0\nx1\na1\na2\nb3")
                throw new Exception($"scoped OrderBA+TakeA: [{MergeEngine.BuildMergedContent(s2)}]");

            // The merged-output preview in the base pane must match the saved content.
            s.ShowMergedOutput = true;
            MergeEngine.RebuildDisplay(s);
            AssertInvariants(s);
            var preview = new List<string>();
            foreach (var l in s.TripleBase)
                if (!l.Rejected && (l.OriginalLineNumber > 0 || l.Status != DiffStatus.Gap))
                    preview.Add(l.Text!);
            string content = MergeEngine.BuildMergedContent(s)!;
            if (string.Join("\n", preview) != content)
                throw new Exception($"preview [{string.Join("|", preview)}] != content [{content}]");

            AssertInvariants(s2);
        });

        Run("merge: scoped insertion-conflict resolution per aligned position", () =>
        {
            // Both sides insert two different lines at the same anchor → 2 slots.
            var s = MergeEngine.CreateMerge(
                MF("base.txt", new() { "b0", "b1" }),
                MF("a.txt",    new() { "b0", "iA1", "iA2", "b1" }),
                MF("b.txt",    new() { "b0", "iB1", "iB2", "b1" }));

            if (s.Conflicts.Count != 1) throw new Exception($"expected 1 conflict block, got {s.Conflicts.Count}");
            var block = s.Conflicts[0];

            var slotTargets = new List<(ConflictBlock, int)>();
            for (int r = 0; r < s.TripleA.Count; r++)
                if (s.ConflictLineAtRow(r) is { } t && !slotTargets.Contains(t)) slotTargets.Add(t);
            if (slotTargets.Count != 2) throw new Exception($"expected 2 slots, got {slotTargets.Count}");

            // Take A at position 0 only — still unsaveable.
            MergeEngine.ResolveConflictLines(s, new[] { slotTargets[0] }, MergeResolution.TakeA);
            MergeEngine.RebuildDisplay(s);
            if (MergeEngine.BuildMergedContent(s) != null || s.UnresolvedCount != 1)
                throw new Exception("partially resolved insertion conflict must block save");
            AssertInvariants(s);

            // Take B at position 1 — output is A's kept lines, then B's.
            MergeEngine.ResolveConflictLines(s, new[] { slotTargets[1] }, MergeResolution.TakeB);
            MergeEngine.RebuildDisplay(s);
            if (MergeEngine.BuildMergedContent(s) != "b0\niA1\niB2\nb1")
                throw new Exception($"[{MergeEngine.BuildMergedContent(s)}]");
            AssertInvariants(s);

            // Mixed ordering: A-before-B at position 0, keep B at position 1.
            MergeEngine.ResolveConflictLines(s, new[] { slotTargets[0] }, MergeResolution.OrderAB);
            MergeEngine.RebuildDisplay(s);
            if (MergeEngine.BuildMergedContent(s) != "b0\niA1\niB1\niB2\nb1")
                throw new Exception($"[{MergeEngine.BuildMergedContent(s)}]");
            AssertInvariants(s);

            // Whole-block ops on multi-slot blocks still produce the classic ordering.
            MergeEngine.ResolveConflict(s, block, MergeResolution.OrderBA);
            if (MergeEngine.BuildMergedContent(s) != "b0\niB1\niB2\niA1\niA2\nb1")
                throw new Exception($"[{MergeEngine.BuildMergedContent(s)}]");
            AssertInvariants(s);
        });

        // ── Resolved-conflict marks (visual indication + Shift+F3 targets) ─────

        Run("merge: resolved TakeA marks winner rows; loser dimmed unmarked", () =>
        {
            var baseL = new List<string> { "one", "two", "three" };
            var a = new List<string> { "one", "TWO-A", "three" };
            var b = new List<string> { "one", "TWO-B", "three" };
            var s = MergeEngine.CreateMerge(MF("base.txt", baseL), MF("a.txt", a), MF("b.txt", b));
            if (s.Conflicts.Count != 1) throw new Exception($"expected 1 conflict, got {s.Conflicts.Count}");

            MergeEngine.ResolveConflict(s, s.Conflicts[0], MergeResolution.TakeA);
            MergeEngine.RebuildDisplay(s);

            // Winner: exactly the A row and the base cell carrying its change are stamped.
            int markedA = s.TripleA.Count(l => l.ResolvedConflict);
            int markedBase = s.TripleBase.Count(l => l.ResolvedConflict);
            if (markedA != 1 || markedBase != 1)
                throw new Exception($"expected 1 stamped A row + 1 base row, got {markedA}/{markedBase}");
            var winA = s.TripleA.First(l => l.ResolvedConflict);
            if (winA.Status != DiffStatus.ChangeA || winA.Text != "TWO-A" || winA.Rejected)
                throw new Exception($"wrong winner row: {winA.Status} '{winA.Text}' rejected={winA.Rejected}");
            int r = s.TripleA.IndexOf(winA);
            if (s.TripleBase[r].Status != DiffStatus.ChangeA || !s.TripleBase[r].ResolvedConflict)
                throw new Exception("base cell of the winner row must carry the mark");

            // Loser: B's row stays dimmed but unmarked.
            var loseB = s.TripleB.First(l => l.Status == DiffStatus.ChangeB);
            if (!loseB.Rejected || loseB.ResolvedConflict)
                throw new Exception("loser row must be Rejected and unmarked");

            // Navigation target + counts.
            if (s.Conflicts[0].FirstResolvedRow != r)
                throw new Exception($"FirstResolvedRow={s.Conflicts[0].FirstResolvedRow}, winner at {r}");
            if (s.ResolvedCount != 1 || s.UnresolvedCount != 0 || !s.IsSaveable)
                throw new Exception($"counts: resolved={s.ResolvedCount} unresolved={s.UnresolvedCount} saveable={s.IsSaveable}");
            AssertInvariants(s);
        });

        Run("merge: resolved-to-deletion leaves no winner marks", () =>
        {
            var baseL = new List<string> { "one", "TWO", "three" };
            var a = new List<string> { "one", "three" };              // A deletes TWO
            var b = new List<string> { "one", "B-TWO", "three" };    // B modifies TWO
            var s = MergeEngine.CreateMerge(MF("base.txt", baseL), MF("a.txt", a), MF("b.txt", b));

            MergeEngine.ResolveConflict(s, s.Conflicts[0], MergeResolution.TakeA);
            MergeEngine.RebuildDisplay(s);

            if (MergeEngine.BuildMergedContent(s) != "one\nthree")
                throw new Exception($"TakeA should drop the line: {MergeEngine.BuildMergedContent(s)}");
            // Nothing landed → nothing to mark; the dimmed ghost Conflict cell is the indicator.
            foreach (var list in new[] { s.TripleA, s.TripleBase, s.TripleB })
                if (list.Any(l => l.ResolvedConflict)) throw new Exception("resolved-to-deletion must not stamp rows");
            if (s.Conflicts[0].FirstResolvedRow != -1)
                throw new Exception($"expected FirstResolvedRow=-1, got {s.Conflicts[0].FirstResolvedRow}");
            var ghost = s.TripleBase.First(l => l.Status == DiffStatus.Conflict);
            if (!ghost.Rejected) throw new Exception("dimmed base ghost expected for resolved-to-deletion");
            AssertInvariants(s);
        });

        Run("merge: scoped insertion-slot TakeA marks only the taken cell", () =>
        {
            var s = MergeEngine.CreateMerge(
                MF("base.txt", new() { "b0", "b1" }),
                MF("a.txt",    new() { "b0", "iA1", "iA2", "b1" }),
                MF("b.txt",    new() { "b0", "iB1", "iB2", "b1" }));

            var slotTargets = new List<(ConflictBlock, int)>();
            for (int r = 0; r < s.TripleA.Count; r++)
                if (s.ConflictLineAtRow(r) is { } t && !slotTargets.Contains(t)) slotTargets.Add(t);
            if (slotTargets.Count != 2) throw new Exception($"expected 2 slots, got {slotTargets.Count}");

            MergeEngine.ResolveConflictLines(s, new[] { slotTargets[0] }, MergeResolution.TakeA);
            MergeEngine.RebuildDisplay(s);

            var win = s.TripleA.Where(l => l.ResolvedConflict).ToList();
            if (win.Count != 1 || win[0].Text != "iA1" || win[0].Status != DiffStatus.ChangeA)
                throw new Exception($"expected exactly iA1 stamped in A pane, got {win.Count} rows");
            int wr = s.TripleA.IndexOf(win[0]);
            var loseB = s.TripleB[wr];
            if (loseB.Text != "iB1" || !loseB.Rejected || loseB.ResolvedConflict)
                throw new Exception($"aligned B cell must be dimmed and unmarked, got '{loseB.Text}'");
            // Base pane carries no change cell on insertion rows.
            if (s.TripleBase.Any(l => l.ResolvedConflict)) throw new Exception("base pane must stay unmarked");
            if (s.Conflicts[0].FirstResolvedRow != wr)
                throw new Exception($"FirstResolvedRow={s.Conflicts[0].FirstResolvedRow}, winner at {wr}");
            if (s.UnresolvedCount != 1 || s.ResolvedCount != 1)
                throw new Exception("one slot must remain unresolved");
            AssertInvariants(s);
        });

        Run("merge: re-resolving moves the marks; reset clears them", () =>
        {
            var baseL = new List<string> { "one", "two", "three" };
            var a = new List<string> { "one", "TWO-A", "three" };
            var b = new List<string> { "one", "TWO-B", "three" };
            MergeState Mk() => MergeEngine.CreateMerge(MF("base.txt", baseL), MF("a.txt", a), MF("b.txt", b));

            // TakeA: marks land on the A side.
            var s = Mk();
            MergeEngine.ResolveConflict(s, s.Conflicts[0], MergeResolution.TakeA);
            MergeEngine.RebuildDisplay(s);
            if (!s.TripleA.Any(l => l.ResolvedConflict) || s.TripleB.Any(l => l.ResolvedConflict))
                throw new Exception("TakeA must stamp A only");

            // TakeB: marks follow the winner to the B side (base cell moves with it).
            MergeEngine.ResolveConflict(s, s.Conflicts[0], MergeResolution.TakeB);
            MergeEngine.RebuildDisplay(s);
            int markedB = s.TripleB.Count(l => l.ResolvedConflict && l.Text == "TWO-B");
            if (markedB != 1 || s.TripleA.Any(l => l.ResolvedConflict) || !s.TripleBase.Any(l => l.ResolvedConflict))
                throw new Exception($"TakeB must move the marks to B (+base), got {markedB} stamped B rows");

            // Reset: everything back to purple, no marks anywhere.
            MergeEngine.ResolveConflict(s, s.Conflicts[0], MergeResolution.Unresolved);
            MergeEngine.RebuildDisplay(s);
            foreach (var list in new[] { s.TripleA, s.TripleBase, s.TripleB })
                if (list.Any(l => l.ResolvedConflict)) throw new Exception("reset must clear all marks");
            if (s.Conflicts[0].FirstResolvedRow != -1)
                throw new Exception("reset must clear FirstResolvedRow");
            AssertInvariants(s);
        });

        Run("merge: merged-output preview keeps A/B marks, base pane stays neutral", () =>
        {
            var baseL = new List<string> { "one", "two", "three" };
            var a = new List<string> { "one", "TWO-A", "three" };
            var b = new List<string> { "one", "TWO-B", "three" };
            var s = MergeEngine.CreateMerge(MF("base.txt", baseL), MF("a.txt", a), MF("b.txt", b));

            MergeEngine.ResolveConflict(s, s.Conflicts[0], MergeResolution.TakeA);
            MergeEngine.RebuildDisplay(s);

            s.ShowMergedOutput = true;
            MergeEngine.RebuildDisplay(s);

            if (!s.TripleA.Any(l => l.ResolvedConflict && l.Text == "TWO-A"))
                throw new Exception("A pane must keep the winner mark in merged-output mode");
            if (s.TripleB.Any(l => l.ResolvedConflict))
                throw new Exception("B pane must have no marks after TakeA");
            if (s.TripleBase.Any(l => l.ResolvedConflict))
                throw new Exception("base preview is neutral — no marks there");
            // Navigation still targets the A-side winner row.
            int r = s.TripleA.IndexOf(s.TripleA.First(l => l.ResolvedConflict));
            if (s.Conflicts[0].FirstResolvedRow != r)
                throw new Exception($"FirstResolvedRow={s.Conflicts[0].FirstResolvedRow}, winner at {r}");
            AssertInvariants(s);
        });

        Run("merge: KeepBase keeps the original base line; both sides dimmed", () =>
        {
            var baseL = new List<string> { "one", "two", "three" };
            var a = new List<string> { "one", "TWO-A", "three" };
            var b = new List<string> { "one", "TWO-B", "three" };
            var s = MergeEngine.CreateMerge(MF("base.txt", baseL), MF("a.txt", a), MF("b.txt", b));

            MergeEngine.ResolveConflict(s, s.Conflicts[0], MergeResolution.KeepBase);
            MergeEngine.RebuildDisplay(s);

            if (MergeEngine.BuildMergedContent(s) != "one\ntwo\nthree")
                throw new Exception($"KeepBase must keep the base line: {MergeEngine.BuildMergedContent(s)}");

            // Both sides' changes are discarded: dimmed, unmarked.
            foreach (var list in new[] { s.TripleA, s.TripleB })
            {
                var cell = list.First(l => l.Status is DiffStatus.ChangeA or DiffStatus.ChangeB);
                if (!cell.Rejected || cell.ResolvedConflict)
                    throw new Exception($"discarded side must be dimmed and unmarked: '{cell.Text}'");
            }

            // The kept base line is the winner: single mark on its neutral base cell.
            var win = s.TripleBase.Where(l => l.ResolvedConflict).ToList();
            if (win.Count != 1 || win[0].Status != DiffStatus.Unchanged || win[0].Text != "two" || win[0].Rejected)
                throw new Exception($"expected exactly the 'two' base row stamped, got {win.Count}");
            int r = s.TripleBase.IndexOf(win[0]);
            if (s.Conflicts[0].FirstResolvedRow != r)
                throw new Exception($"FirstResolvedRow={s.Conflicts[0].FirstResolvedRow}, winner at {r}");
            if (s.ResolvedCount != 1 || s.UnresolvedCount != 0 || !s.IsSaveable)
                throw new Exception($"counts: resolved={s.ResolvedCount} unresolved={s.UnresolvedCount} saveable={s.IsSaveable}");
            AssertInvariants(s);
        });

        Run("merge: insertion KeepBase drops both sides' insertions", () =>
        {
            var s = MergeEngine.CreateMerge(
                MF("base.txt", new() { "b0", "b1" }),
                MF("a.txt",    new() { "b0", "iA", "b1" }),
                MF("b.txt",    new() { "b0", "iB", "b1" }));

            MergeEngine.ResolveConflict(s, s.Conflicts[0], MergeResolution.KeepBase);
            MergeEngine.RebuildDisplay(s);

            if (MergeEngine.BuildMergedContent(s) != "b0\nb1")
                throw new Exception($"insertion KeepBase must land nothing: {MergeEngine.BuildMergedContent(s)}");
            // Nothing lands, but the decision is still marked — on the (empty) base cell — so
            // Shift+F3 can reach it; the dimmed A/B cells stay unmarked.
            var marks = s.TripleBase.Where(l => l.ResolvedConflict).ToList();
            if (marks.Count != 1 || marks[0].Text.Length != 0 || marks[0].Rejected)
                throw new Exception($"expected exactly one marked empty base row, got {marks.Count}");
            int r = s.TripleBase.IndexOf(marks[0]);
            if (s.Conflicts[0].FirstResolvedRow != r)
                throw new Exception($"FirstResolvedRow={s.Conflicts[0].FirstResolvedRow}, mark at {r}");
            if (s.TripleA.Any(l => l.ResolvedConflict) || s.TripleB.Any(l => l.ResolvedConflict))
                throw new Exception("dimmed A/B cells must stay unmarked");
            var aCell = s.TripleA.First(l => l.Text == "iA");
            var bCell = s.TripleB.First(l => l.Text == "iB");
            if (!aCell.Rejected || !bCell.Rejected) throw new Exception("both insertions must be dimmed");
            AssertInvariants(s);

            // The mark (and navigation target) survives the merged-output preview.
            s.ShowMergedOutput = true;
            MergeEngine.RebuildDisplay(s);
            if (!s.TripleBase[r].ResolvedConflict || s.Conflicts[0].FirstResolvedRow != r)
                throw new Exception("merged-output preview must keep the insertion KeepBase mark");
            AssertInvariants(s);
        });

        Run("merge: mixed TakeA + KeepBase in one block", () =>
        {
            // Two adjacent line conflicts → one block with two slots.
            var s = MergeEngine.CreateMerge(
                MF("base.txt", new() { "b0", "b1", "b2", "b3" }),
                MF("a.txt",    new() { "b0", "a1", "a2", "b3" }),
                MF("b.txt",    new() { "b0", "x1", "x2", "b3" }));

            var slotTargets = new List<(ConflictBlock, int)>();
            for (int r = 0; r < s.TripleA.Count; r++)
                if (s.ConflictLineAtRow(r) is { } t && !slotTargets.Contains(t)) slotTargets.Add(t);
            if (slotTargets.Count != 2) throw new Exception($"expected 2 slots, got {slotTargets.Count}");

            MergeEngine.ResolveConflictLines(s, new[] { slotTargets[0] }, MergeResolution.TakeA);
            MergeEngine.ResolveConflictLines(s, new[] { slotTargets[1] }, MergeResolution.KeepBase);
            MergeEngine.RebuildDisplay(s);

            if (MergeEngine.BuildMergedContent(s) != "b0\na1\nb2\nb3")
                throw new Exception($"mixed TakeA+KeepBase: [{MergeEngine.BuildMergedContent(s)}]");

            // Slot 0's winner is A's cell; slot 1's winner is the kept base line — both marked.
            var winA = s.TripleA.Where(l => l.ResolvedConflict).ToList();
            if (winA.Count != 1 || winA[0].Text != "a1")
                throw new Exception($"expected exactly a1 stamped in A pane, got {winA.Count}");
            int wr = s.TripleA.IndexOf(winA[0]);
            if (s.Conflicts[0].FirstResolvedRow != wr)
                throw new Exception($"FirstResolvedRow={s.Conflicts[0].FirstResolvedRow}, first winner at {wr}");
            var baseMarks = s.TripleBase.Where(l => l.ResolvedConflict).Select(l => l.Text).OrderBy(t => t).ToList();
            if (baseMarks.Count != 2 || baseMarks[0] != "b1" || baseMarks[1] != "b2")
                throw new Exception($"base pane must mark both winner rows, got [{string.Join(",", baseMarks)}]");
            if (s.TripleB.Any(l => l.ResolvedConflict)) throw new Exception("B pane must have no marks");
            AssertInvariants(s);
        });

        Run("merge: KeepBase can be reset back to unresolved", () =>
        {
            var baseL = new List<string> { "one", "two", "three" };
            var a = new List<string> { "one", "TWO-A", "three" };
            var b = new List<string> { "one", "TWO-B", "three" };
            var s = MergeEngine.CreateMerge(MF("base.txt", baseL), MF("a.txt", a), MF("b.txt", b));

            MergeEngine.ResolveConflict(s, s.Conflicts[0], MergeResolution.KeepBase);
            MergeEngine.RebuildDisplay(s);
            if (!s.TripleBase.Any(l => l.ResolvedConflict)) throw new Exception("KeepBase must mark the base row");

            MergeEngine.ResolveConflict(s, s.Conflicts[0], MergeResolution.Unresolved);
            MergeEngine.RebuildDisplay(s);
            foreach (var list in new[] { s.TripleA, s.TripleBase, s.TripleB })
                if (list.Any(l => l.ResolvedConflict)) throw new Exception("reset must clear all marks");
            if (!s.TripleBase.Any(l => l.Status == DiffStatus.Conflict))
                throw new Exception("reset must restore the purple conflict row");
            if (MergeEngine.BuildMergedContent(s) != null || s.UnresolvedCount != 1)
                throw new Exception("reset must block save again");
            AssertInvariants(s);
        });

        Run("merge: KeepBase in merged-output preview shows the kept line neutral", () =>
        {
            var baseL = new List<string> { "one", "two", "three" };
            var a = new List<string> { "one", "TWO-A", "three" };
            var b = new List<string> { "one", "TWO-B", "three" };
            var s = MergeEngine.CreateMerge(MF("base.txt", baseL), MF("a.txt", a), MF("b.txt", b));

            MergeEngine.ResolveConflict(s, s.Conflicts[0], MergeResolution.KeepBase);
            s.ShowMergedOutput = true;
            MergeEngine.RebuildDisplay(s);

            // The preview shows what lands: the original base line — neutral text, but it IS the
            // winner in this mode too, so it carries the mark (A/B cells are dimmed losers).
            var cell = s.TripleBase.First(l => l.Text == "two");
            if (cell.Status != DiffStatus.Unchanged || cell.Rejected)
                throw new Exception($"preview cell must be neutral 'two': {cell.Status} rejected={cell.Rejected}");
            var marks = s.TripleBase.Where(l => l.ResolvedConflict).ToList();
            if (marks.Count != 1 || marks[0].Text != "two")
                throw new Exception($"expected exactly the 'two' preview row marked, got {marks.Count}");
            int r = s.TripleBase.IndexOf(cell);
            if (s.Conflicts[0].FirstResolvedRow != r)
                throw new Exception($"FirstResolvedRow={s.Conflicts[0].FirstResolvedRow}, winner at {r}");
            var aCell = s.TripleA.First(l => l.Text == "TWO-A");
            var bCell = s.TripleB.First(l => l.Text == "TWO-B");
            if (!aCell.Rejected || !bCell.Rejected) throw new Exception("both sides must stay dimmed in preview");
            AssertInvariants(s);
        });

        // ── Undo/redo snapshots (MergeUndoStack + CaptureSnapshot/RestoreSnapshot) ──

        Run("undo: snapshot round-trips mixed resolutions, per-line rejects, and edit text", () =>
        {
            // One 2-slot conflict block + a B-only insertion unit elsewhere.
            var s = MergeEngine.CreateMerge(
                MF("base.txt", new() { "b0", "c1", "c2", "k0", "k1", "k2" }),
                MF("a.txt",    new() { "b0", "A1", "A2", "k0", "k1", "k2" }),
                MF("b.txt",    new() { "b0", "X1", "X2", "k0", "INS", "k1", "k2" }));
            if (s.Conflicts.Count != 1 || s.Changes.Count != 1)
                throw new Exception($"expected 1 block/1 unit, got {s.Conflicts.Count}/{s.Changes.Count}");
            var block = s.Conflicts[0];
            var ins = s.Changes[0];

            // Initial snapshot shape: per-slot Unresolved, empty reject set, no edit text.
            var snap0 = MergeEngine.CaptureSnapshot(s);
            if (snap0.BlockResolutions.Length != 1 || snap0.BlockResolutions[0].Length != 2)
                throw new Exception("snapshot must carry one 2-slot block");
            if (!snap0.BlockResolutions[0].All(r => r == MergeResolution.Unresolved))
                throw new Exception("initial resolutions must be Unresolved");
            if (snap0.ChangeRejectedLines.Length != 1 || snap0.ChangeRejectedLines[0].Length != 0)
                throw new Exception("initial reject set must be empty");
            if (snap0.ManualMergedOverride != null || snap0.ManualEditBaseline != null)
                throw new Exception("fresh merge has no edit text");

            // Mutate: mixed per-slot resolutions (OrderAB + TakeB), per-line insertion reject, edits.
            var slots = new List<(ConflictBlock, int)>();
            for (int r = 0; r < s.TripleA.Count; r++)
                if (s.ConflictLineAtRow(r) is { } t && !slots.Contains(t)) slots.Add(t);
            MergeEngine.ResolveConflictLines(s, new[] { slots[0] }, MergeResolution.OrderAB);
            MergeEngine.ResolveConflictLines(s, new[] { slots[1] }, MergeResolution.TakeB);
            if (block.ResolutionAt(0) != MergeResolution.OrderAB || block.ResolutionAt(1) != MergeResolution.TakeB)
                throw new Exception("mixed resolutions not applied");
            MergeEngine.SetChangeLinesAccepted(s, new[] { (ins, 0) }, false);
            if (MergeEngine.BuildMergedContent(s)!.Contains("INS"))
                throw new Exception("per-line reject must drop the inserted line");
            s.ManualMergedOverride = "edited text";
            s.ManualEditBaseline = "baseline text";

            var mutated = MergeEngine.CaptureSnapshot(s);
            if (mutated.ChangeRejectedLines[0].Length != 1 || mutated.ChangeRejectedLines[0][0] != 0)
                throw new Exception("capture must carry the rejected insertion line");

            // Restore the pre-mutation snapshot: everything back to initial.
            MergeEngine.RestoreSnapshot(s, snap0);
            if (block.ResolutionAt(0) != MergeResolution.Unresolved || block.ResolutionAt(1) != MergeResolution.Unresolved)
                throw new Exception("restore must clear resolutions");
            if (s.ManualMergedOverride != null || s.ManualEditBaseline != null)
                throw new Exception("restore must drop edit text");
            if (MergeEngine.BuildMergedContent(s) != null)
                throw new Exception("restored unresolved block must block save again");

            // Re-resolve: the rejected insertion line must be back (reject set was cleared).
            MergeEngine.ResolveConflict(s, block, MergeResolution.TakeA);
            if (!MergeEngine.BuildMergedContent(s)!.Contains("INS"))
                throw new Exception("restore must re-accept the rejected line");

            // Re-apply the same mutations — deterministic, so a fresh state restored from the
            // post-mutation snapshot reproduces it exactly.
            MergeEngine.ResolveConflictLines(s, new[] { slots[0] }, MergeResolution.OrderAB);
            MergeEngine.ResolveConflictLines(s, new[] { slots[1] }, MergeResolution.TakeB);
            MergeEngine.SetChangeLinesAccepted(s, new[] { (ins, 0) }, false);
            s.ManualMergedOverride = "edited text";
            s.ManualEditBaseline = "baseline text";

            var fresh = MergeEngine.CreateMerge(
                MF("base.txt", new() { "b0", "c1", "c2", "k0", "k1", "k2" }),
                MF("a.txt",    new() { "b0", "A1", "A2", "k0", "k1", "k2" }),
                MF("b.txt",    new() { "b0", "X1", "X2", "k0", "INS", "k1", "k2" }));
            MergeEngine.RestoreSnapshot(fresh, mutated);
            if (fresh.Conflicts[0].ResolutionAt(0) != MergeResolution.OrderAB || fresh.Conflicts[0].ResolutionAt(1) != MergeResolution.TakeB)
                throw new Exception("restore must reproduce mixed resolutions");
            if (MergeEngine.BuildMergedContent(fresh) is not { } freshContent || freshContent.Contains("INS"))
                throw new Exception("reject-set round-trip failed: line 0 must stay rejected");
            if (fresh.ManualMergedOverride != "edited text" || fresh.ManualEditBaseline != "baseline text")
                throw new Exception("edit text round-trip failed");

            // Snapshot arrays are copies — later state mutation must not leak into them.
            MergeEngine.ResolveConflict(s, block, MergeResolution.TakeA);
            if (mutated.BlockResolutions[0][0] != MergeResolution.OrderAB)
                throw new Exception("snapshot must be decoupled from live state (clone check)");
            AssertInvariants(fresh);
        });

        Run("undo: stack coalesces edits, ops never merge, pushes clear redo", () =>
        {
            MergeUndoStack Mk() => new();
            MergeSnapshot Snap(string tag) => new()
            {
                BlockResolutions = new[] { new[] { MergeResolution.Unresolved } },
                ChangeRejectedLines = Array.Empty<int[]>(),
                ManualMergedOverride = tag,
            };

            // A back-to-back typing burst coalesces into one step keeping the FIRST pre-state.
            var s1 = Mk();
            s1.Push(Snap("a"), isEdit: true);
            s1.Push(Snap("ab"), isEdit: true);
            s1.Push(Snap("abc"), isEdit: true);
            if (!s1.CanUndo || s1.CanRedo) throw new Exception("burst must be exactly one undo step");
            var burst = s1.PopUndo();
            if (burst is null || burst.ManualMergedOverride != "a")
                throw new Exception("coalesced entry must keep first pre-state");

            // An op right after an edit never coalesces — both steps survive in order.
            var s2 = Mk();
            s2.Push(Snap("edit"), isEdit: true);
            s2.Push(Snap("op"), isEdit: false);
            if (s2.PopUndo()!.ManualMergedOverride != "op") throw new Exception("top must be the op");
            if (s2.PopUndo()!.ManualMergedOverride != "edit") throw new Exception("op must not have merged into the edit");

            // Any new push clears redo; PopUndo/PopRedo move snapshots between stacks intact.
            var s3 = Mk();
            s3.Push(Snap("1"), isEdit: false);
            s3.Push(Snap("2"), isEdit: false);
            if (s3.PopUndo()!.ManualMergedOverride != "2") throw new Exception("PopUndo must return top");
            if (!s3.CanRedo) throw new Exception("redo expected after pop");
            if (s3.PopRedo()!.ManualMergedOverride != "2") throw new Exception("PopRedo must return the popped snapshot");

            // A redo entry re-pushed onto undo is NOT an edit — a following keystroke starts fresh.
            var s4 = Mk();
            s4.Push(Snap("1"), isEdit: false);
            s4.PopUndo();
            s4.PopRedo();
            s4.Push(Snap("2e"), isEdit: true);
            if (s4.PopUndo()!.ManualMergedOverride != "2e")
                throw new Exception("keystroke after a redo must be its own step, not coalesced");

            // History is capped: oldest entries evict first.
            var s5 = Mk();
            for (int i = 0; i < MergeUndoStack.MaxEntries + 5; i++) s5.Push(Snap(i.ToString()), isEdit: false);
            for (int i = MergeUndoStack.MaxEntries + 4; i >= 6; i--)
                if (s5.PopUndo()!.ManualMergedOverride != i.ToString())
                    throw new Exception($"pop order broken at {i}");
            var oldest = s5.PopUndo();
            if (oldest is null || oldest.ManualMergedOverride != "5")
                throw new Exception("expected the first 5 entries evicted, oldest is not '5'");
        });

        // ── CommandLineParser ─────────────────────────────────────────────────

        Run("CLI parse: empty args", () =>
        {
            var a = CommandLineParser.Parse(Array.Empty<string>());
            if (a.ShowHelp || a.Files.Count != 0 || a.OutputPath != null)
                throw new Exception($"help={a.ShowHelp} files=[{string.Join(",", a.Files)}] out={a.OutputPath}");
        });

        Run("CLI parse: one file", () =>
        {
            var a = CommandLineParser.Parse(new[] { "left.txt" });
            if (a.Files.Count != 1 || a.Files[0] != "left.txt" || a.OutputPath != null)
                throw new Exception($"files=[{string.Join(",", a.Files)}] out={a.OutputPath}");
        });

        Run("CLI parse: two files in order", () =>
        {
            var a = CommandLineParser.Parse(new[] { "l.txt", "r.txt" });
            if (a.Files.Count != 2 || a.Files[0] != "l.txt" || a.Files[1] != "r.txt")
                throw new Exception($"files=[{string.Join(",", a.Files)}]");
        });

        Run("CLI parse: three files = merge triple, null output", () =>
        {
            var a = CommandLineParser.Parse(new[] { "base", "a", "b" });
            if (a.Files.Count != 3 || a.OutputPath != null)
                throw new Exception($"files=[{string.Join(",", a.Files)}] out={a.OutputPath}");
        });

        Run("CLI parse: fourth positional becomes output", () =>
        {
            var a = CommandLineParser.Parse(new[] { "base", "a", "b", "out.txt" });
            if (a.Files.Count != 3 || a.OutputPath != "out.txt")
                throw new Exception($"files=[{string.Join(",", a.Files)}] out={a.OutputPath}");
        });

        Run("CLI parse: -o value", () =>
        {
            var a = CommandLineParser.Parse(new[] { "base", "a", "b", "-o", "out.txt" });
            if (a.Files.Count != 3 || a.OutputPath != "out.txt")
                throw new Exception($"files=[{string.Join(",", a.Files)}] out={a.OutputPath}");
        });

        Run("CLI parse: --out / --output long forms", () =>
        {
            var a = CommandLineParser.Parse(new[] { "base", "--out", "o1.txt" });
            if (a.OutputPath != "o1.txt") throw new Exception($"--out → {a.OutputPath}");
            var b = CommandLineParser.Parse(new[] { "base", "--output", "o2.txt" });
            if (b.OutputPath != "o2.txt") throw new Exception($"--output → {b.OutputPath}");
        });

        Run("CLI parse: unknown flags ignored", () =>
        {
            var a = CommandLineParser.Parse(new[] { "--verbose", "l.txt", "-x", "r.txt" });
            if (a.Files.Count != 2 || a.Files[0] != "l.txt" || a.Files[1] != "r.txt")
                throw new Exception($"files=[{string.Join(",", a.Files)}]");
        });

        Run("CLI parse: -o with no value does not throw", () =>
        {
            var a = CommandLineParser.Parse(new[] { "base", "-o" });
            if (a.OutputPath != null || a.Files.Count != 1)
                throw new Exception($"files=[{string.Join(",", a.Files)}] out={a.OutputPath}");
        });

        Run("CLI parse: -o does not swallow following flag", () =>
        {
            var a = CommandLineParser.Parse(new[] { "-o", "--help" });
            if (a.OutputPath != null || !a.ShowHelp)
                throw new Exception($"out={a.OutputPath} help={a.ShowHelp}");
        });

        Run("CLI parse: -h / --HELP set ShowHelp, files still parsed", () =>
        {
            var a = CommandLineParser.Parse(new[] { "-h", "l.txt" });
            if (!a.ShowHelp || a.Files.Count != 1) throw new Exception($"help={a.ShowHelp} files=[{string.Join(",", a.Files)}]");
            var b = CommandLineParser.Parse(new[] { "--HELP" });
            if (!b.ShowHelp) throw new Exception("--HELP must be case-insensitive");
        });

        Run("CLI parse: five positionals capped at 3 + output", () =>
        {
            var a = CommandLineParser.Parse(new[] { "base", "a", "b", "out.txt", "extra" });
            if (a.Files.Count != 3 || a.OutputPath != "out.txt")
                throw new Exception($"files=[{string.Join(",", a.Files)}] out={a.OutputPath}");
        });

        Run("CLI parse: explicit -o wins over fourth positional", () =>
        {
            var a = CommandLineParser.Parse(new[] { "base", "a", "b", "pos4.txt", "-o", "flag.txt" });
            if (a.OutputPath != "flag.txt") throw new Exception($"out={a.OutputPath}");
            if (a.Files.Count != 3) throw new Exception($"files=[{string.Join(",", a.Files)}]");
        });

        Run("CLI parse: lone dash ignored", () =>
        {
            var a = CommandLineParser.Parse(new[] { "-", "l.txt" });
            if (a.Files.Count != 1 || a.Files[0] != "l.txt")
                throw new Exception($"files=[{string.Join(",", a.Files)}]");
        });

        Run("custom colours: enabled role recolours its whole family", () =>
        {
            var o = new ThemeColorOverrides { Added = { Enabled = true, Hex = "#123456" } };
            var t = DiffViewerTheme.Create("Awful", o);
            var c = System.Windows.Media.Color.FromRgb(0x12, 0x34, 0x56);
            if (t.FgAdded.Color != c) throw new Exception($"FgAdded {t.FgAdded.Color}");
            if (t.AccentAdded.Color != c) throw new Exception("AccentAdded not recoloured");
            if (t.DiffMapAdded.Color != c) throw new Exception("DiffMapAdded not recoloured");
            var bg = t.BgAdded.Color;
            if (bg.A != 0x30 || bg.R != 0x12 || bg.G != 0x34 || bg.B != 0x56)
                throw new Exception($"BgAdded tint {bg}");
            // Disabled role stays on the theme default.
            if (t.FgRemoved.Color == c) throw new Exception("FgRemoved should be untouched");
        });

        Run("custom colours: merge candidates hit both A and B families", () =>
        {
            var o = new ThemeColorOverrides { MergeCandidates = { Enabled = true, Hex = "#ABCDEF" } };
            var t = DiffViewerTheme.Create("SystemDark", o);
            var c = System.Windows.Media.Color.FromRgb(0xAB, 0xCD, 0xEF);
            if (t.FgChangeA.Color != c || t.FgChangeB.Color != c) throw new Exception("Change fg not recoloured");
            if (t.AccentChangeA.Color != c || t.AccentChangeB.Color != c) throw new Exception("Change accent not recoloured");
            if (t.BgChangeA.Color != t.BgChangeB.Color) throw new Exception("Change bg should match");
        });

        Run("custom colours: disabled or invalid hex leaves theme defaults", () =>
        {
            var o = new ThemeColorOverrides
            {
                Text = { Enabled = true, Hex = "not-a-colour" },
                Conflicts = { Enabled = false, Hex = "#112233" }
            };
            var t = DiffViewerTheme.Create("SystemLight", o);
            var baseT = DiffViewerTheme.Create("SystemLight");
            if (t.FgUnchanged.Color != baseT.FgUnchanged.Color) throw new Exception("invalid hex must be ignored");
            if (t.FgConflict.Color != baseT.FgConflict.Color) throw new Exception("disabled role must be ignored");

            // null overrides = pure theme
            var t2 = DiffViewerTheme.Create("SystemLight", null);
            if (t2.FgAdded.Color != baseT.FgAdded.Color) throw new Exception("null overrides must leave defaults");
        });

        Run("TryParseHex: strict #RRGGBB / #AARRGGBB only", () =>
        {
            if (!DiffViewerTheme.TryParseHex("#ff00aa", out var c1) || c1 != System.Windows.Media.Color.FromRgb(255, 0, 170))
                throw new Exception("lowercase #RRGGBB must parse");
            if (!DiffViewerTheme.TryParseHex("FF00AA", out var c2) || c2.A != 0xFF)
                throw new Exception("missing # must parse with opaque alpha");
            if (!DiffViewerTheme.TryParseHex("#80FF00AA", out var c3) || c3.A != 0x80)
                throw new Exception("#AARRGGBB must keep alpha");
            if (DiffViewerTheme.TryParseHex("red", out _)) throw new Exception("named colours must be rejected");
            if (DiffViewerTheme.TryParseHex("#12345", out _)) throw new Exception("5 digits must be rejected");
            if (DiffViewerTheme.TryParseHex("", out _)) throw new Exception("empty must be rejected");
        });

        // ── Byte-level hex diff (ByteDiff engine + rowizer + sync points) ────────────────

        Run("byte: empty/identical/disjoint basics", () =>
        {
            if (!ByteDiff.TryCompute(Array.Empty<byte>(), Array.Empty<byte>(), ByteDiff.MaxWorkCells, out var mb0, out var ma0))
                throw new Exception("empty vs empty must succeed");
            if (mb0.Length != 0 || ma0.Length != 0) throw new Exception("empty match arrays must be empty");

            var id = new byte[] { 1, 2, 3, 4, 5 };
            if (!ByteDiff.TryCompute(id, (byte[])id.Clone(), ByteDiff.MaxWorkCells, out var mbI, out var maI))
                throw new Exception("identical must succeed");
            for (int i = 0; i < id.Length; i++)
                if (mbI[i] != i || maI[i] != i) throw new Exception($"identical byte {i} not matched in place");

            var da = new byte[] { 1, 2, 3 };
            var db = new byte[] { 9, 8, 7 };
            if (!ByteDiff.TryCompute(da, db, ByteDiff.MaxWorkCells, out var mbD, out var maD))
                throw new Exception("disjoint must succeed");
            for (int i = 0; i < da.Length; i++)
                if (mbD[i] != -1 || maD[i] != -1) throw new Exception($"disjoint byte {i} unexpectedly matched");

            if (!ByteDiff.TryCompute(da, Array.Empty<byte>(), ByteDiff.MaxWorkCells, out var mbE, out var maE))
                throw new Exception("vs-empty must succeed");
            for (int i = 0; i < da.Length; i++)
                if (mbE[i] != -1) throw new Exception("vs-empty must match nothing");
            if (maE.Length != 0) throw new Exception("vs-empty matchA must be empty");
        });

        Run("byte: 3-byte insertion at offset 40 leaves later matches intact", () =>
        {
            var a = new byte[150];
            for (int i = 0; i < a.Length; i++) a[i] = (byte)i;      // all distinct, none == 0xEE
            var b = new byte[153];
            Array.Copy(a, 0, b, 0, 40);
            b[40] = b[41] = b[42] = 0xEE;
            Array.Copy(a, 40, b, 43, 110);

            if (!ByteDiff.TryCompute(a, b, ByteDiff.MaxWorkCells, out var mb, out var ma))
                throw new Exception("compute failed");
            if (mb[100] != 103) throw new Exception($"matchB[100]={mb[100]}, expected 103");
            int unmatchedB = 0;
            for (int j = 0; j < b.Length; j++)
                if (ma[j] == -1)
                {
                    unmatchedB++;
                    if (j < 40 || j > 42) throw new Exception($"unexpectedly unmatched B byte at {j}");
                }
            if (unmatchedB != 3) throw new Exception($"expected exactly 3 unmatched B bytes, got {unmatchedB}");
            for (int i = 0; i < a.Length; i++)
                if (mb[i] == -1) throw new Exception($"A byte {i} unexpectedly unmatched");
        });

        Run("byte: budget overflow returns false; generous budget succeeds", () =>
        {
            var a = new byte[1000];
            var b = new byte[1000];
            Array.Fill(a, (byte)0x01);
            Array.Fill(b, (byte)0x02);

            if (ByteDiff.TryCompute(a, b, 100_000, out _, out _))
                throw new Exception("100k-cell budget must be exceeded by 1000 disjoint bytes");

            if (!ByteDiff.TryCompute(a, b, ByteDiff.MaxWorkCells, out var mb, out var ma))
                throw new Exception("MaxWorkCells must cover the same input");
            for (int i = 0; i < a.Length; i++)
                if (mb[i] != -1 || ma[i] != -1) throw new Exception("disjoint bytes must all be unmatched");
        });

        Run("byte: prefix/suffix strip isolates a single changed byte in 100k", () =>
        {
            var a = new byte[100_000];
            for (int i = 0; i < a.Length; i++) a[i] = (byte)(i % 256);
            var b = (byte[])a.Clone();
            b[50_000] ^= 0xFF;

            if (!ByteDiff.TryCompute(a, b, ByteDiff.MaxWorkCells, out var mb, out var ma))
                throw new Exception("strip fast path must succeed");
            for (int i = 0; i < a.Length; i++)
            {
                int expect = i == 50_000 ? -1 : i;
                if (mb[i] != expect) throw new Exception($"matchB[{i}]={mb[i]}, expected {expect}");
            }
            if (ma[50_000] != -1) throw new Exception("changed B byte must be unmatched");
        });

        Run("rowizer: 32 identical bytes → 2 Unchanged rows, full masks", () =>
        {
            var a = new byte[32];
            for (int i = 0; i < 32; i++) a[i] = (byte)i;
            if (!ByteDiff.TryCompute(a, (byte[])a.Clone(), ByteDiff.MaxWorkCells, out var mb, out var ma))
                throw new Exception("compute failed");
            var (rowsA, rowsB) = ByteDiff.BuildAlignedRows(a, 0, a.Length, a, 0, a.Length, mb, ma, true);

            if (rowsA.Count != 2 || rowsB.Count != 2) throw new Exception($"expected 2 rows, got {rowsA.Count}/{rowsB.Count}");
            for (int r = 0; r < 2; r++)
            {
                var la = rowsA[r]; var lb = rowsB[r];
                if (la.Status != DiffStatus.Unchanged || lb.Status != DiffStatus.Unchanged)
                    throw new Exception($"row {r} must be Unchanged on both sides, got {la.Status}/{lb.Status}");
                if (la.ByteStart != r * 16 || lb.ByteStart != r * 16)
                    throw new Exception($"row {r} ByteStart must be {r * 16}, got {la.ByteStart}/{lb.ByteStart}");
                if (la.HexSlotMask != 0xFFFF || lb.HexSlotMask != 0xFFFF)
                    throw new Exception($"row {r} mask must be full, got {la.HexSlotMask:X4}/{lb.HexSlotMask:X4}");
                if (la.Text is not { Length: 76 }) throw new Exception("aligned row must be exactly 76 columns");
                if (la.Text.Contains('\0')) throw new Exception("aligned row must not contain NUL padding");
            }
            if (!rowsA[1].Text!.StartsWith("00000010")) throw new Exception("row 2 offset must render as 00000010");
        });

        Run("rowizer: mid-row insertion flags only the affected rows", () =>
        {
            var a = new byte[150];
            for (int i = 0; i < a.Length; i++) a[i] = (byte)i;
            var b = new byte[153];
            Array.Copy(a, 0, b, 0, 40);
            b[40] = b[41] = b[42] = 0xEE;
            Array.Copy(a, 40, b, 43, 110);

            if (!ByteDiff.TryCompute(a, b, ByteDiff.MaxWorkCells, out var mb, out var ma))
                throw new Exception("compute failed");
            var (rowsA, rowsB) = ByteDiff.BuildAlignedRows(a, 0, a.Length, b, 0, b.Length, mb, ma, true);

            if (rowsA.Count != 10 || rowsB.Count != 10) throw new Exception($"expected 10 rows, got {rowsA.Count}/{rowsB.Count}");
            for (int r = 0; r < 10; r++)
                if (rowsA[r].Status != DiffStatus.Unchanged)
                    throw new Exception($"side A row {r} must be Unchanged (every A byte matched), got {rowsA[r].Status}");

            // The inserted bytes land in B row 2 slots 8-10 — only that B row is flagged.
            if (rowsB[2].Status != DiffStatus.Modified) throw new Exception($"row 2 B must be Modified, got {rowsB[2].Status}");
            for (int r = 0; r < 10; r++)
                if (r != 2 && rowsB[r].Status != DiffStatus.Unchanged)
                    throw new Exception($"side B row {r} must be Unchanged, got {rowsB[r].Status}");

            // A row 2 holds a[32..39] in slots 0-7 and a[40..44] in slots 11-15 (holes at 8-10);
            // B row 2 is full.
            if (rowsA[2].ByteStart != 32 || rowsA[2].HexSlotMask != 0xF8FF)
                throw new Exception($"A row 2 start/mask must be 32/F8FF, got {rowsA[2].ByteStart}/{rowsA[2].HexSlotMask:X4}");
            if (rowsB[2].ByteStart != 32 || rowsB[2].HexSlotMask != 0xFFFF)
                throw new Exception($"B row 2 start/mask must be 32/FFFF, got {rowsB[2].ByteStart}/{rowsB[2].HexSlotMask:X4}");

            // Slot 8 of B row 2 must hold the first inserted byte at file index 40.
            int bFileIdx = HexFormat.SlotFileIndex(rowsB[2].ByteStart.Value, rowsB[2].HexSlotMask, 8);
            if (bFileIdx != 40 || b[bFileIdx] != 0xEE)
                throw new Exception($"slot 8 of B row 2 must hold the inserted byte at file index 40, got {bFileIdx}");

            // Last row is partial: 9 bytes each side (A starts at 141 — three slots were holes).
            if (rowsA[9].HexSlotMask != 0x1FF || rowsB[9].HexSlotMask != 0x1FF)
                throw new Exception($"last row mask must be 0x1FF, got {rowsA[9].HexSlotMask:X4}/{rowsB[9].HexSlotMask:X4}");
            if (rowsA[9].ByteStart != 141 || rowsB[9].ByteStart != 144)
                throw new Exception($"last row ByteStarts must be 141/144, got {rowsA[9].ByteStart}/{rowsB[9].ByteStart}");
        });

        Run("rowizer: last partial row (17 bytes → row 2 mask 0x1)", () =>
        {
            var a = new byte[17];
            for (int i = 0; i < 17; i++) a[i] = (byte)i;
            if (!ByteDiff.TryCompute(a, (byte[])a.Clone(), ByteDiff.MaxWorkCells, out var mb, out var ma))
                throw new Exception("compute failed");
            var (rowsA, rowsB) = ByteDiff.BuildAlignedRows(a, 0, a.Length, a, 0, a.Length, mb, ma, true);

            if (rowsA.Count != 2 || rowsB.Count != 2) throw new Exception($"expected 2 rows, got {rowsA.Count}");
            var l = rowsA[1];
            if (l.Status != DiffStatus.Unchanged || l.HexSlotMask != 0x1 || l.ByteStart != 16)
                throw new Exception($"partial row must be Unchanged mask 0x1 start 16, got {l.Status}/{l.HexSlotMask:X4}/{l.ByteStart}");
        });

        Run("rowizer: pure deletion block → Removed(A)+Gap(B), symmetric Added+Gap", () =>
        {
            // Deletion: b drops a[16..31].
            var a = new byte[64];
            for (int i = 0; i < 64; i++) a[i] = (byte)i;
            var bDel = new byte[48];
            Array.Copy(a, 0, bDel, 0, 16);
            Array.Copy(a, 32, bDel, 16, 32);
            if (!ByteDiff.TryCompute(a, bDel, ByteDiff.MaxWorkCells, out var mb, out var ma))
                throw new Exception("compute failed");
            var (rowsA, rowsB) = ByteDiff.BuildAlignedRows(a, 0, a.Length, bDel, 0, bDel.Length, mb, ma, true);

            if (rowsA.Count != 4 || rowsB.Count != 4) throw new Exception($"expected 4 rows, got {rowsA.Count}/{rowsB.Count}");
            if (rowsA[1].Status != DiffStatus.Removed || rowsA[1].HexSlotMask != 0xFFFF || rowsA[1].ByteStart != 16)
                throw new Exception($"deletion row must be Removed full-mask start 16, got {rowsA[1].Status}/{rowsA[1].HexSlotMask:X4}/{rowsA[1].ByteStart}");
            if (rowsB[1].Status != DiffStatus.Gap || rowsB[1].Text != "" || rowsB[1].ByteStart != null || rowsB[1].HexSlotMask != 0)
                throw new Exception($"gap row must be empty (Text='', ByteStart=null, mask 0), got {rowsB[1].Status}/{rowsB[1].Text}/{{null}}/{rowsB[1].HexSlotMask:X4}");
            if (rowsA[2].Status != DiffStatus.Unchanged || rowsB[2].Status != DiffStatus.Unchanged)
                throw new Exception("rows after the deletion must be Unchanged again");
            if (rowsA[3].ByteStart != 48 || rowsB[3].ByteStart != 32)
                throw new Exception($"final row starts must be 48/32, got {rowsA[3].ByteStart}/{rowsB[3].ByteStart}");

            // Symmetric: b gains 16 bytes at offset 16.
            var extra = new byte[16];
            Array.Fill(extra, (byte)0xC0);
            var bIns = new byte[80];
            Array.Copy(a, 0, bIns, 0, 16);
            Array.Copy(extra, 0, bIns, 16, 16);
            Array.Copy(a, 16, bIns, 32, 48);
            if (!ByteDiff.TryCompute(a, bIns, ByteDiff.MaxWorkCells, out var mb2, out var ma2))
                throw new Exception("compute failed");
            var (ra2, rb2) = ByteDiff.BuildAlignedRows(a, 0, a.Length, bIns, 0, bIns.Length, mb2, ma2, true);

            if (ra2.Count != 5 || rb2.Count != 5) throw new Exception($"expected 5 rows, got {ra2.Count}/{rb2.Count}");
            if (ra2[1].Status != DiffStatus.Gap || ra2[1].ByteStart != null)
                throw new Exception($"insertion must leave a Gap on A, got {ra2[1].Status}");
            if (rb2[1].Status != DiffStatus.Added || rb2[1].HexSlotMask != 0xFFFF || rb2[1].ByteStart != 16)
                throw new Exception($"insertion row must be Added full-mask start 16, got {rb2[1].Status}/{rb2[1].HexSlotMask:X4}/{rb2[1].ByteStart}");
            if (ra2[2].Status != DiffStatus.Unchanged || rb2[2].Status != DiffStatus.Unchanged)
                throw new Exception("rows after the insertion must be Unchanged again");
        });

        Run("rowizer: very different sizes (16 vs 1024, B starts with A)", () =>
        {
            var a = new byte[16];
            for (int i = 0; i < 16; i++) a[i] = (byte)i;
            var b = new byte[1024];
            for (int i = 0; i < b.Length; i++) b[i] = (byte)i;   // B starts with A, all distinct

            if (!ByteDiff.TryCompute(a, b, ByteDiff.MaxWorkCells, out var mb, out var ma))
                throw new Exception("compute failed");
            var (rowsA, rowsB) = ByteDiff.BuildAlignedRows(a, 0, a.Length, b, 0, b.Length, mb, ma, true);

            if (rowsA.Count != 64 || rowsB.Count != 64) throw new Exception($"expected 64 rows, got {rowsA.Count}/{rowsB.Count}");
            if (rowsA[0].Status != DiffStatus.Unchanged || rowsB[0].Status != DiffStatus.Unchanged)
                throw new Exception("row 0 must be Unchanged on both sides");
            for (int r = 1; r < 64; r++)
            {
                if (rowsA[r].Status != DiffStatus.Gap || rowsA[r].ByteStart != null)
                    throw new Exception($"A row {r} must be Gap, got {rowsA[r].Status}");
                if (rowsB[r].Status != DiffStatus.Added || rowsB[r].HexSlotMask != 0xFFFF || rowsB[r].ByteStart != r * 16)
                    throw new Exception($"B row {r} must be Added full-mask start {r * 16}, got {rowsB[r].Status}/{rowsB[r].HexSlotMask:X4}/{rowsB[r].ByteStart}");
            }
        });

        Run("rowizer: equal-but-unpaired bytes demote to Match", () =>
        {
            // Hand-built match arrays (all unpaired) — the rowizer must demote X slots whose
            // bytes are equal, so an all-equal row is Unchanged, not Modified.
            var a = new byte[] { 5, 7 };
            var b = new byte[] { 5, 7 };
            var none = new int[] { -1, -1 };
            var (rowsA, rowsB) = ByteDiff.BuildAlignedRows(a, 0, 2, b, 0, 2, none, none, true);
            if (rowsA[0].Status != DiffStatus.Unchanged || rowsB[0].Status != DiffStatus.Unchanged)
                throw new Exception($"equal unpaired bytes must demote to Unchanged, got {rowsA[0].Status}/{rowsB[0].Status}");

            // One equal + one different → Modified, regions only for the differing slot (1).
            var a2 = new byte[] { 5, 6 };
            var b2 = new byte[] { 5, 7 };
            var (ra2, rb2) = ByteDiff.BuildAlignedRows(a2, 0, 2, b2, 0, 2, none, none, true);
            if (ra2[0].Status != DiffStatus.Modified || rb2[0].Status != DiffStatus.Modified)
                throw new Exception($"mixed row must be Modified, got {ra2[0].Status}/{rb2[0].Status}");
            string Regs(List<(int start, int length)>? g) =>
                g == null ? "" : string.Join(",", g.Select(t => $"{t.start}-{t.length}"));
            string expect = $"{HexFormat.HexSlotColumns(1).Col}-2,{HexFormat.AsciiColumn(1)}-1";
            if (Regs(ra2[0].ChangedRegions) != expect || Regs(rb2[0].ChangedRegions) != expect)
                throw new Exception($"slot-1 regions must be [{expect}], got A=[{Regs(ra2[0].ChangedRegions)}] B=[{Regs(rb2[0].ChangedRegions)}]");
        });

        Run("rowizer: ChangedRegions = hex + ascii columns of the changed slot only", () =>
        {
            // One byte inserted at file offset 5 → it occupies slot 5 of row 0 on side B.
            var a = new byte[16];
            for (int i = 0; i < 16; i++) a[i] = (byte)i;
            var b = new byte[17];
            Array.Copy(a, 0, b, 0, 5);
            b[5] = 0xAB;
            Array.Copy(a, 5, b, 6, 11);

            if (!ByteDiff.TryCompute(a, b, ByteDiff.MaxWorkCells, out var mb, out var ma))
                throw new Exception("compute failed");
            var (rowsA, rowsB) = ByteDiff.BuildAlignedRows(a, 0, a.Length, b, 0, b.Length, mb, ma, true);

            if (rowsA.Count != 2 || rowsB.Count != 2) throw new Exception($"expected 2 rows, got {rowsA.Count}/{rowsB.Count}");
            if (rowsA[0].Status != DiffStatus.Unchanged)
                throw new Exception($"side A row 0 must be Unchanged, got {rowsA[0].Status}");
            if (rowsB[0].Status != DiffStatus.Modified)
                throw new Exception($"side B row 0 must be Modified, got {rowsB[0].Status}");

            var regions = rowsB[0].ChangedRegions;
            if (regions is not { Count: 2 })
                throw new Exception($"expected exactly 2 regions, got {(regions == null ? "null" : regions.Count.ToString())}");
            int hexCol = HexFormat.HexSlotColumns(5).Col;   // 25
            if (regions[0] != (hexCol, 2) || regions[1] != (HexFormat.AsciiColumn(5), 1))
                throw new Exception($"regions must be [(25,2),(65,1)], got [{regions[0].start},{regions[0].length}],[{regions[1].start},{regions[1].length}]");

            // The trailing matched byte lands in row 1, single slot each side.
            if (rowsA[1].HexSlotMask != 0x1 || rowsB[1].HexSlotMask != 0x1)
                throw new Exception($"row 1 must be a single slot, got {rowsA[1].HexSlotMask:X4}/{rowsB[1].HexSlotMask:X4}");
            if (rowsA[1].ByteStart != 15 || rowsB[1].ByteStart != 16)
                throw new Exception($"row 1 starts must be 15/16, got {rowsA[1].ByteStart}/{rowsB[1].ByteStart}");
        });

        Run("positional: stamps ByteStart=i*16 with full masks; size mismatch → Gap/Added", () =>
        {
            var a = new byte[32];
            for (int i = 0; i < 32; i++) a[i] = (byte)i;
            var b = new byte[32];
            Array.Fill(b, (byte)0x64);   // every byte differs

            var (rowsA, rowsB) = ByteDiff.BuildPositionalRows(a, 0, a.Length, b, 0, b.Length, true);
            if (rowsA.Count != 2 || rowsB.Count != 2) throw new Exception($"expected 2 rows, got {rowsA.Count}");
            for (int r = 0; r < 2; r++)
            {
                if (rowsA[r].Status != DiffStatus.Modified || rowsB[r].Status != DiffStatus.Modified)
                    throw new Exception($"row {r} must be Modified on both sides, got {rowsA[r].Status}/{rowsB[r].Status}");
                if (rowsA[r].ByteStart != r * 16 || rowsB[r].ByteStart != r * 16)
                    throw new Exception($"row {r} ByteStart must be {r * 16}");
                if (rowsA[r].HexSlotMask != 0xFFFF || rowsB[r].HexSlotMask != 0xFFFF)
                    throw new Exception($"row {r} masks must be full");
            }
            // All 16 slots changed: 16 hex pairs + one merged ascii run (60,16).
            var regions = rowsA[0].ChangedRegions!;
            if (regions.Count != 17) throw new Exception($"expected 17 merged regions, got {regions.Count}");
            if (regions[^1] != (60, 16))
                throw new Exception($"ascii run must merge to (60,16), got ({regions[^1].start},{regions[^1].length})");

            // Size mismatch: A has one full row, B has three → extra rows are Gap/Added.
            var a3 = a.Take(16).ToArray();
            var b3 = new byte[48];
            Array.Fill(b3, (byte)0x77);
            var (ra3, rb3) = ByteDiff.BuildPositionalRows(a3, 0, a3.Length, b3, 0, b3.Length, false);
            if (ra3.Count != 3 || rb3.Count != 3) throw new Exception($"expected 3 rows, got {ra3.Count}/{rb3.Count}");
            for (int r = 1; r < 3; r++)
            {
                if (ra3[r].Status != DiffStatus.Gap || ra3[r].ByteStart != null)
                    throw new Exception($"A row {r} must be Gap, got {ra3[r].Status}");
                if (rb3[r].Status != DiffStatus.Added || rb3[r].HexSlotMask != 0xFFFF || rb3[r].ByteStart != r * 16)
                    throw new Exception($"B row {r} must be Added full-mask start {r * 16}, got {rb3[r].Status}/{rb3[r].HexSlotMask:X4}/{rb3[r].ByteStart}");
            }
        });

        Run("sync: unequal-byte anchor is honored in the same row slot", () =>
        {
            // Natural alignment would pair a[2]↔b[1], a[3]↔b[2] and leave a[1]/b[3] as an X pair.
            // The anchor forces a[1]=2 ↔ b[3]=2 (unequal neighbours around it) instead.
            var a = new byte[] { 1, 2, 3, 4 };
            var b = new byte[] { 1, 3, 4, 2 };
            var (rowsA, rowsB, fallbacks) = ByteDiff.ComputeWithSyncPoints(a, b, new[] { (1, 3) }, true);

            if (fallbacks != 0) throw new Exception($"no segment may fall back, got {fallbacks}");
            if (rowsA.Count != 2 || rowsB.Count != 2) throw new Exception($"expected 2 rows, got {rowsA.Count}/{rowsB.Count}");

            // Segment 1 row: A holds slots 0 and 3, B is full.
            if (rowsA[0].Status != DiffStatus.Unchanged || rowsA[0].HexSlotMask != 0x9)
                throw new Exception($"A row 0 must be Unchanged mask 0x9, got {rowsA[0].Status}/{rowsA[0].HexSlotMask:X4}");
            if (rowsB[0].Status != DiffStatus.Modified || rowsB[0].HexSlotMask != 0xF)
                throw new Exception($"B row 0 must be Modified mask 0xF, got {rowsB[0].Status}/{rowsB[0].HexSlotMask:X4}");

            // The anchored bytes must sit in the SAME slot (3) on both sides.
            int aIdx = HexFormat.SlotFileIndex(rowsA[0].ByteStart.Value, rowsA[0].HexSlotMask, 3);
            int bIdx = HexFormat.SlotFileIndex(rowsB[0].ByteStart.Value, rowsB[0].HexSlotMask, 3);
            if (aIdx != 1 || a[aIdx] != 2) throw new Exception($"A slot 3 must hold anchor byte a[1]=2, got index {aIdx}");
            if (bIdx != 3 || b[bIdx] != 2) throw new Exception($"B slot 3 must hold anchor byte b[3]=2, got index {bIdx}");

            // Trailing segment: the bytes after the anchor are a pure deletion.
            if (rowsA[1].Status != DiffStatus.Removed || rowsA[1].HexSlotMask != 0x3 || rowsA[1].ByteStart != 2)
                throw new Exception($"A row 1 must be Removed mask 0x3 start 2, got {rowsA[1].Status}/{rowsA[1].HexSlotMask:X4}/{rowsA[1].ByteStart}");
            if (rowsB[1].Status != DiffStatus.Gap) throw new Exception($"B row 1 must be Gap, got {rowsB[1].Status}");
        });

        Run("sync: IsAnchorSetOrdered rejects crossing and duplicate anchors", () =>
        {
            if (!ByteDiff.IsAnchorSetOrdered(new[] { (1, 2), (5, 9) })) throw new Exception("co-monotone set must be accepted");
            if (ByteDiff.IsAnchorSetOrdered(new[] { (1, 5), (5, 2) })) throw new Exception("crossing anchors must be rejected");
            if (ByteDiff.IsAnchorSetOrdered(new[] { (3, 1), (3, 4) })) throw new Exception("duplicate ByteA must be rejected");
            if (ByteDiff.IsAnchorSetOrdered(new[] { (2, 2), (2, 2) })) throw new Exception("identical anchor pair must be rejected");
            if (!ByteDiff.IsAnchorSetOrdered(Array.Empty<(int, int)>())) throw new Exception("empty set must be accepted");
            if (!ByteDiff.IsAnchorSetOrdered(new[] { (0, 0) })) throw new Exception("single anchor must be accepted");
        });

        Run("sync: adjacent anchors → 1×1 segment, no crash", () =>
        {
            var a = new byte[] { 7, 8, 9 };
            var b = new byte[] { 7, 5, 9 };
            var (rowsA, rowsB, fallbacks) = ByteDiff.ComputeWithSyncPoints(a, b, new[] { (0, 0), (1, 2) }, true);

            if (fallbacks != 0) throw new Exception($"no segment may fall back, got {fallbacks}");
            if (rowsA.Count != 3 || rowsB.Count != 3) throw new Exception($"expected 3 rows, got {rowsA.Count}/{rowsB.Count}");

            // Row 1 carries the anchor a[1]=8 ↔ b[2]=9 in slot 1 on both sides.
            if (rowsA[1].HexSlotMask != 0x2 || rowsA[1].ByteStart != 1)
                throw new Exception($"A row 1 must be mask 0x2 start 1, got {rowsA[1].HexSlotMask:X4}/{rowsA[1].ByteStart}");
            if (rowsB[1].HexSlotMask != 0x3 || rowsB[1].ByteStart != 1)
                throw new Exception($"B row 1 must be mask 0x3 start 1, got {rowsB[1].HexSlotMask:X4}/{rowsB[1].ByteStart}");
            int aIdx = HexFormat.SlotFileIndex(rowsA[1].ByteStart.Value, rowsA[1].HexSlotMask, 1);
            int bIdx = HexFormat.SlotFileIndex(rowsB[1].ByteStart.Value, rowsB[1].HexSlotMask, 1);
            if (aIdx != 1 || a[aIdx] != 8) throw new Exception($"A slot 1 must hold anchor byte a[1]=8, got index {aIdx}");
            if (bIdx != 2 || b[bIdx] != 9) throw new Exception($"B slot 1 must hold anchor byte b[2]=9, got index {bIdx}");

            // The unanchored trailing byte a[2] is a deletion.
            if (rowsA[2].Status != DiffStatus.Removed || rowsB[2].Status != DiffStatus.Gap)
                throw new Exception($"row 2 must be Removed/Gap, got {rowsA[2].Status}/{rowsB[2].Status}");
        });

        Run("sync: first-byte anchor", () =>
        {
            var a = new byte[] { 3, 4, 5 };
            var b = new byte[] { 9, 3, 4, 5 };   // B has a leading junk byte
            var (rowsA, rowsB, fallbacks) = ByteDiff.ComputeWithSyncPoints(a, b, new[] { (0, 1) }, true);

            if (fallbacks != 0) throw new Exception($"no segment may fall back, got {fallbacks}");
            if (rowsA.Count != 2 || rowsB.Count != 2) throw new Exception($"expected 2 rows, got {rowsA.Count}/{rowsB.Count}");

            // Row 0: anchor a[0]=3 ↔ b[1]=3 in slot 1; B's junk byte occupies slot 0.
            if (rowsA[0].Status != DiffStatus.Unchanged || rowsA[0].HexSlotMask != 0x2)
                throw new Exception($"A row 0 must be Unchanged mask 0x2, got {rowsA[0].Status}/{rowsA[0].HexSlotMask:X4}");
            if (rowsB[0].Status != DiffStatus.Modified || rowsB[0].HexSlotMask != 0x3)
                throw new Exception($"B row 0 must be Modified mask 0x3, got {rowsB[0].Status}/{rowsB[0].HexSlotMask:X4}");
            int aIdx = HexFormat.SlotFileIndex(rowsA[0].ByteStart.Value, rowsA[0].HexSlotMask, 1);
            int bIdx = HexFormat.SlotFileIndex(rowsB[0].ByteStart.Value, rowsB[0].HexSlotMask, 1);
            if (aIdx != 0 || a[aIdx] != 3) throw new Exception($"A slot 1 must hold anchor byte a[0]=3, got index {aIdx}");
            if (bIdx != 1 || b[bIdx] != 3) throw new Exception($"B slot 1 must hold anchor byte b[1]=3, got index {bIdx}");

            // Trailing segment: the remaining bytes match in place.
            if (rowsA[1].Status != DiffStatus.Unchanged || rowsB[1].Status != DiffStatus.Unchanged)
                throw new Exception($"row 1 must be Unchanged on both sides, got {rowsA[1].Status}/{rowsB[1].Status}");
        });

        Run("fuzz: byte swap equivalence on 200 random repetitive cases", () =>
        {
            var rng = new Random(424242);
            byte[] alphabet = { 0x41, 0x42, 0x43, 0x44, 0x45 };   // small alphabet → ambiguity
            for (int iter = 0; iter < 200; iter++)
            {
                var baseBytes = new List<byte>();
                int len = rng.Next(1, 26);
                for (int i = 0; i < len; i++) baseBytes.Add(alphabet[rng.Next(alphabet.Length)]);
                var a = baseBytes.ToArray();
                var b = baseBytes.ToList();

                int muts = rng.Next(0, 7);
                for (int m = 0; m < muts; m++)
                {
                    int op = rng.Next(3);
                    if (op == 0 && b.Count > 0)
                        b[rng.Next(b.Count)] = alphabet[rng.Next(alphabet.Length)];
                    else if (op == 1)
                        b.Insert(rng.Next(b.Count + 1), alphabet[rng.Next(alphabet.Length)]);
                    else if (b.Count > 0)
                        b.RemoveAt(rng.Next(b.Count));
                }

                try
                {
                    AssertByteSwapEquivalence(a, b.ToArray());
                }
                catch (Exception ex)
                {
                    throw new Exception($"iter {iter}: a=[{string.Join(",", a.Select(x => x.ToString("X")))}] " +
                                        $"b=[{string.Join(",", b.Select(x => x.ToString("X")))}] — {ex.Message}");
                }
            }
        });

        Run("per-byte: single in-place change → HexModifiedSlots = that slot only", () =>
        {
            var a = new byte[32];
            for (int i = 0; i < a.Length; i++) a[i] = (byte)i;
            var b = (byte[])a.Clone();
            b[5] = 0xEE;   // one in-place substitution

            if (!ByteDiff.TryCompute(a, b, ByteDiff.MaxWorkCells, out var mb, out var ma))
                throw new Exception("compute failed");
            var (rowsA, rowsB) = ByteDiff.BuildAlignedRows(a, 0, a.Length, b, 0, b.Length, mb, ma, true);

            if (rowsA.Count != 2 || rowsB.Count != 2) throw new Exception($"expected 2 rows, got {rowsA.Count}");
            // Row 0: slot 5 differs → both sides Modified with only bit 5 in HexModifiedSlots
            // (a substitution is KX — yellow on both sides, no add/remove).
            if (rowsA[0].Status != DiffStatus.Modified || rowsB[0].Status != DiffStatus.Modified)
                throw new Exception($"row 0 must be Modified both sides, got {rowsA[0].Status}/{rowsB[0].Status}");
            if (rowsA[0].HexModifiedSlots != 0x20 || rowsB[0].HexModifiedSlots != 0x20)
                throw new Exception($"row 0 modified slots must be 0x20 (bit 5), got {rowsA[0].HexModifiedSlots:X}/{rowsB[0].HexModifiedSlots:X}");
            if (rowsA[0].HexAddedSlots != 0 || rowsA[0].HexRemovedSlots != 0
                || rowsB[0].HexAddedSlots != 0 || rowsB[0].HexRemovedSlots != 0)
                throw new Exception($"row 0 must have no added/removed slots, got A+{rowsA[0].HexAddedSlots:X}/-{rowsA[0].HexRemovedSlots:X} B+{rowsB[0].HexAddedSlots:X}/-{rowsB[0].HexRemovedSlots:X}");
            // Row 1: all match → Unchanged, no changed slots.
            if (rowsA[1].Status != DiffStatus.Unchanged || rowsB[1].Status != DiffStatus.Unchanged)
                throw new Exception($"row 1 must be Unchanged, got {rowsA[1].Status}/{rowsB[1].Status}");
            if ((rowsA[1].HexAddedSlots | rowsA[1].HexRemovedSlots | rowsA[1].HexModifiedSlots) != 0
                || (rowsB[1].HexAddedSlots | rowsB[1].HexRemovedSlots | rowsB[1].HexModifiedSlots) != 0)
                throw new Exception($"row 1 changed slots must be 0, got A {rowsA[1].HexAddedSlots:X}/{rowsA[1].HexRemovedSlots:X}/{rowsA[1].HexModifiedSlots:X} B {rowsB[1].HexAddedSlots:X}/{rowsB[1].HexRemovedSlots:X}/{rowsB[1].HexModifiedSlots:X}");
        });

        Run("per-byte: equal-length substitution → no add/remove holes, changed bytes are Modified", () =>
        {
            // Regression for the user's 0x180 report: a local equal-length byte replacement must
            // render as per-byte Modified (yellow), NOT as staggered add/remove holes. The tail
            // repeats the differing byte values in a different order so LCS can match them across
            // positions — the condition that made the raw walk emit KDel/KIns cells where both
            // files actually have data. After the shuffle, no row may carry an add/remove mask or
            // a gap hole; the changed bytes appear as HexModifiedSlots on both sides.
            var a = new byte[] { 0x75,0x72,0x67,0x6C,0x61,0x72,0x79, 0,0,0,0, (byte)0xF0,(byte)0xCA,(byte)0xA6,0x43,(byte)0xB7,
                                 (byte)0xB7,(byte)0xF0,(byte)0xCA,(byte)0xA6, 1,2,3,4 };
            var b = new byte[] { 0x75,0x72,0x67,0x6C,0x61,0x72,0x79, 0,0,0,0, (byte)0xC6,(byte)0x74,(byte)0x92,0x43,(byte)0x71,
                                 (byte)0x71,(byte)0xC6,(byte)0x74,(byte)0x92, 1,2,3,4 };

            if (!ByteDiff.TryCompute(a, b, ByteDiff.MaxWorkCells, out var mb, out var ma))
                throw new Exception("compute failed");
            var (rowsA, rowsB) = ByteDiff.BuildAlignedRows(a, 0, a.Length, b, 0, b.Length, mb, ma, true);

            if (rowsA.Count != rowsB.Count) throw new Exception($"row counts differ {rowsA.Count}/{rowsB.Count}");
            for (int r = 0; r < rowsA.Count; r++)
            {
                // Nothing was added or removed → no add/remove masks and no gap holes anywhere.
                if ((rowsA[r].HexAddedSlots | rowsA[r].HexRemovedSlots | rowsA[r].HexGapSlots) != 0
                    || (rowsB[r].HexAddedSlots | rowsB[r].HexRemovedSlots | rowsB[r].HexGapSlots) != 0)
                    throw new Exception($"row {r} must have no add/remove holes, got A+{rowsA[r].HexAddedSlots:X}/-{rowsA[r].HexRemovedSlots:X}/g{rowsA[r].HexGapSlots:X} B+{rowsB[r].HexAddedSlots:X}/-{rowsB[r].HexRemovedSlots:X}/g{rowsB[r].HexGapSlots:X}");
                // Both panes stay dense (every slot occupied) — no mid-row blanks.
                if (rowsA[r].HexSlotMask != rowsB[r].HexSlotMask || rowsA[r].HexSlotMask == 0)
                    throw new Exception($"row {r} must be dense and row-aligned, got A{rowsA[r].HexSlotMask:X}/B{rowsB[r].HexSlotMask:X}");
            }

            // The genuinely-different bytes are marked Modified on both sides — e.g. slot 11 of
            // row 0 (F0 vs C6) and slot 0 of row 1 (B7 vs 71).
            if ((rowsA[0].HexModifiedSlots & 0x800) == 0 || (rowsB[0].HexModifiedSlots & 0x800) == 0)
                throw new Exception($"row 0 slot 11 must be Modified both sides, got A{rowsA[0].HexModifiedSlots:X}/B{rowsB[0].HexModifiedSlots:X}");
            if ((rowsA[1].HexModifiedSlots & 0x1) == 0 || (rowsB[1].HexModifiedSlots & 0x1) == 0)
                throw new Exception($"row 1 slot 0 must be Modified both sides, got A{rowsA[1].HexModifiedSlots:X}/B{rowsB[1].HexModifiedSlots:X}");
        });

        Run("per-byte: interleaved isolated indels in a balanced region → no holes (generalized shuffle)", () =>
        {
            // Regression for the Show-Gaps phantom-hole report. A repeated byte value makes the LCS
            // match it non-locally, so the raw walk emits ISOLATED KDel/KIns cells separated by a
            // match cell — e.g. [1,2,3] vs [2,1,3] yields KDel, KMatch, KIns (the '2' or '1' matched
            // across positions). The old contiguous-run shuffle left these alone because the indels
            // weren't adjacent; the generalized excursion re-pairing must flatten the whole net-
            // balanced region into positional substitutions. Nothing was added or removed, so no row
            // may carry an add/remove mask or a gap hole.
            var a = new byte[] { 1, 2, 3 };
            var b = new byte[] { 2, 1, 3 };

            if (!ByteDiff.TryCompute(a, b, ByteDiff.MaxWorkCells, out var mb, out var ma))
                throw new Exception("compute failed");
            var (rowsA, rowsB) = ByteDiff.BuildAlignedRows(a, 0, a.Length, b, 0, b.Length, mb, ma, true);

            if (rowsA.Count != 1 || rowsB.Count != 1) throw new Exception($"expected 1 row, got {rowsA.Count}/{rowsB.Count}");
            // No add/remove holes or gap holes anywhere; both panes dense.
            if ((rowsA[0].HexAddedSlots | rowsA[0].HexRemovedSlots | rowsA[0].HexGapSlots) != 0
                || (rowsB[0].HexAddedSlots | rowsB[0].HexRemovedSlots | rowsB[0].HexGapSlots) != 0)
                throw new Exception($"no add/remove holes allowed, got A+{rowsA[0].HexAddedSlots:X}/-{rowsA[0].HexRemovedSlots:X}/g{rowsA[0].HexGapSlots:X} B+{rowsB[0].HexAddedSlots:X}/-{rowsB[0].HexRemovedSlots:X}/g{rowsB[0].HexGapSlots:X}");
            if (rowsA[0].HexSlotMask != 0x7 || rowsB[0].HexSlotMask != 0x7)
                throw new Exception($"both panes must be dense (0x7), got A{rowsA[0].HexSlotMask:X}/B{rowsB[0].HexSlotMask:X}");
            // Positionally: slot0 (1 vs 2) and slot1 (2 vs 1) are Modified; slot2 (3 vs 3) matches.
            if (rowsA[0].HexModifiedSlots != 0x3 || rowsB[0].HexModifiedSlots != 0x3)
                throw new Exception($"slots 0-1 must be Modified both sides, got A{rowsA[0].HexModifiedSlots:X}/B{rowsB[0].HexModifiedSlots:X}");
        });

        Run("per-byte: insertion flags only the inserted bytes on B, none on A", () =>
        {
            var a = new byte[32];
            for (int i = 0; i < a.Length; i++) a[i] = (byte)i;
            var bList = new List<byte>(a);
            bList.Insert(8, 0xEE); bList.Insert(9, 0xFF);   // two inserted bytes at offset 8
            var b = bList.ToArray();

            if (!ByteDiff.TryCompute(a, b, ByteDiff.MaxWorkCells, out var mb, out var ma))
                throw new Exception("compute failed");
            var (rowsA, rowsB) = ByteDiff.BuildAlignedRows(a, 0, a.Length, b, 0, b.Length, mb, ma, true);

            if (rowsA.Count != 3 || rowsB.Count != 3) throw new Exception($"expected 3 rows, got {rowsA.Count}");
            // Row 0: A has holes at slots 8-9 (no byte of its own changed) → Unchanged, no masks.
            //         B holds the two inserted bytes at slots 8-9 → Modified, HexAddedSlots = bits 8|9.
            if (rowsA[0].Status != DiffStatus.Unchanged
                || (rowsA[0].HexAddedSlots | rowsA[0].HexRemovedSlots | rowsA[0].HexModifiedSlots) != 0)
                throw new Exception($"A row 0 must be Unchanged with no masks, got {rowsA[0].Status} {rowsA[0].HexAddedSlots:X}/{rowsA[0].HexRemovedSlots:X}/{rowsA[0].HexModifiedSlots:X}");
            if (rowsB[0].Status != DiffStatus.Modified || rowsB[0].HexAddedSlots != 0x300)
                throw new Exception($"B row 0 must be Modified with added 0x300, got {rowsB[0].Status}/{rowsB[0].HexAddedSlots:X}");
            if (rowsB[0].HexRemovedSlots != 0 || rowsB[0].HexModifiedSlots != 0)
                throw new Exception($"B row 0 must have no removed/modified slots, got -{rowsB[0].HexRemovedSlots:X}/M{rowsB[0].HexModifiedSlots:X}");
            // Every other row is fully matched → Unchanged, no changed slots.
            for (int r = 1; r < 3; r++)
                if (rowsA[r].Status != DiffStatus.Unchanged || rowsB[r].Status != DiffStatus.Unchanged
                    || (rowsA[r].HexAddedSlots | rowsA[r].HexRemovedSlots | rowsA[r].HexModifiedSlots) != 0
                    || (rowsB[r].HexAddedSlots | rowsB[r].HexRemovedSlots | rowsB[r].HexModifiedSlots) != 0)
                    throw new Exception($"row {r} must be Unchanged with no masks, got A={rowsA[r].Status}/{rowsA[r].HexAddedSlots:X}/{rowsA[r].HexRemovedSlots:X}/{rowsA[r].HexModifiedSlots:X} B={rowsB[r].Status}/{rowsB[r].HexAddedSlots:X}/{rowsB[r].HexRemovedSlots:X}/{rowsB[r].HexModifiedSlots:X}");
        });

        Run("auto-resync: small insertion does not cascade the rest of the file", () =>
        {
            int Pop(int x) { int n = 0; while (x != 0) { n += x & 1; x >>= 1; } return n; }
            var a = new byte[4096];
            for (int i = 0; i < a.Length; i++) a[i] = (byte)(i & 0xFF);
            var bList = new List<byte>(a);
            bList.Insert(2048, 0x01); bList.Insert(2049, 0x02);   // 2-byte insertion mid-file
            var b = bList.ToArray();

            const long budget = 3_000;   // below the ~4096 strip work → full range fails its budget

            // Without resync: positional fallback compares row i vs row i, so everything after the
            // insertion is offset and cascades into one giant Modified block.
            var (pa, pb, pf) = ByteDiff.ComputeWithSyncPoints(a, b, Array.Empty<(int, int)>(), false, budget, autoResync: false);
            int posModA = pa.Count(l => l.Status == DiffStatus.Modified);
            int posChgB = pb.Sum(l => Pop(l.HexAddedSlots | l.HexRemovedSlots | l.HexModifiedSlots));
            if (pf < 1) throw new Exception($"expected a positional fallback, got fb={pf}");
            if (posModA < 100 || posChgB < 1000)
                throw new Exception($"positional should cascade the second half, got modA={posModA} chgB={posChgB}");

            // With resync: side A stays fully clean and only a handful of bytes are flagged on B.
            var (ra, rb, rf) = ByteDiff.ComputeWithSyncPoints(a, b, Array.Empty<(int, int)>(), false, budget, autoResync: true);
            int reModA = ra.Count(l => l.Status == DiffStatus.Modified);
            int reChgB = rb.Sum(l => Pop(l.HexAddedSlots | l.HexRemovedSlots | l.HexModifiedSlots));
            if (rf != 0) throw new Exception($"resync should avoid the positional fallback, got fb={rf}");
            if (reModA != 0) throw new Exception($"side A must have no Modified rows, got {reModA}");
            if (reChgB >= posChgB) throw new Exception($"resync must flag far fewer bytes than positional, got chgB={reChgB} vs {posChgB}");
            if (reChgB > 16) throw new Exception($"resync should flag only the inserted bytes (+boundary), got chgB={reChgB}");
        });

        Run("auto-resync: fully-different equal-length file → all Modified, every byte covered once", () =>
        {
            int Pop(int x) { int n = 0; while (x != 0) { n += x & 1; x >>= 1; } return n; }
            var c = new byte[128]; for (int i = 0; i < c.Length; i++) c[i] = (byte)i;
            var d = new byte[128]; for (int i = 0; i < d.Length; i++) d[i] = (byte)(128 + i);

            var (ra, rb, _) = ByteDiff.ComputeWithSyncPoints(c, d, Array.Empty<(int, int)>(), false, cellBudget: 64, autoResync: true);
            if (ra.Count != rb.Count) throw new Exception($"row counts differ {ra.Count}/{rb.Count}");
            for (int r = 0; r < ra.Count; r++)
                if (ra[r].Status != DiffStatus.Modified || rb[r].Status != DiffStatus.Modified)
                    throw new Exception($"row {r} must be Modified both sides, got {ra[r].Status}/{rb[r].Status}");

            // Contiguous coverage: each side's bytes appear exactly once, in file order.
            void CheckContig(List<DiffLine> rows, string side)
            {
                int expected = 0;
                for (int r = 0; r < rows.Count; r++)
                {
                    if (rows[r].ByteStart != expected)
                        throw new Exception($"{side} row {r} ByteStart must be {expected}, got {rows[r].ByteStart}");
                    expected += Pop(rows[r].HexSlotMask);
                }
                if (expected != 128) throw new Exception($"{side} coverage must be 128, got {expected}");
            }
            CheckContig(ra, "A");
            CheckContig(rb, "B");
        });

        Run("fixed rows: 16-byte alignment, dense masks, equal length (incl. trailing Gap)", () =>
        {
            var a = new byte[24]; for (int i = 0; i < a.Length; i++) a[i] = (byte)(i & 0xFF);          // 2 rows: 16+8
            var b = new byte[48]; for (int i = 0; i < b.Length; i++) b[i] = (byte)(0x40 + (i & 0xFF)); // 3 rows, disjoint

            if (!ByteDiff.TryCompute(a, b, ByteDiff.MaxWorkCells, out var mb, out var ma))
                throw new Exception("compute failed");
            var (alA, alB) = ByteDiff.BuildAlignedRows(a, 0, a.Length, b, 0, b.Length, mb, ma, true);
            var (fA, fB) = ByteDiff.BuildFixedAddressRows(a, a.Length, b, b.Length, alA, alB);

            int maxLines = HexFormat.BytesToHexLines(Math.Max(a.Length, b.Length));   // 3
            if (fA.Count != maxLines || fB.Count != maxLines)
                throw new Exception($"expected {maxLines} rows each side, got A={fA.Count} B={fB.Count}");

            void Check(IReadOnlyList<DiffLine> rows, int len, string side)
            {
                for (int k = 0; k < rows.Count; k++)
                {
                    var row = rows[k];
                    int start = k * HexFormat.BytesPerLine;
                    int count = Math.Min(HexFormat.BytesPerLine, Math.Max(0, len - start));
                    if (count == 0)
                    {
                        // Shorter file's trailing row: no bytes → null ByteStart, empty mask.
                        if (row.ByteStart != null || row.HexSlotMask != 0)
                            throw new Exception($"{side} row {k} must be empty (ByteStart null, mask 0), got {row.ByteStart}/{row.HexSlotMask:X}");
                    }
                    else
                    {
                        if (row.ByteStart != start)
                            throw new Exception($"{side} row {k} ByteStart must be {start}, got {row.ByteStart}");
                        int expected = (1 << count) - 1;   // dense contiguous low-bit run → no internal gaps
                        if (row.HexSlotMask != expected)
                            throw new Exception($"{side} row {k} mask must be dense {expected:X}, got {row.HexSlotMask:X}");
                    }
                }
            }
            Check(fA, a.Length, "A");
            Check(fB, b.Length, "B");
        });

        Run("fixed rows: per-byte trichotomy repacked to file addresses", () =>
        {
            int Added(DiffLine l) => l.HexAddedSlots;
            int Removed(DiffLine l) => l.HexRemovedSlots;
            int Modified(DiffLine l) => l.HexModifiedSlots;
            bool AnyAddRemove(DiffLine l) => (l.HexAddedSlots | l.HexRemovedSlots) != 0;

            (IReadOnlyList<DiffLine> A, IReadOnlyList<DiffLine> B) Fixed(byte[] a, byte[] b)
            {
                if (!ByteDiff.TryCompute(a, b, ByteDiff.MaxWorkCells, out var mb, out var ma))
                    throw new Exception("compute failed");
                var (alA, alB) = ByteDiff.BuildAlignedRows(a, 0, a.Length, b, 0, b.Length, mb, ma, true);
                return ByteDiff.BuildFixedAddressRows(a, a.Length, b, b.Length, alA, alB);
            }

            // (1) Substitution at address 3 → modified bit 3 on BOTH sides; no add/remove anywhere.
            {
                var a = new byte[16]; for (int i = 0; i < 16; i++) a[i] = (byte)i;
                var b = (byte[])a.Clone(); b[3] = 0xEE;
                var f = Fixed(a, b);
                if ((Modified(f.A[0]) & 0x08) == 0 || (Modified(f.B[0]) & 0x08) == 0)
                    throw new Exception($"substitution → modified bit 3 both sides, got A {Modified(f.A[0]):X} B {Modified(f.B[0]):X}");
                if (AnyAddRemove(f.A[0]) || AnyAddRemove(f.B[0]))
                    throw new Exception($"substitution must not set added/removed, got A +{Added(f.A[0]):X}/-{Removed(f.A[0]):X} B +{Added(f.B[0]):X}/-{Removed(f.B[0]):X}");
            }

            // (2) Insertion of one byte at address 3 → B added bit 3; A carries no colour at all.
            {
                var a = new byte[16]; for (int i = 0; i < 16; i++) a[i] = (byte)i;
                var bl = new List<byte>(a); bl.Insert(3, 0xEE); var b = bl.ToArray();   // 17 bytes
                var f = Fixed(a, b);
                if ((Added(f.B[0]) & 0x08) == 0)
                    throw new Exception($"insertion → B added bit 3, got +{Added(f.B[0]):X}");
                if (AnyAddRemove(f.A[0]) || Modified(f.A[0]) != 0)
                    throw new Exception($"insertion must leave A masks clear, got A +{Added(f.A[0]):X}/-{Removed(f.A[0]):X}/M{Modified(f.A[0]):X}");
                if (AnyAddRemove(f.B[0]) && Added(f.B[0]) != 0x08)   // only the one added byte, nothing else
                    throw new Exception($"B must carry only the added bit, got +{Added(f.B[0]):X}/-{Removed(f.B[0]):X}/M{Modified(f.B[0]):X}");
            }

            // (3) Removal of A's byte at address 3 → A removed bit 3; B carries no colour.
            {
                var a = new byte[16]; for (int i = 0; i < 16; i++) a[i] = (byte)i;     // full [0..15]
                var bl = new List<byte>(); for (int i = 0; i < 16; i++) if (i != 3) bl.Add((byte)i);  // drop index 3 → 15 bytes
                var b = bl.ToArray();
                var f = Fixed(a, b);
                if ((Removed(f.A[0]) & 0x08) == 0)
                    throw new Exception($"removal → A removed bit 3, got -{Removed(f.A[0]):X}");
                if (AnyAddRemove(f.B[0]) || Modified(f.B[0]) != 0)
                    throw new Exception($"removal must leave B masks clear, got B +{Added(f.B[0]):X}/-{Removed(f.B[0]):X}/M{Modified(f.B[0]):X}");
                if (Modified(f.A[0]) != 0)
                    throw new Exception($"A must carry only the removed bit, got -{Removed(f.A[0]):X}/M{Modified(f.A[0]):X}");
            }
        });

        Run("fixed rows: R5 cascade — post-insertion rows are positionally Modified but uncoloured", () =>
        {
            int Pop(int x) { int n = 0; while (x != 0) { n += x & 1; x >>= 1; } return n; }
            var a = new byte[64]; for (int i = 0; i < 64; i++) a[i] = (byte)(i & 0xFF);
            var bl = new List<byte>(a); bl.Insert(32, 0xEE); var b = bl.ToArray();   // 65 bytes, insert at addr 32

            if (!ByteDiff.TryCompute(a, b, ByteDiff.MaxWorkCells, out var mb, out var ma))
                throw new Exception("compute failed");
            var (alA, alB) = ByteDiff.BuildAlignedRows(a, 0, a.Length, b, 0, b.Length, mb, ma, true);
            var (fA, fB) = ByteDiff.BuildFixedAddressRows(a, a.Length, b, b.Length, alA, alB);

            // The inserted byte sits at file address 32 → B row 2 (32/16), bit 0 (32%16).
            if ((fB[2].HexAddedSlots & 1) == 0)
                throw new Exception($"inserted byte must colour B row 2 bit 0, got +{fB[2].HexAddedSlots:X}");

            // R5: rows after the insertion are positionally Modified (whole-row) yet carry no colour
            // mask — they're merely misaligned by the earlier insert.
            bool sawUnmodifiedModified = false;
            for (int k = 3; k < fA.Count; k++)
            {
                bool posMod = fA[k].Status == DiffStatus.Modified || fB[k].Status == DiffStatus.Modified;
                int colour = Pop(fA[k].HexAddedSlots | fA[k].HexRemovedSlots | fA[k].HexModifiedSlots)
                           + Pop(fB[k].HexAddedSlots | fB[k].HexRemovedSlots | fB[k].HexModifiedSlots);
                if (posMod && colour == 0) sawUnmodifiedModified = true;
            }
            if (!sawUnmodifiedModified)
                throw new Exception("expected a post-insertion row to be positionally Modified with no coloured bytes (R5 cascade)");
        });

        // ── Directory diff (Services/DirectoryDiff.cs) ────────────────────────────

        Run("DirDiff: identical trees → all Identical, folders NoDifferences", () =>
        {
            var (a, b, cleanup) = MakeDirPair();
            try
            {
                WriteFile(a, "x.txt", "hello");
                WriteFile(a, Path.Combine("sub", "y.txt"), "world");
                WriteFile(b, "x.txt", "hello");
                WriteFile(b, Path.Combine("sub", "y.txt"), "world");

                var r = DirectoryDiff.Compare(a, b);
                if (r.FilesCompared != 2 || r.IdenticalFiles != 2) throw new Exception($"counts: {r.FilesCompared}/{r.IdenticalFiles}");
                if (r.DifferentFiles != 0 || r.OnlyInLeftFiles != 0 || r.OnlyInRightFiles != 0) throw new Exception("unexpected non-identical counts");

                var fx = FindEntry(r.RootEntries, "x.txt");
                var fy = FindEntry(r.RootEntries, Path.Combine("sub", "y.txt"));
                if (fx?.FileStatus != FileEntryStatus.Identical) throw new Exception($"x.txt: {fx?.FileStatus}");
                if (fy?.FileStatus != FileEntryStatus.Identical) throw new Exception($"sub/y.txt: {fy?.FileStatus}");

                var sub = FindEntry(r.RootEntries, "sub");
                if (sub?.FolderStatus != FolderStatus.NoDifferences) throw new Exception($"sub: {sub?.FolderStatus}");
            }
            finally { cleanup(); }
        });

        Run("DirDiff: same size, different content → Different", () =>
        {
            var (a, b, cleanup) = MakeDirPair();
            try
            {
                WriteFile(a, "f.txt", "aaaa");
                WriteFile(b, "f.txt", "bbbb");
                // Back-to-back writes can land in the same filesystem timestamp tick (~1ms on NTFS),
                // which would take the size+time fast path and call this pair Identical without
                // reading bytes. Force distinct mtimes so the byte comparison is what's under test.
                File.SetLastWriteTime(Path.Combine(b, "f.txt"), DateTime.Now.AddSeconds(-1));
                var r = DirectoryDiff.Compare(a, b);
                if (r.DifferentFiles != 1 || r.IdenticalFiles != 0) throw new Exception($"counts: {r.DifferentFiles}/{r.IdenticalFiles}");
                var f = FindEntry(r.RootEntries, "f.txt");
                if (f?.FileStatus != FileEntryStatus.Different) throw new Exception($"status: {f?.FileStatus}");
            }
            finally { cleanup(); }
        });

        Run("DirDiff: size differs → Different", () =>
        {
            var (a, b, cleanup) = MakeDirPair();
            try
            {
                WriteFile(a, "f.txt", "aaaa");
                WriteFile(b, "f.txt", "bb");
                var r = DirectoryDiff.Compare(a, b);
                if (r.DifferentFiles != 1) throw new Exception($"different: {r.DifferentFiles}");
                var f = FindEntry(r.RootEntries, "f.txt");
                if (f?.FileStatus != FileEntryStatus.Different) throw new Exception($"status: {f?.FileStatus}");
            }
            finally { cleanup(); }
        });

        Run("DirDiff: one-sided files carry null path / -1 size on the absent side", () =>
        {
            var (a, b, cleanup) = MakeDirPair();
            try
            {
                WriteFile(a, "onlyA.txt", "left");
                WriteFile(b, "onlyB.txt", "right");
                var r = DirectoryDiff.Compare(a, b);
                if (r.OnlyInLeftFiles != 1 || r.OnlyInRightFiles != 1) throw new Exception($"counts: {r.OnlyInLeftFiles}/{r.OnlyInRightFiles}");

                var la = FindEntry(r.RootEntries, "onlyA.txt");
                if (la?.FileStatus != FileEntryStatus.OnlyInLeft) throw new Exception($"status: {la?.FileStatus}");
                if (la?.PathLeft == null || la?.PathRight != null) throw new Exception("onlyA paths wrong");
                if (la!.SizeRight != -1) throw new Exception("onlyA SizeRight should be -1");

                var rb = FindEntry(r.RootEntries, "onlyB.txt");
                if (rb?.FileStatus != FileEntryStatus.OnlyInRight) throw new Exception($"status: {rb?.FileStatus}");
                if (rb?.PathLeft != null || rb?.PathRight == null) throw new Exception("onlyB paths wrong");
            }
            finally { cleanup(); }
        });

        Run("DirDiff: nested difference propagates HasDifferences to parent folders", () =>
        {
            var (a, b, cleanup) = MakeDirPair();
            try
            {
                WriteFile(a, Path.Combine("sub", "deep.txt"), "A");
                WriteFile(b, Path.Combine("sub", "deep.txt"), "B");
                // Same tick caveat as the same-size test: force distinct mtimes so deep.txt is
                // decided by its bytes, not the size+time fast path.
                File.SetLastWriteTime(Path.Combine(b, "sub", "deep.txt"), DateTime.Now.AddSeconds(-1));
                WriteFile(a, Path.Combine("other", "same.txt"), "x");
                WriteFile(b, Path.Combine("other", "same.txt"), "x");

                var r = DirectoryDiff.Compare(a, b);
                var sub = FindEntry(r.RootEntries, "sub");
                var other = FindEntry(r.RootEntries, "other");
                if (sub?.FolderStatus != FolderStatus.HasDifferences) throw new Exception($"sub: {sub?.FolderStatus}");
                if (other?.FolderStatus != FolderStatus.NoDifferences) throw new Exception($"other: {other?.FolderStatus}");
            }
            finally { cleanup(); }
        });

        Run("DirDiff: case-insensitive name pairing (A.txt vs a.txt)", () =>
        {
            var (a, b, cleanup) = MakeDirPair();
            try
            {
                WriteFile(a, "Alpha.txt", "left");
                WriteFile(b, "alpha.txt", "right");
                var r = DirectoryDiff.Compare(a, b);
                if (r.FilesCompared != 1) throw new Exception($"expected 1 paired file, got {r.FilesCompared}");

                var f = FindEntry(r.RootEntries, "Alpha.txt");   // left-side spelling preferred
                if (f == null) throw new Exception("paired entry not found under the left spelling");
                if (f.FileStatus != FileEntryStatus.Different) throw new Exception($"status: {f.FileStatus}");
                if (f.PathLeft == null || f.PathRight == null) throw new Exception("both paths expected");
            }
            finally { cleanup(); }
        });

        Run("DirDiff: folder on one side only is a leaf with FolderStatus OnlyIn*", () =>
        {
            var (a, b, cleanup) = MakeDirPair();
            try
            {
                Directory.CreateDirectory(Path.Combine(a, "onlyA"));
                WriteFile(a, Path.Combine("onlyA", "inner.txt"), "x");   // contents must NOT be enumerated
                var r = DirectoryDiff.Compare(a, b);

                var f = FindEntry(r.RootEntries, "onlyA");
                if (f?.FolderStatus != FolderStatus.OnlyInLeft) throw new Exception($"status: {f?.FolderStatus}");
                if (f!.PathRight != null) throw new Exception("PathRight should be null");
                if (f.Children.Count != 0) throw new Exception($"one-sided folder must be a leaf, has {f.Children.Count} children");
            }
            finally { cleanup(); }
        });

        Run("DirDiff filter: differences-only prunes identical files and childless folders", () =>
        {
            var (a, b, cleanup) = MakeDirPair();
            try
            {
                WriteFile(a, "same.txt", "x");  WriteFile(b, "same.txt", "x");
                WriteFile(a, "diff.txt", "A");  WriteFile(b, "diff.txt", "B");
                WriteFile(a, Path.Combine("sub", "same2.txt"), "y");  WriteFile(b, Path.Combine("sub", "same2.txt"), "y");
                WriteFile(a, Path.Combine("sub", "diff2.txt"), "A");  WriteFile(b, Path.Combine("sub", "diff2.txt"), "B");
                // Back-to-back writes can share a filesystem timestamp tick (~1ms on NTFS) — the
                // size+time fast path would then call the diff files Identical and prune them.
                // Force distinct mtimes so both are decided by their bytes.
                File.SetLastWriteTime(Path.Combine(b, "diff.txt"), DateTime.Now.AddSeconds(-1));
                File.SetLastWriteTime(Path.Combine(b, Path.Combine("sub", "diff2.txt")), DateTime.Now.AddSeconds(-1));

                var r = DirectoryDiff.Compare(a, b);
                var visible = DirectoryDiff.FilterTree(r.RootEntries, showUnchangedFiles: false, showUnchangedDirs: false);

                if (visible.Count != 2) throw new Exception($"expected 2 visible roots, got {visible.Count}");
                if (FindEntry(visible, "same.txt") != null) throw new Exception("identical file should be pruned");
                if (FindEntry(visible, "diff.txt") == null) throw new Exception("different file missing");

                var sub = FindEntry(visible, "sub");
                if (sub == null) throw new Exception("sub folder missing");
                if (sub.VisibleChildren.Count != 1 || !sub.VisibleChildren[0].Name.Equals("diff2.txt", StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"sub should show only diff2.txt, has {sub.VisibleChildren.Count}");
            }
            finally { cleanup(); }
        });

        Run("DirDiff filter: show-unchanged-files brings back files AND formerly-pruned parents", () =>
        {
            var (a, b, cleanup) = MakeDirPair();
            try
            {
                WriteFile(a, "same.txt", "x");  WriteFile(b, "same.txt", "x");
                WriteFile(a, Path.Combine("sub", "diff2.txt"), "A");  WriteFile(b, Path.Combine("sub", "diff2.txt"), "B");
                // 'quiet' holds only identical files — pruned entirely with differences-only
                WriteFile(a, Path.Combine("quiet", "q.txt"), "z");  WriteFile(b, Path.Combine("quiet", "q.txt"), "z");

                var r = DirectoryDiff.Compare(a, b);
                var visible = DirectoryDiff.FilterTree(r.RootEntries, showUnchangedFiles: true, showUnchangedDirs: false);

                if (FindEntry(visible, "same.txt") == null) throw new Exception("identical file should reappear");

                var quiet = FindEntry(visible, "quiet");
                if (quiet == null) throw new Exception("formerly-pruned parent folder should reappear");
                if (quiet.VisibleChildren.Count != 1 || !quiet.VisibleChildren[0].Name.Equals("q.txt", StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"quiet should show q.txt, has {quiet.VisibleChildren.Count}");

                var sub = FindEntry(visible, "sub");
                if (sub == null || sub.VisibleChildren.Count != 1) throw new Exception("sub should still show only diff2.txt");
            }
            finally { cleanup(); }
        });

        Run("DirDiff filter: both toggles on → full tree", () =>
        {
            var (a, b, cleanup) = MakeDirPair();
            try
            {
                WriteFile(a, "same.txt", "x");  WriteFile(b, "same.txt", "x");
                WriteFile(a, Path.Combine("sub", "diff2.txt"), "A");  WriteFile(b, Path.Combine("sub", "diff2.txt"), "B");
                WriteFile(a, Path.Combine("quiet", "q.txt"), "z");    WriteFile(b, Path.Combine("quiet", "q.txt"), "z");

                var r = DirectoryDiff.Compare(a, b);
                var visible = DirectoryDiff.FilterTree(r.RootEntries, showUnchangedFiles: true, showUnchangedDirs: true);
                if (CountAll(visible) != CountAll(r.RootEntries))
                    throw new Exception($"visible {CountAll(visible)} != full {CountAll(r.RootEntries)}");
            }
            finally { cleanup(); }
        });

        Run("DirDiff filter: all-identical + differences-only → empty root", () =>
        {
            var (a, b, cleanup) = MakeDirPair();
            try
            {
                WriteFile(a, "x.txt", "x");
                WriteFile(b, "x.txt", "x");
                var r = DirectoryDiff.Compare(a, b);
                var visible = DirectoryDiff.FilterTree(r.RootEntries, showUnchangedFiles: false, showUnchangedDirs: false);
                if (visible.Count != 0) throw new Exception($"expected empty, got {visible.Count}");
            }
            finally { cleanup(); }
        });

        Run("DirDiff filter: one-sided empty folder survives differences-only", () =>
        {
            var (a, b, cleanup) = MakeDirPair();
            try
            {
                Directory.CreateDirectory(Path.Combine(a, "emptyA"));   // an added folder IS a difference
                var r = DirectoryDiff.Compare(a, b);
                var visible = DirectoryDiff.FilterTree(r.RootEntries, showUnchangedFiles: false, showUnchangedDirs: false);
                if (visible.Count != 1) throw new Exception($"expected 1, got {visible.Count}");
                if (visible[0].FolderStatus != FolderStatus.OnlyInLeft) throw new Exception($"status: {visible[0].FolderStatus}");
            }
            finally { cleanup(); }
        });

        Run("IgnoreWhitespace: internal whitespace-only difference → Unchanged", () =>
        {
            // The user's exact example: spaces inside the array brackets are the only difference.
            var a = MF("a", new List<string> { @"      ""@/*"": [ ""./src/*"" ]" });
            var b = MF("b", new List<string> { @"      ""@/*"": [""./src/*""]" });

            var (ra, rb, _, _) = DiffEngine.Compute(a, b, ignoreWhitespace: true);
            if (ra[0].Status != DiffStatus.Unchanged || rb[0].Status != DiffStatus.Unchanged)
                throw new Exception($"expected Unchanged/Unchanged, got {ra[0].Status}/{rb[0].Status}");
            if (ra[0].ChangedRegions != null || rb[0].ChangedRegions != null)
                throw new Exception("whitespace-only line must not carry inline ChangedRegions");

            // Contrast: without the option the same pair is Modified.
            var (ra2, rb2, _, _) = DiffEngine.Compute(a, b);
            if (ra2[0].Status != DiffStatus.Modified || rb2[0].Status != DiffStatus.Modified)
                throw new Exception($"expected Modified/Modified without option, got {ra2[0].Status}/{rb2[0].Status}");
        });

        Run("IgnoreWhitespace: leading/trailing and tab differences → Unchanged", () =>
        {
            var a = MF("a", new List<string> { "  x\t  ", "\t", "   " });
            var b = MF("b", new List<string> { "x", "", "" });

            var (ra, rb, _, _) = DiffEngine.Compute(a, b, ignoreWhitespace: true);
            for (int i = 0; i < 3; i++)
                if (ra[i].Status != DiffStatus.Unchanged || rb[i].Status != DiffStatus.Unchanged)
                    throw new Exception($"line {i}: {ra[i].Status}/{rb[i].Status}");
        });

        Run("IgnoreWhitespace: content change highlights only the changed characters", () =>
        {
            // "foo  bar" vs "foo baz": with ignore-on this is Modified (b≠a), but the extra
            // space must not be part of the highlighted region.
            var a = MF("a", new List<string> { "foo  bar" });   // f0 o1 o2 sp3 sp4 b5 a6 r7
            var b = MF("b", new List<string> { "foo baz" });    // f0 o1 o2 sp3 b4 a5 z6

            var (ra, rb, _, _) = DiffEngine.Compute(a, b, ignoreWhitespace: true);
            if (ra[0].Status != DiffStatus.Modified || rb[0].Status != DiffStatus.Modified)
                throw new Exception($"expected Modified/Modified, got {ra[0].Status}/{rb[0].Status}");

            var regionsA = ra[0].ChangedRegions;   // regions within "foo  bar"
            string descA = regionsA == null ? "null" : string.Join(", ", regionsA.Select(r => $"({r.start},{r.length})"));
            if (regionsA is not { Count: 1 } || regionsA![0] != (7, 1))
                throw new Exception($"ra region should be (7,1) [the 'r'], got [{descA}]");

            var regionsB = rb[0].ChangedRegions;   // regions within "foo baz"
            string descB = regionsB == null ? "null" : string.Join(", ", regionsB.Select(r => $"({r.start},{r.length})"));
            if (regionsB is not { Count: 1 } || regionsB![0] != (6, 1))
                throw new Exception($"rb region should be (6,1) [the 'z'], got [{descB}]");
        });

        Run("byteedit: splice replace shorter/longer/mid", () =>
        {
            var src = new byte[] { 0,1,2,3,4,5,6,7,8,9 };
            // Replace [4,8) (4 bytes) with 3 bytes → length 9
            var r = ByteEdit.Splice(src, 4, 4, new byte[] { 0xA0, 0xB0, 0xC0 });
            var expect = new byte[] { 0,1,2,3, 0xA0,0xB0,0xC0, 8,9 };
            if (r.Length != 9 || !r.SequenceEqual(expect)) throw new Exception($"got [{string.Join(",", r)}] len {r.Length}");
            // Replace with longer: [4,8) → 6 bytes → length 12
            r = ByteEdit.Splice(src, 4, 4, new byte[] { 1,2,3,4,5,6 });
            if (r.Length != 12 || r[7] != 4 || r[8] != 5 || r[9] != 6 || r[10] != 8) throw new Exception("longer replace wrong");
            // Source untouched
            if (!src.SequenceEqual(new byte[] { 0,1,2,3,4,5,6,7,8,9 })) throw new Exception("source mutated");
        });

        Run("byteedit: splice pure insert mid + append at EOF", () =>
        {
            var src = new byte[] { 1,2,3,4 };
            var r = ByteEdit.Splice(src, 2, 0, new byte[] { 0xEE });
            if (!r.SequenceEqual(new byte[] { 1,2,0xEE,3,4 })) throw new Exception($"mid insert: [{string.Join(",", r)}]");
            r = ByteEdit.Splice(src, src.Length, 0, new byte[] { 5,6 });   // append
            if (!r.SequenceEqual(new byte[] { 1,2,3,4,5,6 })) throw new Exception($"append: [{string.Join(",", r)}]");
        });

        Run("byteedit: splice pure delete + delete whole file", () =>
        {
            var src = new byte[] { 1,2,3,4,5 };
            var r = ByteEdit.Splice(src, 1, 3, Array.Empty<byte>());      // delete [1,4)
            if (!r.SequenceEqual(new byte[] { 1,5 })) throw new Exception($"delete: [{string.Join(",", r)}]");
            r = ByteEdit.Splice(src, 0, src.Length, Array.Empty<byte>()); // delete all
            if (r.Length != 0) throw new Exception($"delete-all len {r.Length}");
        });

        Run("byteedit: splice clamps out-of-range args + empty source", () =>
        {
            var src = new byte[] { 1,2,3 };
            var r = ByteEdit.Splice(src, 100, 50, new byte[] { 9 });      // start past end → append at end
            if (!r.SequenceEqual(new byte[] { 1,2,3,9 })) throw new Exception("clamp start");
            r = ByteEdit.Splice(Array.Empty<byte>(), 0, 0, new byte[] { 7,8 });
            if (!r.SequenceEqual(new byte[] { 7,8 })) throw new Exception("empty source insert");
            r = ByteEdit.Splice(src, -5, -3, Array.Empty<byte>());        // negative → clamped to no-op
            if (!r.SequenceEqual(src)) throw new Exception("negative clamp");
        });

        Run("byteedit: sync points shift on insert", () =>
        {
            var pts = new List<(int ByteA, int ByteB)> { (5,10), (20,30) };
            ByteEdit.AdjustSyncPoints(pts, 15, +4, 44);   // insert 4 bytes at offset 15
            if (pts.Count != 2 || pts[0] != (5,10) || pts[1] != (20,34))
                throw new Exception($"got [{string.Join(",", pts.Select(p => $"{p.ByteA}->{p.ByteB}"))}]");
        });

        Run("byteedit: sync points drop anchors swallowed by a replace", () =>
        {
            // Replace [2,12) with 1 byte: delta -9, new length 11. Anchors inside the replaced
            // range map to negative offsets and must be dropped; later ones shift left.
            var pts = new List<(int ByteA, int ByteB)> { (5,3), (15,16) };
            ByteEdit.AdjustSyncPoints(pts, 2, -9, 11);
            if (pts.Count != 1 || pts[0] != (15,7))
                throw new Exception($"got [{string.Join(",", pts.Select(p => $"{p.ByteA}->{p.ByteB}"))}]");
        });

        Run("byteedit: sync points deletion crossing → ordered subset", () =>
        {
            // Delete 25 bytes at offset 40: the second anchor shifts left of the first's ByteB,
            // creating a crossing pair — the greedy pass must leave an IsAnchorSetOrdered set.
            var pts = new List<(int ByteA, int ByteB)> { (10,30), (20,50) };
            ByteEdit.AdjustSyncPoints(pts, 40, -25, 35);
            if (!ByteDiff.IsAnchorSetOrdered(pts)) throw new Exception($"result not ordered: [{string.Join(",", pts.Select(p => $"{p.ByteA}->{p.ByteB}"))}]");
            if (pts.Count != 1 || pts[0] != (10,30)) throw new Exception($"expected only (10,30), got [{string.Join(",", pts.Select(p => $"{p.ByteA}->{p.ByteB}"))}]");
        });

        // ── Structural analysis (Services/StructuralDiff.cs) ───────────────────

        Run("Structural: C++ return type changed", () =>
        {
            var a = MF("a.cpp", new List<string> { "int add(int a, int b) {", "    return a + b;", "}" });
            var b = MF("b.cpp", new List<string> { "long add(int a, int b) {", "    return a + b;", "}" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 1) throw new Exception($"expected 1 finding, got {(f == null ? "null" : f.Count.ToString())}");
            if (f[0].Kind != StructuralKind.ReturnTypeChanged || f[0].LineA != 1 || f[0].LineB != 1)
                throw new Exception($"got {f[0].Kind} at {f[0].LineA},{f[0].LineB}");
            if (!f[0].Message.Contains("int") || !f[0].Message.Contains("long")) throw new Exception($"message: {f[0].Message}");
        });

        Run("Structural: C++ access label doesn't leak into the return type", () =>
        {
            // A member sitting directly after "public:" must not absorb the label into its
            // prefix — previously this pair (differing only in a using-alias line before the
            // member) reported a phantom "return type changed: public: X → X".
            var a = MF("a.h", new List<string>
            {
                "class T", "{", "public:",
                "    const Foo& Get() const { return m; }",
                "private:", "    Foo m;", "}"
            });
            var b = MF("b.h", new List<string>
            {
                "class T", "{", "public:",
                "    using Alias = Foo;",
                "    const Foo& Get() const { return m; }",
                "private:", "    Foo m;", "}"
            });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f is { Count: > 0 })
                throw new Exception($"expected no findings, got [{string.Join("; ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))}]");
        });

        Run("Structural: C++ wrapped parameter list is a definition (out-of-class template def)", () =>
        {
            // The reported miss: out-of-class template definitions with params spanning lines
            // were skipped ("the matching ')' must close on this line"), so an added/removed
            // InitiateGOAPJob-style overload produced no finding at all.
            var a = MF("a.h", new List<string>
            {
                "template<class T, class U>",
                "int Foo<T, U>::Bar(const int& x,",
                "               const int& y)",
                "{",
                "    return x + y;",
                "}"
            });
            var b = MF("b.h", new List<string> { "int other() {", "    return 0;", "}" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            // The removal of the wrapped def is the finding under test; the pair also legitimately
            // reports B's unrelated "other" function and the nesting shift it causes.
            if (f == null || !f.Any(x => x.Kind == StructuralKind.FunctionRemoved && x.LineA == 2 && x.LineB == 0))
                throw new Exception($"expected FunctionRemoved@2,0, got {(f == null ? "null" : string.Join(" | ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}")))}");
        });

        Run("Structural: C++ operator overload return type changed", () =>
        {
            // The reported miss (MissingHint_A:104 → B:64): the name scanner only accepted
            // identifier chars before '(', so "operator*()" was never seen as a function and
            // its return-type change produced no finding.
            var a = MF("a.h", new List<string>
            {
                "class A", "{", "public:",
                "    int& operator*() { return m; }",
                "    int m;", "}"
            });
            var b = MF("b.h", new List<string>
            {
                "class A", "{", "public:",
                "    const int& operator*() { return m; }",
                "    int m;", "}"
            });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 1) throw new Exception($"expected 1 finding, got {(f == null ? "null" : string.Join(" | ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}")))}");
            var x = f![0];
            if (x.Kind != StructuralKind.ReturnTypeChanged || x.LineA != 4 || x.LineB != 4)
                throw new Exception($"got {x.Kind}@{x.LineA},{x.LineB}: {x.Message}");
            if (!x.Message.Contains("int&") || !x.Message.Contains("const int&")) throw new Exception($"message: {x.Message}");
        });

        Run("Structural: C++ templated class generic change (template clause on its own line)", () =>
        {
            // FindTypes only saw the same-line head, so a "template<…>" line before "class Foo"
            // was never captured — changing the class's template parameters reported nothing.
            var a = MF("a.h", new List<string> { "template<class T>", "class Foo {", "}" });
            var b = MF("b.h", new List<string> { "template<class T, class U>", "class Foo {", "}" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 1) throw new Exception($"expected 1 finding, got {(f == null ? "null" : string.Join(" | ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}")))}");
            var x = f![0];
            if (x.Kind != StructuralKind.GenericChanged || x.LineA != 2 || x.LineB != 2)
                throw new Exception($"got {x.Kind}@{x.LineA},{x.LineB}: {x.Message}");
        });

        Run("Structural: C++ ctor with member-initializer list is a definition", () =>
        {
            // FindFunctionBody rejected the ':' of an init list, so "Foo(int a) : m(a) { }" was
            // invisible — removing such a constructor produced no finding.
            var a = MF("a.h", new List<string>
            {
                "class Foo {", "public:",
                "    Foo(int a)",
                "        : m(a)",
                "    {",
                "    }",
                "private:",
                "    int m;",
                "}"
            });
            var b = MF("b.h", new List<string> { "class Foo {", "public:", "    int m;", "}" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 1) throw new Exception($"expected 1 finding, got {(f == null ? "null" : string.Join(" | ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}")))}");
            var x = f![0];
            if (x.Kind != StructuralKind.FunctionRemoved || x.LineA != 3 || x.LineB != 0)
                throw new Exception($"got {x.Kind}@{x.LineA},{x.LineB}: {x.Message}");
        });

        Run("Structural: ERRORS removedTemplatedFunction + MissingHint pairs", () =>
        {
            // The reported repros. Skipped if the repro files are cleaned up; the synthetic
            // tests above guard each mechanism regardless.
            string dir = Path.Combine("..", "NeoDiff", "ERRORS");
            string ra1 = Path.Combine(dir, "removedTemplatedFunction_A.h"), rb1 = Path.Combine(dir, "removedTemplatedFunction_B.h");
            string ra2 = Path.Combine(dir, "MissingHint_A.h"), rb2 = Path.Combine(dir, "MissingHint_B.h");
            if (!File.Exists(ra1) || !File.Exists(rb1) || !File.Exists(ra2) || !File.Exists(rb2))
            { Console.WriteLine("  (skipped — repro files absent)"); return; }

            // Pair 1: A has two InitiateGOAPJob overloads (wrapped param lists) whose definitions
            // B removes — both must be reported as removed on A's definition lines (A:87 and A:98).
            var a1 = new FileModel { FilePath = ra1, Lines = File.ReadAllLines(ra1).ToList() };
            var b1 = new FileModel { FilePath = rb1, Lines = File.ReadAllLines(rb1).ToList() };
            var (aToB1, bToA1) = Align(a1, b1);
            var f1 = StructuralDiff.Analyze(a1, b1, aToB1, bToA1);
            if (f1 == null) throw new Exception("pair 1: analysis returned null");
            int rems = 0;
            foreach (var x in f1)
                if (x.Kind == StructuralKind.FunctionRemoved && x.Message.Contains("InitiateGOAPJob"))
                { if (x.LineA is 87 or 98 && x.LineB == 0) rems++; }
            if (rems != 2) throw new Exception($"pair 1: expected 2 FunctionRemoved InitiateGOAPJob at A:87/A:98, got [{string.Join(" | ", f1.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))}]");

            // Pair 2: the operator* return type changed (A:104 → B:64) — previously invisible.
            var a2 = new FileModel { FilePath = ra2, Lines = File.ReadAllLines(ra2).ToList() };
            var b2 = new FileModel { FilePath = rb2, Lines = File.ReadAllLines(rb2).ToList() };
            var (aToB2, bToA2) = Align(a2, b2);
            var f2 = StructuralDiff.Analyze(a2, b2, aToB2, bToA2);
            if (f2 == null) throw new Exception("pair 2: analysis returned null");
            bool hit = false;
            foreach (var x in f2)
                if (x.Kind == StructuralKind.ReturnTypeChanged && x.LineA == 104 && x.LineB == 64) { hit = true; break; }
            if (!hit) throw new Exception($"pair 2: expected ReturnTypeChanged at A:104/B:64, got [{string.Join(" | ", f2.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))}]");
        });

        Run("Structural: C++ function added + removed", () =>
        {
            var a = MF("a.cpp", new List<string> { "void old_fn() {", "}", "int main() {", "    return 0;", "}" });
            var b = MF("b.cpp", new List<string> { "void new_fn() {", "}", "int main() {", "    return 0;", "}" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 2) throw new Exception($"expected 2 findings, got {(f == null ? "null" : f.Count.ToString())}");
            bool hasRem = false, hasAdd = false;
            foreach (var x in f!)
            {
                if (x.Kind == StructuralKind.FunctionRemoved && x.LineA == 1 && x.LineB == 0) hasRem = true;
                if (x.Kind == StructuralKind.FunctionAdded && x.LineA == 0 && x.LineB == 1) hasAdd = true;
            }
            if (!hasRem || !hasAdd) throw new Exception($"kinds: [{string.Join(", ", f!.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}"))}]");
        });

        Run("Structural: C++ parameters changed", () =>
        {
            var a = MF("a.cpp", new List<string> { "int f(int a) {", "    return a;", "}" });
            var b = MF("b.cpp", new List<string> { "int f(int a, int b) {", "    return a + b;", "}" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 1) throw new Exception($"expected 1 finding, got {(f == null ? "null" : f.Count.ToString())}");
            if (f[0].Kind != StructuralKind.ParametersChanged || f[0].LineA != 1 || f[0].LineB != 1)
                throw new Exception($"got {f[0].Kind} at {f[0].LineA},{f[0].LineB}");
        });

        Run("Structural: C++ call sites are not functions", () =>
        {
            var a = MF("a.cpp", new List<string> { "void run(int x) {", "}", "int main() {", "    run(1);", "    return 0;", "}" });
            var b = MF("b.cpp", new List<string> { "void run(int x) {", "}", "int main() {", "    run(2);", "    return 0;", "}" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f != null && f.Count != 0) throw new Exception($"expected 0 findings, got [{string.Join(", ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}"))}]");
        });

        Run("Structural: C++ control flow changed (for → if)", () =>
        {
            // Same position, different keyword — the alignment pairs the region line-for-line,
            // so A's for-block image intersects B's if-block: one modified block, not remove+add.
            var a = MF("a.cpp", new List<string> { "int main() {", "    setup();", "    for (int i = 0; i < n; i++) {", "        work(i);", "    }", "    finish();", "}" });
            var b = MF("b.cpp", new List<string> { "int main() {", "    setup();", "    if (n > 0) {", "        work(1);", "    }", "    finish();", "}" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 1) throw new Exception($"expected 1 finding, got {(f == null ? "null" : string.Join(" | ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))) }");
            var x = f![0];
            if (x.Kind != StructuralKind.ControlBlockModified || x.LineA != 3 || x.LineB != 3) throw new Exception($"got {x.Kind}@{x.LineA},{x.LineB}: {x.Message}");
            if (!x.Message.Contains("for") || !x.Message.Contains("if")) throw new Exception($"message: {x.Message}");
        });

        Run("Structural: C++ loop removed", () =>
        {
            var a = MF("a.cpp", new List<string> { "int main() {", "    setup();", "    for (int i = 0; i < n; i++) {", "        work(i);", "    }", "    finish();", "}" });
            var b = MF("b.cpp", new List<string> { "int main() {", "    setup();", "    finish();", "}" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 1) throw new Exception($"expected 1 finding, got {(f == null ? "null" : string.Join(" | ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))) }");
            var x = f![0];
            if (x.Kind != StructuralKind.ControlBlockRemoved || x.LineA != 3 || x.LineB != 0) throw new Exception($"got {x.Kind}@{x.LineA},{x.LineB}");
        });

        Run("Structural: C++ if added", () =>
        {
            var a = MF("a.cpp", new List<string> { "int main() {", "    setup();", "    finish();", "}" });
            var b = MF("b.cpp", new List<string> { "int main() {", "    setup();", "    if (n > 0) {", "        work(1);", "    }", "    finish();", "}" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 1) throw new Exception($"expected 1 finding, got {(f == null ? "null" : string.Join(" | ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))) }");
            var x = f![0];
            if (x.Kind != StructuralKind.ControlBlockAdded || x.LineA != 0 || x.LineB != 3) throw new Exception($"got {x.Kind}@{x.LineA},{x.LineB}: {x.Message}");
        });

        Run("Structural: C++ nesting shift with identical text is dropped", () =>
        {
            // B wraps x()/y() in an if without re-indenting — every aligned pair still has
            // identical text, so there is no diff line to carry the hint: the run is dropped and
            // only the inserted block's own ControlBlockAdded remains (on its Added line).
            var a = MF("a.cpp", new List<string> { "int main() {", "    x();", "    y();", "}" });
            var b = MF("b.cpp", new List<string> { "int main() {", "    if (ok) {", "    x();", "    y();", "    }", "}" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 1) throw new Exception($"expected 1 finding, got {(f == null ? "null" : string.Join(", ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}"))) }");
            var x = f![0];
            if (x.Kind != StructuralKind.ControlBlockAdded || x.LineA != 0 || x.LineB != 2)
                throw new Exception($"got {x.Kind}@{x.LineA},{x.LineB}: {x.Message}");
        });

        Run("Structural: C++ block inserted inside a loop body (no spurious flow change)", () =>
        {
            // Regression: keyword-grouped matching let A's for-block grab the if nested inside
            // its own body ("Control flow changed: for → if") and left B's real for-block
            // reported as added — dots on an unchanged loop header. Now the for matches its
            // aligned counterpart silently; only the inserted if is reported, on its diff line.
            var a = MF("a.cpp", new List<string> { "void f() {", "    for (int i = 0; i < n; i++)", "    {", "        g(i);", "        h(i);", "    }", "}" });
            var b = MF("b.cpp", new List<string> { "void f() {", "    for (int i = 0; i < n; i++)", "    {", "        g(i);", "        if (i > 3)", "        {", "            h(i);", "        }", "    }", "}" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 1) throw new Exception($"expected 1 finding, got {(f == null ? "null" : string.Join(", ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))) }");
            var x = f![0];
            if (x.Kind != StructuralKind.ControlBlockAdded || x.LineA != 0 || x.LineB != 5)
                throw new Exception($"got {x.Kind}@{x.LineA},{x.LineB}: {x.Message}");
        });

        Run("Structural: C++ nesting hint lands on the first differing line", () =>
        {
            // The shifted run starts at an identical pair (x()); the hint must sit on the first
            // pair whose text actually differs (b(1) → b(2)), not on unchanged code.
            var a = MF("a.cpp", new List<string> { "int main() {", "    x();", "    b(1);", "}" });
            var b = MF("b.cpp", new List<string> { "int main() {", "    if (ok) {", "    x();", "    b(2);", "    }", "}" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 2) throw new Exception($"expected 2 findings, got {(f == null ? "null" : string.Join(", ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))) }");
            bool hasNest = false, hasAdd = false;
            foreach (var x in f!)
            {
                if (x.Kind == StructuralKind.NestingChanged && x.LineA == 3 && x.LineB == 4 && x.Message.Contains("-1")) hasNest = true;
                if (x.Kind == StructuralKind.ControlBlockAdded && x.LineA == 0 && x.LineB == 2) hasAdd = true;
            }
            if (!hasNest || !hasAdd) throw new Exception($"kinds: [{string.Join(", ", f!.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))}]");
        });

        Run("Structural: C++ brace imbalance (missing close)", () =>
        {
            var a = MF("a.cpp", new List<string> { "void f() {", "    g();", "}" });
            var b = MF("b.cpp", new List<string> { "void f() {", "    g();" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 1) throw new Exception($"expected 1 finding, got {(f == null ? "null" : string.Join(", ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}"))) }");
            if (f[0].Kind != StructuralKind.BraceImbalance || f[0].LineA != 0 || f[0].LineB != 2)
                throw new Exception($"got {f[0].Kind} at {f[0].LineA},{f[0].LineB}: {f[0].Message}");
            if (!f[0].Message.Contains("unclosed")) throw new Exception($"message: {f[0].Message}");
        });

        Run("Structural: C++ comments and strings don't count as braces", () =>
        {
            var a = MF("a.cpp", new List<string> { "int f() {   // }", "    /* { */ return 1;", "}", "const char* s = \"{\";" });
            var b = MF("b.cpp", new List<string> { "long f() {   // }", "    /* { */ return 1;", "}", "const char* s = \"{\";" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 1) throw new Exception($"expected 1 finding, got {(f == null ? "null" : string.Join(", ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}"))) }");
            if (f[0].Kind != StructuralKind.ReturnTypeChanged || f[0].LineA != 1 || f[0].LineB != 1)
                throw new Exception($"got {f[0].Kind} at {f[0].LineA},{f[0].LineB}: {f[0].Message}");
        });

        Run("Structural: JSON key add/remove + value type changed (highest-only)", () =>
        {
            var a = MF("a.json", new List<string> { "{", "  \"a\": 1,", "  \"b\": {", "    \"c\": true", "  }", "}" });
            var b = MF("b.json", new List<string> { "{", "  \"a\": \"one\",", "  \"d\": [1, 2]", "}" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 3) throw new Exception($"expected 3 findings, got {(f == null ? "null" : string.Join(", ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))) }");
            bool hasType = false, hasRem = false, hasAdd = false;
            foreach (var x in f!)
            {
                if (x.Kind == StructuralKind.JsonValueTypeChanged && x.LineA == 2 && x.LineB == 2
                    && x.Message.ToLowerInvariant().Contains("number") && x.Message.ToLowerInvariant().Contains("string")) hasType = true;
                if (x.Kind == StructuralKind.JsonKeyRemoved && x.LineA == 3 && x.LineB == 0 && x.Message.Contains("$.b")) hasRem = true;
                if (x.Kind == StructuralKind.JsonKeyAdded && x.LineA == 0 && x.LineB == 3 && x.Message.Contains("$.d")) hasAdd = true;
            }
            if (!hasType || !hasRem || !hasAdd) throw new Exception($"kinds: [{string.Join(", ", f!.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))}]");
            if (f!.Any(x => x.Message.Contains("$.b.c"))) throw new Exception("child of removed key must be suppressed (highest-only)");
        });

        Run("Structural: JSON malformed → InvalidDocument at error line", () =>
        {
            var a = MF("a.json", new List<string> { "{", "  \"a\": 1", "}" });
            var b = MF("b.json", new List<string> { "{", "  \"a\": }" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 1) throw new Exception($"expected 1 finding, got {(f == null ? "null" : string.Join(", ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}"))) }");
            if (f[0].Kind != StructuralKind.InvalidDocument || f[0].LineA != 0 || f[0].LineB != 2)
                throw new Exception($"got {f[0].Kind} at {f[0].LineA},{f[0].LineB}: {f[0].Message}");
        });

        Run("Structural: XML element swap + attribute changes (aggregated)", () =>
        {
            var a = MF("a.xml", new List<string> { "<root x=\"1\">", "  <item/>", "</root>" });
            var b = MF("b.xml", new List<string> { "<root x=\"2\" y=\"3\">", "  <other/>", "</root>" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 3) throw new Exception($"expected 3 findings, got {(f == null ? "null" : string.Join(", ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))) }");
            bool hasAttr = false, hasRem = false, hasAdd = false;
            foreach (var x in f!)
            {
                if (x.Kind == StructuralKind.XmlAttributeChanged && x.LineA == 1 && x.LineB == 1
                    && x.Message.Contains("x") && x.Message.Contains("y")) hasAttr = true;
                if (x.Kind == StructuralKind.XmlElementRemoved && x.LineA == 2 && x.LineB == 0) hasRem = true;
                if (x.Kind == StructuralKind.XmlElementAdded && x.LineA == 0 && x.LineB == 2) hasAdd = true;
            }
            if (!hasAttr || !hasRem || !hasAdd) throw new Exception($"kinds: [{string.Join(", ", f!.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))}]");
        });

        Run("Structural: XML malformed → InvalidDocument on broken side only", () =>
        {
            var a = MF("a.xml", new List<string> { "<root>", "  <item/>", "</root>" });
            var b = MF("b.xml", new List<string> { "<root>", "  <item>", "</root>" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 1) throw new Exception($"expected 1 finding, got {(f == null ? "null" : string.Join(", ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}"))) }");
            if (f[0].Kind != StructuralKind.InvalidDocument || f[0].LineA != 0 || f[0].LineB <= 0)
                throw new Exception($"got {f[0].Kind} at {f[0].LineA},{f[0].LineB}: {f[0].Message}");
        });

        Run("Structural: unsupported / missing / mismatched → null", () =>
        {
            var txt = MF("a.txt", new List<string> { "x" });
            if (StructuralDiff.Analyze(txt, MF("b.txt", new List<string> { "y" }), Array.Empty<int>(), Array.Empty<int>()) != null)
                throw new Exception(".txt pair should be unsupported");
            var cpp = MF("a.cpp", new List<string> { "x" });
            if (StructuralDiff.Analyze(null, cpp, Array.Empty<int>(), Array.Empty<int>()) != null)
                throw new Exception("missing file A should yield null");
            if (StructuralDiff.Analyze(cpp, MF("b.json", new List<string> { "{}" }), Array.Empty<int>(), Array.Empty<int>()) != null)
                throw new Exception("mismatched extensions should yield null");
        });

        Run("Structural: > MaxLines → null", () =>
        {
            var big = new List<string>(StructuralDiff.MaxLines + 1);
            for (int i = 0; i <= StructuralDiff.MaxLines; i++) big.Add("x");
            if (StructuralDiff.Analyze(MF("a.cpp", big), MF("b.cpp", new List<string>(big)), Array.Empty<int>(), Array.Empty<int>()) != null)
                throw new Exception($"pair of {big.Count} lines should exceed MaxLines");
        });

        Run("Structural: alignment maps from real interleaved pair (round trip)", () =>
        {
            var a = MF("a.cpp", new List<string> { "int base() { return 1; }", "int main() { a(); c(); }" });
            var b = MF("b.cpp", new List<string> { "int base() { return 1; }", "int extra() { return 2; }", "int main() { a(); c(); }" });
            var (aToB, bToA) = Align(a, b);
            if (aToB.Length != 2 || aToB[0] != 0 || aToB[1] != 2) throw new Exception($"alignment off: [{string.Join(",", aToB)}]");
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 1 || f[0].Kind != StructuralKind.FunctionAdded || f[0].LineA != 0 || f[0].LineB != 2)
                throw new Exception($"expected FunctionAdded(0,2), got {(f == null ? "null" : string.Join(", ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}"))) }");
        });

        Run("Structural: real repro (ERRORS/TESTA vs TESTB) — hints align to diffs only", () =>
        {
            // The reported bug: line 57 — an identical loop header in both files — carried two
            // contradictory dots. Skipped if the repro files are cleaned up; the unit tests above
            // cover the mechanism regardless.
            string pa = Path.Combine("..", "NeoDiff", "ERRORS", "TESTA.cpp");
            string pb = Path.Combine("..", "NeoDiff", "ERRORS", "TESTB.cpp");
            if (!File.Exists(pa) || !File.Exists(pb)) { Console.WriteLine("  (skipped — repro files absent)"); return; }
            var a = new FileModel { FilePath = pa, Lines = File.ReadAllLines(pa).ToList() };
            var b = new FileModel { FilePath = pb, Lines = File.ReadAllLines(pb).ToList() };
            var (_, _, intA, intB) = DiffEngine.Compute(a, b);
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null) throw new Exception("analysis returned null");

            // Line 57 is identical in both files → no finding may touch it on either side.
            if (f.Any(x => x.LineA == 57 || x.LineB == 57))
                throw new Exception($"line 57 touched: [{string.Join(", ", f.Where(x => x.LineA == 57 || x.LineB == 57).Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))}]");

            // The invariant on the whole pair: every finding line must be a real diff line
            // (not Unchanged) on its side.
            var statusA = new Dictionary<int, DiffStatus>();
            foreach (var l in intA) if (l.Status != DiffStatus.Gap && l.OriginalLineNumber > 0) statusA[l.OriginalLineNumber] = l.Status;
            var statusB = new Dictionary<int, DiffStatus>();
            foreach (var l in intB) if (l.Status != DiffStatus.Gap && l.OriginalLineNumber > 0) statusB[l.OriginalLineNumber] = l.Status;
            foreach (var x in f)
            {
                if (x.LineA > 0 && statusA[x.LineA] == DiffStatus.Unchanged)
                    throw new Exception($"{x.Kind}@{x.LineA},{x.LineB} touches unchanged A line {x.LineA}: {x.Message}");
                if (x.LineB > 0 && statusB[x.LineB] == DiffStatus.Unchanged)
                    throw new Exception($"{x.Kind}@{x.LineA},{x.LineB} touches unchanged B line {x.LineB}: {x.Message}");
            }
        });

        Run("Structural: real repro (ERRORS/WrongStructureHint_A vs B) — access label is not a type change", () =>
        {
            // The reported bug: GetExpectedWorld is identical in both files, but on the A side
            // it is the first member after "public:", so the prefix scan swallowed the section
            // label and the pair reported a phantom "return type changed: public: X → X".
            // Skipped if the repro files are cleaned up; the unit test above covers the mechanism.
            string pa = Path.Combine("..", "NeoDiff", "ERRORS", "WrongStructureHint_A.h");
            string pb = Path.Combine("..", "NeoDiff", "ERRORS", "WrongStructureHint_B.h");
            if (!File.Exists(pa) || !File.Exists(pb)) { Console.WriteLine("  (skipped — repro files absent)"); return; }
            var a = new FileModel { FilePath = pa, Lines = File.ReadAllLines(pa).ToList() };
            var b = new FileModel { FilePath = pb, Lines = File.ReadAllLines(pb).ToList() };
            var (_, _, intA, intB) = DiffEngine.Compute(a, b);
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null) throw new Exception("analysis returned null");

            // The identical GetExpectedWorld pair (A line 23 / B line 26) must carry no finding.
            if (f.Any(x => x.LineA == 23 || x.LineB == 26))
                throw new Exception($"GetExpectedWorld touched: [{string.Join(", ", f.Where(x => x.LineA == 23 || x.LineB == 26).Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))}]");

            // Same whole-pair invariant as the TESTA/TESTB repro: every finding line must be a
            // real diff line on its side — and no message may cite an access label as a type.
            var statusA = new Dictionary<int, DiffStatus>();
            foreach (var l in intA) if (l.Status != DiffStatus.Gap && l.OriginalLineNumber > 0) statusA[l.OriginalLineNumber] = l.Status;
            var statusB = new Dictionary<int, DiffStatus>();
            foreach (var l in intB) if (l.Status != DiffStatus.Gap && l.OriginalLineNumber > 0) statusB[l.OriginalLineNumber] = l.Status;
            foreach (var x in f)
            {
                if (x.Message.Contains("public"))
                    throw new Exception($"access label in finding: {x.Kind}@{x.LineA},{x.LineB}: {x.Message}");
                if (x.LineA > 0 && statusA[x.LineA] == DiffStatus.Unchanged)
                    throw new Exception($"{x.Kind}@{x.LineA},{x.LineB} touches unchanged A line {x.LineA}: {x.Message}");
                if (x.LineB > 0 && statusB[x.LineB] == DiffStatus.Unchanged)
                    throw new Exception($"{x.Kind}@{x.LineA},{x.LineB} touches unchanged B line {x.LineB}: {x.Message}");
            }
        });

        // ── Extended structural analysis: virtuals/const/generics/lambdas + Rust & Python ──

        Run("Structural: C++ virtual added → specific modifier hint", () =>
        {
            var a = MF("a.cpp", new List<string> { "void f() {", "}" });
            var b = MF("b.cpp", new List<string> { "virtual void f() {", "}" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 1) throw new Exception($"expected 1 finding, got {(f == null ? "null" : string.Join(" | ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))) }");
            var x = f![0];
            if (x.Kind != StructuralKind.ModifierChanged || x.LineA != 1 || x.LineB != 1) throw new Exception($"got {x.Kind}@{x.LineA},{x.LineB}");
            if (!x.Message.Contains("virtual")) throw new Exception($"message: {x.Message}");
        });

        Run("Structural: C++ trailing const removed", () =>
        {
            var a = MF("a.cpp", new List<string> { "void f() const {", "}" });
            var b = MF("b.cpp", new List<string> { "void f() {", "}" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 1) throw new Exception($"expected 1 finding, got {(f == null ? "null" : string.Join(" | ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))) }");
            var x = f![0];
            if (x.Kind != StructuralKind.ModifierChanged || x.LineA != 1 || x.LineB != 1) throw new Exception($"got {x.Kind}@{x.LineA},{x.LineB}");
            if (!x.Message.Contains("Trailing qualifier removed: const")) throw new Exception($"message: {x.Message}");
        });

        Run("Structural: C++ template parameter list changed (no return-type noise)", () =>
        {
            // The template clause must be extracted out of the prefix — otherwise the changed
            // <…> list would surface as a spurious ReturnTypeChanged on top of the real one.
            var a = MF("a.cpp", new List<string> { "template<typename T>", "int f(T x) {", "    return x;", "}" });
            var b = MF("b.cpp", new List<string> { "template<typename T, typename U>", "int f(T x) {", "    return x;", "}" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 1) throw new Exception($"expected 1 finding, got {(f == null ? "null" : string.Join(" | ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))) }");
            var x = f![0];
            if (x.Kind != StructuralKind.GenericChanged || x.LineA != 2 || x.LineB != 2) throw new Exception($"got {x.Kind}@{x.LineA},{x.LineB}: {x.Message}");
            if (!x.Message.Contains("typename T") || !x.Message.Contains("typename U")) throw new Exception($"message: {x.Message}");
        });

        Run("Structural: C++ multi-line template list on a function", () =>
        {
            var a = MF("a.cpp", new List<string> { "template<", "    typename T>", "int f(T x) {", "    return x;", "}" });
            var b = MF("b.cpp", new List<string> { "template<", "    typename T,", "    typename U>", "int f(T x) {", "    return x;", "}" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 1) throw new Exception($"expected 1 finding, got {(f == null ? "null" : string.Join(" | ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}")))}");
            var x = f![0];
            if (x.Kind != StructuralKind.GenericChanged || x.LineA != 3 || x.LineB != 4) throw new Exception($"got {x.Kind}@{x.LineA},{x.LineB}: {x.Message}");
            if (!x.Message.Contains("typename T") || !x.Message.Contains("typename U")) throw new Exception($"message: {x.Message}");
        });

        Run("Structural: C++ explicit function specialization argument change (primary present)", () =>
        {
            var a = MF("a.cpp", new List<string> { "template<typename T>", "void log(T x) {", "}", "template<>", "void log<int>(int x) {", "}" });
            var b = MF("b.cpp", new List<string> { "template<typename T>", "void log(T x) {", "}", "template<>", "void log<double>(double x) {", "}" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 1) throw new Exception($"expected 1 finding, got {(f == null ? "null" : string.Join(" | ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}")))}");
            var x = f![0];
            if (x.Kind != StructuralKind.ParametersChanged || x.LineA != 5 || x.LineB != 5) throw new Exception($"got {x.Kind}@{x.LineA},{x.LineB}: {x.Message}");
            if (!x.Message.Contains("(int x)") || !x.Message.Contains("(double x)")) throw new Exception($"message: {x.Message}");
        });

        Run("Structural: C++ explicit function specialization added", () =>
        {
            var a = MF("a.cpp", new List<string> { "void f(int x) {", "}" });
            var b = MF("b.cpp", new List<string> { "void f(int x) {", "}", "template<>", "void f<int>(int x) {", "}" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 1) throw new Exception($"expected 1 finding, got {(f == null ? "null" : string.Join(" | ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}")))}");
            var x = f![0];
            if (x.Kind != StructuralKind.FunctionAdded || x.LineA != 0 || x.LineB != 4) throw new Exception($"got {x.Kind}@{x.LineA},{x.LineB}: {x.Message}");
            if (!x.Message.Contains("f(int x)")) throw new Exception($"message: {x.Message}");
        });

        Run("Structural: C++ explicit function specialization removed", () =>
        {
            var a = MF("a.cpp", new List<string> { "void f(int x) {", "}", "template<>", "void f<int>(int x) {", "}" });
            var b = MF("b.cpp", new List<string> { "void f(int x) {", "}" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 1) throw new Exception($"expected 1 finding, got {(f == null ? "null" : string.Join(" | ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}")))}");
            var x = f![0];
            if (x.Kind != StructuralKind.FunctionRemoved || x.LineA != 4 || x.LineB != 0) throw new Exception($"got {x.Kind}@{x.LineA},{x.LineB}: {x.Message}");
            if (!x.Message.Contains("f(int x)")) throw new Exception($"message: {x.Message}");
        });

        Run("Structural: C++ class specialization argument change (primary template present)", () =>
        {
            var a = MF("a.h", new List<string> { "template<class T, class U>", "struct Pair {", "};", "template<>", "struct Pair<int, int> {", "};" });
            var b = MF("b.h", new List<string> { "template<class T, class U>", "struct Pair {", "};", "template<>", "struct Pair<double, double> {", "};" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 1) throw new Exception($"expected 1 finding, got {(f == null ? "null" : string.Join(" | ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}")))}");
            var x = f![0];
            if (x.Kind != StructuralKind.GenericChanged || x.LineA != 5 || x.LineB != 5) throw new Exception($"got {x.Kind}@{x.LineA},{x.LineB}: {x.Message}");
            if (!x.Message.Contains("Template arguments changed") || !x.Message.Contains("int, int") || !x.Message.Contains("double, double")) throw new Exception($"message: {x.Message}");
        });

        Run("Structural: C++ class specialization added", () =>
        {
            var a = MF("a.h", new List<string> { "template<class T>", "class Box {", "};" });
            var b = MF("b.h", new List<string> { "template<class T>", "class Box {", "};", "template<>", "class Box<int> {", "};" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 1) throw new Exception($"expected 1 finding, got {(f == null ? "null" : string.Join(" | ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}")))}");
            var x = f![0];
            if (x.Kind != StructuralKind.TypeAdded || x.LineA != 0 || x.LineB != 5) throw new Exception($"got {x.Kind}@{x.LineA},{x.LineB}: {x.Message}");
            if (!x.Message.Contains("class Box<int>")) throw new Exception($"message: {x.Message}");
        });

        Run("Structural: C++ class specialization removed", () =>
        {
            var a = MF("a.h", new List<string> { "template<class T>", "class Box {", "};", "template<>", "class Box<int> {", "};" });
            var b = MF("b.h", new List<string> { "template<class T>", "class Box {", "};" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 1) throw new Exception($"expected 1 finding, got {(f == null ? "null" : string.Join(" | ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}")))}");
            var x = f![0];
            if (x.Kind != StructuralKind.TypeRemoved || x.LineA != 5 || x.LineB != 0) throw new Exception($"got {x.Kind}@{x.LineA},{x.LineB}: {x.Message}");
            if (!x.Message.Contains("class Box<int>")) throw new Exception($"message: {x.Message}");
        });

        Run("Structural: C++ same-line template clause on a type", () =>
        {
            var a = MF("a.h", new List<string> { "template<class T> class Foo {", "}" });
            var b = MF("b.h", new List<string> { "template<class T, class U> class Foo {", "}" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 1) throw new Exception($"expected 1 finding, got {(f == null ? "null" : string.Join(" | ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}")))}");
            var x = f![0];
            if (x.Kind != StructuralKind.GenericChanged || x.LineA != 1 || x.LineB != 1) throw new Exception($"got {x.Kind}@{x.LineA},{x.LineB}: {x.Message}");
            if (!x.Message.Contains("class T") || !x.Message.Contains("class U")) throw new Exception($"message: {x.Message}");
        });

        Run("Structural: C# access + static + generics changed", () =>
        {
            var a = MF("a.cs", new List<string> { "private int Max<T>(T a) {", "    return a;", "}" });
            var b = MF("b.cs", new List<string> { "public static int Max<T, U>(T a) {", "    return a;", "}" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 2) throw new Exception($"expected 2 findings, got {(f == null ? "null" : string.Join(" | ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))) }");
            bool hasMod = false, hasGen = false;
            foreach (var x in f!)
            {
                if (x.Kind == StructuralKind.ModifierChanged && x.LineA == 1 && x.LineB == 1
                    && x.Message.Contains("private") && x.Message.Contains("public") && x.Message.Contains("static")) hasMod = true;
                if (x.Kind == StructuralKind.GenericChanged && x.LineA == 1 && x.LineB == 1 && x.Message.Contains("T, U")) hasGen = true;
            }
            if (!hasMod || !hasGen) throw new Exception($"kinds: [{string.Join(", ", f!.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))}]");
        });

        Run("Structural: C# expression-bodied member param change (not a lambda)", () =>
        {
            // "int Add(int a) => …" is an EBM, not a lambda: the arrow-body must be accepted as
            // a function body and the line excluded from lambda extraction.
            var a = MF("a.cs", new List<string> { "int Add(int a) => a + 1;" });
            var b = MF("b.cs", new List<string> { "int Add(int a, int b) => a + b;" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 1) throw new Exception($"expected 1 finding, got {(f == null ? "null" : string.Join(" | ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))) }");
            var x = f![0];
            if (x.Kind != StructuralKind.ParametersChanged || x.LineA != 1 || x.LineB != 1) throw new Exception($"got {x.Kind}@{x.LineA},{x.LineB}: {x.Message}");
            if (!x.Message.Contains("(int a)") || !x.Message.Contains("(int a, int b)")) throw new Exception($"message: {x.Message}");
        });

        Run("Structural: C# lambda signature changed", () =>
        {
            var a = MF("a.cs", new List<string> { "var f = x => x * 2;" });
            var b = MF("b.cs", new List<string> { "var f = y => y + 1;" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 1) throw new Exception($"expected 1 finding, got {(f == null ? "null" : string.Join(" | ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))) }");
            var x = f![0];
            if (x.Kind != StructuralKind.LambdaChanged || x.LineA != 1 || x.LineB != 1) throw new Exception($"got {x.Kind}@{x.LineA},{x.LineB}: {x.Message}");
            if (!x.Message.Contains("Lambda signature changed") || !x.Message.Contains("x") || !x.Message.Contains("y")) throw new Exception($"message: {x.Message}");
        });

        Run("Structural: C++ scoped enum kept + class removed (no phantom type)", () =>
        {
            // "enum class Color" is one type (kind enum) — the inner "class" must not become a
            // phantom of its own, and the real removal is Alpha only.
            var a = MF("a.cpp", new List<string> { "enum class Color {", "    Red,", "};", "", "class Alpha {", "};" });
            var b = MF("b.cpp", new List<string> { "enum class Color {", "    Red,", "};" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 1) throw new Exception($"expected 1 finding, got {(f == null ? "null" : string.Join(" | ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))) }");
            var x = f![0];
            if (x.Kind != StructuralKind.TypeRemoved || x.LineA != 5 || x.LineB != 0) throw new Exception($"got {x.Kind}@{x.LineA},{x.LineB}: {x.Message}");
            if (!x.Message.Contains("Alpha")) throw new Exception($"message: {x.Message}");
        });

        Run("Structural: C++ base clause added, brace on the next line", () =>
        {
            // When the '{' sits on a later line the first scan runs to EOL without assigning
            // basesRaw, so the base clause text was dropped and the change reported nothing.
            var a = MF("a.h", new List<string> { "class A", "{", "}" });
            var b = MF("b.h", new List<string> { "class A : private B", "{", "}" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 1) throw new Exception($"expected 1 finding, got {(f == null ? "null" : string.Join(" | ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}")))}");
            var x = f![0];
            if (x.Kind != StructuralKind.BaseChanged || x.LineA != 1 || x.LineB != 1)
                throw new Exception($"got {x.Kind}@{x.LineA},{x.LineB}: {x.Message}");
            if (!x.Message.Contains("private B") || x.Message.Contains(": private")) throw new Exception($"message: {x.Message}");
        });

        Run("Structural: Java base clause changed", () =>
        {
            var a = MF("a.java", new List<string> { "public class Foo extends Bar {", "}" });
            var b = MF("b.java", new List<string> { "public class Foo extends Baz implements Qux {", "}" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 1) throw new Exception($"expected 1 finding, got {(f == null ? "null" : string.Join(" | ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))) }");
            var x = f![0];
            if (x.Kind != StructuralKind.BaseChanged || x.LineA != 1 || x.LineB != 1) throw new Exception($"got {x.Kind}@{x.LineA},{x.LineB}: {x.Message}");
            if (!x.Message.Contains("Bar") || !x.Message.Contains("Baz Qux")) throw new Exception($"message: {x.Message}");
        });

        Run("Structural: Rust pub + return type + constant type changed", () =>
        {
            var a = MF("a.rs", new List<string> { "fn calc(x: i32) -> i32 {", "    x + 1", "}", "const FOO: i32 = 1;" });
            var b = MF("b.rs", new List<string> { "pub fn calc(x: i32) -> i64 {", "    x + 1", "}", "const FOO: i64 = 1;" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 3) throw new Exception($"expected 3 findings, got {(f == null ? "null" : string.Join(" | ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))) }");
            bool hasPub = false, hasRet = false, hasConst = false;
            foreach (var x in f!)
            {
                if (x.Kind == StructuralKind.ModifierChanged && x.LineA == 1 && x.LineB == 1 && x.Message.Contains("pub")) hasPub = true;
                if (x.Kind == StructuralKind.ReturnTypeChanged && x.LineA == 1 && x.LineB == 1
                    && x.Message.Contains("i32") && x.Message.Contains("i64")) hasRet = true;
                if (x.LineA == 4 && x.LineB == 4 && x.Message.StartsWith("Constant type changed")
                    && x.Message.Contains("i32") && x.Message.Contains("i64")) hasConst = true;
            }
            if (!hasPub || !hasRet || !hasConst) throw new Exception($"kinds: [{string.Join(", ", f!.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))}]");
        });

        Run("Structural: Rust constant added", () =>
        {
            var a = MF("a.rs", new List<string> { "fn main() {", "}" });
            var b = MF("b.rs", new List<string> { "fn main() {", "}", "const LIMIT: u32 = 10;" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 1) throw new Exception($"expected 1 finding, got {(f == null ? "null" : string.Join(" | ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))) }");
            var x = f![0];
            if (x.Kind != StructuralKind.FunctionAdded || x.LineA != 0 || x.LineB != 3) throw new Exception($"got {x.Kind}@{x.LineA},{x.LineB}: {x.Message}");
            if (!x.Message.Contains("Constant added: LIMIT")) throw new Exception($"message: {x.Message}");
        });

        Run("Structural: Rust closure changed, bitwise OR ignored", () =>
        {
            // "a | b" must not read as a closure (char before '|' is an identifier char); the
            // real closure change reports exactly one finding.
            var a = MF("a.rs", new List<string> { "fn apply() {", "    let f = |x| x + 1;", "    let m = a | b;", "}" });
            var b = MF("b.rs", new List<string> { "fn apply() {", "    let f = move |x, y| x + y;", "    let m = a | b;", "}" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 1) throw new Exception($"expected 1 finding, got {(f == null ? "null" : string.Join(" | ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))) }");
            var x = f![0];
            if (x.Kind != StructuralKind.LambdaChanged || x.LineA != 2 || x.LineB != 2) throw new Exception($"got {x.Kind}@{x.LineA},{x.LineB}: {x.Message}");
            if (!x.Message.Contains("|x|") || !x.Message.Contains("move |x, y|")) throw new Exception($"message: {x.Message}");
        });

        Run("Structural: Python decorator + params + annotation (three specific hints)", () =>
        {
            var a = MF("a.py", new List<string> { "@decor", "def f(x) -> int:", "    pass" });
            var b = MF("b.py", new List<string> { "@other", "def f(x, y) -> str:", "    pass" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 3) throw new Exception($"expected 3 findings, got {(f == null ? "null" : string.Join(" | ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))) }");
            bool hasParam = false, hasRet = false, hasDecor = false;
            foreach (var x in f!)
            {
                if (x.Kind == StructuralKind.ParametersChanged && x.LineA == 2 && x.LineB == 2
                    && x.Message.Contains("(x)") && x.Message.Contains("(x, y)")) hasParam = true;
                if (x.Kind == StructuralKind.ReturnTypeChanged && x.LineA == 2 && x.LineB == 2
                    && x.Message.Contains("int") && x.Message.Contains("str")) hasRet = true;
                if (x.Kind == StructuralKind.ModifierChanged && x.LineA == 2 && x.LineB == 2
                    && x.Message.Contains("@decor") && x.Message.Contains("@other")) hasDecor = true;
            }
            if (!hasParam || !hasRet || !hasDecor) throw new Exception($"kinds: [{string.Join(", ", f!.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))}]");
        });

        Run("Structural: Python class removed + base changed", () =>
        {
            var a = MF("a.py", new List<string> { "class Alpha(Base):", "    pass", "", "class Beta(Old):", "    pass" });
            var b = MF("b.py", new List<string> { "class Beta(New):", "    pass" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 2) throw new Exception($"expected 2 findings, got {(f == null ? "null" : string.Join(" | ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))) }");
            bool hasRem = false, hasBase = false;
            foreach (var x in f!)
            {
                if (x.Kind == StructuralKind.TypeRemoved && x.LineA == 1 && x.LineB == 0 && x.Message.Contains("Alpha")) hasRem = true;
                if (x.Kind == StructuralKind.BaseChanged && x.LineA == 4 && x.LineB == 1
                    && x.Message.Contains("Old") && x.Message.Contains("New")) hasBase = true;
            }
            if (!hasRem || !hasBase) throw new Exception($"kinds: [{string.Join(", ", f!.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))}]");
        });

        Run("Structural: Python comments and docstrings don't count", () =>
        {
            // "# def fake():", a brace-bearing line inside a triple-quoted docstring, and the
            // closing quotes must not produce phantom functions — only real() is compared.
            var a = MF("a.py", new List<string> { "# def fake():", "doc = \"\"\"", "{ def ghost(): }", "\"\"\"", "def real() -> int:", "    pass" });
            var b = MF("b.py", new List<string> { "# def fake():", "doc = \"\"\"", "{ def ghost(): }", "\"\"\"", "def real() -> str:", "    pass" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f == null || f.Count != 1) throw new Exception($"expected 1 finding, got {(f == null ? "null" : string.Join(" | ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))) }");
            var x = f![0];
            if (x.Kind != StructuralKind.ReturnTypeChanged || x.LineA != 5 || x.LineB != 5) throw new Exception($"got {x.Kind}@{x.LineA},{x.LineB}: {x.Message}");
            if (!x.Message.Contains("int") || !x.Message.Contains("str")) throw new Exception($"message: {x.Message}");
        });

        Run("Structural: Python indent style agnostic (4-space vs 2-space)", () =>
        {
            // Same code, different indentation width: the depth arrays must match exactly, so a
            // pure reindent produces no structural findings at all.
            var a = MF("a.py", new List<string> { "def f(x):", "    if x > 0:", "        return x", "    return 0" });
            var b = MF("b.py", new List<string> { "def f(x):", "  if x > 0:", "    return x", "  return 0" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f != null && f.Count != 0) throw new Exception($"expected 0 findings, got [{string.Join(", ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))}]");
        });

        Run("Structural: JS plain spec unchanged (arrows are not lambdas)", () =>
        {
            // .js uses the Plain spec: no lambda extraction, no qualifier comparison — an arrow
            // change plus an identical function must yield zero findings.
            var a = MF("a.js", new List<string> { "const f = (x) => x * 2;", "function add(a, b) {", "    return a + b;", "}" });
            var b = MF("b.js", new List<string> { "const g = (y) => y * 3;", "function add(a, b) {", "    return a + b;", "}" });
            var (aToB, bToA) = Align(a, b);
            var f = StructuralDiff.Analyze(a, b, aToB, bToA);
            if (f != null && f.Count != 0) throw new Exception($"expected 0 findings, got [{string.Join(", ", f.Select(x => $"{x.Kind}@{x.LineA},{x.LineB}: {x.Message}"))}]");
        });

        Run("Structural: Detect dispatch + cross-family pair → null", () =>
        {
            if (StructuralDiff.Detect("x.rs") != StructuralFamily.Rust) throw new Exception(".rs should detect Rust");
            if (StructuralDiff.Detect("y.py") != StructuralFamily.Python) throw new Exception(".py should detect Python");
            if (StructuralDiff.Detect("z.pyw") != StructuralFamily.Python) throw new Exception(".pyw should detect Python");
            if (StructuralDiff.Analyze(MF("a.rs", new List<string> { "x" }), MF("b.py", new List<string> { "y" }),
                Array.Empty<int>(), Array.Empty<int>()) != null)
                throw new Exception(".rs vs .py pair should yield null (family mismatch)");
        });

        Run("Myers: ERRORS Rendering_A/B pair — Standard & Patience are LCS-optimal", () =>
        {
            // The reported rendering bug: Standard produced a non-minimum alignment on this pair
            // (246 Unchanged vs true LCS 278), misaligning the #ifdef _DEBUG blocks and
            // scattering the removed InitiateGOAPJob overloads. Skipped if the repro files are
            // cleaned up; the fuzz test below guards the mechanism regardless.
            string pa = Path.Combine("..", "NeoDiff", "ERRORS", "Rendering_A.h");
            string pb = Path.Combine("..", "NeoDiff", "ERRORS", "Rendering_B.h");
            if (!File.Exists(pa) || !File.Exists(pb)) { Console.WriteLine("  (skipped — repro files absent)"); return; }
            var a = new FileModel { FilePath = pa, Lines = File.ReadAllLines(pa).ToList() };
            var b = new FileModel { FilePath = pb, Lines = File.ReadAllLines(pb).ToList() };

            int lcs = LcsDp(a.Lines, b.Lines);
            foreach (var alg in new[] { DiffAlgorithm.Standard, DiffAlgorithm.Patience })
            {
                var (fileA, _, _, _) = DiffEngine.Compute(a, b, ignoreWhitespace: false, algorithm: alg);
                int unchanged = 0;
                foreach (var l in fileA) if (l.Status == DiffStatus.Unchanged) unchanged++;
                if (unchanged != lcs)
                    throw new Exception($"{alg}: {unchanged} Unchanged matches, true LCS is {lcs}");
            }

            // Pin the displayed pairing on the reported regions (Standard): the #ifdef _DEBUG
            // blocks align block-to-block and the removed InitiateGOAPJob overloads stay unpaired.
            var (aToB, _) = Align(a, b);
            if (aToB[145] != 113) throw new Exception($"A:146 should pair with B:114, got B:{(aToB[145] < 0 ? "gap" : (aToB[145] + 1).ToString())}");
            if (aToB[210] != 187) throw new Exception($"A:211 (#ifdef _DEBUG) should pair with B:188, got B:{(aToB[210] < 0 ? "gap" : (aToB[210] + 1).ToString())}");
            // The removed InitiateGOAPJob overloads span A:82..108 except the blank line A:94,
            // which matches B:76 (both blank) and splits the run in two.
            if (aToB[93] != 75) throw new Exception($"A:94 (blank) should pair with B:76, got {(aToB[93] < 0 ? "gap" : (aToB[93] + 1).ToString())}");
            for (int i = 81; i <= 107 && i != 93; i++)
                if (aToB[i] >= 0) throw new Exception($"A:{i + 1} (removed InitiateGOAPJob block) should be unpaired, got B:{aToB[i] + 1}");
        });

        Run("Structural: bare 'template' token at end of stripped line (no OOB)", () =>
        {
            // The reported crash: "Diff computation error. Index was outside the bounds of the array".
            // A multi-line template clause puts "template" alone on a line, so the stripped line is
            // exactly "template" (8 chars). TryTemplateClause / IndexOfWord checked
            // `idx + len <= s.Length && !IsIdentChar(s[idx + len])` — when the token ends at the
            // string end that guard passed and then read s[len], one past the last index. End-of-
            // string is a valid word boundary, so the guard must be `>=`.
            var a = MF("a.h", new List<string>
            {
                "template", "<", "class T>", "class Foo {", "}"
            });
            var b = MF("b.h", new List<string>
            {
                "template", "<", "class T, class U>", "class Foo {", "}"
            });
            var (aToB, bToA) = Align(a, b);
            // Must not throw. (Finding content is not asserted: FindTypes is line-based and a
            // clause split across lines is outside its window — the single-line case is covered
            // by the "template clause on its own line" test above.)
            _ = StructuralDiff.Analyze(a, b, aToB, bToA);
        });

        Run("Structural: ERRORS GraphWorld_A/B pair — full pipeline (Compute + Unified + Analyze)", () =>
        {
            // The reported repro. A has one extra leading copyright comment line; the rest is
            // identical, so structural analysis yields no findings. Skipped if the repro files are
            // cleaned up; the synthetic test above guards the mechanism regardless.
            string pa = Path.Combine("..", "NeoDiff", "ERRORS", "GraphWorld_A.h");
            string pb = Path.Combine("..", "NeoDiff", "ERRORS", "GraphWorld_B.h");
            if (!File.Exists(pa) || !File.Exists(pb)) { Console.WriteLine("  (skipped — repro files absent)"); return; }
            var a = new FileModel { FilePath = pa, Lines = File.ReadAllLines(pa).ToList() };
            var b = new FileModel { FilePath = pb, Lines = File.ReadAllLines(pb).ToList() };

            foreach (var alg in new[] { DiffAlgorithm.Standard, DiffAlgorithm.Patience })
            {
                var (_, _, intA, intB) = DiffEngine.Compute(a, b, ignoreWhitespace: false, algorithm: alg);
                _ = DiffEngine.BuildUnified(intA, intB);
                var (aToB, bToA) = Align(a, b);
                var findings = StructuralDiff.Analyze(a, b, aToB, bToA);
                if (findings != null && findings.Count != 0)
                    throw new Exception($"{alg}: expected no findings, got {findings.Count}");
            }
        });

        Run("Myers: fuzz vs DP reference — Standard match count == LCS", () =>
        {
            // Cross-checks the bidirectional Myers split logic against a naive DP LCS on random
            // small pairs. Fixed seed → deterministic failures with full input for repro.
            var rng = new Random(12345);
            char[] alpha = { 'a', 'b', 'c' };
            for (int t = 0; t < 4000; t++)
            {
                int la = rng.Next(1, 16), lb = rng.Next(1, 16);
                var sa = new List<string>();
                var sb = new List<string>();
                for (int i = 0; i < la; i++) sa.Add(rng.Next(3) == 0 ? "" : alpha[rng.Next(3)].ToString());
                for (int i = 0; i < lb; i++) sb.Add(rng.Next(3) == 0 ? "" : alpha[rng.Next(3)].ToString());
                int lcs = LcsDp(sa, sb);
                var fa = new FileModel { FilePath = "a", Lines = sa };
                var fb = new FileModel { FilePath = "b", Lines = sb };
                var (fileA, _, _, _) = DiffEngine.Compute(fa, fb, ignoreWhitespace: false, algorithm: DiffAlgorithm.Standard);
                int got = 0;
                foreach (var l in fileA) if (l.Status == DiffStatus.Unchanged) got++;
                if (got != lcs)
                    throw new Exception($"t={t} lcs={lcs} got={got} A=[{string.Join("|", sa)}] B=[{string.Join("|", sb)}]");
            }
        });

        // ── CRC-32 (hex context menu) ──

        Run("crc32: standard check vectors", () =>
        {
            // The zlib/IEEE 802.3 variant: empty input → 0x00000000, "123456789" → 0xCBF43926.
            if (Crc32.Compute(ReadOnlySpan<byte>.Empty) != 0x00000000)
                throw new Exception($"empty should be 0x00000000, got 0x{Crc32.Compute(ReadOnlySpan<byte>.Empty):X8}");
            uint check = Crc32.Compute("123456789"u8);
            if (check != 0xCBF43926)
                throw new Exception($"\"123456789\" should be 0xCBF43926, got 0x{check:X8}");
        });

        Run("crc32: matches per-byte reference on random data", () =>
        {
            // Cross-check the table-driven loop against a bit-by-bit implementation (no table)
            // on random spans — catches table-generation regressions.
            static uint Reference(ReadOnlySpan<byte> data)
            {
                uint crc = 0xFFFFFFFF;
                foreach (byte b in data)
                {
                    crc ^= b;
                    for (int k = 0; k < 8; k++)
                        crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
                }
                return crc ^ 0xFFFFFFFF;
            }

            var rng = new Random(99);
            for (int t = 0; t < 200; t++)
            {
                int len = rng.Next(0, 512);
                var data = new byte[len];
                for (int i = 0; i < len; i++) data[i] = (byte)rng.Next(256);
                uint got = Crc32.Compute(data), want = Reference(data);
                if (got != want)
                    throw new Exception($"t={t} len={len}: table 0x{got:X8} != reference 0x{want:X8}");
            }
        });

        // ── HexValueCodec (hex inspector endianness decode/encode) ──────────────

        Run("codec: LE matches legacy BitConverter behavior", () =>
        {
            // Integer types round-trip exactly (decimal text → bytes → same decimal text).
            var ints = new (string Type, string Text)[]
            {
                ("int8", "-7"), ("uint8", "255"),
                ("int16", "-1234"), ("uint16", "65535"),
                ("int32", "123456789"), ("uint32", "4294967295"),
                ("int64", "1234567890123"), ("uint64", "18446744073709551615")
            };
            foreach (var (type, text) in ints)
            {
                if (!HexValueCodec.TryEncode(text, type, bigEndian: false, out var bytes))
                    throw new Exception($"{type}: encode '{text}' failed");
                string decoded = HexValueCodec.Decode(bytes, 0, type, bigEndian: false);
                if (decoded != text)
                    throw new Exception($"{type}: LE round-trip '{text}' → '{decoded}'");
            }

            // float/double: bytes must match BitConverter and display formatting must match the
            // old ElementParts path exactly (G9 / G15).
            if (!HexValueCodec.TryEncode("3.14159", "float", bigEndian: false, out var fb))
                throw new Exception("float encode failed");
            float f = 3.14159f;
            if (!fb.AsSpan().SequenceEqual(BitConverter.GetBytes(f)))
                throw new Exception("float LE bytes != BitConverter.GetBytes");
            if (HexValueCodec.Decode(fb, 0, "float", bigEndian: false) != f.ToString("G9"))
                throw new Exception($"float format: {HexValueCodec.Decode(fb, 0, "float", false)} != {f.ToString("G9")}");

            if (!HexValueCodec.TryEncode("2.71828", "double", bigEndian: false, out var db))
                throw new Exception("double encode failed");
            double d = 2.71828;
            if (!db.AsSpan().SequenceEqual(BitConverter.GetBytes(d)))
                throw new Exception("double LE bytes != BitConverter.GetBytes");
            if (HexValueCodec.Decode(db, 0, "double", bigEndian: false) != d.ToString("G15"))
                throw new Exception($"double format: {HexValueCodec.Decode(db, 0, "double", false)} != {d.ToString("G15")}");
        });

        Run("codec: char decode/encode", () =>
        {
            // Decode: printable ASCII shows the glyph, non-printable shows the placeholder.
            if (HexValueCodec.Decode(new byte[] { 0x41 }, 0, "char", false) != "A")
                throw new Exception("char decode 0x41 should be 'A'");
            if (HexValueCodec.Decode(new byte[] { 0x20 }, 0, "char", false) != " ")
                throw new Exception("char decode 0x20 should be a space");
            if (HexValueCodec.Decode(new byte[] { 0x7E }, 0, "char", false) != "~")
                throw new Exception("char decode 0x7E should be '~'");
            if (HexValueCodec.Decode(new byte[] { 0x00 }, 0, "char", false) != "·")
                throw new Exception("char decode 0x00 should be the placeholder");

            // Encode: a single character → its byte; explicit 0–255 values also accepted.
            if (!HexValueCodec.TryEncode("A", "char", false, out var ca) || ca[0] != 0x41)
                throw new Exception("char encode 'A' should be 0x41");
            if (!HexValueCodec.TryEncode("65", "char", false, out var c65) || c65[0] != 0x41)
                throw new Exception("char encode '65' (numeric) should be 0x41");
            if (!HexValueCodec.TryEncode("0x41", "char", false, out var chex) || chex[0] != 0x41)
                throw new Exception("char encode '0x41' should be 0x41");

            // Rejected: multi-char non-numeric, out-of-range value.
            if (HexValueCodec.TryEncode("AB", "char", false, out _))
                throw new Exception("two characters should fail");
            if (HexValueCodec.TryEncode("256", "char", false, out _))
                throw new Exception("256 should fail (out of byte range)");
        });

        Run("codec: BE int32 known bytes", () =>
        {
            // 0x00000102 = 258 big-endian; the same byte order read little-endian is 0x02010000.
            var be = new byte[] { 0x00, 0x00, 0x01, 0x02 };
            if (HexValueCodec.Decode(be, 0, "int32", bigEndian: true) != "258")
                throw new Exception("BE decode of 00 00 01 02 should be 258");
            if (HexValueCodec.Decode(be, 0, "int32", bigEndian: false) != ((int)(0x02010000)).ToString())
                throw new Exception("LE decode of 00 00 01 02 should be 0x02010000");

            if (!HexValueCodec.TryEncode("666", "int32", bigEndian: true, out var encBe))
                throw new Exception("BE encode 666 failed");
            if (!encBe.AsSpan().SequenceEqual(new byte[] { 0x00, 0x00, 0x02, 0x9A }))
                throw new Exception($"BE encode 666 → {string.Join(" ", encBe.Select(b => b.ToString("X2")))}");

            if (!HexValueCodec.TryEncode("258", "int32", bigEndian: false, out var encLe))
                throw new Exception("LE encode 258 failed");
            if (!encLe.AsSpan().SequenceEqual(new byte[] { 0x02, 0x01, 0x00, 0x00 }))
                throw new Exception($"LE encode 258 → {string.Join(" ", encLe.Select(b => b.ToString("X2")))}");
        });

        Run("codec: BE float/double match reversed BitConverter", () =>
        {
            if (!HexValueCodec.TryEncode("1.5", "float", bigEndian: true, out var f))
                throw new Exception("BE float encode failed");
            var wantF = BitConverter.GetBytes(1.5f);
            Array.Reverse(wantF);
            if (!f.AsSpan().SequenceEqual(wantF))
                throw new Exception("BE float bytes != reversed BitConverter");

            if (!HexValueCodec.TryEncode("2.71828", "double", bigEndian: true, out var d))
                throw new Exception("BE double encode failed");
            var wantD = BitConverter.GetBytes(2.71828);
            Array.Reverse(wantD);
            if (!d.AsSpan().SequenceEqual(wantD))
                throw new Exception("BE double bytes != reversed BitConverter");
        });

        Run("codec: parse and range edges", () =>
        {
            if (!HexValueCodec.TryEncode("-128", "int8", false, out _)) throw new Exception("int8 -128 should pass");
            if (HexValueCodec.TryEncode("-129", "int8", false, out _)) throw new Exception("int8 -129 should fail");
            if (HexValueCodec.TryEncode("128", "int8", false, out _)) throw new Exception("int8 128 should fail");
            if (!HexValueCodec.TryEncode("-0x1", "int32", false, out var neg) ||
                HexValueCodec.Decode(neg, 0, "int32", false) != "-1")
                throw new Exception("int32 -0x1 should encode to -1");
            if (!HexValueCodec.TryEncode("0xFFFFFFFFFFFFFFFF", "uint64", false, out var max))
                throw new Exception("uint64 max hex should pass");
            if (HexValueCodec.Decode(max, 0, "uint64", false) != "18446744073709551615")
                throw new Exception("uint64 max decode mismatch");
            if (!HexValueCodec.TryEncode("0xFF", "uint8", false, out var ff) ||
                HexValueCodec.Decode(ff, 0, "uint8", false) != "255")
                throw new Exception("uint8 0xFF should be 255");
            if (HexValueCodec.TryEncode("-1", "uint16", false, out _)) throw new Exception("signed uint should fail");
            if (HexValueCodec.TryEncode("abc", "float", false, out _)) throw new Exception("non-numeric float should fail");
            if (HexValueCodec.TryEncode("5", "int5", false, out _)) throw new Exception("unknown type should fail");
            if (HexValueCodec.Decode(new byte[] { 1 }, 0, "int5", false) != "?")
                throw new Exception("unknown type decode should be '?'");
        });

        Run("codec: HexBytes formatting", () =>
        {
            var data = new byte[] { 0x0A, 0x1F, 0xFF };
            if (HexValueCodec.HexBytes(data, 0, 3) != "0A 1F FF")
                throw new Exception("HexBytes should be space-separated uppercase hex");
        });

        Run("codec: HexPrimitives lookup", () =>
        {
            if (!HexPrimitives.TryGetSize("int32", out int s) || s != 4) throw new Exception("int32 size");
            if (!HexPrimitives.TryGetSize("DOUBLE", out int s2) || s2 != 8) throw new Exception("case-insensitive lookup");
            if (HexPrimitives.TryGetSize("int5", out _)) throw new Exception("unknown type should fail");
            if (HexPrimitives.TryGetSize("", out _) || HexPrimitives.TryGetSize(null, out _))
                throw new Exception("empty/null should fail");
        });

        Run("codec: struct pattern TotalSize and Validate", () =>
        {
            var p = new AwfulDiff.Models.HexStructPattern
            {
                Name = "Header",
                Fields =
                {
                    new AwfulDiff.Models.HexStructField { Name = "magic", Type = "int16" },
                    new AwfulDiff.Models.HexStructField { Name = "payload", Type = "int8", Count = 3 },
                    new AwfulDiff.Models.HexStructField { Name = "value", Type = "double" }
                }
            };
            if (p.TotalSize != 2 + 3 + 8) throw new Exception($"TotalSize should be 13, got {p.TotalSize}");
            if (p.Validate() != null) throw new Exception("valid pattern should pass Validate");

            var clone = p.Clone();
            clone.Fields[0].Type = "int64";
            if (p.Fields[0].Type != "int16") throw new Exception("Clone must be a deep copy");

            if (new AwfulDiff.Models.HexStructPattern { Name = "" }.Validate() == null)
                throw new Exception("empty name should fail Validate");
            if (new AwfulDiff.Models.HexStructPattern { Name = "x", Fields = new List<AwfulDiff.Models.HexStructField>() }.Validate() == null)
                throw new Exception("no fields should fail Validate");
            if (new AwfulDiff.Models.HexStructPattern
                {
                    Name = "x",
                    Fields = { new AwfulDiff.Models.HexStructField { Type = "int5" } }
                }.Validate() == null)
                throw new Exception("unknown field type should fail Validate");
            if (new AwfulDiff.Models.HexStructPattern
                {
                    Name = "x",
                    Fields = { new AwfulDiff.Models.HexStructField { Count = 0 } }
                }.Validate() == null)
                throw new Exception("count 0 should fail Validate");
        });

        Run("inspector: struct select/reload headless (STA)", () =>
        {
            // Regression test for the combo-rebuild crash: Items.Clear() raises SelectionChanged
            // transiently with index -1, which must not blow up the handler. Also verifies struct
            // selection, TypeSize propagation, and int32 fallback when a pattern is deleted.
            string settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AwfulDiff", "settings.json");
            string backup = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;
            try
            {
                File.WriteAllText(settingsPath,
                    @"{ ""hexInspectorType"": ""TestHdr"", ""hexStructPatterns"": [ { ""name"": ""TestHdr"", ""bigEndian"": false, ""fields"": [ { ""name"": ""magic"", ""type"": ""uint16"", ""count"": 1 }, { ""name"": ""payload"", ""type"": ""int8"", ""count"": 4 } ] } ] }");

                Exception staError = null;
                string result = "";
                var t = new Thread(() =>
                {
                    try
                    {
                        var inspector = new AwfulDiff.Controls.HexInspector();
                        int lastSize = -1;
                        inspector.TypeSizeChanged += s => lastSize = s;

                        inspector.SetTypeByName("TestHdr");
                        result += inspector.TypeName + "|" + inspector.TypeSize + "|";
                        if (inspector.SelectedPattern == null) throw new Exception("SelectedPattern should be set");
                        if (lastSize != 6) throw new Exception($"TypeSizeChanged should fire 6, got {lastSize}");

                        // Reload with the pattern still saved — selection preserved, no crash.
                        inspector.ReloadStructPatterns();
                        result += inspector.TypeName + "|";
                        if (inspector.TypeName != "TestHdr") throw new Exception("reload should preserve selection");

                        // Delete the pattern — falls back to int32.
                        File.WriteAllText(settingsPath, @"{ ""hexInspectorType"": ""TestHdr"", ""hexStructPatterns"": [] }");
                        inspector.ReloadStructPatterns();
                        result += inspector.TypeName + "|" + inspector.TypeSize;
                        if (inspector.TypeName != "int32" || inspector.TypeSize != 4)
                            throw new Exception($"fallback should be int32/4, got {inspector.TypeName}/{inspector.TypeSize}");
                    }
                    catch (Exception ex) { staError = ex; }
                });
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                t.Join();
                if (staError != null) throw staError;
                if (result != "TestHdr|6|TestHdr|int32|4")
                    throw new Exception($"unexpected sequence: {result}");
            }
            finally
            {
                if (backup != null) File.WriteAllText(settingsPath, backup);
                else if (File.Exists(settingsPath)) File.Delete(settingsPath);
            }
        });

        static int LcsDp(List<string> a, List<string> b)
        {
            int n = a.Count, m = b.Count;
            var d = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
                for (int j = m - 1; j >= 0; j--)
                    d[i, j] = a[i] == b[j] ? d[i + 1, j + 1] + 1 : Math.Max(d[i + 1, j], d[i, j + 1]);
            return d[0, 0];
        }

        // ── Syntax highlighter: function-call and type heuristics ─────────────

        Run("syntax: C# call, type, keyword classification", () =>
        {
            var h = SyntaxHighlighter.Create("a.cs")!;
            // Default struct has Start=0/Length=0, so the round-trip check doubles as the "no token" test.
            SyntaxTokenKind? Kind(string line, string word)
            {
                int idx = line.IndexOf(word, StringComparison.Ordinal);
                var t = h.TokenizeLine(line).FirstOrDefault(t => t.Start == idx && t.Length == word.Length);
                return t.Start == idx && t.Length == word.Length ? (SyntaxTokenKind?)t.Kind : null;
            }
            const string line = "    MyType result = MyClass.DoThing(42);";
            if (Kind(line, "MyType") != SyntaxTokenKind.Type) throw new Exception("MyType should be Type");
            if (Kind(line, "result") != null) throw new Exception("plain local should be uncoloured");
            if (Kind(line, "MyClass") != SyntaxTokenKind.Type) throw new Exception("MyClass should be Type");
            if (Kind(line, "DoThing") != SyntaxTokenKind.Function) throw new Exception("DoThing( should be Function");
        });

        Run("syntax: control keywords are not function calls", () =>
        {
            var h = SyntaxHighlighter.Create("a.cs")!;
            const string line = "if (x > 0) { for (int i = 0; i < n; i++) {} }";
            var tokens = h.TokenizeLine(line);
            foreach (var kw in new[] { "if", "for" })
            {
                int idx = line.IndexOf(kw, StringComparison.Ordinal);
                var t = tokens.FirstOrDefault(t => t.Start == idx && t.Length == kw.Length);
                // Guard Start too: a missing token is the default struct, whose Kind is Keyword (0).
                if (t.Start != idx || t.Kind != SyntaxTokenKind.Keyword)
                    throw new Exception($"'{kw}' should be Keyword, got {(t.Start == idx ? t.Kind.ToString() : "no token")}");
            }
        });

        Run("syntax: Python def/class", () =>
        {
            var h = SyntaxHighlighter.Create("a.py")!;
            var t1 = h.TokenizeLine("def make_thing():");
            if (t1.FirstOrDefault(t => t.Start == 4 && t.Length == "make_thing".Length).Kind != SyntaxTokenKind.Function)
                throw new Exception("def name should be Function");
            var t2 = h.TokenizeLine("class Widget:");
            if (t2.FirstOrDefault(t => t.Start == 6 && t.Length == "Widget".Length).Kind != SyntaxTokenKind.Type)
                throw new Exception("class name should be Type");
        });

        Run("syntax: XAML element names are types", () =>
        {
            var h = SyntaxHighlighter.Create("a.xaml")!;
            var tokens = h.TokenizeLine("<StackPanel x:Name=\"panel\">");
            int idx = 1; // after '<'
            if (tokens.FirstOrDefault(t => t.Start == idx && t.Length == "StackPanel".Length).Kind != SyntaxTokenKind.Type)
                throw new Exception("StackPanel should be Type");
        });

        Console.WriteLine($"\nResults: {passed} passed, {failed} failed");
        Environment.Exit(failed > 0 ? 1 : 0);
    }

    static FileModel MF(string name, List<string> lines) => new() { FilePath = name, Lines = lines };

    /// <summary>
    /// Cross-file alignment maps from the row-aligned interleaved pair (same walk as the VM's
    /// BuildAlignmentMaps): aToB[i] = 0-based B index aligned with A line i (-1 unpaired).
    /// </summary>
    static (int[] aToB, int[] bToA) Align(FileModel a, FileModel b)
    {
        var (_, _, intA, intB) = DiffEngine.Compute(a, b);
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

    // ── Directory-diff test helpers (first filesystem-touching tests) ───────────

    /// <summary>Create two empty temp dirs A/B under a unique root; the returned cleanup deletes it.</summary>
    static (string a, string b, Action cleanup) MakeDirPair()
    {
        var root = Path.Combine(Path.GetTempPath(), "AwfulDiffDirTest_" + Guid.NewGuid().ToString("N"));
        string a = Directory.CreateDirectory(Path.Combine(root, "A")).FullName;
        string b = Directory.CreateDirectory(Path.Combine(root, "B")).FullName;
        return (a, b, () => { try { Directory.Delete(root, true); } catch { /* best effort */ } });
    }

    static void WriteFile(string dir, string relativePath, string content)
    {
        var full = Path.Combine(dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>Find an entry by backslash-relative path, descending into folder children.</summary>
    static DirEntry? FindEntry(List<DirEntry> entries, string relativePath)
    {
        foreach (var e in entries)
        {
            if (string.Equals(e.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase)) return e;
            if (e.Kind == DirEntryKind.Directory)
            {
                var hit = FindEntry(e.Children, relativePath);
                if (hit != null) return hit;
            }
        }
        return null;
    }

    static int CountAll(List<DirEntry> entries)
    {
        int n = entries.Count;
        foreach (var e in entries)
            if (e.Kind == DirEntryKind.Directory) n += CountAll(e.Children);
        return n;
    }

    /// <summary>
    /// Structural invariants for every merge state: the three triple lists are row-aligned
    /// (equal length), the base pane covers exactly lines 1..n in order, and every row of an
    /// unresolved conflict is flagged Conflict in the base pane.
    /// </summary>
    static void AssertInvariants(MergeState s)
    {
        if (s.TripleA.Count != s.TripleBase.Count || s.TripleBase.Count != s.TripleB.Count)
            throw new Exception($"triple lengths differ: A={s.TripleA.Count} base={s.TripleBase.Count} B={s.TripleB.Count}");

        int expect = 1;
        foreach (var l in s.TripleBase)
            if (l.OriginalLineNumber > 0)
            {
                if (l.OriginalLineNumber != expect)
                    throw new Exception($"base pane line number {l.OriginalLineNumber}, expected {expect}");
                expect++;
            }
        if (expect - 1 != s.BaseLines.Count)
            throw new Exception($"base pane covers {expect - 1} lines, file has {s.BaseLines.Count}");

        // Every unresolved conflict slot renders as a Conflict row in the base pane. (Resolved
        // slots may still show a dimmed Conflict cell when they resolved to a pure deletion.)
        for (int r = 0; r < s.TripleBase.Count; r++)
        {
            if (s.ConflictLineAtRow(r) is not { } cl) continue;
            if (cl.block.ResolutionAt(cl.slot) == MergeResolution.Unresolved
                && s.TripleBase[r].Status != DiffStatus.Conflict)
                throw new Exception($"unresolved conflict slot at row {r} not flagged Conflict in base pane");
        }

        // A row can never be both a dimmed loser and the winner of a resolved slot.
        foreach (var list in new[] { s.TripleA, s.TripleBase, s.TripleB })
            if (list.Any(l => l.Rejected && l.ResolvedConflict))
                throw new Exception("row flagged Rejected AND ResolvedConflict");
    }

    /// <summary>
    /// Replicates MainWindowViewModel.SwapFiles flip path (flip Removed↔Added in each list,
    /// swap the lists) and asserts the result is identical to a fresh Compute with sides reversed.
    /// </summary>
    static void AssertSwapEquivalence(List<string> linesA, List<string> linesB, DiffAlgorithm algorithm = DiffAlgorithm.Standard)
    {
        var fa = new FileModel { FilePath = "a.txt", Lines = linesA };
        var fb = new FileModel { FilePath = "b.txt", Lines = linesB };

        var (listA, listB, intA, intB) = DiffEngine.Compute(fa, fb, computeInlineDiffs: true, algorithm: algorithm);

        // Flip in place, exactly like SwapFiles does
        foreach (var list in new[] { listA, listB, intA, intB })
            foreach (var line in list)
            {
                if (line.Status == DiffStatus.Removed) line.Status = DiffStatus.Added;
                else if (line.Status == DiffStatus.Added) line.Status = DiffStatus.Removed;
            }
        var flippedLeft = listB;   // new left = old B
        var flippedRight = listA;  // new right = old A
        var flippedIntLeft = intB;
        var flippedIntRight = intA;

        var (freshLeft, freshRight, freshIntLeft, freshIntRight) =
            DiffEngine.Compute(fb, fa, computeInlineDiffs: true, algorithm: algorithm);

        AssertListsEqual(flippedLeft, freshLeft, "left");
        AssertListsEqual(flippedRight, freshRight, "right");
        AssertListsEqual(flippedIntLeft, freshIntLeft, "int-left");
        AssertListsEqual(flippedIntRight, freshIntRight, "int-right");

        // The matches themselves must form a valid alignment of the two inputs
        AssertValidAlignment(linesA, linesB, listA, listB);
    }

    /// <summary>
    /// Every real line must carry the same Status/OriginalText/ChangedRegions in the plain
    /// (gap-off) list and the interleaved (gap-on) list — toggling ShowGaps must not change
    /// how a line is classified. Gap rows are skipped; A's real rows appear 1:1 in both lists.
    /// </summary>
    static void AssertGapNonGapConsistency(List<string> linesA, List<string> linesB, DiffAlgorithm algorithm = DiffAlgorithm.Standard)
    {
        var fa = new FileModel { FilePath = "a.txt", Lines = linesA };
        var fb = new FileModel { FilePath = "b.txt", Lines = linesB };
        var (listA, listB, intA, intB) = DiffEngine.Compute(fa, fb, computeInlineDiffs: true, algorithm: algorithm);

        CheckConsistency("A", listA, intA);
        CheckConsistency("B", listB, intB);

        static void CheckConsistency(string side, List<DiffLine> plain, List<DiffLine> interleaved)
        {
            int p = 0; // pointer into interleaved (skips Gap rows)
            for (int i = 0; i < plain.Count; i++)
            {
                while (p < interleaved.Count && interleaved[p].Status == DiffStatus.Gap) p++;
                if (p >= interleaved.Count)
                    throw new Exception($"{side}: interleaved ran out of rows at plain row {i}");
                var pl = plain[i]; var il = interleaved[p++];
                if (pl.OriginalLineNumber != il.OriginalLineNumber)
                    throw new Exception($"{side} row {i}: OrigLn {pl.OriginalLineNumber}!={il.OriginalLineNumber}");
                string Regs(List<(int start, int length)>? g) =>
                    g == null ? "" : string.Join(",", g.Select(t => $"{t.start}-{t.length}"));
                if (pl.Status != il.Status)
                    throw new Exception($"{side} row {i} ln={pl.OriginalLineNumber} '{pl.Text}': " +
                        $"plain={pl.Status} gap={il.Status}");
                if (!string.Equals(pl.OriginalText, il.OriginalText))
                    throw new Exception($"{side} row {i} ln={pl.OriginalLineNumber}: " +
                        $"OrigText '{pl.OriginalText}'!='{il.OriginalText}'");
                if (Regs(pl.ChangedRegions) != Regs(il.ChangedRegions))
                    throw new Exception($"{side} row {i} ln={pl.OriginalLineNumber}: " +
                        $"Regions [{Regs(pl.ChangedRegions)}]!=[{Regs(il.ChangedRegions)}]");
                // Plain and interleaved are separate DiffLine instances — both must carry the
                // same anchor for Added/Removed rows (AnnotateAnchors stamps each set).
                if (pl.OtherSideAnchor != il.OtherSideAnchor)
                    throw new Exception($"{side} row {i} ln={pl.OriginalLineNumber}: " +
                        $"Anchor plain={pl.OtherSideAnchor} interleaved={il.OtherSideAnchor}");
            }
        }
    }

     /// <summary>
    /// The Unchanged rows must form a valid common subsequence of the two inputs: equal count on
    /// both sides (list order already guarantees strictly increasing original line numbers), and
    /// pairwise-equal text. Holds for both engines, including lines demoted to Unchanged by block pairing.
    /// </summary>
    static void AssertValidAlignment(List<string> linesA, List<string> linesB, List<DiffLine> listA, List<DiffLine> listB)
    {
        var ua = new List<int>();
        var ub = new List<int>();
        foreach (var l in listA) if (l.Status == DiffStatus.Unchanged) ua.Add(l.OriginalLineNumber - 1);
        foreach (var l in listB) if (l.Status == DiffStatus.Unchanged) ub.Add(l.OriginalLineNumber - 1);
        if (ua.Count != ub.Count)
            throw new Exception($"Unchanged count mismatch: A={ua.Count} B={ub.Count}");
        for (int i = 0; i < ua.Count; i++)
            if (linesA[ua[i]] != linesB[ub[i]])
                throw new Exception($"Unchanged pair {i}: A line {ua[i] + 1} '{linesA[ua[i]]}' != B line {ub[i] + 1} '{linesB[ub[i]]}'");
    }

    static void AssertListsEqual(List<DiffLine> x, List<DiffLine> y, string side)
    {
        if (x.Count != y.Count)
            throw new Exception($"{side}: count mismatch flipped={x.Count} fresh={y.Count}");
        for (int i = 0; i < x.Count; i++)
        {
            var f = x[i]; var r = y[i];
            var problems = new List<string>();
            if (f.OriginalLineNumber != r.OriginalLineNumber)
                problems.Add($"OrigLn {f.OriginalLineNumber}!={r.OriginalLineNumber}");
            if (f.Status != r.Status)
                problems.Add($"Status {f.Status}!={r.Status}");
            if (f.Text != r.Text)
                problems.Add($"Text '{f.Text}'!='{r.Text}'");
            if (f.OriginalText != r.OriginalText)
                problems.Add($"OrigText '{f.OriginalText}'!='{r.OriginalText}'");
            string Regs(List<(int start, int length)>? g) =>
                g == null ? "" : string.Join(",", g.Select(t => $"{t.start}-{t.length}"));
            if (Regs(f.ChangedRegions) != Regs(r.ChangedRegions))
                problems.Add($"Regions [{Regs(f.ChangedRegions)}]!=[{Regs(r.ChangedRegions)}]");
            // Flip preserves which rows are real, so the other-side real-line count before each
            // row is unchanged — anchors must survive SwapFiles without re-annotation.
            if (f.OtherSideAnchor != r.OtherSideAnchor)
                problems.Add($"Anchor {f.OtherSideAnchor}!={r.OtherSideAnchor}");
            if (problems.Count > 0)
                throw new Exception($"{side} row {i}: " + string.Join("; ", problems));
        }
    }

    /// <summary>
    /// Byte-level mirror of AssertSwapEquivalence: flipping Removed↔Added and swapping the row
    /// lists must equal a fresh TryCompute + BuildAlignedRows with the sides reversed. Also
    /// checks the match pairing is non-crossing and pairs only equal bytes.
    /// </summary>
    static void AssertByteSwapEquivalence(byte[] a, byte[] b)
    {
        if (!ByteDiff.TryCompute(a, b, ByteDiff.MaxWorkCells, out var mb1, out var ma1))
            throw new Exception("TryCompute(a,b) failed");
        var (rowsA, rowsB) = ByteDiff.BuildAlignedRows(a, 0, a.Length, b, 0, b.Length, mb1, ma1, true);

        foreach (var list in new[] { rowsA, rowsB })
            foreach (var line in list)
                if (line.Status == DiffStatus.Removed) line.Status = DiffStatus.Added;
                else if (line.Status == DiffStatus.Added) line.Status = DiffStatus.Removed;

        if (!ByteDiff.TryCompute(b, a, ByteDiff.MaxWorkCells, out var mb2, out var ma2))
            throw new Exception("TryCompute(b,a) failed");
        var (freshLeft, freshRight) = ByteDiff.BuildAlignedRows(b, 0, b.Length, a, 0, a.Length, mb2, ma2, true);

        AssertByteRowsEqual(rowsB, freshLeft, "left");
        AssertByteRowsEqual(rowsA, freshRight, "right");

        int prevI = -1, prevJ = -1;
        for (int i = 0; i < a.Length; i++)
        {
            if (mb1[i] < 0) continue;
            if (a[i] != b[mb1[i]]) throw new Exception($"match {i}->{mb1[i]} pairs unequal bytes");
            if (i <= prevI || mb1[i] <= prevJ) throw new Exception($"crossing match at {i}->{mb1[i]}");
            prevI = i; prevJ = mb1[i];
        }
    }

    static void AssertByteRowsEqual(List<DiffLine> x, List<DiffLine> y, string side)
    {
        if (x.Count != y.Count)
            throw new Exception($"{side}: count mismatch flipped={x.Count} fresh={y.Count}");
        for (int i = 0; i < x.Count; i++)
        {
            var f = x[i]; var r = y[i];
            string Regs(List<(int start, int length)>? g) =>
                g == null ? "" : string.Join(",", g.Select(t => $"{t.start}-{t.length}"));
            if (f.OriginalLineNumber != r.OriginalLineNumber || f.Status != r.Status || f.Text != r.Text
                || f.ByteStart != r.ByteStart || f.HexSlotMask != r.HexSlotMask || f.OriginalText != r.OriginalText)
                throw new Exception($"{side} row {i}: flipped (Ln={f.OriginalLineNumber} St={f.Status} Bs={f.ByteStart} M={f.HexSlotMask:X4})" +
                                    $" != fresh (Ln={r.OriginalLineNumber} St={r.Status} Bs={r.ByteStart} M={r.HexSlotMask:X4})");
            if (Regs(f.ChangedRegions) != Regs(r.ChangedRegions))
                throw new Exception($"{side} row {i}: regions [{Regs(f.ChangedRegions)}] != [{Regs(r.ChangedRegions)}]");
        }
    }

    static void Run(string name, Action test)
    {
        try
        {
            test();
            passed++;
            Console.WriteLine($"PASS: {name}");
        }
        catch (Exception ex)
        {
            failed++;
            Console.WriteLine($"FAIL: {name} — {ex.Message}");
        }
    }

 }
