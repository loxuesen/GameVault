using System.Windows;
using System.Windows.Input;

namespace GameVault;

/// <summary>
/// Behaviour shared by the frameless dialogs.
///
/// Their windows draw their own title strip and close button, so Windows draws no caption.
/// That removes the drag the caption used to provide, which this puts back.
/// </summary>
public static class FramelessWindow
{
    /// <summary>
    /// Moves a frameless window to follow the mouse, if the left button is still down.
    ///
    /// @param window the window to move
    /// @param e the mouse event that started the drag
    /// </summary>
    public static void Drag(Window window, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        try
        {
            window.DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button was released between the event and the drag starting; nothing to move.
        }
    }
}
