using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AwfulDiff.Controls;
using AwfulDiff.Models;
using AwfulDiff.Services;
using AwfulDiff.ViewModels;

namespace AwfulDiff;

/// <summary>
/// The 3-way merge window: three row-aligned panes (File A | base | File B). Non-conflicting
/// changes auto-merge (cyan = A, green = B); conflicting regions render purple with a ⚠ gutter
/// mark and are resolved via the right-click menu. Saving writes the merged result to the base.
/// </summary>
public partial class ThreeWayMergeWindow : Window
{
    private readonly MergeWindowViewModel _vm;

    public ThreeWayMergeWindow(MergeWindowViewModel vm)
    {
        InitializeComponent();
        WorkAreaMaximize.Enforce(this); // maximize to the work area, not work area + frame border
        _vm = vm;
        DataContext = vm;

        // Viewers self-register with the default theme in their constructors; apply the saved
        // theme/font scale now so the panes match whatever the user has configured.
        ThemeManager.ApplyTheme(ThemeManager.CurrentThemeName, ThemeManager.FontScale);

        ApplyWindowSettings();
        WireViewers();

        // Swap ViewerBase for the plain editor when merged-output editing toggles. The window has
        // no BooleanToVisibilityConverter resource, so both sides are driven from here; with
        // editing off the editor is Collapsed (unfocusable) and no pane accepts typing.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MergeWindowViewModel.EditMergedEnabled))
                UpdateBaseEditorVisibility();
        };

        WindowStateChanged();
        StateChanged += (s, e) => WindowStateChanged();
    }

    private void UpdateBaseEditorVisibility()
    {
        if (_vm.EditMergedEnabled)
        {
            BaseEditor.Visibility = Visibility.Visible;
            ViewerBase.Visibility = Visibility.Collapsed;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                BaseEditor.Focus();
                // Open where the user was looking in the base pane. UpdateLayout first so the
                // measured text gives ScrollToLine an extent.
                int line = EditorLineForAnchor(ViewerBase);
                if (line > 0)
                {
                    BaseEditor.UpdateLayout();
                    BaseEditor.ScrollToLine(line);
                }
            }));
        }
        else
        {
            BaseEditor.Visibility = Visibility.Collapsed;
            ViewerBase.Visibility = Visibility.Visible;
        }
    }

    // The editor always holds the merged output, a different document from what the base pane
    // shows in original mode — so map the anchor row to its position in the merged output.
    private int EditorLineForAnchor(FastDiffViewer viewer)
    {
        int line = viewer.GetScrollAnchor().Line;
        var lines = viewer.Lines;
        if (line <= 0 || lines == null) return 0;

        if (_vm.ShowMergedOutput)
        {
            // Merged-output preview: the pane's rows ARE the editor's lines, except gap rows and
            // ghost rows (rejected deletions render dimmed but are absent from the output).
            int n = 0;
            for (int i = 0; i < Math.Min(line, lines.Count); i++)
                if (lines[i].Status != DiffStatus.Gap && !lines[i].Rejected) n++;
            return n;
        }

        // Original-base preview: map the base line at (or just below) the anchor to its index in
        // the merged output — insertions above it shift the position.
        for (int i = Math.Min(line, lines.Count - 1); i < lines.Count; i++)
            if (lines[i].Status != DiffStatus.Gap && lines[i].OriginalLineNumber > 0)
                return Math.Max(0, MergeEngine.MergedIndexOfBaseLine(_vm.State, lines[i].OriginalLineNumber));
        return 0;
    }

    private void WireViewers()
    {
        // The three triple lists are equal length, so scroll sync is a raw ratio pass-through.
        ViewerA.ScrollRatioChanged += OnScrollRatioChanged;
        ViewerBase.ScrollRatioChanged += OnScrollRatioChanged;
        ViewerB.ScrollRatioChanged += OnScrollRatioChanged;

        // RightClickAnyLine fires for every right-clicked row (incl. Unchanged) — the menu always
        // offers the merged-output toggle, plus resolution/accept items on actionable rows.
        ViewerA.RightClickAnyLine += OnRightClick;
        ViewerBase.RightClickAnyLine += OnRightClick;
        ViewerB.RightClickAnyLine += OnRightClick;

        // All navigation flavors (F3 conflicts, Shift+F3 resolved, Ctrl+arrow changes) scroll all
        // three panes to the same triple-list row — one handler serves them all.
        _vm.ConflictNavigationRequested += ScrollPanesToRow;
        _vm.ResolvedConflictNavigationRequested += ScrollPanesToRow;
        _vm.ChangeNavigationRequested += ScrollPanesToRow;
    }

    // ── Scroll sync ────────────────────────────────────────────────────────────

    private void OnScrollRatioChanged(object? sender, double ratio)
    {
        var source = sender as FastDiffViewer;
        if (source == null) return;

        if (source != ViewerA) ViewerA.SyncScroll(ratio);
        if (source != ViewerBase) ViewerBase.SyncScroll(ratio);
        if (source != ViewerB) ViewerB.SyncScroll(ratio);
    }

    // ── Navigation scrolling (F3 / Ctrl+F3 conflicts, Ctrl+Up/Down changes) ─────

    private void ScrollPanesToRow(int row)
    {
        foreach (var viewer in new[] { ViewerA, ViewerBase, ViewerB })
        {
            // Center the start of the block in each pane
            viewer.ScrollToLine(row, center: true);
            viewer.SelectedLineIndex = row;
        }
    }

    // ── Right-click menus ──────────────────────────────────────────────────────

    private void OnRightClick(object? sender, int idx)
    {
        var viewer = sender as FastDiffViewer;
        if (viewer == null) return;

        IReadOnlyList<DiffLine> lines =
            viewer == ViewerA ? _vm.LinesA :
            viewer == ViewerB ? _vm.LinesB : _vm.LinesBase;
        if (idx < 0 || idx >= lines.Count) return;
        var line = lines[idx];

        var menu = new ContextMenu();
        bool hasRowAction = false;

        // With pending Local Edits AND every conflict resolved, the base pane shows that free text
        // (merged-output mode), whose rows no longer correspond to conflict/change rows — suppress
        // row actions there so a click can't re-resolve a conflict it isn't pointing at. With an
        // open conflict the pane instead shows the computed triple (RebuildDisplay swap condition),
        // so key off UnresolvedCount too, not just override presence.
        bool baseIsFreeText = viewer == ViewerBase && _vm.ShowMergedOutput
            && _vm.State.ManualMergedOverride != null && _vm.State.UnresolvedCount == 0;

        // Conflict rows — unresolved (purple) or already resolved (re-rendered as the
        // winner's ChangeA/ChangeB, or neutral in merged-output view): always offer
        // (re-)resolution. With an explicit drag selection the chosen resolution applies to
        // just the selected conflict lines (possibly across blocks); without one it applies
        // to the whole block under the cursor.
        var clicked = baseIsFreeText ? null : _vm.State.ConflictLineAtRow(idx);
        if (clicked != null)
        {
            var targets = new List<(ConflictBlock block, int slot)>();
            if (viewer.HasSelection)
            {
                var seen = new HashSet<(ConflictBlock, int)>();
                foreach (int r in viewer.GetSelectedLineIndices())
                    if (_vm.State.ConflictLineAtRow(r) is { } t && seen.Add((t.block, t.slot)))
                        targets.Add(t);
            }

            if (targets.Count > 0)
            {
                string n = $"{targets.Count} line{(targets.Count == 1 ? "" : "s")}";
                bool asymmetric = false;
                foreach (var t in targets) if (t.block.IsSlotAsymmetric(t.slot)) { asymmetric = true; break; }

                AddMenuItem(menu, $"⚠ Take A's change ({n})",
                            () => _vm.ResolveConflictLines(targets, MergeResolution.TakeA),
                            AllTargetsResolvedAs(targets, MergeResolution.TakeA));
                AddMenuItem(menu, $"⚠ Take B's change ({n})",
                            () => _vm.ResolveConflictLines(targets, MergeResolution.TakeB),
                            AllTargetsResolvedAs(targets, MergeResolution.TakeB));
                AddMenuItem(menu, $"⊕ Keep merged base ({n})",
                            () => _vm.ResolveConflictLines(targets, MergeResolution.KeepBase),
                            AllTargetsResolvedAs(targets, MergeResolution.KeepBase));
                if (!asymmetric)
                {
                    // Ordering a deletion against a line has no sensible semantics — hide it.
                    AddMenuItem(menu, $"Take A before B ({n})",
                                () => _vm.ResolveConflictLines(targets, MergeResolution.OrderAB),
                                AllTargetsResolvedAs(targets, MergeResolution.OrderAB));
                    AddMenuItem(menu, $"Take B before A ({n})",
                                () => _vm.ResolveConflictLines(targets, MergeResolution.OrderBA),
                                AllTargetsResolvedAs(targets, MergeResolution.OrderBA));
                }
                AddMenuItem(menu, "↺ Reset selected to unresolved",
                            () => _vm.ResolveConflictLines(targets, MergeResolution.Unresolved));
                hasRowAction = true;
            }
            else
            {
                var block = clicked.Value.block;
                AddMenuItem(menu, "⚠ Take A's change",
                            () => _vm.ResolveConflict(block, MergeResolution.TakeA),
                            block.AllResolvedAs(MergeResolution.TakeA));
                AddMenuItem(menu, "⚠ Take B's change",
                            () => _vm.ResolveConflict(block, MergeResolution.TakeB),
                            block.AllResolvedAs(MergeResolution.TakeB));
                AddMenuItem(menu, "⊕ Keep merged base",
                            () => _vm.ResolveConflict(block, MergeResolution.KeepBase),
                            block.AllResolvedAs(MergeResolution.KeepBase));
                if (!block.IsAsymmetric)
                {
                    // Ordering a deletion against a line has no sensible semantics — hide it.
                    AddMenuItem(menu, "Take A before B",
                                () => _vm.ResolveConflict(block, MergeResolution.OrderAB),
                                block.AllResolvedAs(MergeResolution.OrderAB));
                    AddMenuItem(menu, "Take B before A",
                                () => _vm.ResolveConflict(block, MergeResolution.OrderBA),
                                block.AllResolvedAs(MergeResolution.OrderBA));
                }
                if (!block.IsFullyUnresolved)
                    AddMenuItem(menu, "↺ Reset to unresolved",
                                () => _vm.ResolveConflict(block, MergeResolution.Unresolved));
                hasRowAction = true;
            }
        }
        else if (!baseIsFreeText && line.Status is DiffStatus.ChangeA or DiffStatus.ChangeB)
        {
            // With an explicit drag selection the action is scoped to the selected lines only
            // (possibly a subset of one unit, or several units at once); without one it applies
            // to the whole change unit under the cursor. Resolved-conflict rows carry no unit →
            // nothing offered there.
            bool hasSelection = viewer.HasSelection;
            var rows = hasSelection ? viewer.GetSelectedLineIndices() : new List<int> { idx };

            var targets = new List<(MergeChange change, int line)>();
            foreach (int r in rows)
                if (_vm.State.ChangeLineAtRow(r) is { } t) targets.Add(t);

            if (targets.Count > 0)
            {
                if (hasSelection)
                {
                    string n = $"{targets.Count} line{(targets.Count == 1 ? "" : "s")}";
                    AddMenuItem(menu, $"✓ Accept selected ({n})",
                                () => _vm.SetChangeLinesAccepted(targets, true));
                    AddMenuItem(menu, $"✗ Reject selected ({n})",
                                () => _vm.SetChangeLinesAccepted(targets, false));
                }
                else
                {
                    var change = targets[0].change;
                    AddMenuItem(menu, change.Accepted ? "✗ Reject this change" : "✓ Accept this change",
                                () => _vm.SetChangeAccepted(change, !change.Accepted));
                }
                hasRowAction = true;
            }
        }

        // Merge-only option: preview the merged output in the base pane. Two explicit items so
        // the check mark always marks the current mode, not the one you'd switch to.
        if (hasRowAction) menu.Items.Add(new Separator());
        AddMenuItem(menu, "⇄ Base pane: original base", () => _vm.ShowMergedOutput = false, !_vm.ShowMergedOutput);
        AddMenuItem(menu, "⇄ Base pane: merged output", () => _vm.ShowMergedOutput = true, _vm.ShowMergedOutput);

        menu.IsOpen = true;
    }

    private static void AddMenuItem(ContextMenu menu, string header, Action action, bool isChecked = false)
    {
        var item = new MenuItem { Header = header, IsChecked = isChecked };
        item.Click += (s, e) =>
        {
            action();
            e.Handled = true;
        };
        menu.Items.Add(item);
    }

    private static bool AllTargetsResolvedAs(List<(ConflictBlock block, int slot)> targets, MergeResolution resolution)
    {
        foreach (var (block, slot) in targets)
            if (block.ResolutionAt(slot) != resolution) return false;
        return true;
    }

    // ── Keyboard ───────────────────────────────────────────────────────────────

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        if (ctrl && e.Key == Key.S)
        {
            if (_vm.SaveCommand.CanExecute(null)) _vm.Save();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Z && !e.Handled)
        {
            // Inside BaseEditor the TextBox's native text undo consumes Ctrl+Z first (marks it
            // handled), so character-level undo wins there; this merge-op undo applies wherever
            // focus is elsewhere. While editing, the toolbar buttons stay the escape hatch.
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            var cmd = shift ? _vm.RedoCommand : _vm.UndoCommand;
            if (cmd.CanExecute(null)) { cmd.Execute(null); e.Handled = true; }
        }
        else if (ctrl && e.Key == Key.Y && !e.Handled)
        {
            if (_vm.RedoCommand.CanExecute(null)) { _vm.RedoCommand.Execute(null); e.Handled = true; }
        }
        else if (e.Key == Key.F3)
        {
            // Shift selects resolved-conflict navigation (finding already-made choices to
            // re-evaluate); plain F3 walks unresolved conflicts as before.
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            if (shift)
                (ctrl ? _vm.PrevResolvedConflictCommand : _vm.NextResolvedConflictCommand).Execute(null);
            else
                (ctrl ? _vm.PrevConflictCommand : _vm.NextConflictCommand).Execute(null);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Down)
        {
            _vm.NextChangeCommand.Execute(null);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Up)
        {
            _vm.PrevChangeCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ── Window geometry persistence (same pattern as DirectoryDiffsWindow) ─────

    private void ApplyWindowSettings()
    {
        var s = SettingsService.Load();

        // Restore position and size (skip if NaN — first run lets the XAML CenterScreen default apply)
        if (!double.IsNaN(s.MergeWindowLeft) && !double.IsNaN(s.MergeWindowTop))
        {
            // Only restore if the window would be at least partially visible on a monitor
            var workArea = SystemParameters.WorkArea;
            bool visible =
                s.MergeWindowLeft + s.MergeWindowWidth > workArea.Left &&
                s.MergeWindowLeft < workArea.Right &&
                s.MergeWindowTop + s.MergeWindowHeight > workArea.Top &&
                s.MergeWindowTop < workArea.Bottom;

            if (visible)
            {
                // The XAML declares CenterScreen, which WPF applies at show time and would clobber
                // the restored position — switch to Manual so the saved Left/Top actually stick.
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = s.MergeWindowLeft;
                Top = s.MergeWindowTop;
                Width = Math.Max(900, s.MergeWindowWidth);   // XAML MinWidth
                Height = Math.Min(9000, s.MergeWindowHeight);
            }
        }

        // Restore window state LAST so the bounds above become the maximized window's restore
        // bounds — un-maximizing later lands on the previous normal size/position.
        if (s.MergeWindowState == "Maximized")
            WindowState = WindowState.Maximized;
    }

    private void SaveWindowSettings()
    {
        var s = SettingsService.Load();

        // When maximized, save the restore bounds so reopening restores the previous normal size
        if (WindowState == WindowState.Maximized)
        {
            s.MergeWindowWidth = RestoreBounds.Width;
            s.MergeWindowHeight = RestoreBounds.Height;
            s.MergeWindowLeft = RestoreBounds.Left;
            s.MergeWindowTop = RestoreBounds.Top;
        }
        else
        {
            s.MergeWindowWidth = Width;
            s.MergeWindowHeight = Height;
            s.MergeWindowLeft = Left;
            s.MergeWindowTop = Top;
        }

        s.MergeWindowState = WindowState == WindowState.Maximized ? "Maximized" : "Normal";

        SettingsService.Save(s);
    }

    // ── Closing ────────────────────────────────────────────────────────────────

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_vm.HandleClosing())
        {
            e.Cancel = true;
            return;
        }
        SaveWindowSettings();
        base.OnClosing(e);
    }

    // ── Custom chrome (same pattern as MainWindow) ─────────────────────────────

    private void WindowStateChanged()
    {
        bool maximized = WindowState == WindowState.Maximized;
        BtnMaximize.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        BtnRestore.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
    }

    // Dragging down from the top edge of a maximized window un-maximizes it and lets the window
    // follow the cursor — standard Windows behaviour that custom chrome (WindowStyle=None) does
    // not get for free, so the maximized case is tracked manually instead of via DragMove.
    private bool _titleBarDragging;
    private double _titleBarGrabX, _titleBarGrabY;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Don't start drag if the click originated on a window control button
        if (e.Source is Button) return;

        if (e.ClickCount >= 2)
        {
            // Double-click toggles maximize/restore
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            EndTitleBarDrag();
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            var p = e.GetPosition(this);
            _titleBarGrabX = p.X;
            _titleBarGrabY = p.Y;
            _titleBarDragging = true;
            TitleBar.CaptureMouse();           // keep receiving moves even outside the title bar
        }
        else
        {
            DragMove();
        }
    }

    private void TitleBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_titleBarDragging || e.LeftButton != MouseButtonState.Pressed) return;

        var p = e.GetPosition(this);
        if (WindowState == WindowState.Maximized && p.Y - _titleBarGrabY > 5)
        {
            // Dragged down from the top edge — un-maximize. Capture the cursor in screen DIPs
            // BEFORE restoring (WindowState=Normal first jumps to the saved restore bounds), then
            // reposition so the press point stays under the cursor.
            double cx = Left + p.X;
            double cy = Top + p.Y;
            WindowState = WindowState.Normal;
            Left = cx - _titleBarGrabX;
            Top = cy - _titleBarGrabY;
        }

        if (WindowState == WindowState.Normal)
        {
            p = e.GetPosition(this);   // recompute after the possible reposition above
            Left += p.X - _titleBarGrabX;
            Top += p.Y - _titleBarGrabY;
        }
    }

    private void TitleBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndTitleBarDrag();

    private void EndTitleBarDrag()
    {
        if (!_titleBarDragging) return;
        _titleBarDragging = false;
        if (TitleBar.IsMouseCaptured) TitleBar.ReleaseMouseCapture();
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void BtnMaximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Maximized;
    private void BtnRestore_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Normal;
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
