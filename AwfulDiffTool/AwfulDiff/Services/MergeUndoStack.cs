namespace AwfulDiff.Services;

/// <summary>
/// Immutable copy of the small mutable surface of a MergeState — everything a merge operation
/// can change: per-block conflict resolutions, per-unit rejected lines (whole-unit reject ≡
/// full set, accept ≡ empty set), and the Local Edits text pair. Everything else in the state
/// (triples, row maps, counts, IsDirty) is re-derived by RebuildDisplay / derived properties,
/// so restoring a snapshot + one Refresh() restores the whole session. Captured/restored via
/// MergeEngine.CaptureSnapshot / RestoreSnapshot to keep encapsulation in the engine.
/// </summary>
public sealed class MergeSnapshot
{
    /// <summary>Copied Resolutions array per conflict block (index-aligned with state.Conflicts).</summary>
    public required MergeResolution[][] BlockResolutions { get; init; }

    /// <summary>Copied rejected-line arrays per change unit (index-aligned with state.Changes).</summary>
    public required int[][] ChangeRejectedLines { get; init; }

    /// <summary>Reference copy — strings are immutable.</summary>
    public string? ManualMergedOverride { get; init; }

    public string? ManualEditBaseline { get; init; }
}

/// <summary>
/// Undo/redo history for a merge session. Each entry holds the PRE-state snapshot of one user
/// operation, so undo = restore the top snapshot (the state before that op). Edit entries
/// coalesce: consecutive keystrokes inside <see cref="EditCoalesceWindow"/> refresh the top
/// entry's timestamp instead of pushing, so one typing pause is one undo step. Any new
/// mutation clears the redo stack.
/// </summary>
public sealed class MergeUndoStack
{
    public const int MaxEntries = 100;
    public static readonly TimeSpan EditCoalesceWindow = TimeSpan.FromMilliseconds(1500);

    private sealed class Entry
    {
        public required MergeSnapshot Snap { get; init; }
        public bool IsEdit { get; init; }
        public DateTime StampUtc { get; set; }
    }

    private readonly List<Entry> _undo = new();
    private readonly List<MergeSnapshot> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>
    /// Records one operation's pre-state. isEdit entries coalesce with a recent edit on top of
    /// the stack (timestamp refreshed, snapshot dropped); op entries always push and clear redo.
    /// </summary>
    public void Push(MergeSnapshot snap, bool isEdit)
    {
        if (isEdit && _undo.Count > 0)
        {
            var top = _undo[^1];
            if (top.IsEdit && DateTime.UtcNow - top.StampUtc < EditCoalesceWindow)
            {
                top.StampUtc = DateTime.UtcNow;   // sustained typing stays a single step
                return;
            }
        }

        _undo.Add(new Entry { Snap = snap, IsEdit = isEdit, StampUtc = DateTime.UtcNow });
        if (_undo.Count > MaxEntries) _undo.RemoveAt(0);
        _redo.Clear();
    }

    /// <summary>Pops the undo top onto the redo stack; null when empty.</summary>
    public MergeSnapshot? PopUndo()
    {
        if (_undo.Count == 0) return null;
        var e = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(e.Snap);
        return e.Snap;
    }

    /// <summary>Pops the redo top back onto the undo stack; null when empty.</summary>
    public MergeSnapshot? PopRedo()
    {
        if (_redo.Count == 0) return null;
        var s = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        // Redo entries are never "edit" — a later keystroke starts a fresh step, not a merge.
        _undo.Add(new Entry { Snap = s });
        return s;
    }
}
