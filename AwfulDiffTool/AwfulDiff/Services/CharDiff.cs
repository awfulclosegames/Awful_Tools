using System.Text;
using AwfulDiff.Models;

namespace AwfulDiff.Services;

/// <summary>
/// Computes character-level differences between two strings.
/// Used to identify which parts of a "Modified" line actually differ,
/// so the viewer can highlight only changed characters/bytes inline.
/// </summary>
public static class CharDiff
{
    /// <summary>
    /// Maximum product of string lengths for the O(N×M) DP algorithm.
    /// Larger diffs fall back to "entire line changed" approximation.
    /// </summary>
    private const int MaxDpCells = 500_000;

    /// <summary>
    /// Compute which character ranges in <paramref name=" newText"/> differ from <paramref name="oldText"/>.
    /// Returns a list of (start, length) tuples describing changed regions in newText.
    /// Returns null if the strings are identical or either is empty.
    /// When <paramref name="ignoreWhitespace"/> is true, whitespace-only differences (anywhere
    /// in the line, git --ignore-all-space semantics) are not reported as changes, and inline
    /// diff regions ignore all whitespace variations.
    /// </summary>
    public static List<(int start, int length)>? ComputeChangedRegions(string? oldText, string? newText, bool ignoreWhitespace = false)
    {
        if (string.IsNullOrEmpty(newText)) return null;
        if (string.IsNullOrEmpty(oldText)) return newText.Length > 0 ? new List<(int, int)> { (0, newText.Length) } : null;

        // Quick check: identical strings → no changes
        if (oldText == newText) return null;

        // When ignoring whitespace, compute the diff on fully whitespace-stripped text and map
        // regions back to original positions. This ensures whitespace-only differences (anywhere
        // in the line) don't produce inline highlights.
        if (ignoreWhitespace)
        {
            string oldStripped = StripAllWhitespace(oldText);
            string newStripped = StripAllWhitespace(newText);

            if (newStripped.Length == 0) return null;   // nothing to highlight in an all-whitespace line
            if (oldStripped == newStripped)
                return null; // Only whitespace differences → no changes

            var strippedChanged = ComputeChangedRegionsInternal(oldStripped, newStripped);
            if (strippedChanged == null) return null;

            // map[i] = index in newText of the i-th non-whitespace character
            int[] map = new int[newStripped.Length];
            int o = 0;
            for (int i = 0; i < newStripped.Length; i++)
            {
                while (char.IsWhiteSpace(newText[o])) o++;
                map[i] = o++;
            }

            // Map regions from stripped space back to original positions. A region spans the
            // original characters from its first to its last stripped char — whitespace between
            // them stays unhighlighted by construction.
            var mapped = new List<(int, int)>();
            foreach (var (start, len) in strippedChanged)
            {
                int origStart = map[start];
                int origEnd = map[start + len - 1] + 1;
                if (origEnd > origStart)
                    mapped.Add((origStart, origEnd - origStart));
            }
            return mapped.Count > 0 ? mapped : null;
        }

       return ComputeChangedRegionsInternal(oldText, newText);
    }

    /// <summary>
    /// Core diff logic: hex dump auto-detection, LCS-based unchanged runs, gap computation.
    /// </summary>
    private static List<(int start, int length)>? ComputeChangedRegionsInternal(string oldText, string newText)
    {
        // Auto-detect hex dump format and use byte-level comparison for accuracy
        if (LooksLikeHexDump(newText))
        {
            var hexChanged = ComputeHexChangedRegionsFromString(oldText, newText);
            if (hexChanged != null) return hexChanged;
        }

        var unchanged = ComputeUnchangedRuns(oldText, newText);

        if (unchanged == null || unchanged.Count == 0)
        {
            // Entire line is different
            return new List<(int, int)> { (0, newText.Length) };
        }

        // Changed regions are the gaps between unchanged runs in newText
        var changed = new List<(int, int)>();
        int pos = 0;

        foreach (var (ns, len) in unchanged)
        {
            if (pos < ns)
                changed.Add((pos, ns - pos));
            pos = ns + len;
        }

        // Trailing changed region
        if (pos < newText.Length)
            changed.Add((pos, newText.Length - pos));

        return changed.Count > 0 ? changed : null;
    }

    /// <summary>
    /// For hex dump lines, compute which character columns in the formatted text correspond
    /// to bytes that differ between oldBytes and newBytes.
    /// </summary>
    public static List<(int start, int length)>? ComputeHexChangedRegions(
        byte[] oldBytes, int oldOffset,
        byte[] newBytes, int newOffset,
        int byteCount,
        string formattedLine)
    {
        var changedCols = new bool[formattedLine.Length];
        int bytesCompared = Math.Min(byteCount, Math.Min(oldBytes.Length - oldOffset, newBytes.Length - newOffset));

        for (int b = 0; b < bytesCompared; b++)
        {
            if (oldBytes[oldOffset + b] != newBytes[newOffset + b])
            {
                // Mark hex digit columns: 10 + b*3 + (b >= 8 ? 1 : 0), 2 characters
                int hexCol = 10 + b * 3 + (b >= 8 ? 1 : 0);
                if (hexCol < formattedLine.Length)
                    changedCols[hexCol] = true;
                if (hexCol + 1 < formattedLine.Length)
                    changedCols[hexCol + 1] = true;

                // Mark ASCII column: 60 + b, 1 character
                int charCol = 60 + b;
                if (charCol < formattedLine.Length)
                    changedCols[charCol] = true;
            }
        }

        // If one side has more bytes than the other, mark extra positions as changed
        int remaining = byteCount - bytesCompared;
        for (int b = bytesCompared; b < byteCount; b++)
        {
            int hexCol = 10 + b * 3 + (b >= 8 ? 1 : 0);
            if (hexCol < formattedLine.Length)
                changedCols[hexCol] = true;
            if (hexCol + 1 < formattedLine.Length)
                changedCols[hexCol + 1] = true;
            int charCol = 60 + b;
            if (charCol < formattedLine.Length)
                changedCols[charCol] = true;
        }

        // Collapse consecutive true values into contiguous runs
        var regions = new List<(int, int)>();
        int i = 0;
        while (i < changedCols.Length)
        {
            if (changedCols[i])
            {
                int start = i;
                while (i < changedCols.Length && changedCols[i]) i++;
                regions.Add((start, i - start));
            }
            else
            {
                i++;
            }
        }

        return regions.Count > 0 ? regions : null;
    }

    /// <summary>
    /// For hex dump lines, compute changed regions by parsing byte values from two formatted
    /// hex strings and comparing byte-by-byte. This is more accurate than a generic string LCS
    /// because it identifies exactly which bytes differ, then maps them to character columns.
    /// Falls back to generic character-level diff if parsing fails.
    /// </summary>
    public static List<(int start, int length)>? ComputeHexChangedRegionsFromString(string? oldText, string? newText)
    {
        if (string.IsNullOrEmpty(newText)) return null;
        if (string.IsNullOrEmpty(oldText)) return newText.Length > 0 ? new List<(int, int)> { (0, newText.Length) } : null;
        if (oldText == newText) return null;

        // Parse bytes from both hex lines
        byte[] oldBytes = ParseHexLineBytes(oldText);
        byte[] newBytes = ParseHexLineBytes(newText);

        if (oldBytes.Length == 0 && newBytes.Length == 0)
            return null; // Can't parse, no diff possible

        int compareCount = Math.Min(oldBytes.Length, newBytes.Length);
        var changedCols = new bool[newText.Length];
        bool anyChanged = false;

        for (int b = 0; b < compareCount; b++)
        {
            if (oldBytes[b] != newBytes[b])
            {
                // Mark hex digit columns: 10 + b*3 + (b >= 8 ? 1 : 0), 2 characters
                int hexCol = 10 + b * 3 + (b >= 8 ? 1 : 0);
                for (int ch = 0; ch < 2; ch++)
                {
                    int c = hexCol + ch;
                    if (c < newText.Length) { changedCols[c] = true; anyChanged = true; }
                }

                // Mark ASCII column: 60 + b, 1 character
                int charCol = 60 + b;
                if (charCol < newText.Length) { changedCols[charCol] = true; anyChanged = true; }
            }
        }

        // If new text has more bytes than old, mark extra as changed
        for (int b = compareCount; b < newBytes.Length; b++)
        {
            int hexCol = 10 + b * 3 + (b >= 8 ? 1 : 0);
            for (int ch = 0; ch < 2; ch++)
            {
                int c = hexCol + ch;
                if (c < newText.Length) { changedCols[c] = true; anyChanged = true; }
            }
            int charCol = 60 + b;
            if (charCol < newText.Length) { changedCols[charCol] = true; anyChanged = true; }
        }

        if (!anyChanged) return null;

        // Collapse consecutive true values into contiguous runs
        var regions = new List<(int, int)>();
        int i = 0;
        while (i < changedCols.Length)
        {
            if (changedCols[i])
            {
                int start = i;
                while (i < changedCols.Length && changedCols[i]) i++;
                regions.Add((start, i - start));
            }
            else
            {
                i++;
            }
        }

        return regions.Count > 0 ? regions : null;
    }

    /// <summary>
    /// Extract byte values from a formatted hex dump line by reading the hex digit pairs.
    /// Format: "00000040  48 65 6C 6C 6F ...  Hello.World...."
    /// Hex digits are at columns 10 + b*3 + (b >= 8 ? 1 : 0) for byte index b.
    /// </summary>
    static byte[] ParseHexLineBytes(string lineText)
    {
        var bytes = new byte[16];
        int count = 0;

        for (int b = 0; b < 16; b++)
        {
            int col = 10 + b * 3 + (b >= 8 ? 1 : 0);
            if (col + 1 < lineText.Length)
            {
                int high = HexDigitValue(lineText[col]);
                int low = HexDigitValue(lineText[col + 1]);
                if (high >= 0 && low >= 0)
                {
                    bytes[count++] = (byte)((high << 4) | low);
                }
                else
                {
                    break; // Invalid hex digits, stop parsing
                }
            }
            else
            {
                break; // Column out of range
            }
        }

        Array.Resize(ref bytes, count);
        return bytes;
    }

    static int HexDigitValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'F' => c - 'A' + 10,
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => -1
    };

    /// <summary>
    /// Detect if a string looks like a hex dump line (starts with 8 hex digits for the offset,
    /// followed by spaces, then hex byte pairs).
    /// </summary>
    static bool LooksLikeHexDump(string text)
    {
        // Check: 8 hex digits at the start, followed by spaces
        if (text.Length < 12) return false;
        for (int i = 0; i < 8; i++)
        {
            if (HexDigitValue(text[i]) < 0) return false;
        }
        // Columns 8-9 should be spaces
        if (text[8] != ' ' || text[9] != ' ') return false;
        // Column 10 should be a hex digit (start of first byte)
        if (HexDigitValue(text[10]) < 0) return false;
        return true;
    }

    /// <summary>
    /// Compute unchanged character runs using LCS DP. Returns list of (startInNew, length) tuples.
    /// </summary>
    static List<(int start, int length)>? ComputeUnchangedRuns(string oldText, string newText)
    {
        int n = oldText.Length;
        int m = newText.Length;

        // Guard against expensive computation
        if ((long)n * m > MaxDpCells) return null;

        // Build LCS DP table (shorter string on rows to minimize cache misses)
        bool doTranspose = n > m;
        int rows, cols;
        string horz, vert;

        if (doTranspose)
        {
            rows = m; cols = n;
            horz = newText; vert = oldText;
        }
        else
        {
            rows = n; cols = m;
            horz = oldText; vert = newText;
        }

        // Use short[] to save memory vs int[]
        short[,] dp = new short[rows + 1, cols + 1];

        for (int r = 1; r <= rows; r++)
        {
            for (int c = 1; c <= cols; c++)
            {
                if (horz[r - 1] == vert[c - 1])
                    dp[r, c] = (short)(dp[r - 1, c - 1] + 1);
                else
                    dp[r, c] = (short)Math.Max(dp[r - 1, c], dp[r, c - 1]);
            }
        }

       // Traceback to find matching character pairs
        var pairs = new List<(int oldIdx, int newIdx)>();
        int tr = rows, tc = cols;
        while (tr > 0 && tc > 0)
        {
            if (horz[tr - 1] == vert[tc - 1])
            {
                pairs.Add((tr - 1, tc - 1));
                tr--; tc--;
            }
            else if (dp[tr - 1, tc] >= dp[tr, tc - 1])
                tr--;
            else
                tc--;
        }

        pairs.Reverse();

        if (pairs.Count == 0) return null;

        // Convert pairs to runs in newText space
        // Each pair is (idxInHorz, idxInVert). If doTranspose, swap.
        var runs = new List<(int start, int length)>();
        int runStart = -1, runLen = 0;
        int prevOld = -2, prevNew = -2;

        for (int i = 0; i < pairs.Count; i++)
        {
            int oldI, newI;
            if (doTranspose)
            {
                oldI = pairs[i].newIdx;
                newI = pairs[i].oldIdx;
            }
            else
            {
                oldI = pairs[i].oldIdx;
                newI = pairs[i].newIdx;
            }

            // Check if this character is contiguous with the previous in both strings
            bool contiguous = (runLen == 0) || (oldI == prevOld + 1 && newI == prevNew + 1);

            if (contiguous)
            {
                if (runLen == 0) runStart = newI;
                runLen++;
            }
            else
            {
                if (runLen >= 1) runs.Add((runStart, runLen));
                runStart = newI;
                runLen = 1;
            }

            prevOld = oldI;
            prevNew = newI;
        }

        if (runLen >= 1) runs.Add((runStart, runLen));

        return runs.Count > 0 ? runs : null;
    }

    /// <summary>
    /// Given ChangedRegions for a Modified line, compute the complement: unchanged regions.
    /// </summary>
    public static List<(int start, int length)> ComputeUnchangedFromChanged(string text, List<(int start, int length)> changedRegions)
    {
        var unchanged = new List<(int, int)>();
        int pos = 0;

        foreach (var (start, len) in changedRegions)
        {
            if (pos < start)
                unchanged.Add((pos, start - pos));
            pos = start + len;
        }

        if (pos < text.Length)
            unchanged.Add((pos, text.Length - pos));

        return unchanged;
    }

    /// <summary>
    /// Removes every whitespace character from a line — the normalization used whenever
    /// "ignore whitespace" is on (git --ignore-all-space semantics). Displayed text is never
    /// altered; this only feeds equality tests and inline-diff computation.
    /// </summary>
    public static string StripAllWhitespace(string s)
    {
        bool hasWs = false;
        for (int i = 0; i < s.Length; i++)
            if (char.IsWhiteSpace(s[i])) { hasWs = true; break; }
        if (!hasWs) return s;   // fast path: no allocation when the line has no whitespace at all

        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            if (!char.IsWhiteSpace(c)) sb.Append(c);
        return sb.ToString();
    }

    static int LeadingSpace(string s)
    {
        int i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i;
    }

    static int TrailingSpace(string s)
    {
        int i = s.Length - 1;
        while (i >= 0 && char.IsWhiteSpace(s[i])) i--;
        return s.Length - 1 - i;
    }
}
