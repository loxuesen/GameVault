using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace GameVault;

/// <summary>
/// The per-user "start with Windows" entry.
///
/// It lives in the current user's Run key rather than the machine-wide one, so enabling it
/// needs no administrator rights and only affects the person who turned it on.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "一切游戏管理家";

    /// <summary>Argument that tells a freshly started copy to go straight to the tray.</summary>
    public const string TrayArgument = "--tray";

    /// <summary>@return the running executable, which is what the Run entry has to launch</summary>
    public static string ExecutablePath =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? Assembly.GetEntryAssembly()?.Location ?? "";

    /// <summary>@return whether this user currently has the app registered to start with Windows</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception)
        {
            // An unreadable key means the feature is simply unavailable, not that it is on.
            return false;
        }
    }

    /// <summary>
    /// Adds or removes the start-with-Windows entry.
    ///
    /// @param enabled true to register the app, false to remove the entry
    /// @return an error message, or an empty string on success
    /// </summary>
    public static string Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return "无法打开注册表启动项";

            if (enabled)
            {
                var path = ExecutablePath;
                if (path.Length == 0) return "无法确定程序路径";
                // Quoted because the path may contain spaces; --tray starts it out of the way.
                key.SetValue(ValueName, $"\"{path}\" {TrayArgument}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return "";
        }
        catch (Exception error)
        {
            return error.Message;
        }
    }
}
