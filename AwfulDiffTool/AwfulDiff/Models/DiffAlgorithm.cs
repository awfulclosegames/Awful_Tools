namespace AwfulDiff.Models;

/// <summary>
/// Line-matching algorithm used by the diff engine.
/// </summary>
public enum DiffAlgorithm
{
    /// <summary>
    /// Myers' O(N+D²) bidirectional diff — the faster of the two engines.
    /// </summary>
    Standard,

    /// <summary>
    /// Patience diff (default) — anchors on lines unique to both files (largest non-crossing
    /// set via LIS), recursing between anchors with Myers as the fallback for ranges without
    /// unique lines. Tends to produce more intuitive alignments on repetitive/structured code.
    /// </summary>
    Patience
}
