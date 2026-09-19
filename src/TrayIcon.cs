using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace GameVault;

/// <summary>
/// The notification-area icon.
///
/// It is what keeps the app reachable once its window is closed to the background, and it
/// gives the user a way back in and a way to really quit.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Icon _ownedIcon;
    private bool _notified;

    /// <summary>Raised when the user asks for the window back.</summary>
    public event Action? ShowRequested;

    /// <summary>Raised when the user asks the application to quit.</summary>
    public event Action? ExitRequested;

    public TrayIcon()
    {
        _ownedIcon = LoadIcon();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示主界面", null, (_, _) => ShowRequested?.Invoke());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke());

        _icon = new Forms.NotifyIcon
        {
            Icon = _ownedIcon,
            Text = "一切游戏管理家",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke();
    }

    /// <summary>@return the application icon, at the size the notification area draws</summary>
    private static Icon LoadIcon()
    {
        var stream = Application.GetResourceStream(new Uri("app.ico", UriKind.Relative))?.Stream
                     ?? throw new InvalidOperationException("找不到程序图标资源");
        using (stream)
        {
            return new Icon(stream, Forms.SystemInformation.SmallIconSize);
        }
    }

    /// <summary>
    /// Tells the user, once, that the app is still running after the window was closed.
    /// </summary>
    public void NotifyHidden()
    {
        if (_notified) return;
        _notified = true;
        try
        {
            _icon.ShowBalloonTip(4000, "一切游戏管理家",
                "程序已最小化到后台，双击托盘图标可以重新打开。", Forms.ToolTipIcon.Info);
        }
        catch (Exception)
        {
            // Balloon tips are unavailable on some systems; the tray icon itself still works.
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _ownedIcon.Dispose();
    }
}
