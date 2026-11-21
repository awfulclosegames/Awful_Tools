using System.Numerics;

namespace AwfulDiff.Services;

/// <summary>
/// Shared hex-dump formatting and column math — the single source of truth for byte→column
/// positions, used by the rowizer (ChangedRegions), the viewer (hit-testing, selection
/// highlights) and the formatters below. Layout (fixed 76 columns):
///   cols 0-7   offset (X8)
///   cols 8-9   spaces
///   slot c    hex digits at col 10 + c*3 + (c>=8 ? 1 : 0), ascii char at col 60 + c
/// </summary>
public static class HexFormat
{
    public const int BytesPerLine = 16;

    private static readonly char[] HexDigits = { '0','1','2','3','4','5','6','7','8','9','A','B','C','D','E','F' };

    public static int BytesToHexLines(int byteCount)
    {
        if (byteCount == 0) return 0;
        return (byteCount + BytesPerLine - 1) / BytesPerLine;
    }

    /// <summary>(colStart, colLen) of the hex digit pair for slot c.</summary>
    public static (int Col, int Len) HexSlotColumns(int slot) => (10 + slot * 3 + (slot >= 8 ? 1 : 0), 2);

    /// <summary>Column of the ascii-section character for slot c.</summary>
    public static int AsciiColumn(int slot) => 60 + slot;

    /// <summary>Number of occupied slots in a row mask.</summary>
    public static int SlotCount(int slotMask) => BitOperations.PopCount((uint)slotMask);

    /// <summary>
    /// File-space index (relative to startIdx) of the byte occupying slot c.
    /// Caller must ensure bit c is set in slotMask.
    /// </summary>
    public static int SlotFileIndex(int startIdx, int slotMask, int slot) =>
        startIdx + BitOperations.PopCount((uint)(slotMask & ((1 << slot) - 1)));

    /// <summary>
    /// Ordinal (0-based) of slot c among the row's occupied slots. Caller must ensure bit c is set.
    /// </summary>
    public static int SlotOrdinal(int slotMask, int slot) =>
        BitOperations.PopCount((uint)(slotMask & ((1 << slot) - 1)));

    /// <summary>Highest occupied slot in the mask (-1 if no slots are occupied).</summary>
    public static int LastSlot(int slotMask) =>
        slotMask == 0 ? -1 : 31 - BitOperations.LeadingZeroCount((uint)slotMask);

    /// <summary>
    /// Format a contiguous hex row (all slots filled up to the end of the file):
    /// "00000040  48 65 6C 6C 6F ...  Hello.World...." — used for single-file dumps and
    /// the positional fallback. Trailing columns beyond the last byte are not emitted.
    /// </summary>
    public static string FormatContiguousRow(int startOffset, byte[] bytes, int totalLength)
    {
        int endOffset = Math.Min(startOffset + BytesPerLine, totalLength);
        int actualCount = endOffset - startOffset;

        // Use char[] for efficient building — fixed format: 8 offset + 2 space + hex + ascii
        var chars = new char[80];
        int pos = 0;

        // Offset (8 hex digits, zero-padded)
        string offsetStr = startOffset.ToString("X8");
        offsetStr.CopyTo(0, chars, pos, 8);
        pos += 8;
        chars[pos++] = ' ';
        chars[pos++] = ' ';

        // Hex bytes with gap after byte 8
        for (int j = 0; j < BytesPerLine; j++)
        {
            if (j == actualCount) break;
            int byteIdx = startOffset + j;
            if (byteIdx >= totalLength) break;

            if (j == 8)
                chars[pos++] = ' '; // Extra space for the gap between bytes 8 and 9

            chars[pos++] = HexDigits[(bytes[byteIdx] >> 4) & 0x0F];
            chars[pos++] = HexDigits[bytes[byteIdx] & 0x0F];
            chars[pos++] = ' ';
        }

        // Pad remaining positions with spaces for missing bytes
        // Full line hex section: 16 bytes × 3 chars each + 1 gap = 49 chars
        // So ASCII section starts at column 8 + 2 + 49 = 59, then 1 space separator = col 60
        while (pos < 8 + 2 + 49) // Target column for ASCII section
            chars[pos++] = ' ';

        chars[pos++] = ' ';

        // ASCII representation
        for (int j = 0; j < BytesPerLine; j++)
        {
            int byteIdx = startOffset + j;
            if (byteIdx >= totalLength) break;

            char c = bytes[byteIdx] >= 0x20 && bytes[byteIdx] < 0x7F ? (char)bytes[byteIdx] : '.';
            chars[pos++] = c;
        }

        return new string(chars, 0, pos);
    }

    /// <summary>
    /// Format a byte-aligned hex row where slots may have holes: occupied slot c renders
    /// bytes[startIdx + popcount(mask &amp; ((1&lt;&lt;c)-1))] at its column positions, empty
    /// slots render as blanks. Always returns the full 76-column width (trailing blanks are
    /// invisible in the viewer). slotMask must be non-zero — Gap rows use Text = "".
    /// </summary>
    public static string FormatAlignedRow(int fileOffset, byte[] bytes, int startIdx, int slotMask)
    {
        // Space-fill: unwritten columns (offset padding, hex separators, the mid gap) must be
        // blanks, not '\0' — embedded NULs would break monospace column alignment in the viewer.
        var chars = new char[80];
        Array.Fill(chars, ' ');

        // Offset (8 hex digits, zero-padded); cols 8-9 stay blank
        fileOffset.ToString("X8").CopyTo(0, chars, 0, 8);

        int fileIdx = startIdx;
        for (int c = 0; c < BytesPerLine; c++)
        {
            int hexCol = HexSlotColumns(c).Col;
            if (((slotMask >> c) & 1) != 0)
            {
                byte b = bytes[fileIdx++];
                chars[hexCol] = HexDigits[(b >> 4) & 0x0F];
                chars[hexCol + 1] = HexDigits[b & 0x0F];
                chars[AsciiColumn(c)] = b >= 0x20 && b < 0x7F ? (char)b : '.';
            }
            else
            {
                chars[hexCol] = ' ';
                chars[hexCol + 1] = ' ';
                chars[AsciiColumn(c)] = ' ';
            }
        }

        return new string(chars, 0, 76);
    }
}
