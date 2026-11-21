namespace AwfulDiff.Services;

/// <summary>
/// Pure byte-editing helpers shared by the hex editing entry points (inspector value commit,
/// direct hex field, delete key). WPF-free so they can be unit tested.
/// </summary>
public static class ByteEdit
{
    /// <summary>
    /// Replaces [startByte, startByte+removeCount) of source with insertBytes and returns a new
    /// array (source untouched). Pure insert when removeCount == 0; pure delete when insertBytes
    /// is empty. startByte may equal source.Length (append at end of file). Arguments are clamped
    /// into range rather than throwing — callers pass selection-derived offsets that can drift
    /// after concurrent edits.
    /// </summary>
    public static byte[] Splice(byte[] source, int startByte, int removeCount, byte[] insertBytes)
    {
        if (startByte < 0) startByte = 0;
        if (startByte > source.Length) startByte = source.Length;
        if (removeCount < 0) removeCount = 0;
        if (startByte + removeCount > source.Length) removeCount = source.Length - startByte;

        int newLength = source.Length - removeCount + insertBytes.Length;
        var result = new byte[newLength];
        Array.Copy(source, 0, result, 0, startByte);
        Array.Copy(insertBytes, 0, result, startByte, insertBytes.Length);
        Array.Copy(source, startByte + removeCount, result, startByte + insertBytes.Length,
                   source.Length - (startByte + removeCount));
        return result;
    }

    /// <summary>
    /// Adjusts right-side sync anchors after an edit at startByte whose net length change is
    /// delta: shifts anchors with ByteB >= startByte by delta, drops anchors whose ByteB falls
    /// outside [0, newLengthB), then greedily removes crossing pairs so the result satisfies
    /// <see cref="ByteDiff.IsAnchorSetOrdered"/>. ByteA values are untouched (the left file is
    /// never edited). Mutates points in place.
    /// </summary>
    public static void AdjustSyncPoints(List<(int ByteA, int ByteB)> points, int startByte, int delta, int newLengthB)
    {
        for (int i = points.Count - 1; i >= 0; i--)
        {
            var (a, b) = points[i];
            if (b >= startByte) b += delta;
            if (b < 0 || b >= newLengthB)
            {
                points.RemoveAt(i);
                continue;
            }
            points[i] = (a, b);
        }

        // A deletion can make two anchors cross (later ByteA mapping to an earlier ByteB); keep
        // the longest co-monotone subsequence by greedy scan — same predicate as IsAnchorSetOrdered.
        points.Sort((p, q) => p.ByteA != q.ByteA ? p.ByteA.CompareTo(q.ByteA) : p.ByteB.CompareTo(q.ByteB));
        int kept = 0, prevA = -1, prevB = -1;
        for (int i = 0; i < points.Count; i++)
        {
            var (a, b) = points[i];
            if (a <= prevA || b <= prevB) continue;
            if (kept != i) points[kept] = points[i];
            kept++;
            prevA = a; prevB = b;
        }
        points.RemoveRange(kept, points.Count - kept);
    }
}
