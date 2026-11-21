namespace AwfulDiff.Models;

/// <summary>
/// One field of a hex struct pattern: a primitive type (one of <see cref="HexPrimitives"/>)
/// repeated Count times, i.e. an array. Name is an optional display label for the inspector.
/// </summary>
public class HexStructField
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "int32";
    public int Count { get; set; } = 1;

    /// <summary>Bytes occupied by this field (unknown type contributes 0).</summary>
    public int ByteSize =>
        (HexPrimitives.TryGetSize(Type, out var size) ? size : 0) * Math.Max(1, Count);
}

/// <summary>
/// A named custom struct the hex inspector can decode/encode: an ordered list of fields,
/// with a per-pattern byte order. The inspector interprets a selected byte range as an
/// array of instances of this struct (like its "array of type" behavior for primitives).
/// Persisted in AppSettings.HexStructPatterns.
/// </summary>
public class HexStructPattern
{
    public string Name { get; set; } = "";
    public bool BigEndian { get; set; } = false;   // default little-endian, like the rest of the inspector
    public List<HexStructField> Fields { get; set; } = new();

    /// <summary>Total bytes per struct instance: sum of all field sizes.</summary>
    public int TotalSize => Fields.Sum(f => f.ByteSize);

    /// <summary>
    /// Deep-copy so the manager dialog can edit a loaded pattern without mutating the saved one.
    /// </summary>
    public HexStructPattern Clone() => new()
    {
        Name = Name,
        BigEndian = BigEndian,
        Fields = Fields.Select(f => new HexStructField { Name = f.Name, Type = f.Type, Count = f.Count }).ToList()
    };

    /// <summary>
    /// Validation for the manager dialog: returns an error message, or null when the pattern is valid.
    /// </summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "Pattern name is required.";
        if (Fields.Count == 0) return "The struct needs at least one field.";
        for (int i = 0; i < Fields.Count; i++)
        {
            var f = Fields[i];
            if (!HexPrimitives.IsKnown(f.Type))
                return $"Field {i + 1}: unknown type '{f.Type}'.";
            if (f.Count < 1)
                return $"Field {i + 1}: count must be at least 1.";
        }
        return null;
    }
}
