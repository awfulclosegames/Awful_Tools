using System.Windows.Controls;

namespace AwfulDiff.Controls;

/// <summary>
/// Toolbar user control — uses ElementName bindings to MainWindowRoot so it always
/// resolves commands and properties from the window's DataContext regardless of
/// DataContext inheritance through the UserControl visual tree.
/// </summary>
public partial class DiffToolbar : UserControl
{
    public DiffToolbar()
    {
        InitializeComponent();
    }
}
