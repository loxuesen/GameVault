using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GameVault;

/// <summary>
/// A user-supplied background for the library window: a still image or a looping MP4.
///
/// Imported files are copied into <c>data\wallpapers\</c>; the file itself is checked before
/// it is accepted, because a background smaller than 1280x720 looks broken once it is
/// stretched across a desktop-sized window.
/// </summary>
public static class Wallpaper
{
    public const int MinimumWidth = 1280;
    public const int MinimumHeight = 720;

    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".gif" };

    /// <summary>@param path a file path @return whether the file is a still image this app accepts</summary>
    public static bool IsImage(string? path) => ImageExtensions.Contains(Path.GetExtension(path ?? ""));

    /// <summary>@param path a file path @return whether the file is an MP4 video</summary>
    public static bool IsVideo(string? path) => Path.GetExtension(path ?? "").Equals(".mp4", StringComparison.OrdinalIgnoreCase);

    /// <summary>@return the file dialog filter covering every supported wallpaper</summary>
    public static string FileFilter =>
        "图片或视频 (*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif;*.mp4)|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif;*.mp4|" +
        "图片 (*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif|" +
        "视频 (*.mp4)|*.mp4";

    /// <summary>
    /// Reads the pixel size of a candidate wallpaper.
    ///
    /// Images are measured from the decoded frame; videos are measured from the media
    /// pipeline, which only reports a size once the file has been opened.
    ///
    /// @param path the file to measure
    /// @return the pixel size
    /// </summary>
    public static async Task<(int Width, int Height)> MeasureAsync(string path)
    {
        if (IsVideo(path)) return await MeasureVideoAsync(path);
        return MeasureImage(path);
    }

    private static (int Width, int Height) MeasureImage(string path)
    {
        var decoder = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (decoder.Frames.Count == 0) throw new InvalidOperationException("无法读取这个图片文件");
        var frame = decoder.Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    private static async Task<(int Width, int Height)> MeasureVideoAsync(string path)
    {
        var player = new MediaPlayer();
        var opened = new TaskCompletionSource<bool>();
        player.MediaOpened += (_, _) => opened.TrySetResult(true);
        player.MediaFailed += (_, e) =>
            opened.TrySetException(new InvalidOperationException(e.ErrorException?.Message ?? "无法读取这个 MP4 文件"));
        player.Open(new Uri(path));

        // A file with an unsupported codec can hang the open rather than fail it.
        var finished = await Task.WhenAny(opened.Task, Task.Delay(10000));

        // MediaPlayer is dispatcher-affine: its properties may only be touched from the thread
        // that created it, which is not necessarily the thread this continuation resumes on.
        return player.Dispatcher.Invoke(() =>
        {
            try
            {
                if (finished != opened.Task)
                    throw new InvalidOperationException("读取视频信息超时，请确认它是标准的 H.264 编码 MP4");
                opened.Task.GetAwaiter().GetResult();
                return (player.NaturalVideoWidth, player.NaturalVideoHeight);
            }
            finally
            {
                player.Close();
            }
        });
    }

    /// <summary>
    /// Copies a chosen file into the data folder as the new background.
    ///
    /// The file name is unique so that a file the running window still holds open can be
    /// replaced; unreferenced wallpapers are swept away on the next start.
    ///
    /// @param source the file the user picked
    /// @return the stored relative path
    /// </summary>
    public static string Import(string source)
    {
        Paths.EnsureCreated();
        var extension = Path.GetExtension(source).ToLowerInvariant();
        var relative = $"wallpapers/{(IsVideo(source) ? "video" : "image")}_{DateTime.UtcNow.Ticks}{extension}";
        File.Copy(source, Paths.Media(relative), overwrite: true);
        return relative;
    }

    /// <summary>
    /// Validates a candidate file and copies it in.
    ///
    /// @param source the file the user picked
    /// @return the stored relative path
    /// </summary>
    public static async Task<string> ImportCheckedAsync(string source)
    {
        if (!IsImage(source) && !IsVideo(source))
            throw new InvalidOperationException("只支持 PNG / JPG / BMP / WEBP / GIF 图片和 MP4 视频");

        var (width, height) = await MeasureAsync(source);
        if (width < MinimumWidth || height < MinimumHeight)
        {
            throw new InvalidOperationException(
                $"分辨率太低：这张是 {width} × {height}，要求至少 {MinimumWidth} × {MinimumHeight}。");
        }
        return Import(source);
    }
}
