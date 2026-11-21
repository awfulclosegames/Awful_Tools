using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using AwfulDiff.Services;

namespace AwfulDiff;

/// <summary>
/// Pops up after "Compute CRC-32" from the hex context menu: shows the checksum over the
/// selected byte range (hex value selectable + copy button, decimal equivalent, and the
/// file-space range it covers).
/// </summary>
public partial class CrcDialog : Window
{
    private readonly string _hexValue;
    private readonly DispatcherTimer _flashTimer = new() { Interval = TimeSpan.FromMilliseconds(1200) };

    public CrcDialog(int startByte, int byteCount, uint crc)
    {
        InitializeComponent();

        _hexValue = crc.ToString("X8");
        RangeText.Text = $"0x{startByte:X} – 0x{startByte + byteCount - 1:X}";
        LengthText.Text = byteCount == 1 ? "1 byte" : $"{byteCount} bytes";
        HexBox.Text = _hexValue;
        DecimalText.Text = ((ulong)crc).ToString();

        // Clicking the value selects it all (read-only, so there is no caret placement to fight).
        HexBox.PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
                HexBox.SelectAll();
        };

        _flashTimer.Tick += (_, _) =>
        {
            _flashTimer.Stop();
            CopyBtn.Content = "Copy";
        };
    }

    private void Copy_Click(object? sender, RoutedEventArgs e)
    {
        if (!ClipboardHelper.TrySetText(_hexValue)) return;
        CopyBtn.Content = "✓ Copied";
        _flashTimer.Stop();
        _flashTimer.Start();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.ClickCount == 1)
            DragMove();
    }
}
