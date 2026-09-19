using System.Text.Json.Serialization;
using System.Windows.Media;

namespace GameVault;

/// <summary>
/// The WPF-facing half of <see cref="Game"/>.
///
/// It lives in its own file so the data model keeps no dependency on the UI toolkit,
/// apart from the two decoded bitmaps the card templates bind to.
/// </summary>
public sealed partial class Game
{
    private ImageSource? _coverImage;
    private ImageSource? _heroImage;

    /// <summary>The decoded cover art, null until <see cref="ImageLoader"/> finishes with it.</summary>
    [JsonIgnore]
    public ImageSource? CoverImage
    {
        get => _coverImage;
        set { if (Set(ref _coverImage, value)) Raise(nameof(HasCover)); }
    }

    /// <summary>The decoded wide hero image, used by the detail view and the continue banner.</summary>
    [JsonIgnore]
    public ImageSource? HeroImage { get => _heroImage; set => Set(ref _heroImage, value); }

    /// <summary>@return whether decoded cover art is available to draw</summary>
    [JsonIgnore] public bool HasCover => _coverImage is not null;

    /// <summary>@return the single character shown when a game has no cover art</summary>
    [JsonIgnore] public string Initial
    {
        get
        {
            var name = Name?.Trim() ?? "";
            return name.Length == 0 ? "?" : name[..1].ToUpperInvariant();
        }
    }
}
