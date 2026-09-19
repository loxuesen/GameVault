using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace GameVault;

/// <summary>
/// Starting games, watching the processes this app launched, and measuring how long they run.
///
/// Steam titles launch through the <c>steam://</c> protocol so Steam keeps ownership of
/// DRM, updates and the overlay. Plain executables launch directly, which also yields a
/// process id the playtime tracker can watch.
///
/// A watched process is reported the moment it exits rather than at the next poll, so the
/// play controls flip back immediately. Both events are raised on the UI thread.
/// </summary>
public static class Launcher
{
    private sealed class Session
    {
        public int Pid { get; init; }
        public DateTime StartedAt { get; init; }
        public string Name { get; init; } = "";
    }

    // Written from the exit callback and read from the UI thread, so it must be concurrent.
    private static readonly ConcurrentDictionary<string, Session> Running = new();
    private static DispatcherTimer? _timer;

    /// <summary>Raised when a tracked process exits, carrying the game id and how long it ran.</summary>
    public static event Action<string, TimeSpan>? SessionEnded;

    /// <summary>Raised when the set of running games changes, so the UI can refresh.</summary>
    public static event Action? SessionsChanged;

    /// <summary>@param id game id @return whether this process launched the game and still sees it running</summary>
    public static bool IsRunning(string id) => Running.ContainsKey(id);

    /// <summary>@return how long each running game has been up</summary>
    public static IReadOnlyDictionary<string, TimeSpan> Elapsed()
    {
        var now = DateTime.UtcNow;
        return Running.ToDictionary(pair => pair.Key, pair => now - pair.Value.StartedAt);
    }

    /// <summary>
    /// Splits a command-line argument string into individual arguments, honoring quotes.
    ///
    /// @param input raw arguments typed by the user
    /// @return the individual arguments
    /// </summary>
    public static List<string> ParseArgs(string? input)
    {
        var arguments = new List<string>();
        if (string.IsNullOrWhiteSpace(input)) return arguments;
        foreach (Match match in System.Text.RegularExpressions.Regex.Matches(input, "\"[^\"]*\"|'[^']*'|\\S+"))
        {
            arguments.Add(match.Value.Trim('"', '\''));
        }
        return arguments;
    }

    /// <summary>
    /// Launches a game and, for direct executable launches, starts a play session.
    ///
    /// @param game the game to start
    /// @return the process id when one is tracked, or zero
    /// </summary>
    public static int Launch(Game game)
    {
        switch (game.LaunchType)
        {
            case "steam":
                if (string.IsNullOrWhiteSpace(game.AppId)) throw new InvalidOperationException("该游戏没有 Steam AppID，无法通过 Steam 启动");
                OpenExternal($"steam://rungameid/{game.AppId}");
                return 0;

            case "shell":
                if (string.IsNullOrWhiteSpace(game.ExePath)) throw new InvalidOperationException("该游戏没有设置启动路径");
                OpenExternal(game.ExePath);
                return 0;

            default:
                if (string.IsNullOrWhiteSpace(game.ExePath)) throw new InvalidOperationException("该游戏没有设置启动路径");
                var startInfo = new ProcessStartInfo
                {
                    FileName = game.ExePath,
                    Arguments = game.Args ?? "",
                    WorkingDirectory = string.IsNullOrWhiteSpace(game.WorkDir) ? Path.GetDirectoryName(game.ExePath) ?? "" : game.WorkDir,
                    UseShellExecute = true,
                };
                var process = Process.Start(startInfo) ?? throw new InvalidOperationException("启动失败：未能创建进程");
                Running[game.Id] = new Session { Pid = process.Id, StartedAt = DateTime.UtcNow, Name = game.Name };
                WatchExit(game.Id, process);
                SessionsChanged?.Invoke();
                return process.Id;
        }
    }

    /// <summary>Opens a path or protocol URL through the Windows shell.</summary>
    public static void OpenExternal(string target) =>
        Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });

    /// <summary>Reveals a file in File Explorer, selecting it when it is a file.</summary>
    public static void RevealInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{path}\"", UseShellExecute = true });
        }
        catch (Exception)
        {
            // Explorer is occasionally unavailable in restricted sessions; nothing to recover.
        }
    }

    /// <summary>Starts polling tracked processes for exit, on the UI dispatcher.</summary>
    public static void StartTracking()
    {
        if (_timer is not null) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    /// <summary>Stops the playtime poller.</summary>
    public static void StopTracking()
    {
        _timer?.Stop();
        _timer = null;
    }

    private static void Poll()
    {
        if (Running.IsEmpty) return;
        foreach (var pair in Running)
        {
            if (IsAlive(pair.Value.Pid)) continue;
            Complete(pair.Key);
        }
    }

    /// <summary>
    /// Reports a session as finished as soon as its process exits.
    ///
    /// The poller alone would leave the play control showing "结束运行" for up to a full
    /// interval after the user quit the game. The poller stays as the backstop for
    /// processes whose handle could not be attached to.
    ///
    /// @param gameId game whose process to watch
    /// @param process the process that was just started
    /// </summary>
    private static void WatchExit(string gameId, Process process)
    {
        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => Complete(gameId);
        }
        catch (Exception)
        {
            // The process exited before a handle could be attached; the poller will catch it.
        }
    }

    /// <summary>
    /// Ends a session exactly once, whoever noticed the exit first.
    ///
    /// @param gameId game whose session ended
    /// </summary>
    private static void Complete(string gameId)
    {
        if (!Running.TryRemove(gameId, out var session)) return;
        var elapsed = DateTime.UtcNow - session.StartedAt;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            // The exit callback runs on a thread pool thread, and the app may be closing.
            if (dispatcher.HasShutdownStarted) return;
            dispatcher.Invoke(() => Report(gameId, elapsed));
            return;
        }
        Report(gameId, elapsed);
    }

    private static void Report(string gameId, TimeSpan elapsed)
    {
        SessionEnded?.Invoke(gameId, elapsed);
        SessionsChanged?.Invoke();
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Force-stops a game and everything it spawned.</summary>
    public static async Task KillAsync(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        catch (Exception)
        {
            // The process already exited, or belongs to another user; both are fine here.
        }
    }

    /// <summary>Removes a session without reporting elapsed time, used when the user stops a game.</summary>
    public static TimeSpan Forget(string gameId)
    {
        if (!Running.Remove(gameId, out var session)) return TimeSpan.Zero;
        SessionsChanged?.Invoke();
        return DateTime.UtcNow - session.StartedAt;
    }

    /// <summary>@param gameId game id @return the watched process id, or zero</summary>
    public static int PidOf(string gameId) => Running.TryGetValue(gameId, out var session) ? session.Pid : 0;
}
