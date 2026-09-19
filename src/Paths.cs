namespace GameVault;

/// <summary>
/// Every path the application reads or writes.
///
/// A portable copy keeps all of its state in <c>data\</c> beside the executable, so the whole
/// folder can be moved or backed up as a unit. An installed copy cannot do that: the installer
/// puts it under Program Files, which an ordinary user may not write to. In that case the data
/// folder moves to the per-user application data directory instead.
/// </summary>
public static class Paths
{
    public static string BaseDir { get; } = AppContext.BaseDirectory;

    /// <summary>True when state is kept beside the executable rather than in the user profile.</summary>
    public static bool IsPortable { get; }

    public static string DataDir { get; }
    public static string CoverDir { get; }
    public static string ShotDir { get; }
    public static string WallpaperDir { get; }
    public static string LibraryFile { get; }
    public static string SettingsFile { get; }
    public static string AppListFile { get; }

    static Paths()
    {
        var beside = Path.Combine(BaseDir, "data");
        IsPortable = CanKeepDataBeside(beside);
        DataDir = IsPortable
            ? beside
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "一切游戏管理家", "data");

        CoverDir = Path.Combine(DataDir, "covers");
        ShotDir = Path.Combine(DataDir, "shots");
        WallpaperDir = Path.Combine(DataDir, "wallpapers");
        LibraryFile = Path.Combine(DataDir, "library.json");
        SettingsFile = Path.Combine(DataDir, "settings.json");
        AppListFile = Path.Combine(DataDir, "applist.json");
    }

    /// <summary>
    /// Decides whether the executable's own folder can hold the library.
    ///
    /// Program Files is rejected outright rather than probed, because an administrator running
    /// the installed app would otherwise succeed at the probe and end up with a second, separate
    /// library from the one an ordinary user sees.
    ///
    /// @param directory the candidate data folder beside the executable
    /// @return whether state may be kept there
    /// </summary>
    private static bool CanKeepDataBeside(string directory)
    {
        if (IsUnderProgramFiles(BaseDir)) return false;
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".write-probe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            // Read-only media, a locked-down folder, or no permission: fall back to the profile.
            return false;
        }
    }

    private static bool IsUnderProgramFiles(string path)
    {
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                 })
        {
            var root = Environment.GetFolderPath(folder);
            if (root.Length == 0) continue;
            if (path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Resolves a stored relative media path such as <c>covers/550.jpg</c>.
    ///
    /// @param relative the stored path, which may be empty
    /// @return the absolute path, or an empty string when there is no media
    /// </summary>
    public static string Media(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return "";
        var full = Path.GetFullPath(Path.Combine(DataDir, relative.Replace('/', Path.DirectorySeparatorChar)));
        // A stored path must stay inside data\; anything else is treated as missing.
        return full.StartsWith(Path.GetFullPath(DataDir), StringComparison.OrdinalIgnoreCase) ? full : "";
    }

    /// <summary>Creates the data directories if they do not exist yet.</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(CoverDir);
        Directory.CreateDirectory(ShotDir);
        Directory.CreateDirectory(WallpaperDir);
    }
}
