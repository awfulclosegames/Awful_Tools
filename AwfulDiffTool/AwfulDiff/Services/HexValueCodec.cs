using System.Buffers.Binary;
using System.Globalization;
using AwfulDiff.Models;

namespace AwfulDiff.Services;

/// <summary>
/// Pure byte-order-aware decode/encode for the hex inspector's primitive types. Replaces the
/// inspector's inline BitConverter logic so struct patterns can honor a per-pattern endianness.
/// Little-endian results are byte-identical to the previous BitConverter path on x86/x64, and
/// display formatting matches the old behavior (float "G9", double "G15"). No WPF dependencies —
/// covered by the AwfulDiff.Tests console harness.
/// </summary>
public static class HexValueCodec
{
    /// <summary>
    /// Decodes one value at bytes[offset] as typeName in the given byte order into a display string.
    /// Callers must guarantee offset + size fits within bytes.
    /// </summary>
    public static string Decode(byte[] bytes, int offset, string typeName, bool bigEndian)
    {
        var span = bytes.AsSpan(offset);
        return typeName switch
        {
            "int8" => ((sbyte)bytes[offset]).ToString(),
            "uint8" => bytes[offset].ToString(),
            "char" => CharDisplay(bytes[offset]),
            "int16" => (bigEndian ? BinaryPrimitives.ReadInt16BigEndian(span) : BinaryPrimitives.ReadInt16LittleEndian(span)).ToString(),
            "uint16" => (bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(span) : BinaryPrimitives.ReadUInt16LittleEndian(span)).ToString(),
            "int32" => (bigEndian ? BinaryPrimitives.ReadInt32BigEndian(span) : BinaryPrimitives.ReadInt32LittleEndian(span)).ToString(),
            "uint32" => (bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(span) : BinaryPrimitives.ReadUInt32LittleEndian(span)).ToString(),
            "float" => (bigEndian ? BinaryPrimitives.ReadSingleBigEndian(span) : BinaryPrimitives.ReadSingleLittleEndian(span)).ToString("G9"),
            "int64" => (bigEndian ? BinaryPrimitives.ReadInt64BigEndian(span) : BinaryPrimitives.ReadInt64LittleEndian(span)).ToString(),
            "uint64" => (bigEndian ? BinaryPrimitives.ReadUInt64BigEndian(span) : BinaryPrimitives.ReadUInt64LittleEndian(span)).ToString(),
            "double" => (bigEndian ? BinaryPrimitives.ReadDoubleBigEndian(span) : BinaryPrimitives.ReadDoubleLittleEndian(span)).ToString("G15"),
            _ => "?"
        };
    }

    /// <summary>Space-separated uppercase hex of bytes[offset..offset+size] for row comments.</summary>
    public static string HexBytes(byte[] bytes, int offset, int size)
    {
        var parts = new string[size];
        for (int j = 0; j < size; j++)
            parts[j] = bytes[offset + j].ToString("X2");
        return string.Join(" ", parts);
    }

    /// <summary>
    /// Parses user-entered text into raw bytes of typeName in the given byte order. Integers accept
    /// decimal or 0x-prefixed hex (sign only for signed types) and are range-checked; float/double
    /// parse as invariant-culture doubles. Returns false on any parse, range, or unknown-type error.
    /// </summary>
    public static bool TryEncode(string text, string typeName, bool bigEndian, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (!HexPrimitives.TryGetSize(typeName, out int size)) return false;

        if (typeName is "float" or "double")
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                return false;
            bytes = new byte[size];
            var fspan = bytes.AsSpan();
            if (size == 4)
            {
                if (bigEndian) BinaryPrimitives.WriteSingleBigEndian(fspan, (float)d);
                else BinaryPrimitives.WriteSingleLittleEndian(fspan, (float)d);
            }
            else
            {
                if (bigEndian) BinaryPrimitives.WriteDoubleBigEndian(fspan, d);
                else BinaryPrimitives.WriteDoubleLittleEndian(fspan, d);
            }
            return true;
        }

        // char — a single character → its byte, or an explicit 0–255 value (decimal / 0x-hex).
        if (typeName == "char")
        {
            string t = text.Trim();
            if (t.Length == 1)
            {
                int code = t[0];
                if (code > 255) return false;
                bytes = new[] { unchecked((byte)code) };
                return true;
            }
            ulong cv;
            bool okChar = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? ulong.TryParse(t.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out cv)
                : ulong.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out cv);
            if (!okChar || cv > 255) return false;
            bytes = new[] { unchecked((byte)cv) };
            return true;
        }

        // Integer types — decimal (optional sign) or 0x-prefixed hex.
        if (typeName.StartsWith("int"))
        {
            long l;
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("-0x", StringComparison.OrdinalIgnoreCase))
            {
                string digits = text.TrimStart('-', '+').Substring(2);
                if (!long.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out l))
                    return false;
                if (text[0] == '-') l = unchecked(-l);
            }
            else if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l))
            {
                return false;
            }
            switch (size)
            {
                case 1: if (l < sbyte.MinValue || l > sbyte.MaxValue) return false; break;
                case 2: if (l < short.MinValue || l > short.MaxValue) return false; break;
                case 4: if (l < int.MinValue || l > int.MaxValue) return false; break;
            }
            bytes = WriteInteger(l, size, bigEndian);
            return true;
        }

        // Unsigned types — decimal or 0x-prefixed hex (no sign).
        if (text.StartsWith('-') || text.StartsWith('+'))
            return false;
        ulong ul;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!ulong.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ul))
                return false;
        }
        else if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ul))
        {
            return false;
        }
        switch (size)
        {
            case 1: if (ul > byte.MaxValue) return false; break;
            case 2: if (ul > ushort.MaxValue) return false; break;
            case 4: if (ul > uint.MaxValue) return false; break;
        }
        bytes = WriteInteger(unchecked((long)ul), size, bigEndian);
        return true;
    }

    /// <summary>Displays one byte as a character: printable ASCII (0x20–0x7E) as itself, else a placeholder.</summary>
    private static string CharDisplay(byte b)
        => b >= 0x20 && b <= 0x7E ? ((char)b).ToString() : "·";

    /// <summary>Writes the low `size` bytes of value in the given byte order (sign is irrelevant at the byte level).</summary>
    private static byte[] WriteInteger(long value, int size, bool bigEndian)
    {
        var b = new byte[size];
        var span = b.AsSpan();
        switch (size)
        {
            case 1:
                span[0] = unchecked((byte)value);
                break;
            case 2:
                if (bigEndian) BinaryPrimitives.WriteInt16BigEndian(span, unchecked((short)value));
                else BinaryPrimitives.WriteInt16LittleEndian(span, unchecked((short)value));
                break;
            case 4:
                if (bigEndian) BinaryPrimitives.WriteInt32BigEndian(span, unchecked((int)value));
                else BinaryPrimitives.WriteInt32LittleEndian(span, unchecked((int)value));
                break;
            default:
                if (bigEndian) BinaryPrimitives.WriteInt64BigEndian(span, value);
                else BinaryPrimitives.WriteInt64LittleEndian(span, value);
                break;
        }
        return b;
    }
}
