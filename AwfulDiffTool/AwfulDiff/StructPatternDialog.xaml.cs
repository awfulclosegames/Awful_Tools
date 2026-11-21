using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AwfulDiff.Models;
using AwfulDiff.Services;

namespace AwfulDiff;

/// <summary>
/// Manager for the hex inspector's saved struct patterns: a list of patterns on the left, and an
/// editor (name, endianness, ordered fields with type + array count) on the right. Save/Delete
/// persist immediately to settings.json (the app's load→mutate→save idiom), so the changes are
/// durable even if the main window is closed without a normal save cycle. The host calls
/// HexInspector.ReloadStructPatterns() after this dialog closes.
/// </summary>
public partial class StructPatternDialog : Window
{
    /// <summary>Working copy of the saved patterns (clones — disk is only touched on Save/Delete).</summary>
    private readonly List<HexStructPattern> _patterns;

    /// <summary>Fields of the pattern currently in the editor. Row controls are rebuilt on
    /// structural changes; text values are read back from the rows before Save.</summary>
    private readonly List<HexStructField> _fields = new();

    /// <summary>Last editor row that had keyboard focus — the target for ↑/↓ moves (the move
    /// buttons themselves steal focus, so live focus tracking at click time would miss).</summary>
    private int _focusedRow = -1;

    public StructPatternDialog()
    {
        InitializeComponent();

        _patterns = SettingsService.Load().HexStructPatterns.Select(p => p.Clone()).ToList();
        RefreshPatternList();
        LoadEditor(null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        base.OnKeyDown(e);
    }

    // ── Pattern list ──────────────────────────────────────────────

    private void RefreshPatternList()
    {
        PatternList.Items.Clear();
        foreach (var pattern in _patterns)
            PatternList.Items.Add(pattern.Name);
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        PatternList.SelectedIndex = -1;
        LoadEditor(null);
    }

    private void PatternList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PatternList.SelectedItem is string name)
            LoadEditor(_patterns.FirstOrDefault(p => p.Name == name));
    }

    // ── Editor population ─────────────────────────────────────────

    /// <summary>Loads a pattern (or a blank template) into the editor. Field instances are copied,
    /// so editing never mutates the saved copy until Save.</summary>
    private void LoadEditor(HexStructPattern? pattern)
    {
        TxtName.Text = pattern?.Name ?? "";
        EndiannessCombo.SelectedIndex = pattern?.BigEndian == true ? 1 : 0;

        _fields.Clear();
        if (pattern != null)
            foreach (var f in pattern.Fields)
                _fields.Add(new HexStructField { Name = f.Name, Type = f.Type, Count = f.Count });
        else
            _fields.Add(new HexStructField()); // one default row for a new pattern

        RebuildFieldRows();
        UpdateTotalSize();
    }

    private void RebuildFieldRows()
    {
        FieldsPanel.Children.Clear();
        for (int i = 0; i < _fields.Count; i++)
            FieldsPanel.Children.Add(BuildFieldRow(i));
    }

    /// <summary>One editor row: [name TextBox] [type ComboBox] [count TextBox] [× remove].</summary>
    private Grid BuildFieldRow(int index)
    {
        var field = _fields[index];

        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameBox = new TextBox
        {
            Text = field.Name,
            Height = 22,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Field name (optional — unnamed fields show as f0, f1, …)"
        };
        ThemeBox(nameBox);

        var typeCombo = new ComboBox
        {
            Height = 22,
            Margin = new Thickness(8, 0, 0, 0)
        };
        typeCombo.SetResourceReference(ComboBox.StyleProperty, "ThemedComboBoxStyle");
        foreach (var (display, _) in HexPrimitives.All)
            typeCombo.Items.Add(display);
        int typeIndex = 0;
        for (int i = 0; i < HexPrimitives.All.Length; i++)
        {
            if (string.Equals(HexPrimitives.All[i].Display, field.Type, StringComparison.OrdinalIgnoreCase))
            {
                typeIndex = i;
                break;
            }
        }
        typeCombo.SelectedIndex = typeIndex;

        var countBox = new TextBox
        {
            Text = field.Count.ToString(),
            Height = 22,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Array count (1 = single value)"
        };
        ThemeBox(countBox);

        var removeBtn = new Button
        {
            Content = "×",
            Width = 24,
            Height = 22,
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 13,
            Background = Brushes.Transparent
        };
        removeBtn.SetResourceReference(Button.ForegroundProperty, "TextSecondary");
        removeBtn.SetResourceReference(Button.BorderBrushProperty, "BorderSubtle");
        removeBtn.Click += (_, _) => RemoveField(index);

        Grid.SetColumn(nameBox, 0);
        Grid.SetColumn(typeCombo, 1);
        Grid.SetColumn(countBox, 2);
        Grid.SetColumn(removeBtn, 3);
        row.Children.Add(nameBox);
        row.Children.Add(typeCombo);
        row.Children.Add(countBox);
        row.Children.Add(removeBtn);

        // Remember which row has focus so ↑/↓ can target it even after a button steals focus.
        int capturedIndex = index;
        row.PreviewGotKeyboardFocus += (_, _) => _focusedRow = capturedIndex;
        return row;
    }

    private static void ThemeBox(TextBox box)
    {
        box.SetResourceReference(TextBox.BackgroundProperty, "BgSurface");
        box.SetResourceReference(TextBox.ForegroundProperty, "TextPrimary");
        box.SetResourceReference(TextBox.BorderBrushProperty, "BorderSubtle");
    }

    /// <summary>Reads the current row controls' values back into _fields (called before Save).</summary>
    private void ReadFieldRows()
    {
        for (int i = 0; i < _fields.Count && i < FieldsPanel.Children.Count; i++)
        {
            if (FieldsPanel.Children[i] is not Grid row) continue;
            var nameBox = (TextBox)row.Children[0];
            var typeCombo = (ComboBox)row.Children[1];
            var countBox = (TextBox)row.Children[2];

            _fields[i].Name = nameBox.Text.Trim();
            if (typeCombo.SelectedItem is string type)
                _fields[i].Type = type;
            _fields[i].Count = int.TryParse(countBox.Text.Trim(), out int c) && c >= 1 ? c : 1;
        }
    }

    private void UpdateTotalSize()
    {
        ReadFieldRows();
        int total = _fields.Sum(f => f.ByteSize);
        TotalSizeText.Text = $"Total size: {total} byte{(total == 1 ? "" : "s")} per instance";
    }

    // ── Field structure edits ─────────────────────────────────────

    private void AddField_Click(object sender, RoutedEventArgs e)
    {
        ReadFieldRows(); // keep typed values in the existing rows across the rebuild
        _fields.Add(new HexStructField());
        RebuildFieldRows();
        UpdateTotalSize();
    }

    private void RemoveField(int index)
    {
        if (index < 0 || index >= _fields.Count) return;
        ReadFieldRows();
        _fields.RemoveAt(index);
        if (_fields.Count == 0)
            _fields.Add(new HexStructField()); // keep at least one row in the editor
        RebuildFieldRows();
        UpdateTotalSize();
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(+1);

    private void MoveSelected(int delta)
    {
        int index = _focusedRow;
        if (index < 0 || index >= _fields.Count) return;

        int target = index + delta;
        if (target < 0 || target >= _fields.Count) return;
        ReadFieldRows();
        (_fields[index], _fields[target]) = (_fields[target], _fields[index]);
        _focusedRow = target; // keep the move targeted at the row we just moved
        RebuildFieldRows();
        UpdateTotalSize();
    }

    // ── Save / Delete / Close ─────────────────────────────────────

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ReadFieldRows();
        var pattern = new HexStructPattern
        {
            Name = TxtName.Text.Trim(),
            BigEndian = EndiannessCombo.SelectedIndex == 1,
            Fields = _fields.Select(f => new HexStructField { Name = f.Name, Type = f.Type, Count = f.Count }).ToList()
        };

        string? error = pattern.Validate();
        if (error != null)
        {
            MessageBox.Show(this, error, "Hex Struct Patterns", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Upsert by case-insensitive name (editing a pattern keeps its list position).
        int existing = _patterns.FindIndex(p => string.Equals(p.Name, pattern.Name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) _patterns[existing] = pattern;
        else _patterns.Add(pattern);

        Persist();
        RefreshPatternList();
        PatternList.SelectedItem = pattern.Name; // re-select (editor keeps its contents)
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (PatternList.SelectedItem is not string name) return;
        _patterns.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        Persist();
        RefreshPatternList(); // clears selection → no SelectionChanged side effects
        LoadEditor(null);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.ClickCount == 1)
            DragMove();
    }

    /// <summary>Persists the working copy to settings.json via the app's load→mutate→save idiom.</summary>
    private void Persist()
    {
        var s = SettingsService.Load();
        s.HexStructPatterns = _patterns;
        SettingsService.Save(s);
    }
}
