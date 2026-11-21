using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;
using AwfulDiff.Models;
using AwfulDiff.Services;

namespace AwfulDiff.ViewModels;

/// <summary>
/// View model for the 3-way merge window. Owns a <see cref="MergeState"/> and re-exposes its
/// triple display lists plus status after every mutation (rebuild → reassign → requery).
/// </summary>
public class MergeWindowViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly MergeState _state;

    /// <summary>Merge output path requested via CLI (-o or 4th positional); null = save to the base file.</summary>
    private readonly string? _outputPath;

    /// <summary>Undo/redo history for this session (fresh per window).</summary>
    private readonly MergeUndoStack _undo = new();

    /// <summary>
    /// Set while writing MergedEditorText FROM state (enable-editing fill, rebase push-back) so
    /// those programmatic sets don't record undo entries — only user keystrokes do.
    /// </summary>
    private bool _editorSetTextIsProgrammatic;

    /// <summary>Writes editor text from state without recording an undo entry.</summary>
    private void SetEditorTextFromState(string text)
    {
        _editorSetTextIsProgrammatic = true;
        try { MergedEditorText = text; }
        finally { _editorSetTextIsProgrammatic = false; }
    }

    /// <summary>Last conflict block visited by F3/Ctrl+F3 (index into State.Conflicts).</summary>
    private int _conflictCursor = -1;

    /// <summary>Last conflict block with a resolved slot visited by Shift+F3/Ctrl+Shift+F3.</summary>
    private int _resolvedCursor = -1;

    /// <summary>Last change unit visited by Ctrl+Down/Ctrl+Up (index into State.Changes).</summary>
    private int _changeCursor = -1;

    // ── Display lists (reassigned after each RebuildDisplay) ───────────────────

    private IReadOnlyList<DiffLine> _linesA = Array.Empty<DiffLine>();
    public IReadOnlyList<DiffLine> LinesA { get => _linesA; private set => SetProperty(ref _linesA, value); }

    private IReadOnlyList<DiffLine> _linesBase = Array.Empty<DiffLine>();
    public IReadOnlyList<DiffLine> LinesBase { get => _linesBase; private set => SetProperty(ref _linesBase, value); }

    private IReadOnlyList<DiffLine> _linesB = Array.Empty<DiffLine>();
    public IReadOnlyList<DiffLine> LinesB { get => _linesB; private set => SetProperty(ref _linesB, value); }

    // ── Status ─────────────────────────────────────────────────────────────────

    private int _unresolvedCount;
    public int UnresolvedCount { get => _unresolvedCount; private set => SetProperty(ref _unresolvedCount, value); }

    private bool _isDirty;
    public bool IsDirty { get => _isDirty; private set => SetProperty(ref _isDirty, value); }

    /// <summary>True once the merged result has reached disk (Save or clean-close auto-write). Drives the CLI exit code.</summary>
    public bool WroteMergedOutput { get; private set; }

    private string _statusInfo = "";
    public string StatusInfo { get => _statusInfo; private set => SetProperty(ref _statusInfo, value); }

    private bool _showMergedOutput;
    /// <summary>When true, the base pane previews the merged output instead of the original base text.</summary>
    public bool ShowMergedOutput
    {
        get => _showMergedOutput;
        set
        {
            if (_showMergedOutput == value) return;
            _showMergedOutput = value;
            _state.ShowMergedOutput = value;

            // Persist immediately so the choice survives even if the app exits before MainWindow's
            // save-on-close runs (the merge window can be the only open window).
            var s = SettingsService.Load();
            s.MergeShowMergedOutput = value;
            SettingsService.Save(s);

            OnPropertyChanged(nameof(CanEditMerged));
            OnPropertyChanged(nameof(MetaBase));
            Refresh();
        }
    }

    // ── Merged-output editing (transient — never persisted) ────────────────────

    private bool _editMergedEnabled;
    /// <summary>When true, the base pane shows a plain editor bound to MergedEditorText instead of the merged preview.</summary>
    public bool EditMergedEnabled
    {
        get => _editMergedEnabled;
        set
        {
            // SetProperty raises EditMergedEnabled itself — the window swaps ViewerBase for the
            // editor in its PropertyChanged handler, which would never fire otherwise.
            if (!SetProperty(ref _editMergedEnabled, value)) return;

            if (value)
            {
                SetEditorTextFromState(MergeEngine.EffectiveContent(_state) ?? "");
                // Snapshot the computed merge as the edits' reference frame so a later rebase (after
                // resolutions change) knows what the typed text was based on. CanEditMerged already
                // guarantees every conflict is resolved, so BuildMergedContent is non-null here; it
                // also keeps the baseline in step when re-enabling over retained edits.
                _state.ManualEditBaseline = MergeEngine.BuildMergedContent(_state);
            }
            else
            {
                // Unchecking only stops editing — typed edits stay pending (IsDirty), exactly like
                // the main window's working copy. They reach disk via Save/Ctrl+S or the close
                // prompt, and are retained across conflict reopens (Local Edits / "File C"). The
                // full rebuild is what puts the edited text into the base pane immediately — without
                // it the viewer keeps showing the pre-edit computed merge until some other
                // operation happens to rebuild.
                Refresh();
            }

            OnPropertyChanged(nameof(CanEditMerged));
        }
    }

    /// <summary>
    /// Editing the base file (what Save writes) is offered once every conflict is resolved — the
    /// merged content doesn't exist before then. Preview mode is not required, matching the main
    /// window's right-file editing. Raised from Refresh() so resolving the last conflict enables it.
    /// </summary>
    public bool CanEditMerged => UnresolvedCount == 0;

    private string _mergedEditorText = "";
    public string MergedEditorText
    {
        get => _mergedEditorText;
        set
        {
            // SetProperty raises MergedEditorText so the TextBox picks up the filled-in merged
            // content when editing is enabled (the echo of per-keystroke writes back into the
            // same TextBox is a no-op via the equality guard).
            if (!SetProperty(ref _mergedEditorText, value)) return;

            // Per-keystroke override: no display rebuild needed, but dirty/status/save-gating must see it.
            // The TextBox reports CRLF; store LF-only so the override matches BuildMergedContent's
            // convention and Save writes LF like every other save path.
            var normalized = value.Replace("\r\n", "\n").Replace('\r', '\n');

            // User keystroke that actually changes the override → one undo entry per typing pause
            // (edit entries coalesce in MergeUndoStack). The pre-state snapshot carries whatever
            // RebaseManualEdits last settled, since this runs after any prior op's Refresh().
            if (!_editorSetTextIsProgrammatic && normalized != _state.ManualMergedOverride)
                _undo.Push(MergeEngine.CaptureSnapshot(_state), isEdit: true);

            _state.ManualMergedOverride = normalized;
            IsDirty = _state.IsDirty;
            StatusInfo = BuildStatusInfo();
            OnPropertyChanged(nameof(MetaBase));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    // ── Pane headers ───────────────────────────────────────────────────────────

    public string TitleA => $"File A — {_state.FileA.DisplayName}";
    public string MetaA => $"{_state.FileA.Lines.Count} lines · changes in File A";
    public string TitleBase => $"Base — {_state.BaseFile.DisplayName}";

    /// <summary>With a pending manual edit, the merged-output preview shows that edited text.</summary>
    public string MetaBase
    {
        get
        {
            if (ShowMergedOutput && _state.ManualMergedOverride != null && _state.UnresolvedCount == 0)
                return $"{_state.ManualMergedOverride.Split('\n').Length} lines · merged output (edited)";
            return $"{_state.BaseLines.Count} lines · {(ShowMergedOutput ? "merged output preview" : "read-only original")}";
        }
    }
    public string TitleB => $"File B — {_state.FileB.DisplayName}";
    public string MetaB => $"{_state.FileB.Lines.Count} lines · changes in File B";

    // Paths for syntax language detection (bound to FastDiffViewer.FilePath) — invariant
    // for the lifetime of the merge session, so no change notification is needed.
    public string? FilePathA => _state.FileA.FilePath;
    public string? FilePathBase => _state.BaseFile.FilePath;
    public string? FilePathB => _state.FileB.FilePath;

    // ── Commands ───────────────────────────────────────────────────────────────

    /// <summary>Save is gated: enabled only when every conflict is resolved and content differs from the last save.</summary>
    public ICommand SaveCommand { get; }
    public ICommand AcceptAllCommand { get; }
    public ICommand RejectAllCommand { get; }
    public ICommand NextConflictCommand { get; }
    public ICommand PrevConflictCommand { get; }
    public ICommand NextResolvedConflictCommand { get; }
    public ICommand PrevResolvedConflictCommand { get; }
    public ICommand NextChangeCommand { get; }
    public ICommand PrevChangeCommand { get; }

    /// <summary>Gated on undo/redo stack state (re-queried via CommandManager in Refresh and the editor setter).</summary>
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }

    /// <summary>Raised when the user navigates to a conflict (F3/Ctrl+F3); carries the triple-list row to scroll to.</summary>
    public event Action<int>? ConflictNavigationRequested;

    /// <summary>Raised when the user navigates to a resolved conflict (Shift+F3/Ctrl+Shift+F3); carries the triple-list row to scroll to.</summary>
    public event Action<int>? ResolvedConflictNavigationRequested;

    /// <summary>Raised when the user navigates to a non-conflicting change (Ctrl+Down/Ctrl+Up); carries the triple-list row to scroll to.</summary>
    public event Action<int>? ChangeNavigationRequested;

    // ── Construction ───────────────────────────────────────────────────────────

    public MergeWindowViewModel(FileModel baseFile, FileModel fileA, FileModel fileB, string? outputPath = null)
    {
        _outputPath = outputPath;

        var s = SettingsService.Load();
        bool ignoreWhitespace = s.IgnoreWhitespace;
        var algorithm = Enum.TryParse<DiffAlgorithm>(s.DiffAlgorithm, out var alg) ? alg : DiffAlgorithm.Patience;

        _state = MergeEngine.CreateMerge(baseFile, fileA, fileB, ignoreWhitespace, algorithm);

        // Restore the remembered base-pane mode (original base vs. merged output preview).
        _showMergedOutput = s.MergeShowMergedOutput;
        _state.ShowMergedOutput = _showMergedOutput;

        SaveCommand = new GatedRelayCommand(() => Save(), () => _state.IsSaveable && IsDirty);
        AcceptAllCommand = new RelayCommand(() => SetAllAccepted(true));
        RejectAllCommand = new RelayCommand(() => SetAllAccepted(false));
        NextConflictCommand = new RelayCommand(() => NavigateConflict(+1));
        PrevConflictCommand = new RelayCommand(() => NavigateConflict(-1));
        NextResolvedConflictCommand = new RelayCommand(() => NavigateResolvedConflict(+1));
        PrevResolvedConflictCommand = new RelayCommand(() => NavigateResolvedConflict(-1));
        NextChangeCommand = new RelayCommand(() => NavigateChange(+1));
        PrevChangeCommand = new RelayCommand(() => NavigateChange(-1));
        UndoCommand = new GatedRelayCommand(Undo, () => _undo.CanUndo);
        RedoCommand = new GatedRelayCommand(Redo, () => _undo.CanRedo);

        Refresh();
    }

    /// <summary>The underlying merge state (exposed for row→unit/block lookups in the window).</summary>
    public MergeState State => _state;

    // ── Mutations ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Stops base-pane editing (swapping the editor back to the merged viewer) WITHOUT touching
    /// the pending Local Edits. Used when a conflict reopens — an open conflict can't be shown in
    /// the editor, so we drop to the viewer; the edits stay pending and are retained. The backing
    /// field is set directly (not via the EditMergedEnabled setter) so this doesn't re-enter
    /// Refresh() from inside itself.
    /// </summary>
    private void StopMergedEditing()
    {
        if (!_editMergedEnabled) return;
        _editMergedEnabled = false;
        OnPropertyChanged(nameof(EditMergedEnabled));   // window swaps editor → viewer
    }

    /// <summary>Resolves a conflict block; the block is marked resolved and the display rebuilds.</summary>
    public void ResolveConflict(ConflictBlock block, MergeResolution resolution)
    {
        _undo.Push(MergeEngine.CaptureSnapshot(_state), isEdit: false);
        MergeEngine.ResolveConflict(_state, block, resolution);
        _conflictCursor = _state.Conflicts.IndexOf(block);
        Refresh();
    }

    /// <summary>Resolves specific conflict lines (selection-scoped); Unresolved resets them.</summary>
    public void ResolveConflictLines(IEnumerable<(ConflictBlock block, int slot)> targets, MergeResolution resolution)
    {
        _undo.Push(MergeEngine.CaptureSnapshot(_state), isEdit: false);
        MergeEngine.ResolveConflictLines(_state, targets, resolution);
        foreach (var (block, _) in targets)
        {
            _conflictCursor = _state.Conflicts.IndexOf(block);
            break;
        }
        Refresh();
    }

    /// <summary>Accepts or rejects one auto-mergeable change unit (all of its lines).</summary>
    public void SetChangeAccepted(MergeChange change, bool accepted)
    {
        _undo.Push(MergeEngine.CaptureSnapshot(_state), isEdit: false);
        MergeEngine.SetChangeAccepted(_state, change, accepted);
        Refresh();
    }

    /// <summary>Accepts or rejects specific lines of change units (selection-scoped).</summary>
    public void SetChangeLinesAccepted(IEnumerable<(MergeChange change, int line)> targets, bool accepted)
    {
        _undo.Push(MergeEngine.CaptureSnapshot(_state), isEdit: false);
        MergeEngine.SetChangeLinesAccepted(_state, targets, accepted);
        Refresh();
    }

    private void SetAllAccepted(bool accepted)
    {
        _undo.Push(MergeEngine.CaptureSnapshot(_state), isEdit: false);
        MergeEngine.AcceptAll(_state, accepted);
        Refresh();
    }

    // ── Undo / redo ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Restores the pre-state of the last operation. Refresh() re-derives everything else
    /// (triples, counts, IsDirty, status); if a restore reopens conflicts its StopMergedEditing
    /// closes the editor and the retained Local Edits keep their existing semantics. History is
    /// kept across Save — Save only touches SavedSnapshot, which snapshots don't carry.
    /// </summary>
    private void Undo() => RestoreTop(_undo.PopUndo());

    private void Redo() => RestoreTop(_undo.PopRedo());

    private void RestoreTop(MergeSnapshot? snap)
    {
        if (snap == null) return;
        MergeEngine.RestoreSnapshot(_state, snap);
        Refresh();
        // On restore the rebase inside Refresh is a no-op (override+baseline come back in
        // lockstep), so it won't push text back — sync the open editor manually.
        if (_editMergedEnabled)
            SetEditorTextFromState(_state.ManualMergedOverride ?? "");
    }

    // ── Conflict navigation (F3 / Ctrl+F3) ─────────────────────────────────────

    private void NavigateConflict(int direction)
    {
        var blocks = _state.Conflicts;
        int n = blocks.Count;
        if (n == 0) return;

        // Walk from just past the current cursor, wrapping, until an unresolved block is found.
        int i = _conflictCursor < 0 ? (direction > 0 ? -1 : 0) : _conflictCursor;
        for (int step = 0; step < n; step++)
        {
            i = (i + direction + n) % n;
            if (!blocks[i].IsFullyResolved)
            {
                _conflictCursor = i;
                ConflictNavigationRequested?.Invoke(blocks[i].StartRow);
                return;
            }
        }
        // No unresolved conflicts — nothing to jump to.
    }

    /// <summary>
    /// Shift+F3/Ctrl+Shift+F3: walk conflict blocks that contain a resolved slot, so the user
    /// can find and re-evaluate already-made choices. Blocks whose resolution lands no visible
    /// winner row (e.g. resolved to deletion) have FirstResolvedRow == −1 and are skipped.
    /// </summary>
    private void NavigateResolvedConflict(int direction)
    {
        var blocks = _state.Conflicts;
        int n = blocks.Count;
        if (n == 0) return;

        int i = _resolvedCursor < 0 ? (direction > 0 ? -1 : 0) : _resolvedCursor;
        for (int step = 0; step < n; step++)
        {
            i = (i + direction + n) % n;
            if (blocks[i].FirstResolvedRow >= 0)
            {
                _resolvedCursor = i;
                ResolvedConflictNavigationRequested?.Invoke(blocks[i].FirstResolvedRow);
                return;
            }
        }
    }

    /// <summary>
    /// Ctrl+Down/Ctrl+Up: walk the auto-merged change units in display order (wrapping), like
    /// the main window's diff-block navigation. Changes and conflicts are separate lists, so
    /// conflict blocks are never visited here — F3/Ctrl+F3 owns those.
    /// </summary>
    private void NavigateChange(int direction)
    {
        var changes = _state.Changes;
        int n = changes.Count;
        if (n == 0) return;

        // Changes is appended in item order, which is exactly the triple-list display order.
        int i = _changeCursor < 0 ? (direction > 0 ? -1 : 0) : _changeCursor;
        _changeCursor = (i + direction + n) % n;
        ChangeNavigationRequested?.Invoke(_state.ItemFirstRow[changes[_changeCursor].ItemIndex]);
    }

    // ── Save / close ───────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the merged content to the requested output file, or the base file when no output
    /// was requested (UTF-8, no BOM — same convention as the 2-way save). Returns false if the
    /// merge is not fully resolved or writing fails.
    /// </summary>
    public bool Save()
    {
        var content = MergeEngine.EffectiveContent(_state);
        string? path = _outputPath ?? _state.BaseFile.FilePath;
        if (content == null || path == null) return false;

        try
        {
            File.WriteAllText(path, content, Encoding.UTF8);
            _state.SavedSnapshot = content;   // clears IsDirty
            WroteMergedOutput = true;
            Refresh();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save {Path.GetFileName(path)}:\n{ex.Message}",
                            "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>
    /// Called by App when a CLI-launched merge window closes. With no output requested (or one
    /// already written) the current state is returned as-is. Otherwise: a clean merge (content
    /// equals the base, nothing dirty) is written automatically so the caller's exit-code
    /// contract holds even though SaveCommand was disabled; a dirty merge closed via "discard"
    /// or an unresolved merge is deliberately left unwritten — returning false yields exit code
    /// 1, which tells the VCS tool to fall back to its own merge.
    /// </summary>
    public bool EnsureMergedOutputWritten()
    {
        if (_outputPath == null || WroteMergedOutput) return WroteMergedOutput;
        if (_state.UnresolvedCount > 0 || _state.IsDirty) return false;   // never override an explicit "discard"

        var content = MergeEngine.EffectiveContent(_state);
        if (content == null) return false;
        try
        {
            File.WriteAllText(_outputPath, content, Encoding.UTF8);
            WroteMergedOutput = true;
            return true;
        }
        catch
        {
            return false;   // write failed — exit code 1 reports the failure to the caller
        }
    }

    /// <summary>
    /// Called by the window on close. Returns true if closing may proceed.
    /// Unresolved conflicts → "continue resolving / exit &amp; discard"; otherwise unsaved
    /// changes → Save / Don't Save / Cancel.
    /// </summary>
    public bool HandleClosing()
    {
        // With a CLI output path the base file is never touched and the prompts refer to the
        // output target instead.
        string untouchedNote = _outputPath != null ? "(no output file will be written)" : "(the base file is left untouched)";
        string dirtyFirstLine = _outputPath != null ? "The merged result has unsaved changes." : "The merged base file has unsaved changes.";
        string saveTarget = _outputPath != null ? Path.GetFileName(_outputPath) : "the base file";

        if (_state.UnresolvedCount > 0)
        {
            var r = MessageBox.Show(
                $"{_state.UnresolvedCount} conflict{(_state.UnresolvedCount == 1 ? "" : "s")} still unresolved.\n\n" +
                "Yes — keep this window open and continue resolving.\n" +
                $"No — exit now and discard all merge changes {untouchedNote}.",
                "Unresolved Conflicts", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            return r == MessageBoxResult.No;   // No → close & discard, Yes → stay
        }

        if (_state.IsDirty)
        {
            var r = MessageBox.Show(
                dirtyFirstLine + "\n\n" +
                $"Yes — save the merged result to {saveTarget}.\n" +
                "No — discard the changes.\n" +
                "Cancel — stay in the merge window.",
                "Unsaved Changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Yes) return Save();
            if (r == MessageBoxResult.No) return true;   // discard
            return false;                                // cancel close
        }

        return true;
    }

    // ── Refresh pipeline ───────────────────────────────────────────────────────

    /// <summary>
    /// Regenerates the triple display lists from the current resolutions/accept flags and
    /// pushes all observable state (lists, counts, status text, command requery).
    /// </summary>
    private void Refresh()
    {
        // Local Edits (ManualMergedOverride) are RETAINED across conflict reopens — they act as a
        // persistent third source ("File C") that survives resolution changes. But an open
        // conflict can't be shown in the base-pane editor, so if one is now unresolved we drop
        // back to the viewer; the edits stay pending and reappear once every conflict resolves.
        if (_state.UnresolvedCount > 0)
            StopMergedEditing();

        // Rebase pending Local Edits onto a recomputed merge after resolutions/accepts changed, so
        // re-resolving a conflict flows into the edited output instead of being masked by the stale
        // frozen snapshot (see MergeEngine.RebaseManualEdits). No-ops while unresolved or when there
        // are no edits to reconcile. Runs before RebuildDisplay so the display reflects the result.
        bool rebased = MergeEngine.RebaseManualEdits(_state);

        MergeEngine.RebuildDisplay(_state);

        LinesA = _state.TripleA;
        LinesBase = _state.TripleBase;
        LinesB = _state.TripleB;

        UnresolvedCount = _state.UnresolvedCount;
        IsDirty = _state.IsDirty;
        StatusInfo = BuildStatusInfo();

        // If a rebase rewrote the override while the base-pane editor is open (re-resolving a
        // conflict mid-edit), push the reconciled text back so the editor and state don't diverge.
        if (rebased && _editMergedEnabled)
            SetEditorTextFromState(_state.ManualMergedOverride ?? "");

        // CanEditMerged is computed from UnresolvedCount — it must be raised here, not just when
        // the properties that feed it change individually, or resolving the last conflict would
        // never re-enable the checkbox. MetaBase depends on the override too (discarded above).
        OnPropertyChanged(nameof(CanEditMerged));
        OnPropertyChanged(nameof(MetaBase));

        CommandManager.InvalidateRequerySuggested();
    }

    private string BuildStatusInfo()
    {
        int total = _state.Changes.Count;
        int rejected = _state.Changes.Count(c => !c.Accepted);

        string changesPart = total == 0
            ? "no changes"
            : $"{total} change{(total == 1 ? "" : "s")}{(rejected > 0 ? $", {rejected} rejected" : "")}";

        int resolved = _state.ResolvedCount;
        string conflictPart = UnresolvedCount > 0
            ? (resolved > 0
                ? $"{UnresolvedCount} unresolved · {resolved} resolved"
                : $"{UnresolvedCount} unresolved conflict{(UnresolvedCount == 1 ? "" : "s")}")
            : resolved > 0
                ? $"all conflicts resolved ({resolved})"
                : "no conflicts";

        string dirtyPart = IsDirty ? " · unsaved changes" : "";
        return $"{conflictPart} · {changesPart}{dirtyPart}";
    }

    // ── INotifyPropertyChanged plumbing (same pattern as MainWindowViewModel) ──

    private bool SetProperty<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// ICommand whose CanExecute is driven by a predicate and re-queried via
/// CommandManager.RequerySuggested (the shared RelayCommand always reports true).
/// </summary>
internal sealed class GatedRelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool> _canExecute;

    public GatedRelayCommand(Action execute, Func<bool> canExecute)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute();
    public void Execute(object? parameter) => _execute();

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
