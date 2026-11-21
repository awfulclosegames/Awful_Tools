using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AwfulDiff.Services;
using AwfulDiff.ViewModels;

namespace AwfulDiff;

/// <summary>
/// Directory-diffs window: recursively compares two folders and shows the result as a tree
/// (differences only by default — the toolbar toggles reveal unchanged files/folders).
/// Double-clicking a file row opens it, plus its counterpart from the other folder, in the
/// main diff window. Chrome mirrors ThreeWayMergeWindow.
/// </summary>
public partial class DirectoryDiffsWindow : Window
{
    private readonly DirectoryDiffsViewModel _vm;

    public DirectoryDiffsWindow(MainWindowViewModel mainVm, string? initialLeftDir, string? initialRightDir)
    {
        InitializeComponent();
        WorkAreaMaximize.Enforce(this); // maximize to the work area, not work area + frame border
        _vm = new DirectoryDiffsViewModel(mainVm, initialLeftDir, initialRightDir);
        DataContext = _vm;
        SetupRevealContextMenu();

        // The window is created outside MainWindow's startup path; apply the saved theme/font
        // scale now so brushes and sizing match whatever the user has configured.
        ThemeManager.ApplyTheme(ThemeManager.CurrentThemeName, ThemeManager.FontScale);

        ApplyWindowSettings();
    }

    // ── Window geometry persistence (same pattern as MainWindow) ───────────────

    private void ApplyWindowSettings()
    {
        var s = SettingsService.Load();

        // Restore position and size (skip if NaN — first run lets the XAML CenterScreen default apply)
        if (!double.IsNaN(s.DirDiffWindowLeft) && !double.IsNaN(s.DirDiffWindowTop))
        {
            // Only restore if the window would be at least partially visible on a monitor
            var workArea = SystemParameters.WorkArea;
            bool visible =
                s.DirDiffWindowLeft + s.DirDiffWindowWidth > workArea.Left &&
                s.DirDiffWindowLeft < workArea.Right &&
                s.DirDiffWindowTop + s.DirDiffWindowHeight > workArea.Top &&
                s.DirDiffWindowTop < workArea.Bottom;

            if (visible)
            {
                // The XAML declares CenterScreen, which WPF applies at show time and would clobber
                // the restored position — switch to Manual so the saved Left/Top actually stick.
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = s.DirDiffWindowLeft;
                Top = s.DirDiffWindowTop;
                Width = Math.Max(700, s.DirDiffWindowWidth);
                Height = Math.Min(9000, s.DirDiffWindowHeight);
            }
        }

        // Restore window state LAST so the bounds above become the maximized window's restore
        // bounds — un-maximizing later lands on the previous normal size/position.
        if (s.DirDiffWindowState == "Maximized")
            WindowState = WindowState.Maximized;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        var s = SettingsService.Load();

        // When maximized, save the restore bounds so reopening restores the previous normal size
        if (WindowState == WindowState.Maximized)
        {
            s.DirDiffWindowWidth = RestoreBounds.Width;
            s.DirDiffWindowHeight = RestoreBounds.Height;
            s.DirDiffWindowLeft = RestoreBounds.Left;
            s.DirDiffWindowTop = RestoreBounds.Top;
        }
        else
        {
            s.DirDiffWindowWidth = Width;
            s.DirDiffWindowHeight = Height;
            s.DirDiffWindowLeft = Left;
            s.DirDiffWindowTop = Top;
        }

        s.DirDiffWindowState = WindowState == WindowState.Maximized ? "Maximized" : "Normal";

        SettingsService.Save(s);
        base.OnClosing(e);
    }

    // ── Toolbar: folder pickers (WPF has no folder dialog — WinForms' does) ─────

    private void BrowseLeft_Click(object sender, RoutedEventArgs e)
        => _vm.LeftPath = BrowseFolder(_vm.LeftPath) ?? _vm.LeftPath;

    private void BrowseRight_Click(object sender, RoutedEventArgs e)
        => _vm.RightPath = BrowseFolder(_vm.RightPath) ?? _vm.RightPath;

    private static string? BrowseFolder(string current)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select a folder to compare",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(current) ? current : ""
        };
        return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dlg.SelectedPath : null;
    }

    // ── Drag & drop: 1–2 directories from Explorer ───────────────────────────────

    private void Window_DragEnter(object sender, DragEventArgs e) => SetDropEffect(e);
    private void Window_DragOver(object sender, DragEventArgs e) => SetDropEffect(e);

    private static void SetDropEffect(DragEventArgs e)
    {
        e.Effects = DroppedDirectories(e.Data).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    // Tunneling PreviewDrop fires at the window root before any bubbling handler, no matter which
    // element WPF routes the drop to — a bubbling Drop on the zone Border is missed when the drop
    // lands on the path TextBox, so the targeted side is decided by hit-testing the drop point
    // instead of relying on the event's source. Drops outside both zones fall back to the view
    // model's default routing (empty side, left preferred).
    private void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        var dirs = DroppedDirectories(e.Data);
        if (dirs.Count == 0) return;   // not a folder drop — leave it for default handling

        Point p = e.GetPosition(this);
        bool? side = PointInZone(p, LeftDropZone) ? true : PointInZone(p, RightDropZone) ? false : null;
        _vm.ApplyDroppedDirectories(dirs.ToArray(), side: side);
        e.Handled = true;   // suppress any remaining Drop handlers in the bubble phase
    }

    private bool PointInZone(Point windowPoint, FrameworkElement zone)
    {
        if (zone.ActualWidth <= 0 || zone.ActualHeight <= 0) return false;
        var origin = zone.TranslatePoint(new Point(0, 0), this);
        return windowPoint.X >= origin.X && windowPoint.X < origin.X + zone.ActualWidth
            && windowPoint.Y >= origin.Y && windowPoint.Y < origin.Y + zone.ActualHeight;
    }

    private static List<string> DroppedDirectories(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] paths)
            return paths.Where(p =>
            {
                try { return Directory.Exists(p); }
                catch { return false; }   // inaccessible/invalid entry — treat as a file
            }).ToList();
        return new List<string>();
    }

    private void Swap_Click(object sender, RoutedEventArgs e) => _vm.SwapPaths();

    // ── Context menu: reveal the selected row's file/folder in Explorer ─────────

    private DirEntry? _contextEntry;
    private List<DirEntry>? _contextExpandTargets;   // directories expand/collapse-all applies to (null = disabled)
    private readonly ContextMenu _revealMenu = new();
    private readonly MenuItem _revealLeftItem = new() { Header = "◄ Show in Explorer (left)" };
    private readonly MenuItem _revealRightItem = new() { Header = "► Show in Explorer (right)" };
    private readonly MenuItem _expandAllItem = new() { Header = "Expand all" };
    private readonly MenuItem _collapseAllItem = new() { Header = "Collapse all" };

    private void SetupRevealContextMenu()
    {
        _revealLeftItem.Click += (s, e) => RevealInExplorer(_contextEntry?.PathLeft);
        _revealRightItem.Click += (s, e) => RevealInExplorer(_contextEntry?.PathRight);
        _expandAllItem.Click += (s, e) => ApplyExpandCollapse(expanded: true);
        _collapseAllItem.Click += (s, e) => ApplyExpandCollapse(expanded: false);
        _revealMenu.Items.Add(_revealLeftItem);
        _revealMenu.Items.Add(_revealRightItem);
        _revealMenu.Items.Add(new Separator());
        _revealMenu.Items.Add(_expandAllItem);
        _revealMenu.Items.Add(_collapseAllItem);
        Tree.ContextMenu = _revealMenu;

        // ContextMenuOpening is a BUBBLING FrameworkElement routed event raised on the right-clicked
        // element before the menu displays — subscribing on the Tree catches row clicks, and
        // handling it suppresses the menu (right-click on blank tree area). WPF has no Preview
        // (tunneling) variant of this event.
        Tree.ContextMenuOpening += OnTreeContextMenuOpening;
    }

    private void OnTreeContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var item = FindParent<TreeViewItem>(e.OriginalSource as DependencyObject);
        _contextEntry = item?.DataContext as DirEntry;
        if (_contextEntry == null) { e.Handled = true; return; }   // blank tree area — no menu
        _revealLeftItem.IsEnabled = _contextEntry.PathLeft != null;
        _revealRightItem.IsEnabled = _contextEntry.PathRight != null;

        // Expand/collapse-all applies to the directory under the cursor (or, for a file row, the
        // directory containing it) and everything beneath. A top-level file is "in" the diff root —
        // apply the action to all top-level directories (full list, so filter-hidden ones are covered).
        List<DirEntry>? targets = null;
        if (_contextEntry.Kind == DirEntryKind.Directory)
            targets = new List<DirEntry> { _contextEntry };
        else
        {
            var parent = item == null ? null : FindParent<TreeViewItem>(VisualTreeHelper.GetParent(item))?.DataContext as DirEntry;
            targets = parent != null
                ? new List<DirEntry> { parent }
                : _vm.FullRoot?.Where(e => e.Kind == DirEntryKind.Directory).ToList();
        }
        _contextExpandTargets = targets is { Count: > 0 } ? targets : null;
        _expandAllItem.IsEnabled = _contextExpandTargets != null;
        _collapseAllItem.IsEnabled = _contextExpandTargets != null;
    }

    private void ApplyExpandCollapse(bool expanded)
    {
        if (_contextExpandTargets == null) return;
        foreach (var dir in _contextExpandTargets)
            SetSubtreeExpanded(dir, expanded);
    }

    /// <summary>
    /// Flips IsExpanded on the given directory and every directory beneath it. The two-way binding
    /// pushes the change into realized rows, and containers generated later (when a collapsed
    /// subtree expands) pick up the model value at creation — so no visual-tree walking is needed.
    /// </summary>
    private static void SetSubtreeExpanded(DirEntry? dir, bool expanded)
    {
        if (dir == null) return;
        dir.IsExpanded = expanded;
        foreach (var child in dir.Children)
            if (child.Kind == DirEntryKind.Directory)
                SetSubtreeExpanded(child, expanded);
    }

    /// <summary>Opens Explorer showing the given path — /select for files, plain open for folders.</summary>
    private static void RevealInExplorer(string? path)
    {
        if (path == null) return;
        string args = Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    // ── Tree: double-click a file row → open the pair in the main window ────────

    private void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // OriginalSource is whatever element was clicked (often a TextBlock inside the row) —
        // walk up the visual tree to the owning TreeViewItem.
        var item = FindParent<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is DirEntry entry && entry.Kind == DirEntryKind.File)
            _vm.OpenInMain(entry);
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T match) return match;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    // ── Custom chrome (same pattern as ThreeWayMergeWindow) ─────────────────────

    private void WindowStateChanged()
    {
        bool maximized = WindowState == WindowState.Maximized;
        BtnMaximize.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        BtnRestore.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override void OnStateChanged(System.EventArgs e)
    {
        base.OnStateChanged(e);
        WindowStateChanged();
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
