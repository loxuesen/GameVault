using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace GameVault;

/// <summary>
/// Network and API-key settings, plus the background wallpaper and window size,
/// with a connection test that shows which metadata sources this machine can reach.
/// </summary>
public partial class SettingsWindow : Window
{
    private const string SteamKeyUrl = "https://steamcommunity.com/dev/apikey";
    private const string GridDbKeyUrl = "https://www.steamgriddb.com/profile/preferences";

    /// <summary>Common desktop resolutions, offered as one-click presets.</summary>
    private static readonly (string Label, double Width, double Height)[] Resolutions =
    {
        ("1280 × 720", 1280, 720),
        ("1600 × 900", 1600, 900),
        ("1920 × 1080", 1920, 1080),
        ("2560 × 1440（2K）", 2560, 1440),
        ("3440 × 1440（带鱼屏）", 3440, 1440),
        ("3840 × 2160（4K）", 3840, 2160),
    };

    /// <summary>@return the library window this dialog was opened from</summary>
    private MainWindow? Host => Owner as MainWindow;

    public SettingsWindow()
    {
        InitializeComponent();
        var settings = Library.Current.Settings;
        ProxyBox.Text = settings.Proxy;
        TimeoutBox.Text = settings.Timeout.ToString();
        SteamKeyBox.Text = settings.SteamApiKey;
        GridDbKeyBox.Text = settings.SteamGridDbKey;
        TranslateTokenBox.Text = settings.TranslateToken;

        BuildResolutionPresets();
        UpdateWallpaperInfo();
        UpdateResolutionInfo();
        UpdateStartupControls();
    }

    // ------------------------------------------------------------- background

    /// <summary>
    /// Reflects the close-button preference and the real state of the Windows startup entry.
    ///
    /// The registry is the source of truth for startup rather than a saved copy, so the
    /// checkbox stays correct even if the entry was removed outside this app.
    /// </summary>
    private void UpdateStartupControls()
    {
        var close = Library.Current.Settings.CloseAction;
        CloseAskBox.IsChecked = close == "ask";
        CloseTrayBox.IsChecked = close == "tray";
        CloseExitBox.IsChecked = close == "exit";
        if (CloseAskBox.IsChecked != true && CloseTrayBox.IsChecked != true && CloseExitBox.IsChecked != true)
        {
            CloseAskBox.IsChecked = true;
        }
        StartupBox.IsChecked = StartupRegistration.IsEnabled();
    }

    /// <summary>@return the close behaviour currently selected in the dialog</summary>
    private string ReadCloseAction() =>
        CloseTrayBox.IsChecked == true ? "tray"
        : CloseExitBox.IsChecked == true ? "exit"
        : "ask";

    /// <summary>Writes or removes the Windows startup entry as soon as the box is ticked.</summary>
    private void Startup_Toggled(object sender, RoutedEventArgs e)
    {
        var error = StartupRegistration.Apply(StartupBox.IsChecked == true);
        if (error.Length > 0)
        {
            MessageBox.Show(this, $"设置开机启动失败：{error}", "一切游戏管理家",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            StartupBox.IsChecked = StartupRegistration.IsEnabled();
        }
    }

    // ------------------------------------------------------------- wallpaper

    /// <summary>@return the imported file's size, or an empty string when nothing is set</summary>
    private void UpdateWallpaperInfo()
    {
        var path = Paths.Media(Library.Current.Settings.Wallpaper);
        if (path.Length == 0 || !File.Exists(path))
        {
            WallpaperInfo.Text = "当前：默认深色背景";
            ClearWallpaperButton.IsEnabled = false;
            return;
        }
        WallpaperInfo.Text = $"当前：{(Wallpaper.IsVideo(path) ? "MP4 动态壁纸" : "图片")} · {Path.GetFileName(path)}";
        ClearWallpaperButton.IsEnabled = true;
    }

    private async void PickWallpaper_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择背景图片或视频", Filter = Wallpaper.FileFilter };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var stored = await Wallpaper.ImportCheckedAsync(dialog.FileName);
            Library.Current.Settings.Wallpaper = stored;
            Library.Current.SaveSettings();
            // Apply first, so the window releases the previous file before it is swept away.
            Host?.ApplyWallpaper();
            Library.Current.RemoveOrphanedMedia();
            UpdateWallpaperInfo();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, $"导入失败：{error.Message}", "一切游戏管理家", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearWallpaper_Click(object sender, RoutedEventArgs e)
    {
        Library.Current.Settings.Wallpaper = "";
        Library.Current.SaveSettings();
        Host?.ApplyWallpaper();
        Library.Current.RemoveOrphanedMedia();
        UpdateWallpaperInfo();
    }

    // ------------------------------------------------------------- window size

    private void BuildResolutionPresets()
    {
        foreach (var (label, width, height) in Resolutions)
        {
            var button = new Button
            {
                Content = label,
                Style = (Style)FindResource("TinyButton"),
                Margin = new Thickness(0, 0, 6, 6),
                Tag = $"{width}x{height}",
            };
            button.Click += Resolution_Click;
            ResolutionPanel.Children.Add(button);
        }

        var fill = new Button
        {
            Content = "适应屏幕",
            Style = (Style)FindResource("TinyButton"),
            Margin = new Thickness(0, 0, 6, 6),
            Tag = "max",
        };
        fill.Click += Resolution_Click;
        ResolutionPanel.Children.Add(fill);
    }

    private void Resolution_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || Host is null) return;
        if (tag == "max")
        {
            Host.ApplyResolution(0, 0, maximize: true);
        }
        else
        {
            var parts = tag.Split('x');
            Host.ApplyResolution(double.Parse(parts[0]), double.Parse(parts[1]), maximize: false);
        }
        UpdateResolutionInfo();
    }

    private void UpdateResolutionInfo()
    {
        if (Host is null)
        {
            ResolutionInfo.Text = "窗口可以拖边框自由调整，大小会被记住。";
            return;
        }
        var (width, height) = Host.CurrentPixelSize();
        ResolutionInfo.Text = $"当前窗口：{width:0} × {height:0} 像素。" +
                              "也可以随时拖窗口边框自由调整，大小会自动记住。";
    }

    /// <summary>@return the timeout typed into the form, clamped to the supported range</summary>
    private int ReadTimeout()
    {
        if (!int.TryParse(TimeoutBox.Text.Trim(), out var value)) return Library.Current.Settings.Timeout;
        return Math.Clamp(value, 4000, 60000);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var proxy = ProxyBox.Text.Trim();
        if (!SteamApi.IsValidProxy(proxy))
        {
            MessageBox.Show(this, "代理地址格式不正确，例如 http://127.0.0.1:7890", "一切游戏管理家",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Library.Current.Settings.Proxy = proxy;
        Library.Current.Settings.Timeout = ReadTimeout();
        Library.Current.Settings.SteamApiKey = SteamKeyBox.Text.Trim();
        Library.Current.Settings.SteamGridDbKey = GridDbKeyBox.Text.Trim();
        Library.Current.Settings.TranslateToken = TranslateTokenBox.Text.Trim();
        Library.Current.Settings.CloseAction = ReadCloseAction();
        Library.Current.SaveSettings();
        SteamApi.ApplyProxy(proxy);

        DialogResult = true;
        Close();
    }

    /// <summary>
    /// Probes every metadata source using the settings currently typed in, then restores
    /// the saved proxy so testing never changes what the app is actually using.
    /// </summary>
    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var proxy = ProxyBox.Text.Trim();
        if (!SteamApi.IsValidProxy(proxy))
        {
            MessageBox.Show(this, "代理地址格式不正确，例如 http://127.0.0.1:7890", "一切游戏管理家",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var previousProxy = Library.Current.Settings.Proxy;
        TestButton.IsEnabled = false;
        TestButton.Content = "测试中…";
        ResultList.ItemsSource = new List<ProbeResult>
        {
            new() { Name = "正在测试…", Message = "每项最多等待 8 秒，请稍候" },
        };

        SteamApi.ApplyProxy(proxy);
        try
        {
            var results = new List<ProbeResult>
            {
                await SteamApi.ProbeAsync("Steam 商店（搜索与简介）",
                    "https://store.steampowered.com/api/appdetails?appids=550&l=schinese"),
                await SteamApi.ProbeAsync("Steam 图片 CDN（封面下载）",
                    "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/550/header.jpg"),
                await SteamApi.ProbeAsync("Steam Web API（AppID 解析）",
                    "https://api.steampowered.com/ISteamWebAPIUtil/GetServerInfo/v1/"),
            };

            var gridKey = GridDbKeyBox.Text.Trim();
            if (gridKey.Length > 0)
            {
                var headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {gridKey}" };
                results.Add(await SteamApi.ProbeAsync("SteamGridDB（非 Steam 游戏封面）",
                    "https://www.steamgriddb.com/api/v2/search/autocomplete/portal", headers));
            }
            else
            {
                results.Add(new ProbeResult { Name = "SteamGridDB（非 Steam 游戏封面）", Ok = false, Message = "未填写 API Key，已跳过" });
            }

            ResultList.ItemsSource = results;
        }
        catch (Exception error)
        {
            ResultList.ItemsSource = new List<ProbeResult>
            {
                new() { Name = "测试失败", Message = error.Message, Ok = false },
            };
        }
        finally
        {
            SteamApi.ApplyProxy(previousProxy);
            TestButton.IsEnabled = true;
            TestButton.Content = "测试连接";
        }
    }

    private void OpenSteamKey_Click(object sender, RoutedEventArgs e) => OpenUrl(SteamKeyUrl);

    private void OpenGridDbKey_Click(object sender, RoutedEventArgs e) => OpenUrl(GridDbKeyUrl);

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception)
        {
            // No default browser association; the URL is also documented in README.
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Moves the window when the user drags its header, which replaces the caption.</summary>
    private void Chrome_Drag(object sender, MouseButtonEventArgs e) => FramelessWindow.Drag(this, e);
}
