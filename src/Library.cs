using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace GameVault;

/// <summary>
/// The library document and user settings, kept in memory and written to disk on change.
///
/// The file format matches the JSON the bundled web version produces, so either
/// front end can open the same <c>data\library.json</c>.
/// </summary>
public sealed class Library
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // Without this, Chinese text is written as \uXXXX escapes and the file stops being readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The single library instance the whole application shares.</summary>
    public static Library Current { get; } = new();

    public LibraryDocument Document { get; private set; } = new();
    public AppSettings Settings { get; private set; } = new();

    public IReadOnlyList<Game> Games => Document.Games;

    /// <summary>Raised after any change that the UI needs to reflect.</summary>
    public event Action? Changed;

    /// <summary>Reads the library and settings from disk, tolerating a missing file on first run.</summary>
    public void Load()
    {
        Paths.EnsureCreated();
        Document = Read<LibraryDocument>(Paths.LibraryFile) ?? new LibraryDocument();
        Settings = Read<AppSettings>(Paths.SettingsFile) ?? new AppSettings();
        foreach (var game in Document.Games) game.Normalize();
        Document.Categories = Document.Categories.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList();
        if (string.IsNullOrEmpty(Document.CreatedAt)) Document.CreatedAt = DateTime.UtcNow.ToString("o");
    }

    /// <summary>
    /// Reads one JSON document.
    ///
    /// @param file absolute path to read
    /// @return the parsed value, or null when the file is absent or unreadable
    /// </summary>
    private static T? Read<T>(string file) where T : class
    {
        try
        {
            if (!File.Exists(file)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(file));
        }
        catch (Exception error)
        {
            // A corrupt file must not be silently replaced; the user is told and keeps the original.
            System.Windows.MessageBox.Show(
                $"无法读取 {Path.GetFileName(file)}：\n\n{error.Message}\n\n程序将以空白数据继续，原文件不会被覆盖。",
                "一切游戏管理家", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return null;
        }
    }

    /// <summary>Writes the library through a temporary file so a crash cannot truncate it.</summary>
    public void Save()
    {
        Paths.EnsureCreated();
        WriteAtomic(Paths.LibraryFile, Document);
        Changed?.Invoke();
    }

    /// <summary>Writes the settings document.</summary>
    public void SaveSettings()
    {
        Paths.EnsureCreated();
        WriteAtomic(Paths.SettingsFile, Settings);
    }

    private static void WriteAtomic<T>(string file, T value)
    {
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, file, overwrite: true);
    }

    /// <summary>@param id game id @return the game, or null when it is not in the library</summary>
    public Game? Find(string id) => Document.Games.FirstOrDefault(g => g.Id == id);

    /// <summary>Adds a game and persists the library.</summary>
    public void Add(Game game)
    {
        game.Normalize();
        Document.Games.Add(game);
        foreach (var category in game.Categories) EnsureCategory(category);
        Save();
    }

    /// <summary>Removes a game, its stored images, and any category it left empty.</summary>
    public void Remove(Game game)
    {
        Document.Games.Remove(game);
        DeleteMedia(game.Cover);
        DeleteMedia(game.Hero);
        foreach (var shot in game.Screenshots) DeleteMedia(shot);
        Document.Categories = Document.Categories
            .Where(c => Document.Games.Any(g => g.Categories.Contains(c)))
            .ToList();
        Save();
    }

    /// <summary>Deletes a stored media file, ignoring references outside the data directory.</summary>
    public void DeleteMedia(string? relative)
    {
        var full = Paths.Media(relative);
        if (full.Length == 0) return;
        try
        {
            if (File.Exists(full)) File.Delete(full);
        }
        catch (IOException)
        {
            // The file may still be held open by a decoded cover; leaving it is harmless.
        }
    }

    /// <summary>
    /// Deletes cover and screenshot files that no game references.
    ///
    /// A dialog downloads artwork as soon as it is fetched, so cancelling the dialog leaves
    /// the file behind with nothing pointing at it. Sweeping at startup keeps the folder from
    /// growing without bound; anything still in use is re-downloadable if it is ever lost.
    /// </summary>
    public void RemoveOrphanedMedia()
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in Document.Games)
        {
            if (game.Cover.Length > 0) referenced.Add(game.Cover);
            if (game.Hero.Length > 0) referenced.Add(game.Hero);
            foreach (var shot in game.Screenshots) referenced.Add(shot);
        }
        // A wallpaper is referenced by the settings rather than by any game.
        if (Settings.Wallpaper.Length > 0) referenced.Add(Settings.Wallpaper);

        foreach (var folder in new[] { Paths.CoverDir, Paths.ShotDir, Paths.WallpaperDir })
        {
            if (!Directory.Exists(folder)) continue;
            var prefix = Path.GetFileName(folder);
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var relative = $"{prefix}/{Path.GetFileName(file)}";
                if (!referenced.Contains(relative)) DeleteMedia(relative);
            }
        }
    }

    /// <summary>Adds a category when it is not already present.</summary>
    public void EnsureCategory(string name)
    {
        var clean = name.Trim();
        if (clean.Length == 0 || Document.Categories.Contains(clean)) return;
        Document.Categories.Add(clean);
        Document.Categories.Sort(StringComparer.CurrentCulture);
    }

    /// <summary>Removes a category and detaches it from every game.</summary>
    public void RemoveCategory(string name)
    {
        Document.Categories.Remove(name);
        foreach (var game in Document.Games) game.Categories.Remove(name);
        Save();
    }

    /// <summary>Saves after a game's own fields changed.</summary>
    public void Touch(Game game)
    {
        game.UpdatedAt = DateTime.UtcNow.ToString("o");
        Save();
    }

    /// <summary>@return a new identifier for a game or a stored file</summary>
    public static string NewId() => Guid.NewGuid().ToString();
}
