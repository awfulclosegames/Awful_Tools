using System.Windows;
using System.Windows.Media;
using AwfulDiff.Services;

namespace AwfulDiff;

/// <summary>
/// Small themed colour picker: live preview, strict hex field (#RRGGBB / #AARRGGBB) and
/// R/G/B sliders, kept in sync both ways. The canonical output is SelectedHex ("#RRGGBB",
/// alpha dropped — the diff viewer tints backgrounds itself).
/// </summary>
public partial class ColorPickerDialog : Window
{
    private bool _syncing;

    /// <summary>Chosen colour as "#RRGGBB" (valid when DialogResult is true).</summary>
    public string SelectedHex { get; private set; } = "#000000";

    public ColorPickerDialog(Color initial)
    {
        InitializeComponent();

        _syncing = true;
        SlR.Value = initial.R;
        SlG.Value = initial.G;
        SlB.Value = initial.B;
        HexBox.Text = ToHex(initial);
        _syncing = false;
        UpdateLabelsAndPreview();
    }

    private Color CurrentColor => Color.FromRgb((byte)SlR.Value, (byte)SlG.Value, (byte)SlB.Value);

    private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private void UpdateLabelsAndPreview()
    {
        ValR.Text = ((int)SlR.Value).ToString();
        ValG.Text = ((int)SlG.Value).ToString();
        ValB.Text = ((int)SlB.Value).ToString();
        PreviewBox.Background = new SolidColorBrush(CurrentColor);
    }

    private void Channel_ValueChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;
        UpdateLabelsAndPreview();

        // Keep the hex field in step unless the user is mid-edit with an invalid value.
        _syncing = true;
        HexBox.Text = ToHex(CurrentColor);
        _syncing = false;
    }

    private void HexBox_TextChanged(object? sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_syncing) return;

        // Strict parse only — partial input ("#FF") and named colours are ignored until valid.
        if (!DiffViewerTheme.TryParseHex(HexBox.Text, out var c)) return;

        _syncing = true;
        SlR.Value = c.R;
        SlG.Value = c.G;
        SlB.Value = c.B;
        _syncing = false;
        UpdateLabelsAndPreview();
    }

    // ── Buttons / title bar ───────────────────────────────────────────────────

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        SelectedHex = ToHex(CurrentColor);
        DialogResult = true;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => DialogResult = false;

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed && e.ClickCount == 1)
            DragMove();
    }
}
