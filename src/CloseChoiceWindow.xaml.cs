using System.Windows;
using System.Windows.Input;

namespace GameVault;

/// <summary>What the user chose to do with the window.</summary>
public enum CloseChoice
{
    /// <summary>Leave the window open.</summary>
    Cancel,

    /// <summary>Quit the application.</summary>
    Exit,

    /// <summary>Keep running, hidden in the notification area.</summary>
    Tray,
}

/// <summary>
/// Asks what the close button should do, and offers to remember the answer.
/// </summary>
public partial class CloseChoiceWindow : Window
{
    private CloseChoiceWindow() => InitializeComponent();

    private CloseChoice Choice { get; set; } = CloseChoice.Cancel;

    private bool Remember => RememberBox.IsChecked == true;

    /// <summary>
    /// Prompts the user.
    ///
    /// @param owner the library window, used to centre the prompt
    /// @return the chosen action and whether the user asked not to be asked again
    /// </summary>
    public static (CloseChoice Choice, bool Remember) Ask(Window owner)
    {
        var dialog = new CloseChoiceWindow { Owner = owner };
        dialog.ShowDialog();
        return (dialog.Choice, dialog.Remember);
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseChoice.Exit;
        DialogResult = true;
    }

    private void Tray_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseChoice.Tray;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseChoice.Cancel;
        Close();
    }

    /// <summary>Moves the window when the user drags its header, which replaces the caption.</summary>
    private void Chrome_Drag(object sender, MouseButtonEventArgs e) => FramelessWindow.Drag(this, e);
}
