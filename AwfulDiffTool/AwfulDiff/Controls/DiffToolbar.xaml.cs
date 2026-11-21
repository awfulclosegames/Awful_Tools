using System.Windows;
using System.Windows.Input;
using AwfulDiff.ViewModels;

namespace AwfulDiff.Controls;

public partial class DiffToolbar
{
    /// <summary>
    /// Opens the directory diff window. Holding Ctrl while clicking targets the parent folders
    /// of the two open files instead of the most recently compared pair (the default).
    /// </summary>
    private void DirectoriesButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        bool ctrlHeld = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        vm.OpenDirectories(ctrlHeld);
    }
}
