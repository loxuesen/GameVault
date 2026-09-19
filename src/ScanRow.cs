using System.Windows.Media;

namespace GameVault;

/// <summary>
/// One row of the Steam import list: an installed game plus the selection state and
/// thumbnail the list binds to.
/// </summary>
public sealed class ScanRow : Observable
{
    private bool _selected;
    private ImageSource? _thumb;

    /// <summary>@param game the installed game this row represents</summary>
    public ScanRow(InstalledGame game)
    {
        Game = game;
        _selected = !game.Imported;
    }

    public InstalledGame Game { get; }

    /// <summary>Whether the row is checked for import.</summary>
    public bool Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    /// <summary>Games already in the library cannot be selected again.</summary>
    public bool CanSelect => !Game.Imported;

    public string Name => Game.Name;
    public string StatusText => Game.StatusText;

    /// <summary>The store thumbnail, null until it has been fetched.</summary>
    public ImageSource? Thumb
    {
        get => _thumb;
        set => Set(ref _thumb, value);
    }
}
