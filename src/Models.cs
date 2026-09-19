using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace GameVault;

/// <summary>Notifies the UI when a value changes, so bindings update without a full reload.</summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>
/// One game in the library.
/// Field names match <c>data/library.json</c> exactly, so the desktop app and the
/// bundled local web version can open the same library file.
/// </summary>
public sealed partial class Game : Observable
{
    private string _name = "";
    private string _launchType = "exe";
    private string _exePath = "";
    private string _args = "";
    private string _workDir = "";
    private string _appId = "";
    private string _description = "";
    private string _detailed = "";
    private string _developer = "";
    private string _publisher = "";
    private string _releaseDate = "";
    private bool _favorite;
    private string _cover = "";
    private string _hero = "";
    private long _playtimeMs;
    private int _launchCount;
    private string _lastPlayedAt = "";
    private bool _isRunning;

    [JsonPropertyName("id")] public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get => _name; set => Set(ref _name, value ?? ""); }

    [JsonPropertyName("launchType")]
    public string LaunchType { get => _launchType; set => Set(ref _launchType, value ?? "exe"); }

    [JsonPropertyName("exePath")]
    public string ExePath { get => _exePath; set => Set(ref _exePath, value ?? ""); }

    [JsonPropertyName("args")]
    public string Args { get => _args; set => Set(ref _args, value ?? ""); }

    [JsonPropertyName("workDir")]
    public string WorkDir { get => _workDir; set => Set(ref _workDir, value ?? ""); }

    [JsonPropertyName("appid")]
    public string AppId { get => _appId; set => Set(ref _appId, value ?? ""); }

    [JsonPropertyName("description")]
    public string Description { get => _description; set => Set(ref _description, value ?? ""); }

    [JsonPropertyName("detailed")]
    public string Detailed { get => _detailed; set => Set(ref _detailed, value ?? ""); }

    [JsonPropertyName("developer")]
    public string Developer { get => _developer; set => Set(ref _developer, value ?? ""); }

    [JsonPropertyName("publisher")]
    public string Publisher { get => _publisher; set => Set(ref _publisher, value ?? ""); }

    [JsonPropertyName("releaseDate")]
    public string ReleaseDate { get => _releaseDate; set => Set(ref _releaseDate, value ?? ""); }

    [JsonPropertyName("favorite")]
    public bool Favorite { get => _favorite; set => Set(ref _favorite, value); }

    [JsonPropertyName("cover")]
    public string Cover { get => _cover; set { if (Set(ref _cover, value ?? "")) { CoverImage = null; Raise(nameof(CoverFullPath)); } } }

    [JsonPropertyName("hero")]
    public string Hero { get => _hero; set { if (Set(ref _hero, value ?? "")) { HeroImage = null; Raise(nameof(HeroFullPath)); } } }

    [JsonPropertyName("source")] public string Source { get; set; } = "manual";

    [JsonPropertyName("playtimeMs")]
    public long PlaytimeMs { get => _playtimeMs; set { if (Set(ref _playtimeMs, value)) Raise(nameof(PlaytimeText)); } }

    [JsonPropertyName("launchCount")]
    public int LaunchCount { get => _launchCount; set => Set(ref _launchCount, value); }

    [JsonPropertyName("lastPlayedAt")]
    public string LastPlayedAt { get => _lastPlayedAt; set { if (Set(ref _lastPlayedAt, value ?? "")) Raise(nameof(Subtitle)); } }

    [JsonPropertyName("addedAt")] public string AddedAt { get; set; } = "";
    [JsonPropertyName("updatedAt")] public string UpdatedAt { get; set; } = "";

    [JsonPropertyName("genres")] public List<string> Genres { get; set; } = new();
    [JsonPropertyName("categories")] public List<string> Categories { get; set; } = new();
    [JsonPropertyName("screenshots")] public List<string> Screenshots { get; set; } = new();

    /// <summary>Whether this process launched the game and still sees it running.</summary>
    [JsonIgnore]
    public bool IsRunning { get => _isRunning; set { if (Set(ref _isRunning, value)) Raise(nameof(PlayGlyph)); } }

    /// <summary>The play/pause glyph shown on the card, which depends on <see cref="IsRunning"/>.</summary>
    [JsonIgnore] public string PlayGlyph => IsRunning ? "■" : "▶";

    /// <summary>Human-readable total playtime, empty when the game has never been tracked.</summary>
    [JsonIgnore]
    public string PlaytimeText
    {
        get
        {
            if (PlaytimeMs < 60000) return "";
            var hours = PlaytimeMs / 3600000.0;
            return hours < 1 ? $"{PlaytimeMs / 60000} 分钟" : $"{hours:0.#} 小时";
        }
    }

    /// <summary>One-line summary shown under the cover: playtime, first category, first genre.</summary>
    [JsonIgnore]
    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(PlaytimeText)) parts.Add(PlaytimeText);
            if (Categories.Count > 0) parts.Add(Categories[0]);
            if (Genres.Count > 0) parts.Add(Genres[0]);
            if (parts.Count == 0) parts.Add(string.IsNullOrEmpty(LastPlayedAt) ? "尚未启动" : "已玩过");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Absolute path of the stored cover image, or empty when the game has none.</summary>
    [JsonIgnore] public string CoverFullPath => Paths.Media(Cover);

    /// <summary>Absolute path of the stored hero image, falling back to the cover.</summary>
    [JsonIgnore] public string HeroFullPath => Paths.Media(string.IsNullOrEmpty(Hero) ? Cover : Hero);

    /// <summary>
    /// @return whether the card shows everything a metadata refresh would fetch.
    /// A Steam app id is not required: games imported by hand never have one.
    /// </summary>
    [JsonIgnore]
    public bool IsComplete => !string.IsNullOrEmpty(Cover) && !string.IsNullOrEmpty(Description);

    /// <summary>Removes blank entries and duplicates from the list fields after loading.</summary>
    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Id)) Id = Guid.NewGuid().ToString();
        Genres = Clean(Genres);
        Categories = Clean(Categories);
        Screenshots = Clean(Screenshots);
        if (string.IsNullOrWhiteSpace(Name)) Name = "未命名游戏";
        if (string.IsNullOrWhiteSpace(AddedAt)) AddedAt = DateTime.UtcNow.ToString("o");
    }

    private static List<string> Clean(List<string>? values) =>
        values is null ? new List<string>() : values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct().ToList();
}

/// <summary>The whole library document as stored on disk.</summary>
public sealed class LibraryDocument
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");
    [JsonPropertyName("categories")] public List<string> Categories { get; set; } = new();
    [JsonPropertyName("games")] public List<Game> Games { get; set; } = new();
}

/// <summary>User settings: network proxy and optional API keys.</summary>
public sealed class AppSettings
{
    [JsonPropertyName("proxy")] public string Proxy { get; set; } = "";
    [JsonPropertyName("steamApiKey")] public string SteamApiKey { get; set; } = "";
    [JsonPropertyName("steamGridDbKey")] public string SteamGridDbKey { get; set; } = "";
    [JsonPropertyName("translateToken")] public string TranslateToken { get; set; } = "";
    [JsonPropertyName("timeout")] public int Timeout { get; set; } = 15000;

    /// <summary>Stored path of the custom background, or empty for the plain dark theme.</summary>
    [JsonPropertyName("wallpaper")] public string Wallpaper { get; set; } = "";

    /// <summary>Remembered main window size, so the library reopens the way the user left it.</summary>
    [JsonPropertyName("windowWidth")] public double WindowWidth { get; set; }
    [JsonPropertyName("windowHeight")] public double WindowHeight { get; set; }

    /// <summary>
    /// What the close button does: <c>ask</c> prompts, <c>exit</c> quits,
    /// <c>tray</c> keeps the app running in the notification area.
    /// </summary>
    [JsonPropertyName("closeAction")] public string CloseAction { get; set; } = "ask";

    /// <summary>@returns a copy, so a settings dialog can be cancelled without side effects.</summary>
    public AppSettings Clone() => new()
    {
        Proxy = Proxy,
        SteamApiKey = SteamApiKey,
        SteamGridDbKey = SteamGridDbKey,
        TranslateToken = TranslateToken,
        Timeout = Timeout,
        Wallpaper = Wallpaper,
        WindowWidth = WindowWidth,
        WindowHeight = WindowHeight,
        CloseAction = CloseAction,
    };
}

/// <summary>Result of one metadata-source reachability probe.</summary>
public sealed class ProbeResult
{
    public string Name { get; init; } = "";
    public bool Ok { get; init; }
    public long Ms { get; init; }
    public string Message { get; init; } = "";
    public string Icon => Ok ? "✅" : "❌";
}

/// <summary>Search hit from the Steam store.</summary>
public sealed class SteamSearchResult
{
    public string AppId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Image { get; init; } = "";
}

/// <summary>Metadata resolved for one Steam app.</summary>
public sealed class SteamDetails
{
    public string AppId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Detailed { get; init; } = "";
    public string Developer { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string ReleaseDate { get; init; } = "";
    public List<string> Genres { get; init; } = new();
}

/// <summary>An installed Steam title found by scanning the local Steam libraries.</summary>
public sealed class InstalledGame
{
    public string AppId { get; init; } = "";
    public string Name { get; init; } = "";
    public string InstallDir { get; init; } = "";
    public string LibraryRoot { get; init; } = "";
    public long LastPlayedMs { get; init; }
    public long SizeOnDisk { get; init; }
    public string Image => string.IsNullOrEmpty(AppId) ? "" : SteamApi.HeaderUrl(AppId);
    public bool Imported { get; set; }

    /// <summary>@returns the install size formatted for the import list.</summary>
    public string SizeText => SizeOnDisk <= 0 ? "" : $"{SizeOnDisk / 1e9:0.0} GB";

    /// <summary>@returns the label shown at the right of the import row.</summary>
    public string StatusText => Imported ? "已导入" : SizeText;
}
