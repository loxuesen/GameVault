using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GameVault;

/// <summary>
/// Decodes cover and hero images off the UI thread.
///
/// Every bitmap is frozen before it is handed to the UI, which lets one decode be
/// shared across threads and keeps a large library from stalling the window.
/// </summary>
public static class ImageLoader
{
    /// <summary>
    /// Decodes an image from disk at a bounded width.
    ///
    /// @param path absolute file path
    /// @param decodeWidth width to decode to; 0 keeps the original size
    /// @return the frozen bitmap, or null when the file is missing or unreadable
    /// </summary>
    public static BitmapImage? FromFile(string path, int decodeWidth)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            // OnLoad releases the file handle immediately, so covers can be replaced later.
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            // A half-written or unsupported image is treated as "no cover".
            return null;
        }
    }

    /// <summary>
    /// Decodes an image from a byte buffer, used for images fetched over the network.
    ///
    /// @param bytes the encoded image
    /// @param decodeWidth width to decode to; 0 keeps the original size
    /// @return the frozen bitmap, or null when the bytes are not a usable image
    /// </summary>
    public static BitmapImage? FromBytes(byte[] bytes, int decodeWidth)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads and decodes a remote thumbnail, such as a Steam search hit.
    ///
    /// @param url absolute image URL
    /// @param decodeWidth width to decode to
    /// @return the frozen bitmap, or null when the download failed
    /// </summary>
    public static async Task<BitmapImage?> FromUrlAsync(string url, int decodeWidth)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            return FromBytes(await SteamApi.GetBytesAsync(url, 15000), decodeWidth);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Loads cover art for a set of games in parallel, assigning each bitmap on the UI thread.
    ///
    /// @param games games whose covers should be loaded
    /// @param decodeWidth width to decode covers to
    /// @param heroWidth width to decode heroes to; 0 loads covers only
    /// </summary>
    public static Task LoadCoversAsync(IReadOnlyList<Game> games, int decodeWidth = 360, int heroWidth = 0)
    {
        var pending = games.Where(g => g.CoverImage is null || (heroWidth > 0 && !string.IsNullOrEmpty(g.Hero) && g.HeroImage is null)).ToList();
        if (pending.Count == 0) return Task.CompletedTask;

        return Task.Run(() =>
        {
            Parallel.ForEach(pending, new ParallelOptions { MaxDegreeOfParallelism = 6 }, game =>
            {
                var cover = game.CoverImage ?? FromFile(game.CoverFullPath, decodeWidth);
                var hero = heroWidth > 0 && game.HeroImage is null && !string.IsNullOrEmpty(game.Hero)
                    ? FromFile(game.HeroFullPath, heroWidth)
                    : null;
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null) return;
                dispatcher.Invoke(() =>
                {
                    if (cover is not null && game.CoverImage is null) game.CoverImage = cover;
                    if (hero is not null) game.HeroImage = hero;
                });
            });
        });
    }
}
