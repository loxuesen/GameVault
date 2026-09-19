namespace GameVault;

/// <summary>
/// Starting and stopping games, shared by the library window and the detail window
/// so both keep launch counts and playtime consistent.
/// </summary>
public static class GameActions
{
    /// <summary>
    /// Starts a game, or stops it when this process already tracks it.
    ///
    /// @param game the game to toggle
    /// @return a message to show the user, and whether it reports a failure
    /// </summary>
    public static async Task<(string Message, bool Error)> ToggleAsync(Game game)
    {
        if (Launcher.IsRunning(game.Id))
        {
            var pid = Launcher.PidOf(game.Id);
            if (pid != 0) await Launcher.KillAsync(pid);
            var elapsed = Launcher.Forget(game.Id);
            if (elapsed > TimeSpan.Zero)
            {
                game.PlaytimeMs += (long)elapsed.TotalMilliseconds;
                Library.Current.Touch(game);
            }
            return ($"已结束 {game.Name}", false);
        }

        if (game.LaunchType != "steam" && string.IsNullOrWhiteSpace(game.ExePath))
            return ("这个游戏还没有设置启动路径，请先编辑它填写程序位置。", true);

        try
        {
            Launcher.Launch(game);
            game.LaunchCount++;
            game.LastPlayedAt = DateTime.UtcNow.ToString("o");
            Library.Current.Touch(game);
            return ($"正在启动 {game.Name}", false);
        }
        catch (Exception error)
        {
            return ($"启动失败：{error.Message}", true);
        }
    }
}
