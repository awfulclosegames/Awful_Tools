using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using AwfulDiff.Services;

namespace AwfulDiff;

/// <summary>
/// Modal dialog that collects the three file paths for a 3-way merge:
/// the base file plus the two change files (A and B).
/// </summary>
public partial class MergeSetupDialog : Window
{
    private const string FileFilter =
        "Text Files|*.txt;*.cs;*.vb;*.xml;*.json;*.yaml;*.yml;*.md;*.ini;*.cfg" +
        "|Source Files|*.cs;*.vb;*.cpp;*.h;*.hpp;*.js;*.ts;*.py;*.java;*.rs;*.go" +
        "|Data Files|*.csv;*.sql;*.xml;*.json;*.yaml;*.yml;*.toml" +
        "|All Files|*.*";

    public string SelectedBase => TxtBase.Text.Trim();
    public string SelectedA => TxtA.Text.Trim();
    public string SelectedB => TxtB.Text.Trim();

    /// <summary>
    /// Creates the dialog. A and B are prefilled (typically from the files currently open
    /// in the 2-way diff window); the base file must always be chosen.
    /// </summary>
    private Border? _dragOverRow;
    private readonly Brush _rowHoverBorder;

    public MergeSetupDialog(string? prefillA = null, string? prefillB = null)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(prefillA)) TxtA.Text = prefillA!;
        if (!string.IsNullOrWhiteSpace(prefillB)) TxtB.Text = prefillB!;

        // Remembered size from a previous launch. First run keeps SizeToContent so the dialog
        // fits its content exactly, then freezes to Manual on Loaded — a SizeToContent window
        // snaps back to content size after any user resize, so it must be off for resizing to stick.
        var s = SettingsService.Load();
        if (s.MergeDialogWidth > 100 && s.MergeDialogHeight > 100)
        {
            SizeToContent = SizeToContent.Manual;
            Width = s.MergeDialogWidth;
            Height = s.MergeDialogHeight;
        }
        Loaded += (obj, e) => { if (SizeToContent != SizeToContent.Manual) SizeToContent = SizeToContent.Manual; };

        _rowHoverBorder = TryFindResource("TextAccent") as Brush ?? Brushes.DodgerBlue;
        AllowDrop = true;
        // WPF routes drag events through the hit-tested element, which must itself be a drop
        // target (AllowDrop=true) — enabling only the window is not enough. With the rows'
        // children (read-only path TextBoxes, buttons) left at the default false, a drag that
        // crosses them can leave the OLE session without a current target and the drop is then
        // silently ignored. Enable every element; the window-level PreviewDrop handler does all
        // the work and marks the event handled, so no child ever processes a drop itself.
        // (Same pattern as MainWindow.SetupDragDrop enabling its viewers.)
        EnableDropOn(this);
        PreviewDragEnter += OnWindowDrag;
        PreviewDragOver += OnWindowDrag;
        PreviewDragLeave += (s, e) => SetDragOverRow(null);
        PreviewDrop += OnWindowDrop;

        // Each row is an explicit drop target with its own preview handlers. Marking the drag
        // events handled at row level stops them reaching the read-only path TextBoxes, whose
        // internal text-drop handling would otherwise reject file drags (no-drop cursor) or
        // swallow the drop before it reaches the window-level handler.
        foreach (var row in new[] { RowBase, RowA, RowB })
        {
            row.AllowDrop = true;
            row.PreviewDragEnter += OnRowDrag;
            row.PreviewDragOver += OnRowDrag;
            row.PreviewDrop += OnRowDrop;
        }

        UpdateOkState();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed && e.ClickCount == 1)
            DragMove();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var tag = (sender as Button)?.Tag as string;
       var dlg = new OpenFileDialog { Filter = FileFilter };
        if (dlg.ShowDialog(this) == true)
        {
            SetField(tag, dlg.FileName);
            UpdateOkState();
        }
    }

    // ── Drag & drop ────────────────────────────────────────────────────────────

    /// <summary>Recursively sets AllowDrop=true on every element in the logical tree.</summary>
    private static void EnableDropOn(DependencyObject root)
    {
        if (root is UIElement ue) ue.AllowDrop = true;
        foreach (var child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject d) EnableDropOn(d);
    }

    /// <summary>
    /// Files can be dropped onto a specific row (fills that field) or anywhere else in the
    /// dialog (fills the empty fields Base → A → B in drop order). Window-level preview
    /// handlers plus geometry hit-testing keep the highlight stable over child elements.
    /// </summary>
    private void OnWindowDrag(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        SetDragOverRow(RowAt(e.GetPosition(this)));
        e.Effects = DragDropEffects.Copy;
    }

    private void OnRowDrag(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        SetDragOverRow(sender as Border);
        e.Effects = DragDropEffects.Copy;
        // Stop the event here so the read-only path TextBox under the cursor never sees it —
        // its internal text-drop handling would otherwise override the effect (no-drop) or
        // consume the drop.
        e.Handled = true;
    }

    private void OnRowDrop(object sender, DragEventArgs e)
    {
        if (sender is not Border row) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (files.Length == 0) return;

        SetField(row.Tag as string, files[0]);
        SetDragOverRow(null);
        UpdateOkState();
        e.Handled = true;
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        var row = RowAt(e.GetPosition(this));
        SetDragOverRow(null);

        string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (files.Length == 0) return;

        if (row != null)
        {
            SetField(row.Tag as string, files[0]);
            UpdateOkState();
            e.Handled = true;
        }
        else
        {
            int i = 0;
            foreach (var box in new[] { TxtBase, TxtA, TxtB })
                if (i < files.Length && string.IsNullOrWhiteSpace(box.Text))
                    box.Text = files[i++];
            // Only consume the drop if a file was actually placed — otherwise leave it
            // unhandled so a row-level handler can still pick it up.
            if (i > 0) { UpdateOkState(); e.Handled = true; }
        }
    }

    private Border? RowAt(Point position)
    {
        // Compare in window coordinates (DIPs) throughout. The previous PointToScreen approach
        // mixed physical screen pixels with DIP-based ActualWidth/Height, so at any DPI scaling
        // other than 100% most points over a row failed the bounds check and fell through to
        // the "fill empty fields" background path.
        foreach (var row in new[] { RowBase, RowA, RowB })
        {
            if (row.ActualWidth <= 0 || row.ActualHeight <= 0) continue;
            var origin = row.TransformToAncestor(this).Transform(new Point(0, 0));
            if (position.X >= origin.X && position.X < origin.X + row.ActualWidth &&
                position.Y >= origin.Y && position.Y < origin.Y + row.ActualHeight)
                return row;
        }
        return null;
    }

    private void SetDragOverRow(Border? row)
    {
        if (_dragOverRow == row) return;
        if (_dragOverRow != null) _dragOverRow.BorderBrush = Brushes.Transparent;
        if (row != null) row.BorderBrush = _rowHoverBorder;
        _dragOverRow = row;
    }

    /// <summary>Sets the path in the field identified by tag ("Base"/"A"/"B").</summary>
    private void SetField(string? tag, string path)
    {
        var box = tag switch
        {
            "Base" => TxtBase,
            "A" => TxtA,
            "B" => TxtB,
            _ => null
        };
        if (box != null) box.Text = path;
    }

    private void PathBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateOkState();

    /// <summary>OK is enabled only when all three paths are chosen.</summary>
    private void UpdateOkState()
    {
        BtnOk.IsEnabled = !string.IsNullOrWhiteSpace(SelectedBase)
                       && !string.IsNullOrWhiteSpace(SelectedA)
                       && !string.IsNullOrWhiteSpace(SelectedB);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!BtnOk.IsEnabled) return;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Persists the dialog size so the next launch opens at the user's chosen size.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Skip closes that happen before layout (ActualWidth still 0) — persisting 0×0 would be
        // meaningless and would keep the ctor on the SizeToContent first-run path forever.
        if (ActualWidth > 50 && ActualHeight > 50)
        {
            var s = SettingsService.Load();
            s.MergeDialogWidth = ActualWidth;
            s.MergeDialogHeight = ActualHeight;
            SettingsService.Save(s);
        }
        base.OnClosing(e);
    }
}
