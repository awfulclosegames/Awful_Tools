using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AwfulDiff.Services;

namespace AwfulDiff;

/// <summary>
/// Read-only window rendering the bundled user manual with a section index along the left.
/// Opened non-modally from the Settings dialog's Help button (single instance).
/// </summary>
public partial class ManualWindow : Window
{
    private readonly List<ManualSection> _sections;

    // While a jump is in flight, intermediate ScrollChanged events must not re-select the index.
    private bool _suppressIndexSync;
    private double _jumpTargetOffset = -1;
    // Set while the scroll handler updates the selection itself, so SelectionChanged doesn't
    // feed back into JumpToSection and yank the view back to the section top mid-scroll.
    private bool _syncingIndex;

    public ManualWindow()
    {
        InitializeComponent();
        WorkAreaMaximize.Enforce(this); // maximize to the work area, not work area + frame border

        var content = ManualMarkdown.Parse(ManualMarkdown.LoadEmbeddedManual(), anchor => ScrollToAnchor(anchor));
        _sections = content.Sections;
        DocViewer.Content = content.Root;
        LstIndex.ItemsSource = _sections;
        DocViewer.ScrollChanged += Scroller_ScrollChanged;

        WindowStateChanged();
        StateChanged += (s, e) => WindowStateChanged();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Highlight the first section without scrolling — the view stays at the top of the doc.
        if (LstIndex.SelectedIndex < 0)
        {
            _syncingIndex = true;
            try { LstIndex.SelectedIndex = 0; }
            finally { _syncingIndex = false; }
        }
    }

    // ── Index ↔ document sync ────────────────────────────────────────────────

    private void LstIndex_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingIndex || LstIndex.SelectedIndex < 0) return;
        JumpToSection(LstIndex.SelectedIndex);
    }

    private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        double offset = DocViewer.VerticalOffset;

        if (_suppressIndexSync)
        {
            // The target may exceed the max scrollable offset — clamp before comparing.
            if (Math.Abs(offset - Math.Min(_jumpTargetOffset, DocViewer.ScrollableHeight)) >= 2) return;
            _suppressIndexSync = false;
            _jumpTargetOffset = -1;
        }

        // Highlight the last section whose heading sits at or above the viewport top.
        // TranslatePoint is viewport-relative (it shifts as we scroll), so compare against 0.
        int current = -1;
        for (int s = 0; s < _sections.Count; s++)
        {
            double y = _sections[s].Element.TranslatePoint(new Point(0, 0), DocViewer).Y;
            if (y <= 8) current = s; else break;
        }
        if (current >= 0 && LstIndex.SelectedIndex != current)
        {
            _syncingIndex = true;
            try { LstIndex.SelectedIndex = current; }
            finally { _syncingIndex = false; }
        }
    }

    private void JumpToSection(int index)
    {
        if (index < 0 || index >= _sections.Count) return;
        LstIndex.SelectedIndex = index;

        // TranslatePoint is viewport-relative, so add the current offset to get document Y.
        double docY = _sections[index].Element.TranslatePoint(new Point(0, 0), DocViewer).Y + DocViewer.VerticalOffset;
        double y = Math.Max(0, docY - 12);
        if (Math.Abs(DocViewer.VerticalOffset - y) <= 2) return;   // already there — nothing to suppress
        _jumpTargetOffset = y;
        _suppressIndexSync = true;
        DocViewer.ScrollToVerticalOffset(y);
    }

    /// <summary>Called by in-document [text](#anchor) links from the parsed manual.</summary>
    private void ScrollToAnchor(string anchor)
    {
        int idx = _sections.FindIndex(s => ManualMarkdown.Slug(s.Title) == anchor);
        if (idx >= 0) JumpToSection(idx);
    }

    // ── Title bar / window controls (same pattern as MainWindow) ────────────

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

    private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnMaximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Maximized;

    private void BtnRestore_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Normal;

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
