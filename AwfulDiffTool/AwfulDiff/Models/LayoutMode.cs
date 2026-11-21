namespace AwfulDiff.Models;

/// <summary>
/// Diff layout: two-pane arrangements and the git-style single panel.
/// </summary>
public enum LayoutMode
{
    /// <summary>
    /// Two side-by-side panes (File A | File B).
    /// </summary>
    SideBySide,

    /// <summary>
    /// Two stacked panes (File A above File B) with a draggable splitter.
    /// </summary>
    TopBottom,

    /// <summary>
    /// Single git-style panel: unchanged lines plain, deletions as '-' lines,
    /// additions as '+' lines; modified lines render as a remove+add pair.
    /// Text view only (hex view forces a two-pane layout).
    /// </summary>
    Unified
}
