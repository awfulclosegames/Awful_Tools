using AwfulDiff.Controls;
using AwfulDiff.Models;
using AwfulDiff.Services;
using AwfulDiff.ViewModels;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AwfulDiff;

/// <summary>
/// Main window — slim code-behind. All logic lives in MainWindowViewModel.
/// Code-behind only handles: view model wiring, layout toggle visibility,
/// sync scroll routing, keyboard shortcuts, drag-and-drop, and window close.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _vm;

    // ── Search State ──────────────────────────────────────────────────────────

    private bool _searchActive;
    private string? _searchQuery;
    private FastDiffViewer? _activeSearchViewer;  // Last-clicked viewer — search target
    private FastDiffViewer? _dragOverViewer;     // Viewer currently being hovered during drag-and-drop
    private List<int> _searchMatches = new();
    private int _matchIndex = -1;

    // ── Sync Point State ───────────────────────────────────────────────────────

    /// <summary>
    /// When the user initiates "Set Sync Point" from a context menu, this tracks
    /// which panel initiated it ('A' = left/top, 'B' = right/bottom) and at which
    /// display-line index. The opposite viewer's single-click confirms the pairing.
    /// </summary>
    private (char panel, int line)? _pendingSync;

    /// <summary>
    /// Hex view counterpart of _pendingSync: the panel and byte position (display row index +
    /// hex slot) where "Set Sync Point" was initiated. A single click on an occupied slot in
    /// the opposite viewer confirms the pairing.
    /// </summary>
    private (char panel, int line, int slot)? _pendingHexSync;

    /// <summary>
    /// Creates the window. Optional file paths (from the CLI) load into the left/right panels.
    /// </summary>
    public MainWindow(string? initialLeftFile = null, string? initialRightFile = null)
        : this(new MainWindowViewModel(initialLeftFile, initialRightFile))
    {
    }

    /// <summary>
    /// Creates the window around an existing view model — used to reopen the main window after
    /// it was closed. A closed WPF Window cannot be shown again (Show throws InvalidOperationException),
    /// but the VM outlives its windows, so a fresh window reattaches to all open-file/diff state.
    /// </summary>
    public MainWindow(MainWindowViewModel vm)
    {
        _vm = vm;
        DataContext = _vm;

        InitializeComponent();
        WorkAreaMaximize.Enforce(this); // maximize to the work area, not work area + frame border

        // Version shown next to the branding — read from the assembly so the csproj <Version>
        // is the single source of truth (major.minor only, e.g. "v1.0").
        TxtVersion.Text = "v" + Assembly.GetExecutingAssembly().GetName().Version!.ToString(2);

        // Restore saved window position/size from previous session
        ApplyWindowSettings();

        Debug.WriteLine($"[MainWindow] DataContext set to {_vm.GetHashCode():X}");
        Debug.WriteLine($"[MainWindow] LayoutSideBySide.InitialVisibility = {LayoutSideBySide.Visibility}");
        Debug.WriteLine($"[MainWindow] LayoutTopBottom.InitialVisibility = {LayoutTopBottom.Visibility}");

        // Subscribe to layout changes so we can update visibility reliably
        _vm.PropertyChanged += (s, e) =>
        {
            Debug.WriteLine($"[MainWindow] PropertyChanged received: {e.PropertyName}");
            if (e.PropertyName == nameof(_vm.LayoutMode))
            {
                UpdateLayoutVisibility();
            }
            else if (e.PropertyName == nameof(_vm.EditRightEnabled))
            {
                UpdateEditorVisibility();
            }
            else if (e.PropertyName == nameof(_vm.IsHexView))
            {
                // Propagate hex view mode to all viewers so they adjust gutter width
                bool hexMode = _vm.IsHexView;
                LeftViewer.IsHexView = hexMode;
                RightViewer.IsHexView = hexMode;
                TopViewer.IsHexView = hexMode;
                BottomViewer.IsHexView = hexMode;
                UnifiedViewer.IsHexView = hexMode;

                // A mode switch invalidates any in-flight sync-point prompt.
                if (_pendingSync.HasValue) CancelPendingSync();
                if (_pendingHexSync.HasValue) CancelPendingHexSync();

                // Show/hide inspector panel — collapse to zero height in text mode
                if (hexMode)
                {
                    InspectorRow.Height = new GridLength(_vm.HexInspectorHeight, GridUnitType.Pixel);
                    InspectorPanel.Visibility = Visibility.Visible;
                    InspectorSplitter.Visibility = Visibility.Visible;
                    InspectorGrabZone.Visibility = Visibility.Visible;
                    // Clear both panels when hex mode is activated
                    HexInspector.Clear();
                }
                else
                {
                    InspectorRow.Height = new GridLength(0, GridUnitType.Pixel);
                    InspectorPanel.Visibility = Visibility.Collapsed;
                    InspectorSplitter.Visibility = Visibility.Collapsed;
                    InspectorGrabZone.Visibility = Visibility.Collapsed;
                }

                // A mode switch changes which edit surface is live (text editor vs. hex field +
                // inspector), so re-run the visibility pass even though EditRightEnabled didn't change.
                UpdateEditorVisibility();
            }
            else if (e.PropertyName == nameof(_vm.HexPerByte))
            {
                // Hex per-byte display flag — re-render each viewer with the new slot-shading mode.
                bool perByte = _vm.HexPerByte;
                LeftViewer.HexPerByte = perByte;
                RightViewer.HexPerByte = perByte;
                TopViewer.HexPerByte = perByte;
                BottomViewer.HexPerByte = perByte;
                UnifiedViewer.HexPerByte = perByte;
            }
        };

        // Apply initial visibility (the VM may have loaded a different value from settings
        // before we subscribed above, so set it now). Seed _lastLayout first so the
        // initial pass is treated as "no switch" rather than a transition.
        _lastLayout = _vm.LayoutMode;
        UpdateLayoutVisibility();

        // Explicitly set toolbar DataContext — UserControl internal bindings don't inherit
        // the Window's DataContext through the visual tree automatically.
        DiffToolbar.DataContext = _vm;
        Debug.WriteLine($"[MainWindow] DiffToolbar.DataContext set to {_vm.GetHashCode():X}");

        // Verify bindings after load
        Loaded += OnMainWindowLoaded;

        SetupDragDrop();
        SetupSearchBar();
        TrackActiveViewer();
        SetupApplyContextMenu();
        SetupSyncPoints();
        SetupHexInspector();
        SetupTitleBar();
        SetupThemeSubscription();
    }

    // ── Theme Subscription ─────────────────────────────────────────────────────

    private void SetupThemeSubscription()
    {
        ThemeManager.ThemeChanged += OnThemeChanged;
        UpdateWindowChromeForTheme();
    }

    private void OnThemeChanged(string themeName)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            UpdateWindowChromeForTheme();
        });
    }

    private void UpdateWindowChromeForTheme()
    {
        // WPF does not allow changing WindowStyle or AllowsTransparency at runtime.
        // We always use WindowStyle=None with a custom title bar, but the title bar
        // colors adapt via DynamicResource when the theme switches.
        // No action needed here — just keep the title bar visible for all themes.
    }

    // ── Title Bar / Window Chrome ──────────────────────────────────────────────

    private void SetupTitleBar()
    {
        // Wire up maximize/restore button visibility to window state changes
        WindowStateChanged();
        StateChanged += (s, e) => WindowStateChanged();
    }

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
        if (e.Source is System.Windows.Controls.Button) return;

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

    // ── Hex inspector resize ───────────────────────────────────────────────────
    // The built-in GridSplitter doesn't respond to drags in this row layout, so the separator
    // Border and the wide grab zone at the panes' bottom edge drive the resize manually (same
    // capture pattern as the title bar above). Drag up grows the inspector, down shrinks it.
    private bool _inspectorResizing;
    private double _inspectorGrabY, _inspectorStartHeight;

    private void InspectorGrab_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (InspectorRow.Height.Value < 1) return;   // inspector hidden (text mode)

        _inspectorResizing = true;
        _inspectorGrabY = e.GetPosition(this).Y;
        _inspectorStartHeight = InspectorRow.Height.Value;
        ((UIElement)sender).CaptureMouse();          // keep receiving moves even off the grab zone
        e.Handled = true;
    }

    private void InspectorGrab_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_inspectorResizing || e.LeftButton != MouseButtonState.Pressed) return;

        double delta = _inspectorGrabY - e.GetPosition(this).Y;   // up = grow
        InspectorRow.Height = new GridLength(ClampInspectorHeight(_inspectorStartHeight + delta), GridUnitType.Pixel);
    }

    private void InspectorGrab_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_inspectorResizing) return;
        _inspectorResizing = false;
        ((UIElement)sender).ReleaseMouseCapture();
        _vm.HexInspectorHeight = InspectorRow.Height.Value;   // persisted on close via SaveAndCleanup
    }

    private static double ClampInspectorHeight(double h)
    {
        const double min = 60;                              // type selector row + a few byte rows
        return Math.Max(min, Math.Min(h, 1200));
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnMaximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Maximized;

    private void BtnRestore_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Normal;

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Sets up the right-click context menu for "Apply to Right" on the right-side viewers.
    /// </summary>
    private void SetupApplyContextMenu()
    {
        // Only wire up right-side viewers (RightViewer, BottomViewer)
        foreach (var viewer in new[] { RightViewer, BottomViewer })
        {
            viewer.RightClickLineIndex += (s, selectedIndices) =>
            {
                ShowApplyContextMenu(viewer, selectedIndices);
            };
        }
    }

    private void ShowApplyContextMenu(FastDiffViewer viewer, List<int> selectedIndices)
    {
        // Apply-to-right mutates the text working copy — meaningless for hex rows.
        if (_vm.IsHexView) return;
        if (_vm.FileBLines == null || selectedIndices.Count == 0) return;

        // Filter to only actionable indices (Modified, Added, or Gap lines)
        var actionableIndices = selectedIndices
            .Where(i => i >= 0 && i < _vm.FileBLines.Count)
            .Where(i => _vm.FileBLines[i].Status is AwfulDiff.Models.DiffStatus.Modified or AwfulDiff.Models.DiffStatus.Added or AwfulDiff.Models.DiffStatus.Gap)
            .ToList();

        if (actionableIndices.Count == 0) return;

        // Determine the most common status among selected lines for the menu label
        var dominantStatus = actionableIndices
            .Select(i => _vm.FileBLines[i].Status)
            .GroupBy(s => s)
            .OrderByDescending(g => g.Count())
            .First().Key;

        int count = actionableIndices.Count;

        // Build context menu
        var menu = new System.Windows.Controls.ContextMenu();

        string actionText = dominantStatus switch
        {
            AwfulDiff.Models.DiffStatus.Modified => count == 1 ? "Replace with left version" : $"Replace {count} lines with left version",
            AwfulDiff.Models.DiffStatus.Added => count == 1 ? "Remove this line" : $"Remove {count} selected lines",
            AwfulDiff.Models.DiffStatus.Gap => count == 1 ? "Insert missing line from left" : $"Insert {count} missing lines from left",
            _ => $"Apply to {count} selected lines"
        };

        var applyItem = new System.Windows.Controls.MenuItem
        {
            Header = $"▸ {actionText}",
            Icon = CreateStatusIcon(dominantStatus),
            IsEnabled = true
        };
        // Capture indices at menu-creation time. Convert display indices to canonical
        // (interleaved) space: ApplyChange(s)ToRight indexes the interleaved list, and with
        // ShowGaps off the displayed list is compacted (gap rows removed), so raw display
        // indices would point at the wrong rows below any left-only line. Identity when gaps
        // are shown (the displayed list IS the interleaved list). Sort descending for
        // bottom-to-top processing.
        var canonicalDescending = actionableIndices
            .Select(i => _vm.PaneRightIndexToCanonical(i))
            .OrderByDescending(i => i)
            .ToList();
        applyItem.Click += (s, e) =>
        {
            if (canonicalDescending.Count > 1)
                _vm.ApplyChangesToRight(canonicalDescending);
            else if (canonicalDescending.Count == 1)
                _vm.ApplyChangeToRight(canonicalDescending[0]);
            e.Handled = true;
        };
        menu.Items.Add(applyItem);

        // Whole-change-block option: right-clicking a line inside a contiguous change block
        // (no larger selection active) offers reverting the ENTIRE block, not just that line.
        // The run is found in display space so it matches the coloured block the user sees;
        // only shown when the block has more than one row (single-row == the item above).
        if (selectedIndices.Count == 1)
        {
            var blockRun = GetChangeBlockRun(selectedIndices[0]);
            if (blockRun.Count > 1)
            {
                bool allAdded = blockRun.All(i => _vm.FileBLines[i].Status == AwfulDiff.Models.DiffStatus.Added);
                string blockText = allAdded
                    ? $"✕ Remove this change ({blockRun.Count} lines)"
                    : $"↩ Revert this change ({blockRun.Count} lines)";

                var blockItem = new System.Windows.Controls.MenuItem { Header = blockText };
                var blockCanonical = blockRun
                    .Select(i => _vm.PaneRightIndexToCanonical(i))
                    .OrderByDescending(i => i)
                    .ToList();
                blockItem.Click += (s, e) =>
                {
                    _vm.ApplyChangesToRight(blockCanonical);
                    e.Handled = true;
                };
                menu.Items.Add(blockItem);
            }
        }

        // Also add "Apply all" option
        if (actionableIndices.Any())
        {
            menu.Items.Add(new System.Windows.Controls.Separator());

            var applyAllItem = new System.Windows.Controls.MenuItem
            {
                Header = "▸ Apply all changes",
                IsEnabled = true
            };
            applyAllItem.Click += (s, e) =>
            {
                ApplyAllChanges();
                e.Handled = true;
            };
            menu.Items.Add(applyAllItem);
        }

        // Add sync point options to right-viewer context menu
        menu.Items.Add(new System.Windows.Controls.Separator());
        int firstLine = selectedIndices[0];

        // Check if this line is an active sync point on panel B
        var spDisplayB = _vm.SyncPointLinesB;
        bool isActiveSpB = spDisplayB != null && BinarySearch(spDisplayB, firstLine);
        if (isActiveSpB && _vm.FileBLines != null && firstLine < _vm.FileBLines.Count)
        {
            int origLineB = _vm.FileBLines[firstLine].OriginalLineNumber;
            int? removeLineA = FindSyncPointPartner(origLineB, 'B');
            var removeItem = new System.Windows.Controls.MenuItem
            {
                Header = "✕ Remove Sync Point Here",
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.IndianRed),
                ToolTip = $"Remove sync point at line {origLineB}"
            };
            removeItem.Click += (s, e) =>
            {
                if (removeLineA.HasValue)
                    _vm.RemoveSyncPoint(removeLineA.Value, origLineB);
                e.Handled = true;
            };
            menu.Items.Add(removeItem);
        }

        var syncItem = new System.Windows.Controls.MenuItem
        {
            Header = "🔗 Set Sync Point",
            ToolTip = "Click the corresponding line in the left file to confirm"
        };
        syncItem.Click += (s, e) =>
        {
            BeginPendingSync('B', firstLine);
            e.Handled = true;
        };
        menu.Items.Add(syncItem);

        // Diff engine selector
        AddDiffEngineItems(menu);

        // Show context menu at mouse position
        menu.IsOpen = true;
    }

    /// <summary>
    /// Maximal contiguous run of change rows (Modified/Added/Gap) in the displayed right pane
    /// containing the given index — i.e., the coloured change block under the cursor. Display
    /// space: with ShowGaps off there are no Gap rows, so a left-only line between two changes
    /// does not split the run and the whole visible block is covered.
    /// </summary>
    private List<int> GetChangeBlockRun(int anchor)
    {
        var lines = _vm.FileBLines!;
        bool IsChangeRow(int i) => lines[i].Status is AwfulDiff.Models.DiffStatus.Modified
            or AwfulDiff.Models.DiffStatus.Added
            or AwfulDiff.Models.DiffStatus.Gap;

        int lo = anchor, hi = anchor;
        while (lo > 0 && IsChangeRow(lo - 1)) lo--;
        while (hi < lines.Count - 1 && IsChangeRow(hi + 1)) hi++;

        var run = new List<int>();
        for (int i = lo; i <= hi; i++) run.Add(i);
        return run;
    }

    /// <summary>
    /// Sets up the sync point interaction: right-click any line to propose a sync point,
    /// then left-click the corresponding line in the opposite viewer to confirm.
    /// </summary>
    private void SetupSyncPoints()
    {
        // Map viewers to their panel identifier
        var panelAViewers = new[] { LeftViewer, TopViewer };
        var panelBViewers = new[] { RightViewer, BottomViewer };

        foreach (var viewer in panelAViewers.Concat(panelBViewers))
        {
            char panel = panelAViewers.Contains(viewer) ? 'A' : 'B';

            // Right-click: show sync point option in context menu
            viewer.RightClickAnyLine += (s, lineIdx) =>
            {
                ShowSyncPointMenu(viewer, panel, lineIdx);
            };

            // Left-click: confirm pending sync point if in that mode
            viewer.SingleClickedLine += (s, lineIdx) =>
            {
                if (_pendingSync.HasValue)
                {
                    var (sourcePanel, sourceLine) = _pendingSync.Value;
                    // Only confirm if clicked on the opposite panel
                    if (panel != sourcePanel)
                    {
                        ConfirmSyncPoint(sourcePanel, sourceLine, panel, lineIdx);
                    }
                }
            };

            // Hex view: right-click a row → byte-granularity menu (sync point on the clicked byte,
            // CRC-32 over the selected bytes whenever a range is available)
            viewer.HexRightClicked += (s, e) =>
            {
                ShowHexContextMenu(viewer, panel, e.LineIndex, e.Slot);
            };

            // Hex view: single-click a byte slot to confirm the pending hex sync point
            viewer.HexSlotSingleClicked += (s, e) =>
            {
                if (_pendingHexSync.HasValue)
                {
                    var (sourcePanel, sourceLine, sourceSlot) = _pendingHexSync.Value;
                    // Only confirm if clicked on the opposite panel
                    if (panel != sourcePanel)
                    {
                        ConfirmHexSyncPoint(sourcePanel, sourceLine, sourceSlot, panel, e.LineIndex, e.Slot);
                    }
                }
            };
        }

        // Handle Escape to cancel pending sync
        this.PreviewKeyDown += (s, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            if (_pendingSync.HasValue) CancelPendingSync();
            if (_pendingHexSync.HasValue) CancelPendingHexSync();
        };
    }

    /// <summary>
    /// Adds "Set Sync Point" and per-point removal to the context menu for any right-clicked line.
    /// </summary>
    private void ShowSyncPointMenu(FastDiffViewer viewer, char panel, int lineIdx)
    {
        // Hex view uses the byte-granularity menu instead (HexRightClicked → ShowHexContextMenu).
        if (_vm.IsHexView) return;

        // Determine which file's lines and sync-point display indices to check
        var fileLines = panel == 'A' ? _vm.FileALines : _vm.FileBLines;
        var spDisplayLines = panel == 'A' ? _vm.SyncPointLinesA : _vm.SyncPointLinesB;

        // Check if this line is an active sync point
        bool isActiveSp = false;
        int origLineOnThisPanel = -1;
        if (fileLines != null && lineIdx >= 0 && lineIdx < fileLines.Count)
        {
            origLineOnThisPanel = fileLines[lineIdx].OriginalLineNumber;
            isActiveSp = spDisplayLines != null && BinarySearch(spDisplayLines, lineIdx);
        }

        // For panel A (left/top): show standalone sync point menu
        if (panel == 'A')
        {
            var menu = new System.Windows.Controls.ContextMenu();

            if (isActiveSp)
            {
                // Find the paired original line number from the other side
                int? removeLineB = FindSyncPointPartner(origLineOnThisPanel, 'A');
                var removeItem = new System.Windows.Controls.MenuItem
                {
                    Header = "✕ Remove Sync Point Here",
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.IndianRed),
                    ToolTip = $"Remove sync point at line {origLineOnThisPanel}"
                };
                removeItem.Click += (s, e) =>
                {
                    if (removeLineB.HasValue)
                        _vm.RemoveSyncPoint(origLineOnThisPanel, removeLineB.Value);
                    e.Handled = true;
                };
                menu.Items.Add(removeItem);
            }

            var syncItem = new System.Windows.Controls.MenuItem
            {
                Header = "🔗 Set Sync Point",
                ToolTip = "Click the corresponding line in the other file to confirm"
            };
            syncItem.Click += (s, e) =>
            {
                BeginPendingSync(panel, lineIdx);
                e.Handled = true;
            };
            menu.Items.Add(syncItem);

            // Diff engine selector
            AddDiffEngineItems(menu);

            menu.IsOpen = true;
        }
        else
        {
            // For panel B (right/bottom): only show standalone menu for non-actionable lines.
            // For actionable lines, the apply context menu (ShowApplyContextMenu) already
            // includes sync point options and will be shown by the RightClickLineIndex handler.
            if (fileLines != null && lineIdx >= 0 && lineIdx < fileLines.Count)
            {
                var status = fileLines[lineIdx].Status;
                // Only show standalone menu if this is NOT an actionable line
                // (actionable lines get the apply context menu with sync point injection)
                if (status != AwfulDiff.Models.DiffStatus.Modified &&
                    status != AwfulDiff.Models.DiffStatus.Added &&
                    status != AwfulDiff.Models.DiffStatus.Gap)
                {
                    var menu = new System.Windows.Controls.ContextMenu();

                    if (isActiveSp)
                    {
                        int? removeLineA = FindSyncPointPartner(origLineOnThisPanel, 'B');
                        var removeItem = new System.Windows.Controls.MenuItem
                        {
                            Header = "✕ Remove Sync Point Here",
                            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.IndianRed),
                            ToolTip = $"Remove sync point at line {origLineOnThisPanel}"
                        };
                        removeItem.Click += (s, e) =>
                        {
                            if (removeLineA.HasValue)
                                _vm.RemoveSyncPoint(removeLineA.Value, origLineOnThisPanel);
                            e.Handled = true;
                        };
                        menu.Items.Add(removeItem);
                    }

                    var syncItemB = new System.Windows.Controls.MenuItem
                    {
                        Header = "🔗 Set Sync Point",
                        ToolTip = "Click the corresponding line in the left file to confirm"
                    };
                    syncItemB.Click += (s, e) =>
                    {
                        BeginPendingSync(panel, lineIdx);
                        e.Handled = true;
                    };
                    menu.Items.Add(syncItemB);

                    // Diff engine selector
                    AddDiffEngineItems(menu);

                    menu.IsOpen = true;
                }
            }
        }
    }

    /// <summary>
    /// Appends the diff engine selector (Standard/Patience) to a context menu.
    /// Selecting an engine goes through the VM property setter, which triggers a recompute.
    /// </summary>
    private void AddDiffEngineItems(System.Windows.Controls.ContextMenu menu)
    {
        menu.Items.Add(new System.Windows.Controls.Separator());

        var standardItem = new System.Windows.Controls.MenuItem
        {
            Header = "⚙ Myers diff (faster)",
            IsChecked = _vm.DiffAlgorithm == AwfulDiff.Models.DiffAlgorithm.Standard
        };
        standardItem.Click += (s, e) =>
        {
            _vm.DiffAlgorithm = AwfulDiff.Models.DiffAlgorithm.Standard;
            e.Handled = true;
        };
        menu.Items.Add(standardItem);

        var patienceItem = new System.Windows.Controls.MenuItem
        {
            Header = "⚙ Patience diff (default)",
            IsChecked = _vm.DiffAlgorithm == AwfulDiff.Models.DiffAlgorithm.Patience
        };
        patienceItem.Click += (s, e) =>
        {
            _vm.DiffAlgorithm = AwfulDiff.Models.DiffAlgorithm.Patience;
            e.Handled = true;
        };
        menu.Items.Add(patienceItem);
    }

    /// <summary>
    /// Finds the partner original line number for a sync point on the given panel.
    /// </summary>
    private int? FindSyncPointPartner(int origLine, char panel)
    {
        foreach (var sp in _vm.SyncPoints)
        {
            if (panel == 'A' && sp.LineA == origLine) return sp.LineB;
            if (panel == 'B' && sp.LineB == origLine) return sp.LineA;
        }
        return null;
    }

    /// <summary>
    /// Binary search for a value in a sorted list.
    /// </summary>
    private static bool BinarySearch(IReadOnlyList<int> sorted, int value)
    {
        int lo = 0, hi = sorted.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (sorted[mid] == value) return true;
            if (sorted[mid] < value) lo = mid + 1;
            else hi = mid - 1;
        }
        return false;
    }

    /// <summary>
    /// Enters pending sync mode: the opposite viewer waits for a click to confirm.
    /// </summary>
    private void BeginPendingSync(char panel, int lineIdx)
    {
        _pendingSync = (panel, lineIdx);

        // Set the PendingSyncLine on the opposite viewer to -1 (no specific highlight),
        // but update status bar to indicate pending mode.
        _vm.StatusInfo = $"🔗 Click the corresponding line in {(panel == 'A' ? "right" : "left")} file to confirm sync point...";

        // Cancel on next right-click (to avoid stacking)
    }

    /// <summary>
    /// Confirms a sync point pairing between two lines.
    /// </summary>
    private void ConfirmSyncPoint(char sourcePanel, int sourceLine, char targetPanel, int targetLine)
    {
        // Convert display-line indices to original line numbers
        var fileA = _vm.FileALines;
        var fileB = _vm.FileBLines;

        if (fileA == null || fileB == null)
        {
            CancelPendingSync();
            return;
        }

        int origLineA, origLineB;

        if (sourcePanel == 'A')
        {
            // Source is panel A, target is panel B
            if (sourceLine < 0 || sourceLine >= fileA.Count || targetLine < 0 || targetLine >= fileB.Count)
            {
                CancelPendingSync();
                return;
            }
            origLineA = fileA[sourceLine].OriginalLineNumber;
            origLineB = fileB[targetLine].OriginalLineNumber;
        }
        else
        {
            // Source is panel B, target is panel A
            if (targetLine < 0 || targetLine >= fileA.Count || sourceLine < 0 || sourceLine >= fileB.Count)
            {
                CancelPendingSync();
                return;
            }
            origLineA = fileA[targetLine].OriginalLineNumber;
            origLineB = fileB[sourceLine].OriginalLineNumber;
        }

        _vm.AddSyncPoint(origLineA, origLineB);
        _pendingSync = null;
    }

    /// <summary>
    /// Cancels the pending sync point operation.
    /// </summary>
    private void CancelPendingSync()
    {
        _pendingSync = null;
        _vm.StatusInfo = ""; // Clear pending sync message
    }

    // ── Hex Sync Points (byte granularity) ─────────────────────────────────────

    /// <summary>
    /// File-space byte index of a hex row slot, or -1 if the row carries no byte there
    /// (Gap filler or a hole in the aligned row).
    /// </summary>
    private static int ResolveHexByte(System.Collections.Generic.IReadOnlyList<AwfulDiff.Models.DiffLine>? lines, int lineIdx, int slot)
    {
        if (lines == null || lineIdx < 0 || lineIdx >= lines.Count) return -1;
        var row = lines[lineIdx];
        if (row.ByteStart == null) return -1;
        if (((row.HexSlotMask >> slot) & 1) == 0) return -1;
        return row.ByteStart.Value + AwfulDiff.Services.HexFormat.SlotOrdinal(row.HexSlotMask, slot);
    }

    /// <summary>
    /// Context menu for right-clicking a hex row: set/remove a sync point on an occupied byte slot,
    /// and compute CRC-32 over the selected bytes (falling back to the single clicked byte when
    /// nothing is selected).
    /// </summary>
    private void ShowHexContextMenu(FastDiffViewer viewer, char panel, int lineIdx, int slot)
    {
        if (!_vm.IsHexView) return;

        var fileLines = panel == 'A' ? _vm.FileALines : _vm.FileBLines;
        int fileByte = slot >= 0 ? ResolveHexByte(fileLines, lineIdx, slot) : -1;

        var menu = new System.Windows.Controls.ContextMenu();

        // ── Sync point items (only meaningful on an occupied slot) ──
        if (fileByte >= 0)
        {
            // Offer removal when this byte is an active anchor
            var activeAnchor = _vm.HexSyncPoints.FirstOrDefault(sp => panel == 'A' ? sp.ByteA == fileByte : sp.ByteB == fileByte);
            if (activeAnchor != default)
            {
                var removeItem = new System.Windows.Controls.MenuItem
                {
                    Header = "✕ Remove Sync Point Here",
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.IndianRed),
                    ToolTip = $"Remove sync point at 0x{fileByte:X}"
                };
                removeItem.Click += (s, e) =>
                {
                    _vm.RemoveHexSyncPoint(activeAnchor.ByteA, activeAnchor.ByteB);
                    e.Handled = true;
                };
                menu.Items.Add(removeItem);
            }

            var syncItem = new System.Windows.Controls.MenuItem
            {
                Header = $"🔗 Set Sync Point @ 0x{fileByte:X}",
                ToolTip = "Click the corresponding byte in the other file to confirm"
            };
            syncItem.Click += (s, e) =>
            {
                BeginPendingHexSync(panel, lineIdx, slot);
                e.Handled = true;
            };
            menu.Items.Add(syncItem);
        }

        // ── CRC-32 over the selected bytes (or the clicked byte if nothing is selected) ──
        (int startByte, int byteCount)? crcRange = viewer.GetByteSelectionRange();
        if (!crcRange.HasValue && fileByte >= 0)
            crcRange = (fileByte, 1);

        var paneBytes = panel == 'A' ? _vm.LeftFile?.RawBytes : _vm.RightEditableBytes;
        if (crcRange.HasValue && paneBytes != null && paneBytes.Length > 0)
        {
            int start = Math.Clamp(crcRange.Value.startByte, 0, paneBytes.Length - 1);
            int count = Math.Min(crcRange.Value.byteCount, paneBytes.Length - start);
            if (count > 0)
            {
                var crcItem = new System.Windows.Controls.MenuItem
                {
                    Header = count == 1 ? "🔢 CRC-32 of selected byte" : $"🔢 CRC-32 of {count} selected bytes",
                    ToolTip = $"CRC-32 over bytes 0x{start:X} – 0x{start + count - 1:X}"
                };
                crcItem.Click += (s, e) =>
                {
                    uint crc = Crc32.Compute(paneBytes.AsSpan(start, count));
                    var dlg = new CrcDialog(start, count, crc) { Owner = this };
                    dlg.ShowDialog();
                    e.Handled = true;
                };
                menu.Items.Add(crcItem);
            }
        }

        if (menu.Items.Count > 0)
            menu.IsOpen = true;
    }

    /// <summary>
    /// Enters pending hex sync mode: the opposite viewer waits for a byte-slot click to confirm.
    /// </summary>
    private void BeginPendingHexSync(char panel, int lineIdx, int slot)
    {
        _pendingHexSync = (panel, lineIdx, slot);
        _vm.StatusInfo = $"🔗 Click the corresponding byte in {(panel == 'A' ? "right" : "left")} file to confirm sync point...";
    }

    /// <summary>
    /// Confirms a hex sync point pairing between two bytes.
    /// </summary>
    private void ConfirmHexSyncPoint(char sourcePanel, int sourceLine, int sourceSlot, char targetPanel, int targetLine, int targetSlot)
    {
        var fileA = _vm.FileALines;
        var fileB = _vm.FileBLines;

        int byteA = sourcePanel == 'A' ? ResolveHexByte(fileA, sourceLine, sourceSlot) : ResolveHexByte(fileA, targetLine, targetSlot);
        int byteB = sourcePanel == 'A' ? ResolveHexByte(fileB, targetLine, targetSlot) : ResolveHexByte(fileB, sourceLine, sourceSlot);

        if (byteA < 0 || byteB < 0)
        {
            CancelPendingHexSync();
            return;
        }

        _vm.AddHexSyncPoint(byteA, byteB);
        _pendingHexSync = null;
    }

    /// <summary>
    /// Cancels the pending hex sync point operation.
    /// </summary>
    private void CancelPendingHexSync()
    {
        _pendingHexSync = null;
        _vm.StatusInfo = ""; // Clear pending sync message
    }

    private System.Windows.Controls.Image? CreateStatusIcon(AwfulDiff.Models.DiffStatus status)
    {
        // Simple text-based icon using a TextBlock instead of Image
        var tb = new System.Windows.Controls.TextBlock
        {
            Text = status switch
            {
                AwfulDiff.Models.DiffStatus.Modified => "⇄",
                AwfulDiff.Models.DiffStatus.Added => "✕",
                AwfulDiff.Models.DiffStatus.Gap => "➕",
                _ => ""
            },
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White),
            FontSize = 12,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        return null; // We use TextBlock directly in MenuItem.Icon
    }

 /// <summary>
    /// Apply all remaining changes from the right file to make it match the left file.
    /// Collects all actionable indices once, then delegates to the batch method so the
    /// diff is recomputed only a single time at the end.
    /// </summary>
    private void ApplyAllChanges()
    {
        if (_vm.FileBLines == null) return;

        var indicesToApply = new List<int>();
        for (int i = _vm.FileBLines.Count - 1; i >= 0; i--)
        {
            var line = _vm.FileBLines[i];
            if (line.Status is AwfulDiff.Models.DiffStatus.Modified or AwfulDiff.Models.DiffStatus.Added or AwfulDiff.Models.DiffStatus.Gap)
            {
                indicesToApply.Add(i);
            }
        }

        // Use the batch method — it handles Modified/Added/Gap in the correct order
        // and recomputes the diff only once at the end.
        if (indicesToApply.Count > 0)
            _vm.ApplyChangesToRight(indicesToApply);
    }

    /// <summary>
    /// Track which viewer was last clicked so search targets that pane.
    /// </summary>
    private void TrackActiveViewer()
    {
        MouseButtonEventHandler handle = (s, e) =>
        {
            if (s is FastDiffViewer v)
                _activeSearchViewer = v;
        };

        LeftViewer.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, handle);
        RightViewer.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, handle);
        TopViewer.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, handle);
        BottomViewer.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, handle);
        UnifiedViewer.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, handle);

        // Double-click an Added/Removed block (anchor markers visible) → partner pane jumps
        // to where the block's gap would sit. The unified layout shares one list, so it never
        // raises this — only the two-pane viewers are wired.
        LeftViewer.AnchorJumpRequested += (slot, st) => OnAnchorJumpRequested(LeftViewer, slot, st);
        RightViewer.AnchorJumpRequested += (slot, st) => OnAnchorJumpRequested(RightViewer, slot, st);
        TopViewer.AnchorJumpRequested += (slot, st) => OnAnchorJumpRequested(TopViewer, slot, st);
        BottomViewer.AnchorJumpRequested += (slot, st) => OnAnchorJumpRequested(BottomViewer, slot, st);

        // Default to left/top viewer
        _activeSearchViewer = LeftViewer;
    }

    private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine($"[MainWindow.Loaded] DataContext = {(DataContext == _vm ? "OK" : "MISMATCH")}");
        Debug.WriteLine($"[MainWindow.Loaded] DiffToolbar.DataContext = {(DiffToolbar.DataContext == _vm ? "OK" : "MISMATCH")}");
        Debug.WriteLine($"[MainWindow.Loaded] LayoutSideBySide.Visibility = {LayoutSideBySide.Visibility}");
        Debug.WriteLine($"[MainWindow.Loaded] LayoutTopBottom.Visibility = {LayoutTopBottom.Visibility}");
        Debug.WriteLine($"[MainWindow.Loaded] IsSideBySide = {_vm.IsSideBySide}");

        // Apply initial hex view state to all viewers (in case persisted from previous session)
        LeftViewer.IsHexView = _vm.IsHexView;
        RightViewer.IsHexView = _vm.IsHexView;
        TopViewer.IsHexView = _vm.IsHexView;
        BottomViewer.IsHexView = _vm.IsHexView;
        UnifiedViewer.IsHexView = _vm.IsHexView;

        // Apply initial hex per-byte display mode (persisted)
        LeftViewer.HexPerByte = _vm.HexPerByte;
        RightViewer.HexPerByte = _vm.HexPerByte;
        TopViewer.HexPerByte = _vm.HexPerByte;
        BottomViewer.HexPerByte = _vm.HexPerByte;
        UnifiedViewer.HexPerByte = _vm.HexPerByte;

        // Apply initial inspector visibility
        if (_vm.IsHexView)
        {
            InspectorRow.Height = new GridLength(_vm.HexInspectorHeight, GridUnitType.Pixel);
            InspectorPanel.Visibility = Visibility.Visible;
            InspectorSplitter.Visibility = Visibility.Visible;
            InspectorGrabZone.Visibility = Visibility.Visible;
        }
        else
        {
            InspectorRow.Height = new GridLength(0, GridUnitType.Pixel);
            InspectorPanel.Visibility = Visibility.Collapsed;
            InspectorSplitter.Visibility = Visibility.Collapsed;
            InspectorGrabZone.Visibility = Visibility.Collapsed;
        }

        // Check status bar bindings
        var statusBindings = new[]
        {
            ("StatusLeft", _vm.StatusLeft),
            ("StatusRight", _vm.StatusRight),
            ("StatusInfo", _vm.StatusInfo)
        };
        foreach (var (name, value) in statusBindings)
        {
            Debug.WriteLine($"[MainWindow.Loaded] {name} = \"{value?.Substring(0, Math.Min(50, value?.Length ?? 0))}\"");
        }
    }

    // Layout that was visible before the latest switch — the source of the scroll anchor.
    // (Can't be read from _vm here: PropertyChanged fires after the change, so the VM's
    // Is*SideBySide/IsTopBottom/IsUnified flags already reflect the target.)
    private LayoutMode _lastLayout;

    private void UpdateLayoutVisibility()
    {
        var target = _vm.LayoutMode;

        // Capture the scroll anchor from the layout we're leaving, translated into
        // canonical (interleaved row) space so it can be mapped onto any target layout.
        int anchorCanonical = 0;
        double anchorSubLine = 0;

        if (_lastLayout == LayoutMode.Unified)
        {
            var (line, sub) = UnifiedViewer.GetScrollAnchor();
            anchorCanonical = _vm.UnifiedIndexToCanonical(line);
            anchorSubLine = sub;
        }
        else if (_lastLayout == LayoutMode.SideBySide)
        {
            // Anchor on the populated pane (left when both present; right only in
            // single-file-on-right mode, where all index maps are identity anyway).
            bool leftHasLines = LeftViewer.Lines?.Count > 0;
            var (line, sub) = leftHasLines ? LeftViewer.GetScrollAnchor() : RightViewer.GetScrollAnchor();
            anchorCanonical = leftHasLines ? _vm.PaneLeftIndexToCanonical(line) : _vm.PaneRightIndexToCanonical(line);
            anchorSubLine = sub;
        }
        else // TopBottom
        {
            bool topHasLines = TopViewer.Lines?.Count > 0;
            var (line, sub) = topHasLines ? TopViewer.GetScrollAnchor() : BottomViewer.GetScrollAnchor();
            anchorCanonical = topHasLines ? _vm.PaneLeftIndexToCanonical(line) : _vm.PaneRightIndexToCanonical(line);
            anchorSubLine = sub;
        }

        LayoutSideBySide.Visibility = target == LayoutMode.SideBySide ? Visibility.Visible : Visibility.Collapsed;
        LayoutTopBottom.Visibility = target == LayoutMode.TopBottom ? Visibility.Visible : Visibility.Collapsed;
        LayoutUnified.Visibility = target == LayoutMode.Unified ? Visibility.Visible : Visibility.Collapsed;
        _lastLayout = target;
        Debug.WriteLine($"[UpdateLayoutVisibility] Layout={target} (anchorCanonical={anchorCanonical}, sub={anchorSubLine:F1})");

        // Restore the anchor after layout is applied (viewport dimensions are valid)
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (target == LayoutMode.Unified)
            {
                UnifiedViewer.SetScrollAnchor(_vm.CanonicalToUnifiedIndex(anchorCanonical), anchorSubLine);
            }
            else
            {
                var (l, r) = _vm.CanonicalToPaneIndices(anchorCanonical);
                if (target == LayoutMode.SideBySide)
                {
                    LeftViewer.SetScrollAnchor(l, anchorSubLine);
                    RightViewer.SetScrollAnchor(r, anchorSubLine);
                }
                else
                {
                    TopViewer.SetScrollAnchor(l, anchorSubLine);
                    BottomViewer.SetScrollAnchor(r, anchorSubLine);
                }
            }
        }), System.Windows.Threading.DispatcherPriority.Loaded);

        // Re-swap the editor cell if edit mode is on (layout changed underneath it).
        UpdateEditorVisibility();
    }

    /// <summary>
    /// Swaps the right-side viewer for the plain editor while text-mode edit is on. One TextBox
    /// per layout cell (not a shared instance) so each keeps its own undo stack while
    /// Collapsed; content is shared through the bound RightEditorText anyway. When editing
    /// is off every editor is Collapsed and unfocusable, so no pane accepts typing.
    /// In hex mode the text editors are never shown — editing happens through the hex entry
    /// field in the pane header and the data inspector instead, which this also toggles.
    /// </summary>
    private void UpdateEditorVisibility()
    {
        bool editing = _vm.EditRightEnabled;
        bool showEditors = editing && !_vm.IsHexView;

        (TextBox editor, FastDiffViewer viewer) active = _lastLayout switch
        {
            LayoutMode.TopBottom => (RightEditorTb, BottomViewer),
            LayoutMode.Unified => (UnifiedEditor, UnifiedViewer),
            _ => (RightEditorSbs, RightViewer)
        };

        RightEditorSbs.Visibility = Visibility.Collapsed;
        RightEditorTb.Visibility = Visibility.Collapsed;
        UnifiedEditor.Visibility = Visibility.Collapsed;

        if (showEditors)
        {
            active.editor.Visibility = Visibility.Visible;
            active.viewer.Visibility = Visibility.Collapsed;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                active.editor.Focus();
                // Open the editor where the user was looking in the diff view (gap rows don't
                // exist in the plain text). UpdateLayout first so ScrollToLine has an extent.
                int line = FastDiffViewer.AnchorToPlainLine(active.viewer);
                if (line > 0)
                {
                    active.editor.UpdateLayout();
                    active.editor.ScrollToLine(line);
                }
            }));
        }
        else
        {
            active.viewer.Visibility = Visibility.Visible;
        }

        // Direct hex entry field + inspector editability: only in hex mode with edit enabled.
        bool showHexEntry = editing && _vm.IsHexView;
        HexEntryLabelSbs.Visibility = showHexEntry ? Visibility.Visible : Visibility.Collapsed;
        HexEntrySbs.Visibility = showHexEntry ? Visibility.Visible : Visibility.Collapsed;
        HexEntryLabelTb.Visibility = showHexEntry ? Visibility.Visible : Visibility.Collapsed;
        HexEntryTb.Visibility = showHexEntry ? Visibility.Visible : Visibility.Collapsed;
        HexInspector.SetEditable(showHexEntry);
    }

    // ── Direct hex entry field ───────────────────────────────────────────────────

    private System.Windows.Threading.DispatcherTimer? _hexEntryFlashTimer;

    /// <summary>
    /// Key handler for the pane-header hex entry fields (HexEntrySbs/HexEntryTb): Enter commits,
    /// Esc clears.
    /// </summary>
    private void HexEntry_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitHexEntry(sender as TextBox);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _vm.HexEntryText = "";
        }
    }

    /// <summary>
    /// Commits the hex entry field: replaces the right pane's byte selection, or appends at EOF
    /// when nothing is selected. Invalid input flashes the field red and keeps it for correction.
    /// </summary>
    private void CommitHexEntry(TextBox? field)
    {
        if (!_vm.IsHexView || !_vm.EditRightEnabled) return;

        // Accept spaces, colons and dots between pairs ("DE AD", "DE:AD", "DE.AD").
        string hex = string.Concat((_vm.HexEntryText ?? "")
            .Where(c => !char.IsWhiteSpace(c) && c != ':' && c != '.')).ToUpperInvariant();

        if (hex.Length == 0 || hex.Length % 2 != 0 || hex.Length > 8192 ||   // cap: 4096 bytes per commit
            hex.Any(c => (c < '0' || c > '9') && (c < 'A' || c > 'F')))
        {
            FlashHexEntry(field);
            _vm.StatusInfo = "Invalid hex entry — expected an even number of hex digits, e.g. DE AD 00";
            return;
        }

        // All characters are validated hex digits above, so decode nibble pairs directly.
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)((HexNibble(hex[i * 2]) << 4) | HexNibble(hex[i * 2 + 1]));

        var viewer = _lastLayout == LayoutMode.TopBottom ? BottomViewer : RightViewer;
        var sel = viewer.GetByteSelectionRange();
        int startByte, removeCount;
        if (sel.HasValue)
            (startByte, removeCount) = sel.Value;
        else
        {
            startByte = _vm.RightEditableBytes?.Length ?? 0;   // no selection → append at EOF
            removeCount = 0;
        }

        _vm.ApplyRightByteEdit(startByte, removeCount, bytes);

        // Clear the field but keep focus so entries can be chained.
        _vm.HexEntryText = "";
        field?.Focus();
    }

     /// <summary>
    /// Numeric value of an uppercase hex digit (caller guarantees the character is 0-9A-F).
    /// </summary>
    private static int HexNibble(char c) => c <= '9' ? c - '0' : c - 'A' + 10;

    /// <summary>
    /// Brief red border on an invalid hex entry; restores the themed brush afterwards.
    /// </summary>
    private void FlashHexEntry(TextBox? field)
    {
        if (field == null) return;
        field.BorderBrush = Brushes.Red;
        _hexEntryFlashTimer?.Stop();
        _hexEntryFlashTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _hexEntryFlashTimer.Tick += (s, e) =>
        {
            _hexEntryFlashTimer!.Stop();
            // Re-establish the themed border reference (clears the red local value).
            field.SetResourceReference(TextBox.BorderBrushProperty, "BorderGlowDim");
        };
        _hexEntryFlashTimer.Start();
    }

    // ── Hex Inspector ───────────────────────────────────────────────────────────

    private void SetupHexInspector()
    {
        // Wire selection changed events from all viewers to the inspector
        // Left/Top viewers → left panel, Right/Bottom viewers → right panel
        foreach (var viewer in new[] { LeftViewer, TopViewer })
        {
            viewer.SelectionChanged += (s, e) => UpdateInspectorPanel(s, e, 'L');
        }

        foreach (var viewer in new[] { RightViewer, BottomViewer })
        {
            viewer.SelectionChanged += (s, e) => UpdateInspectorPanel(s, e, 'R');
        }

        // Right-panel commits (typed element values, ⊕ insert) route into the byte splice path.
        HexInspector.RightEditRequested += (startByte, removeCount, insertBytes) =>
            _vm.ApplyRightByteEdit(startByte, removeCount, insertBytes);

        // After a byte edit re-diffs, restore the edited range's selection on the active
        // layout's right pane so the inspector stays pointed at the changed bytes.
        _vm.RightByteSelectionToRestore += (restore) =>
        {
            var viewer = _lastLayout == LayoutMode.TopBottom ? BottomViewer : RightViewer;
            viewer.SetByteSelectionRange(restore.StartByte, restore.ByteCount);
        };

        // Keep type size in sync with inspector so viewers can snap selections
        HexInspector.TypeSizeChanged += ts =>
        {
            foreach (var viewer in new[] { LeftViewer, RightViewer, TopViewer, BottomViewer, UnifiedViewer })
                viewer.InspectorTypeSize = ts;
            _vm.HexInspectorTypeName = HexInspector.TypeName;   // remembered on exit
        };

        // "⚙ structs…" button → pattern manager dialog; on close, refresh the inspector's combo.
        HexInspector.ManageStructsRequested += () =>
        {
            new StructPatternDialog { Owner = this }.ShowDialog();
            HexInspector.ReloadStructPatterns();
        };

        // Load saved struct patterns before restoring the last-used type so a stored pattern
        // name resolves (unknown/missing names leave the int32 default in place). The first sync
        // below already carries the resulting type size.
        HexInspector.ReloadStructPatterns();
        HexInspector.SetTypeByName(SettingsService.Load().HexInspectorType);
        _vm.HexInspectorTypeName = HexInspector.TypeName;

        // Initialize to current type size
        foreach (var viewer in new[] { LeftViewer, RightViewer, TopViewer, BottomViewer, UnifiedViewer })
            viewer.InspectorTypeSize = HexInspector.TypeSize;
    }

    private void UpdateInspectorPanel(object? sender, List<int> selectedIndices, char panel)
    {
        if (!_vm.IsHexView) return;

        var viewer = sender as FastDiffViewer;
        if (viewer == null || viewer.Lines == null) return;

        // Determine which file's bytes to use based on which panel. The right side reads the
        // editable working copy (spliced by hex edits) so typed values reflect un-saved state.
        byte[]? bytes = panel == 'L'
            ? _vm.LeftFile?.RawBytes
            : _vm.RightEditableBytes;

        bool isLeft = panel == 'L';

        if (bytes == null || selectedIndices.Count == 0)
        {
            if (isLeft) HexInspector.ClearLeft();
            else HexInspector.ClearRight();
            return;
        }

        // Try the byte-precise selection first (file-accurate under aligned rows; Gap filler
        // rows between the endpoints are skipped, so the count is exact), falling back to
        // the bytes actually held by the selected display rows.
        int startByte;
        int count;
        var byteRange = viewer.GetByteSelectionRange();
        if (byteRange.HasValue)
        {
            (startByte, count) = byteRange.Value;
        }
        else
        {
            // Aligned rows may have holes or be Gap fillers, so sum each row's exact byte
            // range instead of assuming 16 bytes per line. Consecutive display rows on one
            // side always hold file-contiguous bytes, so min start + total count is exact.
            startByte = int.MaxValue;
            count = 0;
            foreach (var idx in selectedIndices)
            {
                if (idx < 0 || idx >= viewer.Lines.Count) continue;
                var row = viewer.Lines[idx];
                if (row.ByteStart == null) continue; // Gap filler — no bytes on this side
                startByte = Math.Min(startByte, row.ByteStart.Value);
                count += AwfulDiff.Services.HexFormat.SlotCount(row.HexSlotMask);
            }

            if (startByte == int.MaxValue)
            {
                if (isLeft) HexInspector.ClearLeft();
                else HexInspector.ClearRight();
                return;
            }
        }

        // Clamp to actual file size
        count = Math.Min(count, bytes.Length - startByte);
        if (count < 0) count = 0;

        if (isLeft) HexInspector.SetLeftSelection(bytes, startByte, count);
        else HexInspector.SetRightSelection(bytes, startByte, count);
    }

    // ── Window Settings Persistence ──────────────────────────────────────────

    /// <summary>
    /// Apply saved window position, size, and state from the last session.
    /// Called after InitializeComponent so that the window geometry can be set before it's shown.
    /// </summary>
    private void ApplyWindowSettings()
    {
        var s = SettingsService.Load();

        // Restore position and size (skip if NaN — first run lets the XAML CenterScreen default apply)
        if (!double.IsNaN(s.WindowLeft))
        {
            // Only restore if the window would be at least partially visible on a monitor
            var workArea = SystemParameters.WorkArea;
            bool visible =
                s.WindowLeft + s.WindowWidth > workArea.Left &&
                s.WindowLeft < workArea.Right &&
                s.WindowTop + s.WindowHeight > workArea.Top &&
                s.WindowTop < workArea.Bottom;

            if (visible)
            {
                // The XAML declares CenterScreen, which WPF applies at show time and would clobber
                // the restored position — switch to Manual so the saved Left/Top actually stick.
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = s.WindowLeft;
                Top = s.WindowTop;
                Width = Math.Max(600, s.WindowWidth);
                Height = Math.Min(9000, s.WindowHeight);
            }
        }
        else if (!double.IsNaN(s.WindowWidth))
        {
            // Valid size but no position (first run or NaN position) — center with saved size
            Width = Math.Max(600, s.WindowWidth);
            Height = Math.Min(9000, s.WindowHeight);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        // Restore window state LAST so the bounds above become the maximized window's restore
        // bounds — un-maximizing later lands on the previous normal size/position.
        if (s.WindowState == "Maximized")
            WindowState = WindowState.Maximized;
    }

    // ── Window Close: Persist Settings ─────────────────────────────────────────

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_vm.HandleClosing())
        {
            e.Cancel = true;
            return;
        }

        // Save window geometry so it's restored on next launch
        SaveWindowSettings();

        _vm.SaveAndCleanup();
        base.OnClosing(e);
    }

    /// <summary>
    /// Save current window position, size, and state to disk.
    /// When maximized, save the restore bounds so we can restore the previous normal size.
    /// </summary>
    private void SaveWindowSettings()
    {
        var s = SettingsService.Load();

        // When maximized, use RestoreBounds to save the window's normal size/position
        if (WindowState == WindowState.Maximized)
        {
            s.WindowWidth = RestoreBounds.Width;
            s.WindowHeight = RestoreBounds.Height;
            s.WindowLeft = RestoreBounds.Left;
            s.WindowTop = RestoreBounds.Top;
        }
        else
        {
            s.WindowWidth = Width;
            s.WindowHeight = Height;
            s.WindowLeft = Left;
            s.WindowTop = Top;
        }

        s.WindowState = WindowState == WindowState.Maximized ? "Maximized" : "Normal";

        SettingsService.Save(s);
    }

    // ── Keyboard Shortcuts ─────────────────────────────────────────────────────

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        bool ctrl = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
        bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);

        if (ctrl && shift && e.Key == Key.R)
        {
            e.Handled = true;
            _vm.OpenRightFileCommand.Execute(null);
        }
        else if (ctrl && e.Key == Key.Down)
        {
            e.Handled = true;
            NavigateDiff(1);
        }
        else if (ctrl && e.Key == Key.Up)
        {
            e.Handled = true;
            NavigateDiff(-1);
        }
        else if (ctrl && e.Key == Key.L)
        {
            e.Handled = true;
            _vm.OpenLeftFileCommand.Execute(null);
        }
        else if (ctrl && e.Key == Key.R)
        {
            e.Handled = true;
            _vm.RefreshCommand.Execute(null);
        }
        else if (ctrl && e.Key == Key.S)
        {
            e.Handled = true;
            // While the right-document editor has focus, Ctrl+S saves instead of swapping —
            // swap would discard the in-flight edits. In hex edit mode there is no text editor
            // (editing happens via the hex field / inspector), so save whenever edit is on.
            var focused = Keyboard.FocusedElement as TextBox;
            bool editorFocused = focused is not null &&
                (focused == RightEditorSbs || focused == RightEditorTb || focused == UnifiedEditor);
            if (_vm.EditRightEnabled && (_vm.IsHexView || editorFocused))
            {
                _vm.SaveRightFileCommand.Execute(null);
            }
            else
            {
                _vm.SwapFilesCommand.Execute(null);
            }
        }
        else if (ctrl && e.Key == Key.M)
        {
            e.Handled = true;
            _vm.StartMergeCommand.Execute(null);
        }
        else if (ctrl && e.Key == Key.F)
        {
            e.Handled = true;
            ShowSearchBar();
        }
        else if (e.Key == Key.F3)
        {
            if (_searchActive)
            {
                e.Handled = true;
                NavigateSearch(shift ? -1 : 1);
            }
        }
        else if (ctrl && e.Key == Key.C)
        {
            // Copy selected text from whichever viewer has a selection
            var activeViewers = _vm.LayoutMode switch
            {
                LayoutMode.Unified => new[] { UnifiedViewer },
                LayoutMode.TopBottom => new[] { TopViewer, BottomViewer },
                _ => new[] { LeftViewer, RightViewer }
            };

            // Copy from the pane whose selection changed most recently — clicking a line never
            // clears the OTHER pane's selection, so both can hold one at once and the stale one
            // must not shadow the fresh one (old code always copied from the left pane first).
            FastDiffViewer? target = null;
            foreach (var v in activeViewers)
                if (v.HasSelection && (target == null || v.SelectionSeq > target.SelectionSeq))
                    target = v;

            if (target != null)
            {
                string text = target.GetSelectedText();
                if (!string.IsNullOrEmpty(text) && TrySetClipboard(text))
                    e.Handled = true;
            }
        }
        else if (e.Key == Key.Delete)
        {
            // Hex edit mode: Del with the right pane focused deletes its byte selection.
            if (_vm.IsHexView && _vm.EditRightEnabled &&
                Keyboard.FocusedElement is FastDiffViewer v && (v == RightViewer || v == BottomViewer))
            {
                var sel = v.GetByteSelectionRange();
                if (sel.HasValue)
                {
                    e.Handled = true;
                    _vm.ApplyRightByteEdit(sel.Value.startByte, sel.Value.byteCount, Array.Empty<byte>());
                }
            }
        }
        else if (e.Key == Key.Escape)
        {
            if (_searchActive)
            {
                e.Handled = true;
                HideSearchBar();
            }
        }
        else if (e.Key == Key.F11)
        {
            e.Handled = true;
            _vm.ToggleLayoutCommand.Execute(null);
        }
    }

    /// <summary>
    /// Clipboard write that never freezes the UI thread — see <see cref="Services.ClipboardHelper"/>.
    /// Returns true if the copy was made or scheduled (treat the key as handled).
    /// </summary>
    private bool TrySetClipboard(string text) => ClipboardHelper.TrySetText(text);

    // ── Sync Scroll Routing ────────────────────────────────────────────────────

    private void OnScrollRatioChanged(object? sender, double ratio)
    {
        if (!_vm.SyncScroll) return;
        if (_vm.LayoutMode == LayoutMode.Unified) return; // single viewer — nothing to sync

        var source = sender as FastDiffViewer;
        if (source == null) return;

        // Determine active viewers based on current layout
        var (left, right) = _vm.IsSideBySide
            ? (LeftViewer, RightViewer)
            : (TopViewer, BottomViewer);

        // Sync the viewer that isn't the source
        if (source == left)
            right.SyncScroll(ratio);
        else if (source == right)
            left.SyncScroll(ratio);
    }

    private void OnHorizontalScrollRatioChanged(object? sender, double ratio)
    {
        if (!_vm.SyncScroll) return;
        if (_vm.LayoutMode == LayoutMode.Unified) return; // single viewer — nothing to sync

        var source = sender as FastDiffViewer;
        if (source == null) return;

        var (left, right) = _vm.IsSideBySide
            ? (LeftViewer, RightViewer)
            : (TopViewer, BottomViewer);

        if (source == left)
            right.SyncHorizontalScroll(ratio);
        else if (source == right)
            left.SyncHorizontalScroll(ratio);
    }

    /// <summary>
    /// A pane's Added/Removed block was double-clicked while anchor markers were visible:
    /// scroll the partner pane to where the block's gap would sit and mark it with a thin
    /// accent line. The jump raises ScrollRatioChanged, so sync scroll brings the source
    /// pane along too — both panes converge on the change.
    /// </summary>
    private void OnAnchorJumpRequested(FastDiffViewer source, int slot, DiffStatus status)
    {
        if (_vm.LayoutMode == LayoutMode.Unified) return; // single viewer — nowhere to jump

        var (left, right) = _vm.IsSideBySide
            ? (LeftViewer, RightViewer)
            : (TopViewer, BottomViewer);

        if (source == left) right.ShowAnchorSlot(slot, status);
        else if (source == right) left.ShowAnchorSlot(slot, status);
    }

    // ── Diff Navigation ────────────────────────────────────────────────────────

    private void NavigateDiff(int direction)
    {
        var viewers = _vm.LayoutMode switch
        {
            LayoutMode.Unified => new[] { UnifiedViewer },
            LayoutMode.TopBottom => new[] { TopViewer, BottomViewer },
            _ => new[] { LeftViewer, RightViewer }
        };

        foreach (var viewer in viewers)
            viewer.NavigateToDiffBlock(direction);
    }

    // ── Search ─────────────────────────────────────────────────────────────────

    private void SetupSearchBar()
    {
        SearchCloseButton.Click += (s, e) => HideSearchBar();

        SearchTextBox.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter)
                NavigateSearch(1);
            else if (e.Key == Key.Escape)
                HideSearchBar();
        };

        SearchTextBox.TextChanged += (s, e) =>
        {
            _searchQuery = SearchTextBox.Text;
            RunSearch();
        };
    }

    private void ShowSearchBar()
    {
        // Determine active viewer — default to the visible layout's first viewer if none clicked yet
        var viewers = _vm.LayoutMode switch
        {
            LayoutMode.Unified => new[] { UnifiedViewer },
            LayoutMode.TopBottom => new[] { TopViewer, BottomViewer },
            _ => new[] { LeftViewer, RightViewer }
        };

        if (_activeSearchViewer == null || !_activeSearchViewer.IsEnabled)
            _activeSearchViewer = viewers[0];

        // Position search bar over the active viewer
        PositionSearchBar();
        SearchBarOverlay.Visibility = Visibility.Visible;
        _searchActive = true;

        if (string.IsNullOrEmpty(_searchQuery))
            _searchQuery = "";
        SearchTextBox.Text = _searchQuery;

        SearchTextBox.SelectAll();
        SearchTextBox.Focus();

        RunSearch();
    }

    /// <summary>
    /// Position the search bar overlay at the bottom of the active viewer,
    /// and set the side label to indicate which file is being searched.
    /// </summary>
    private void PositionSearchBar()
    {
        var viewer = _activeSearchViewer;
        if (viewer == null || viewer.RenderSize.Width <= 0) return;

        // Determine side label based on which viewer is active
        string sideLabel;
        if (viewer == LeftViewer || viewer == TopViewer)
            sideLabel = "Left";
        else if (viewer == RightViewer || viewer == BottomViewer)
            sideLabel = "Right";
        else if (viewer == UnifiedViewer)
            sideLabel = "Unified";
        else
            sideLabel = "";

        SearchSideLabel.Text = sideLabel;

        // Get viewer bounds relative to ContentArea
        var vector = viewer.TransformToVisual(ContentArea).Transform(new System.Windows.Point(0, 0));
        double left = vector.X;
        double top = vector.Y + viewer.RenderSize.Height - 36; // 36 = approximate bar height + padding
        double width = viewer.RenderSize.Width;

        System.Windows.Controls.Canvas.SetLeft(SearchBarOverlay, left);
        System.Windows.Controls.Canvas.SetTop(SearchBarOverlay, top);
        SearchBarOverlay.Width = width;
    }

    private void HideSearchBar()
    {
        _searchActive = false;
        SearchBarOverlay.Visibility = Visibility.Collapsed;

        // Clear search state from all viewers
        var allViewers = new[] { LeftViewer, RightViewer, TopViewer, BottomViewer, UnifiedViewer };
        foreach (var v in allViewers)
        {
            v.SearchQuery = null;
            v.CurrentSearchMatchIndex = -1;
        }

        _searchMatches.Clear();
        _matchIndex = -1;
    }

    private void RunSearch()
    {
        _searchMatches.Clear();

        string query = SearchTextBox.Text ?? "";
        var viewer = _activeSearchViewer;

        if (string.IsNullOrEmpty(query))
        {
            if (viewer != null)
            {
                viewer.SearchQuery = null;
                viewer.CurrentSearchMatchIndex = -1;
            }
            SearchCountLabel.Text = "";
            return;
        }

        // Only search in the active viewer
        if (viewer == null || viewer.Lines == null)
        {
            SearchCountLabel.Text = "No results";
            return;
        }

        var lines = viewer.Lines;
        for (int i = 0; i < lines.Count; i++)
        {
            string? text = lines[i].Text;
            if (text != null && text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                _searchMatches.Add(i);
        }

        viewer.SearchQuery = query;
        int totalMatches = _searchMatches.Count;
        SearchCountLabel.Text = totalMatches == 0 ? "No results" : $"Match {_matchIndex + 1} of {totalMatches}";
    }

    private void NavigateSearch(int direction)
    {
        var viewer = _activeSearchViewer;
        if (viewer == null || _searchMatches.Count == 0) return;

        _matchIndex += direction;
        if (_matchIndex >= _searchMatches.Count) _matchIndex = 0;
        if (_matchIndex < 0) _matchIndex = _searchMatches.Count - 1;

        int lineIdx = _searchMatches[_matchIndex];
        viewer.CurrentSearchMatchIndex = lineIdx;
        viewer.ScrollToLine(lineIdx);

        SearchCountLabel.Text = $"Match {_matchIndex + 1} of {_searchMatches.Count}";
    }

    private void SetupDragDrop()
    {
        AllowDrop = true;

        // Enable drag-and-drop on all viewers so that drag events route properly
        // even when hovering over empty panes (which have zero visual children)
        foreach (var viewer in new[] { LeftViewer, RightViewer, TopViewer, BottomViewer, UnifiedViewer })
        {
            viewer.AllowDrop = true;

            viewer.PreviewDragEnter += OnViewerDragEnter;
            viewer.PreviewDragOver += OnViewerDragOver;
            viewer.PreviewDragLeave += OnViewerDragLeave;
            viewer.PreviewDrop += OnViewerDrop;
        }

        PreviewDragEnter += OnWindowDragEnter;
        PreviewDragOver += OnWindowDragOver;
        PreviewDragLeave += OnWindowDragLeave;
        PreviewDrop += OnWindowDrop;
    }

    // ── Viewer-level drag handlers (ensure feedback works on empty panes) ──────────────

    private void OnViewerDragEnter(object sender, DragEventArgs e)
    {
        if (!(sender is FastDiffViewer viewer)) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        // Folder drags open the directory-diffs window instead of filling a pane — no highlight.
        if (DropContainsDirectory(e.Data))
        {
            SetDragOverViewer(null);
            e.Effects = DragDropEffects.Copy;
            return;
        }

        SetDragOverViewer(viewer);
        e.Effects = DragDropEffects.Copy;
    }

    private void OnViewerDragOver(object sender, DragEventArgs e)
    {
        if (!(sender is FastDiffViewer viewer)) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        // Folder drags open the directory-diffs window instead of filling a pane — no highlight.
        if (DropContainsDirectory(e.Data))
        {
            SetDragOverViewer(null);
            e.Effects = DragDropEffects.Copy;
            return;
        }

        SetDragOverViewer(viewer);
        e.Effects = DragDropEffects.Copy;
    }

    private void OnViewerDragLeave(object sender, DragEventArgs e)
    {
        if (!(sender is FastDiffViewer viewer)) return;
        // Only clear if the previous target was this viewer
        if (_dragOverViewer == viewer)
            SetDragOverViewer(null);
    }

    private void OnViewerDrop(object sender, DragEventArgs e)
    {
        if (!(sender is FastDiffViewer viewer)) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        SetDragOverViewer(null);

        // Folder drags route to the directory-diffs window (the window-level PreviewDrop also
        // fires for this, but Handled below stops it).
        if (DropContainsDirectory(e.Data))
        {
            HandleFolderDrop(e.Data);
            e.Handled = true;
            return;
        }

        HandleFileDrop(e.Data, viewer);
        e.Handled = true;
    }

    private void OnWindowDragLeave(object sender, DragEventArgs e)
    {
        SetDragOverViewer(null);
    }

   private FastDiffViewer? GetViewerAt(Point position)
    {
        // Compare in window coordinates (DIPs) throughout — PointToScreen returns physical
        // screen pixels, which mis-compares against DIP-based ActualWidth/Height at any DPI
        // scaling other than 100% (drops then missed the right/bottom part of a pane).
        var viewers = _vm.LayoutMode switch
        {
            LayoutMode.Unified => new[] { UnifiedViewer },
            LayoutMode.TopBottom => new[] { TopViewer, BottomViewer },
            _ => new[] { LeftViewer, RightViewer }
        };

        foreach (var viewer in viewers)
        {
            if (viewer.ActualWidth <= 0 || viewer.ActualHeight <= 0) continue;

            var origin = viewer.TransformToAncestor(this).Transform(new Point(0, 0));
            if (position.X >= origin.X && position.X < origin.X + viewer.ActualWidth &&
                position.Y >= origin.Y && position.Y < origin.Y + viewer.ActualHeight)
                return viewer;
        }
        return null;
    }

    private void OnWindowDragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        // Folder drags open the directory-diffs window — valid anywhere in the window (no pane
        // highlight, unlike file drops which only land on a viewer).
        if (DropContainsDirectory(e.Data))
        {
            SetDragOverViewer(null);
            e.Effects = DragDropEffects.Copy;
            return;
        }

        var pos = e.GetPosition(this);
        var viewer = GetViewerAt(pos);
        SetDragOverViewer(viewer);
        e.Effects = viewer != null ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        // Folder drags open the directory-diffs window — valid anywhere in the window (no pane
        // highlight, unlike file drops which only land on a viewer).
        if (DropContainsDirectory(e.Data))
        {
            SetDragOverViewer(null);
            e.Effects = DragDropEffects.Copy;
            return;
        }

        var pos = e.GetPosition(this);
        var viewer = GetViewerAt(pos);
        SetDragOverViewer(viewer);
        e.Effects = viewer != null ? DragDropEffects.Copy : DragDropEffects.None;
    }

   private void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        var pos = e.GetPosition(this);
        var viewer = GetViewerAt(pos);

        // Clear drag-over highlight immediately
        SetDragOverViewer(null);

        // Folder drags open the directory-diffs window. (Drops over a pane are already handled
        // by the viewer-level PreviewDrop — this covers drops on non-viewer areas too.)
        if (DropContainsDirectory(e.Data))
        {
            HandleFolderDrop(e.Data);
            e.Handled = true;
            return;
        }

        if (viewer != null)
        {
            HandleFileDrop(e.Data, viewer);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Set the IsDragOver flag on the target viewer and clear it on any previous one.
    /// </summary>
    private void SetDragOverViewer(FastDiffViewer? viewer)
    {
        if (_dragOverViewer != null && _dragOverViewer != viewer)
            _dragOverViewer.IsDragOver = false;

        if (viewer != null)
            viewer.IsDragOver = true;

        _dragOverViewer = viewer;
    }

    /// <summary>
    /// Handle dropped files. If 2+ files are dropped, load the first two into the two panes.
    /// If only one file is dropped, load it into the hovered pane.
    /// </summary>
    private void HandleFileDrop(IDataObject data, FastDiffViewer? targetViewer)
    {
        var files = (string[])data.GetData(DataFormats.FileDrop)!;
        if (files.Length == 0) return;

        var (first, second) = _vm.IsSideBySide
            ? (LeftViewer, RightViewer)
            : (TopViewer, BottomViewer);

        if (files.Length >= 2)
        {
            // Two or more files — load first two into the two panes
            if (!_vm.PromptSaveIfNeeded()) return;
            _vm.LoadFileIntoPanel(files[0], true);   // left/top
            _vm.LoadFileIntoPanel(files[1], false);  // right/bottom
        }
        else if (files.Length == 1)
        {
            // Single file — load into the hovered pane
            if (!_vm.PromptSaveIfNeeded()) return;
            bool isFirstPanel = targetViewer != null && IsFirstPanel(targetViewer);
            _vm.LoadFileIntoPanel(files[0], isFirstPanel);
        }
    }

    /// <summary>
    /// True when the drag payload contains at least one existing directory. Checked during
    /// drag-enter/over (the FileDrop paths are available before the drop), so folder drags can
    /// be routed differently from file drags.
    /// </summary>
    private static bool DropContainsDirectory(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop)) return false;
        if (data.GetData(DataFormats.FileDrop) is not string[] paths) return false;
        return paths.Any(IsExistingDir);
    }

    private static bool IsExistingDir(string path)
    {
        try { return Directory.Exists(path); }
        catch { return false; }   // inaccessible/invalid entry — treat as a file
    }

    /// <summary>
    /// Folder drop(s) → directory-diffs window. The first dropped folder becomes the left side;
    /// a second dropped folder is the right side, or (with a single folder) the directory of the
    /// currently loaded right file. Mixed file+folder drops route here too — folders win.
    /// </summary>
    private void HandleFolderDrop(IDataObject data)
    {
        var paths = (string[])data.GetData(DataFormats.FileDrop)!;
        var dirs = new List<string>();
        foreach (var p in paths)
            if (IsExistingDir(p)) dirs.Add(p);
        if (dirs.Count == 0) return;

        string? rightDir = dirs.Count >= 2
            ? dirs[1]
            : _vm.RightFile != null ? Path.GetDirectoryName(_vm.RightFile.FilePath) : null;

        new DirectoryDiffsWindow(_vm, dirs[0], rightDir).Show();
    }

    private bool IsFirstPanel(FastDiffViewer viewer)
    {
        // Unified has a single pane — a dropped file fills whichever side is still empty.
        if (_vm.LayoutMode == LayoutMode.Unified)
            return _vm.LeftFile == null;

        return _vm.IsSideBySide
            ? viewer == LeftViewer
            : viewer == TopViewer;
    }
}
