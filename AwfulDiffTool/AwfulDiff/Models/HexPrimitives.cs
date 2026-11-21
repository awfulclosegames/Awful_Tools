namespace AwfulDiff.Models;

/// <summary>
/// The primitive value types the hex inspector can decode/encode (display name + byte size).
/// Single source of truth shared by the inspector, the endianness codec, and the struct
/// pattern manager's type selectors.
/// </summary>
public static class HexPrimitives
{
    public static readonly (string Display, int Size)[] All =
    {
        ("int8",   1), ("uint8",  1), ("char",  1),
        ("int16",  2), ("uint16", 2),
        ("int32",  4), ("uint32", 4), ("float",  4),
        ("int64",  8), ("uint64", 8), ("double", 8)
    };

    /// <summary>Case-insensitive size lookup; false for unknown or empty names.</summary>
    public static bool TryGetSize(string? name, out int size)
    {
        size = 0;
        if (string.IsNullOrWhiteSpace(name)) return false;
        foreach (var (display, s) in All)
        {
            if (string.Equals(display, name, StringComparison.OrdinalIgnoreCase))
            {
                size = s;
                return true;
            }
        }
        return false;
    }

    /// <summary>True when the name is one of the known primitive display names.</summary>
    public static bool IsKnown(string? name) => TryGetSize(name, out _);
}
