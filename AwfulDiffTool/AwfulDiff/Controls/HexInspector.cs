using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AwfulDiff.Models;
using AwfulDiff.Services;

namespace AwfulDiff.Controls;

/// <summary>
/// Inspector panel for hex view mode. Displays selected bytes interpreted either as an array of
/// a chosen primitive type (int8 … double) or as an array of instances of a user-defined struct
/// pattern (named fields, per-pattern endianness — see HexStructPattern).
/// Two independent panels: left and right, each driven by its own viewer selection.
/// The right panel becomes editable when <see cref="SetEditable"/> is called with true (hex "edit"
/// mode): each element/field renders as a TextBox that commits on Enter/focus loss, plus an
/// ⊕ insert button. Commits surface via <see cref="RightEditRequested"/> — the host routes them
/// into the byte splice path (VM.ApplyRightByteEdit), which re-diffs and refreshes this panel.
/// </summary>
public partial class HexInspector : UserControl
{
    /// <summary>One entry in the type combo: a primitive, or a saved struct pattern.</summary>
    private sealed class TypeItem
    {
        public required string Display;      // primitive name or pattern name
        public int Size;                     // primitive size, or pattern.TotalSize
        public HexStructPattern? Pattern;    // null ⇒ primitive (always little-endian)
        public override string ToString() => Display;
    }

    private readonly List<TypeItem> _typeItems = new();

    /// <summary>Inline editing is capped — larger selections fall back to the read-only summary.</summary>
    private const int MaxInlineElements = 512;

    // Left panel state
    private byte[]? _leftBytes;
    private int _leftStartByte;
    private int _leftByteCount;

    // Right panel state
    private byte[]? _rightBytes;
    private int _rightStartByte;
    private int _rightByteCount;

    // Right panel editability + invalid-entry flash
    private bool _editable;
    private DispatcherTimer? _flashTimer;
    private TextBox? _flashTarget;

    public HexInspector()
    {
        InitializeComponent();

        RebuildTypeItems();

        TypeSelector.SelectedIndex = IndexByName("int32") ?? 0; // Default: int32 (by name — the table order may change)

        TypeSelector.SelectionChanged += (s, e) =>
        {
            // Items.Clear() during a combo rebuild raises this transiently with index -1.
            if (TypeSelector.SelectedIndex < 0) return;
            UpdateLeftDisplay();
            UpdateRightDisplay();
            TypeSizeChanged?.Invoke(TypeSize);
        };

        // Initialize both panels to empty
        LeftValuesText.Text = "No selection";
        UpdateRightDisplay();
    }

    /// <summary>
    /// Raised when the selected type (and thus type size) changes. For a struct pattern this is
    /// the pattern's TotalSize, so viewer selection snapping covers whole instances.
    /// </summary>
    public event Action<int>? TypeSizeChanged;

    /// <summary>
    /// Raised when the user commits a right-panel edit: replace [startByte, startByte+removeCount)
    /// with insertBytes. For struct patterns this is per-field (size = field element size). The
    /// host routes this into the VM's byte splice path.
    /// </summary>
    public event Action<int, int, byte[]>? RightEditRequested;

    /// <summary>Raised when the user asks to manage saved struct patterns ("⚙ structs…" button).</summary>
    public event Action? ManageStructsRequested;

    // Defensive clamp: the combo can be transiently out of sync with _typeItems while its items
    // are being cleared/rebuilt (e.g. SelectionChanged mid-rebuild).
    private TypeItem Current
    {
        get
        {
            int i = TypeSelector.SelectedIndex;
            if (i < 0 || i >= _typeItems.Count) i = 0;
            return _typeItems[i];
        }
    }

    /// <summary>
    /// Size in bytes of one element of the currently selected type (primitive size, or the
    /// struct pattern's total size).
    /// </summary>
    public int TypeSize => Current.Size;

    /// <summary>
    /// Display name of the currently selected type (primitive name or pattern name).
    /// </summary>
    public string TypeName => Current.Display;

    /// <summary>The selected struct pattern, or null when a primitive is selected.</summary>
    public HexStructPattern? SelectedPattern => Current.Pattern;

    private void ManageStructsButton_Click(object sender, RoutedEventArgs e)
        => ManageStructsRequested?.Invoke();

    /// <summary>
    /// Select a type by display name (primitive or pattern), case-insensitive. Unknown or empty
    /// names leave the current selection untouched. Fires TypeSizeChanged like a user selection,
    /// so callers should wire that event first.
    /// </summary>
    public void SetTypeByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        for (int i = 0; i < _typeItems.Count; i++)
        {
            if (!string.Equals(_typeItems[i].Display, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (TypeSelector.SelectedIndex != i) TypeSelector.SelectedIndex = i;
            return;
        }
    }

    /// <summary>
    /// Re-reads saved struct patterns from settings and rebuilds the type combo (primitives first).
    /// Preserves the current selection by display name — falling back to int32 if a selected
    /// pattern was deleted or renamed. Refreshes both panels and fires TypeSizeChanged when the
    /// effective size changed (including when WPF skips SelectionChanged for an unchanged index).
    /// </summary>
    public void ReloadStructPatterns()
    {
        string keep = Current.Display;

        RebuildTypeItems();

        int target = IndexByName(keep) ?? IndexByName("int32") ?? 0;

        if (TypeSelector.SelectedIndex == target)
        {
            // WPF does not fire SelectionChanged when the index is unchanged — refresh manually.
            UpdateLeftDisplay();
            UpdateRightDisplay();
            TypeSizeChanged?.Invoke(TypeSize);
        }
        else
        {
            TypeSelector.SelectedIndex = target; // SelectionChanged handler does the rest
        }
    }

    /// <summary>Rebuilds _typeItems and the combo: all primitives, then saved patterns (skipping
    /// invalid ones and names that collide with an existing entry).</summary>
    private void RebuildTypeItems()
    {
        _typeItems.Clear();
        foreach (var (display, size) in HexPrimitives.All)
            _typeItems.Add(new TypeItem { Display = display, Size = size });

        foreach (var pattern in SettingsService.Load().HexStructPatterns)
        {
            if (string.IsNullOrWhiteSpace(pattern.Name) || pattern.TotalSize == 0) continue;
            if (_typeItems.Any(t => string.Equals(t.Display, pattern.Name, StringComparison.OrdinalIgnoreCase)))
                continue; // name collision — keep the first entry
            _typeItems.Add(new TypeItem
            {
                Display = pattern.Name,
                Size = pattern.TotalSize,
                Pattern = pattern.Clone()
            });
        }

        TypeSelector.Items.Clear();
        foreach (var item in _typeItems)
            TypeSelector.Items.Add(item);
    }

    private int? IndexByName(string name)
    {
        for (int i = 0; i < _typeItems.Count; i++)
            if (string.Equals(_typeItems[i].Display, name, StringComparison.OrdinalIgnoreCase))
                return i;
        return null;
    }

    /// <summary>
    /// Enables/disables inline editing of the right panel (and shows/hides the ⊕ insert button).
    /// Call when "edit right" or hex mode toggles.
    /// </summary>
    public void SetEditable(bool editable)
    {
        if (_editable == editable) return;
        _editable = editable;
        InsertButton.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        UpdateRightDisplay();
    }

    // ── Left Panel ────────────────────────────────────────────────

    /// <summary>
    /// Set the byte data and range for the left panel. Call when selection changes in the left/top viewer.
    /// </summary>
    public void SetLeftSelection(byte[]? bytes, int startByte, int byteCount)
    {
        _leftBytes = bytes;
        _leftStartByte = startByte;
        _leftByteCount = byteCount;
        UpdateLeftDisplay();
    }

    /// <summary>
    /// Clear the left panel display.
    /// </summary>
    public void ClearLeft()
    {
        _leftBytes = null;
        _leftStartByte = 0;
        _leftByteCount = 0;
        LeftValuesText.Text = "No selection";
    }

    private void UpdateLeftDisplay()
    {
        LeftValuesText.Text = FormatValues(_leftBytes, _leftStartByte, _leftByteCount);
    }

    // ── Right Panel ───────────────────────────────────────────────

    /// <summary>
    /// Set the byte data and range for the right panel. Call when selection changes in the right/bottom viewer.
    /// </summary>
    public void SetRightSelection(byte[]? bytes, int startByte, int byteCount)
    {
        _rightBytes = bytes;
        _rightStartByte = startByte;
        _rightByteCount = byteCount;
        UpdateRightDisplay();
    }

    /// <summary>
    /// Clear the right panel display.
    /// </summary>
    public void ClearRight()
    {
        _rightBytes = null;
        _rightStartByte = 0;
        _rightByteCount = 0;
        UpdateRightDisplay();
    }

    private void UpdateRightDisplay()
    {
        RightValuesPanel.Children.Clear();

        if (_rightBytes == null || _rightByteCount == 0)
        {
            AddPlainText("No selection");
            return;
        }

        var pattern = SelectedPattern;
        if (pattern != null)
        {
            int instances = _rightByteCount / pattern.TotalSize;
            if (instances == 0)
            {
                AddPlainText($"Insufficient bytes for struct '{pattern.Name}' (need {pattern.TotalSize}, have {_rightByteCount})");
                return;
            }

            // Cap counts field rows (not instances) — that's what inline editing actually builds.
            int rowsPerInstance = pattern.Fields.Sum(f => HexPrimitives.IsKnown(f.Type) ? Math.Max(1, f.Count) : 0);
            int totalRows = instances * rowsPerInstance;
            if (totalRows > MaxInlineElements)
            {
                AddPlainText(FormatStructValues(_rightBytes, _rightStartByte, _rightByteCount, pattern));
                AddPlainText($"({totalRows} field rows — too many for inline edit)", dim: true);
                return;
            }

            int fieldOffset = 0;
            for (int inst = 0; inst < instances; inst++)
            {
                fieldOffset = 0;
                for (int fi = 0; fi < pattern.Fields.Count; fi++)
                {
                    var field = pattern.Fields[fi];
                    if (!HexPrimitives.TryGetSize(field.Type, out int primSize)) continue;
                    int count = Math.Max(1, field.Count);
                    for (int k = 0; k < count; k++)
                        AddRightFieldRow(field, fi, k, pattern,
                            _rightStartByte + inst * pattern.TotalSize + fieldOffset + k * primSize, primSize);
                    fieldOffset += count * primSize;
                }
            }
            return;
        }

        int elemSize = TypeSize;
        int numElements = _rightByteCount / elemSize;
        if (numElements == 0)
        {
            AddPlainText($"Insufficient bytes for {TypeName} (need {elemSize}, have {_rightByteCount})");
            return;
        }

        if (numElements > MaxInlineElements)
        {
            // Inline editing is capped — show the read-only summary plus a note.
            AddPlainText(FormatValues(_rightBytes, _rightStartByte, _rightByteCount));
            AddPlainText($"({numElements} elements — too many for inline edit)", dim: true);
            return;
        }

        for (int i = 0; i < numElements; i++)
            AddRightRow(i);
    }

    private void AddPlainText(string text, bool dim = false)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.NoWrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        if (dim) block.Opacity = 0.6;
        RightValuesPanel.Children.Add(block);
    }

    private void AddRightRow(int elementIndex)
    {
        int elemSize = TypeSize;
        int offset = _rightStartByte + elementIndex * elemSize;
        string valueStr = HexValueCodec.Decode(_rightBytes!, offset, TypeName, bigEndian: false);
        string hexStr = HexValueCodec.HexBytes(_rightBytes!, offset, elemSize);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 1, 0, 1),
            VerticalAlignment = VerticalAlignment.Center
        };

        var prefix = new TextBlock
        {
            Text = $"[{offset:X8}]",
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        prefix.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

        var box = CreateValueBox(valueStr);
        int capturedOffset = offset;
        WireCommit(box, () => CommitRightField(capturedOffset, elemSize, TypeName, bigEndian: false, box));

        var comment = new TextBlock
        {
            Text = $"// {hexStr}",
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        comment.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

        row.Children.Add(prefix);
        row.Children.Add(box);
        row.Children.Add(comment);
        RightValuesPanel.Children.Add(row);
    }

    /// <summary>
    /// One editable row for a struct field element: [offset] label TextBox // hex. Label is the
    /// field name (unnamed fields render as f{i}), suffixed [k] for array repetitions.
    /// </summary>
    private void AddRightFieldRow(HexStructField field, int fieldIndex, int rep, HexStructPattern pattern, int offset, int primSize)
    {
        string valueStr = HexValueCodec.Decode(_rightBytes!, offset, field.Type, pattern.BigEndian);
        string hexStr = HexValueCodec.HexBytes(_rightBytes!, offset, primSize);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 1, 0, 1),
            VerticalAlignment = VerticalAlignment.Center
        };

        var prefix = new TextBlock
        {
            Text = $"[{offset:X8}]",
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        prefix.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

        string label = FieldLabel(field, fieldIndex);
        if (Math.Max(1, field.Count) > 1) label += $"[{rep}]";
        var labelText = new TextBlock
        {
            Text = $"{label}:",
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        labelText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

        var box = CreateValueBox(valueStr);
        WireCommit(box, () => CommitRightField(offset, primSize, field.Type, pattern.BigEndian, box));

        var comment = new TextBlock
        {
            Text = $"// {hexStr}",
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        comment.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

        row.Children.Add(prefix);
        row.Children.Add(labelText);
        row.Children.Add(box);
        row.Children.Add(comment);
        RightValuesPanel.Children.Add(row);
    }

    private static string FieldLabel(HexStructField field, int fieldIndex)
        => string.IsNullOrWhiteSpace(field.Name) ? $"f{fieldIndex}" : field.Name;

    /// <summary>Themed value TextBox for an inspector row (shared by primitive and struct rows).</summary>
    private TextBox CreateValueBox(string text)
    {
        var box = new TextBox
        {
            Text = text,
            Width = 110,
            Height = 20,
            Padding = new Thickness(3, 0, 3, 0),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            IsReadOnly = !_editable
        };
        box.SetResourceReference(TextBox.BackgroundProperty, "BgSurface");
        box.SetResourceReference(TextBox.ForegroundProperty, "TextPrimary");
        box.SetResourceReference(TextBox.BorderBrushProperty, "BorderSubtle");

        // Seed Tag with the displayed value so a focus-loss without any edit is a no-op. Essential
        // for "char": its non-printable placeholder ("·") does not round-trip to the original byte,
        // so without this guard merely clicking away would rewrite the byte to 0xB7.
        box.Tag = text;
        return box;
    }

    /// <summary>Wires Enter / focus-loss on a value box to its commit action.</summary>
    private void WireCommit(TextBox box, Action commit)
    {
        box.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                commit();
            }
        };
        box.LostFocus += (s, e) => commit();
    }

    /// <summary>
    /// Commits a value typed into a row at byteOffset: parses it for the given type/byte order and,
    /// if valid and different from the current bytes, raises RightEditRequested. Parse failures
    /// flash the row's border red and keep focus/text for correction.
    /// </summary>
    private void CommitRightField(int byteOffset, int size, string typeName, bool bigEndian, TextBox box)
    {
        if (!_editable || _rightBytes == null) return;

        string text = box.Text.Trim();
        if (box.Tag is string lastCommitted && lastCommitted == text) return;  // already committed this value

        if (!HexValueCodec.TryEncode(text, typeName, bigEndian, out byte[] bytes))
        {
            FlashInvalid(box);
            return;
        }

        // Focus loss without a change — same bytes as currently displayed, nothing to do.
        if (bytes.AsSpan().SequenceEqual(_rightBytes.AsSpan(byteOffset, size)))
            return;

        box.Tag = text;
        RightEditRequested?.Invoke(byteOffset, size, bytes);
    }

    private void FlashInvalid(TextBox box)
    {
        box.BorderBrush = Brushes.Red;
        _flashTimer?.Stop();
        _flashTarget = box;
        _flashTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _flashTimer.Tick += (s, e) =>
        {
            _flashTimer!.Stop();
            // Re-establish the themed border reference (clears the red local value).
            _flashTarget?.SetResourceReference(TextBox.BorderBrushProperty, "BorderSubtle");
            _flashTarget = null;
        };
        _flashTimer.Start();
    }

    private void InsertButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_editable) return;
        // One zeroed element of the selected type at the selection start (or EOF when nothing is
        // selected). For a struct pattern this inserts one zeroed full-struct instance.
        int pos = _rightByteCount > 0 ? _rightStartByte : (_rightBytes?.Length ?? 0);
        RightEditRequested?.Invoke(pos, 0, new byte[TypeSize]);
    }

    // ── Backward compatibility (clears both sides) ────────────────

    /// <summary>
    /// Clear both inspector panels. Kept for backward compatibility.
    /// </summary>
    public void Clear()
    {
        ClearLeft();
        ClearRight();
    }

    /// <summary>
    /// SetSelection is kept for backward compat — sets the left panel.
    /// </summary>
    public void SetSelection(byte[]? bytes, int startByte, int byteCount)
    {
        SetLeftSelection(bytes, startByte, byteCount);
    }

    // ── Shared Formatting Logic ───────────────────────────────────

    private string FormatValues(byte[]? bytes, int startByte, int byteCount)
    {
        if (bytes == null || byteCount == 0)
            return "No selection";

        var pattern = SelectedPattern;
        if (pattern != null)
            return FormatStructValues(bytes, startByte, byteCount, pattern);

        int elemSize = TypeSize;
        int numElements = byteCount / elemSize;

        if (numElements == 0)
            return $"Insufficient bytes for {TypeName} (need {elemSize}, have {byteCount})";

        var lines = new string[numElements];
        for (int i = 0; i < numElements; i++)
        {
            int offset = startByte + i * elemSize;
            lines[i] = FormatElement(bytes, offset);
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Formats selected bytes as an array of struct instances: one line per field element,
    /// [offset]  label[k]:  value  // hex. Trailing bytes that don't fill a whole instance are ignored.
    /// </summary>
    private string FormatStructValues(byte[] bytes, int startByte, int byteCount, HexStructPattern pattern)
    {
        int instances = byteCount / pattern.TotalSize;
        if (instances == 0)
            return $"Insufficient bytes for struct '{pattern.Name}' (need {pattern.TotalSize}, have {byteCount})";

        var lines = new List<string>();
        for (int inst = 0; inst < instances; inst++)
        {
            int baseOffset = startByte + inst * pattern.TotalSize;
            int fieldOffset = 0;
            for (int fi = 0; fi < pattern.Fields.Count; fi++)
            {
                var field = pattern.Fields[fi];
                if (!HexPrimitives.TryGetSize(field.Type, out int primSize)) continue;
                int count = Math.Max(1, field.Count);
                string label = FieldLabel(field, fi);
                for (int k = 0; k < count; k++)
                {
                    int offset = baseOffset + fieldOffset + k * primSize;
                    string valueStr = HexValueCodec.Decode(bytes, offset, field.Type, pattern.BigEndian);
                    string hexStr = HexValueCodec.HexBytes(bytes, offset, primSize);
                    string display = count > 1 ? $"{label}[{k}]" : label;
                    lines.Add($"[{offset:X8}]  {display,-24}  {valueStr,-20}  // {hexStr}");
                }
                fieldOffset += count * primSize;
            }
        }

        return string.Join("\n", lines);
    }

    private string FormatElement(byte[] bytes, int byteOffset)
    {
        string valueStr = HexValueCodec.Decode(bytes, byteOffset, TypeName, bigEndian: false);
        string hexStr = HexValueCodec.HexBytes(bytes, byteOffset, TypeSize);
        return $"[{byteOffset:X8}]  {valueStr,-20}  // {hexStr}";
    }
}
