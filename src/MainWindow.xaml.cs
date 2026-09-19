using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace GameVault;

/// <summary>
/// The library window: sidebar navigation on the left, the game grid on the right.
///
/// The window owns no state of its own beyond the current filter, sort and view.
/// Every refresh rebuilds the visible list from <see cref="Library.Current"/>, so a
/// change made in a dialog is reflected without any incremental bookkeeping.
/// </summary>
public partial class MainWindow : Window
{
    private string _filterType = "all";
    private string _filterValue = "";
    private string _sort = "recent";
    private string _view = "grid";
    private Game? _heroGame;
    private string _navSignature = "";
    private string _categorySignature = "";
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4.5) };

    /// <summary>Notification-area icon; null once the window has closed for good.</summary>
    private TrayIcon? _tray;

    /// <summary>Set while the application is quitting, so closing must not be turned into a hide.</summary>
    private bool _exiting;

    public MainWindow()
    {
        InitializeComponent();

        Library.Current.Load();
        Library.Current.RemoveOrphanedMedia();
        SteamApi.ApplyProxy(Library.Current.Settings.Proxy);

        Launcher.SessionEnded += OnSessionEnded;
        Launcher.SessionsChanged += OnSessionsChanged;
        Launcher.StartTracking();

        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast.Visibility = Visibility.Collapsed;
        };

        SortBox.SelectedIndex = 0;
        StateChanged += OnWindowStateChanged;
        Closed += OnWindowClosed;
        Loaded += OnLoaded;

        _tray = new TrayIcon();
        _tray.ShowRequested += RestoreFromTray;
        _tray.ExitRequested += ExitApplication;
    }

    /// <summary>
    /// True when Windows started this copy through the "start with Windows" entry, in which
    /// case it opens into the notification area rather than on screen.
    /// </summary>
    public bool StartHidden { get; } = Environment.GetCommandLineArgs().Any(
        argument => string.Equals(argument, StartupRegistration.TrayArgument, StringComparison.OrdinalIgnoreCase));

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RestoreWindowSize();
        ApplyWallpaper();
        foreach (var game in Library.Current.Games) game.IsRunning = Launcher.IsRunning(game.Id);
        Refresh();
        if (StartHidden) HideToTray(notify: false);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        WallpaperVideo.Stop();
        _tray?.Dispose();
        _tray = null;
    }

    // ------------------------------------------------------------- background

    /// <summary>
    /// Keeps the process alive with no window, reachable from the notification area.
    ///
    /// @param notify whether to explain the first hide with a balloon tip
    /// </summary>
    private void HideToTray(bool notify = true)
    {
        Opacity = 1;
        ShowInTaskbar = false;
        Hide();
        if (notify) _tray?.NotifyHidden();
    }

    /// <summary>
    /// Brings the window back, used when a second launch asks for it as well as by the tray icon.
    /// </summary>
    public void ShowFromBackground() => RestoreFromTray();

    /// <summary>Brings the window back from the notification area.</summary>
    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        // Toggling topmost is what reliably pulls a window in front of the current foreground app.
        Topmost = true;
        Topmost = false;
    }

    /// <summary>Quits for real, bypassing the close-button preference.</summary>
    private void ExitApplication()
    {
        _exiting = true;
        Close();
    }

    /// <summary>
    /// Applies the user's preference for what the close button does.
    ///
    /// The window is only really closed for "exit"; otherwise closing it becomes a hide, so the
    /// process keeps tracking playtime and can be reopened from the notification area.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting)
        {
            SaveWindowSize();
            return;
        }

        var action = Library.Current.Settings.CloseAction;
        if (action == "ask")
        {
            var (choice, remember) = CloseChoiceWindow.Ask(this);
            if (choice == CloseChoice.Cancel)
            {
                e.Cancel = true;
                return;
            }
            if (remember)
            {
                Library.Current.Settings.CloseAction = choice == CloseChoice.Exit ? "exit" : "tray";
                Library.Current.SaveSettings();
            }
            action = choice == CloseChoice.Exit ? "exit" : "tray";
        }

        SaveWindowSize();
        if (action == "exit") return;

        e.Cancel = true;
        HideToTray();
    }

    // ------------------------------------------------------------- window chrome

    /// <summary>
    /// Hooks the window procedure so maximising can be corrected.
    ///
    /// A frameless window is maximised to the whole monitor by default, which puts its outer
    /// border off screen and lets the taskbar cover the bottom of the sidebar. WM_GETMINMAXINFO
    /// is where the real work area has to be supplied instead.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WindowMessageHook);
    }

    private const int WmGetMinMaxInfo = 0x0024;
    private const int MonitorDefaultToNearest = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public int Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private static IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmGetMinMaxInfo) return IntPtr.Zero;
        ApplyWorkAreaToMaximize(hwnd, lParam);
        handled = true;
        return IntPtr.Zero;
    }

    /// <summary>
    /// Replaces the monitor-sized maximise bounds with the monitor's work area.
    ///
    /// WindowChrome maximises a frameless window to the whole monitor, which pushes the top
    /// bar off the top edge and lets the taskbar cover the bottom of the sidebar. The content
    /// fills the window rectangle exactly, so the work area is used verbatim, expressed
    /// relative to the monitor so this is also correct on a secondary display.
    ///
    /// @param hwnd the window being maximised
    /// @param lParam pointer to the MINMAXINFO Windows wants filled in
    /// </summary>
    private static void ApplyWorkAreaToMaximize(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        var target = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        target.MaxPosition.X = info.Work.Left - info.Monitor.Left;
        target.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
        target.MaxSize.X = info.Work.Right - info.Work.Left;
        target.MaxSize.Y = info.Work.Bottom - info.Work.Top;
        target.MaxTrackSize = target.MaxSize;
        Marshal.StructureToPtr(target, lParam, true);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Keeps the maximise button showing the action it will perform.</summary>
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "❐" : "▢";
        MaximizeButton.ToolTip = maximized ? "向下还原" : "最大化";
        // A video wallpaper showing behind a minimised window is wasted decoding.
        if (WindowState == WindowState.Minimized) WallpaperVideo.Pause();
        else if (WallpaperVideo.Source is not null) WallpaperVideo.Play();
    }

    /// <summary>Reopens the window at the size the user last left it.</summary>
    private void RestoreWindowSize()
    {
        var settings = Library.Current.Settings;
        var work = SystemParameters.WorkArea;
        if (settings.WindowWidth >= MinWidth && settings.WindowHeight >= MinHeight)
        {
            Width = Math.Min(settings.WindowWidth, work.Width);
            Height = Math.Min(settings.WindowHeight, work.Height);
        }
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + (work.Height - Height) / 2;
    }

    private void SaveWindowSize()
    {
        // A maximised window reports the screen size, so its restore bounds are what to keep.
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
        if (bounds.Width < MinWidth || bounds.Height < MinHeight) return;

        Library.Current.Settings.WindowWidth = bounds.Width;
        Library.Current.Settings.WindowHeight = bounds.Height;
        Library.Current.SaveSettings();
    }

    /// <summary>
    /// Resizes the library window to a pixel resolution, or fills the screen.
    ///
    /// The requested size is a monitor resolution, so it is converted from pixels to the
    /// device-independent units WPF sizes windows in, then clamped to what the screen shows.
    ///
    /// @param width target width in pixels
    /// @param height target height in pixels
    /// @param maximize true to fill the working area instead of using an exact size
    /// </summary>
    public void ApplyResolution(double width, double height, bool maximize)
    {
        if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
        var work = SystemParameters.WorkArea;

        if (maximize)
        {
            WindowState = WindowState.Maximized;
            SaveWindowSize();
            return;
        }

        var scale = VisualTreeHelper.GetDpi(this);
        Width = Math.Min(width / scale.DpiScaleX, work.Width);
        Height = Math.Min(height / scale.DpiScaleY, work.Height);
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + (work.Height - Height) / 2;
        SaveWindowSize();
    }

    /// <summary>@return the window size in device pixels, which is what the presets are named in</summary>
    public (double Width, double Height) CurrentPixelSize()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(0, 0, Width, Height) : RestoreBounds;
        var scale = VisualTreeHelper.GetDpi(this);
        return (Math.Round(bounds.Width * scale.DpiScaleX), Math.Round(bounds.Height * scale.DpiScaleY));
    }

    // ------------------------------------------------------------- wallpaper

    /// <summary>
    /// Restarts the clip so a video wallpaper keeps playing.
    ///
    /// MediaElement has no repeat property; looping is a matter of seeking back and playing
    /// again every time the media ends.
    /// </summary>
    private void WallpaperVideo_MediaEnded(object sender, RoutedEventArgs e)
    {
        WallpaperVideo.Position = TimeSpan.Zero;
        WallpaperVideo.Play();
    }

    /// <summary>
    /// Draws the user's background behind the library, or restores the plain dark theme.
    ///
    /// The sidebar and top bar turn translucent while a wallpaper is showing, so the image
    /// reads as the window's background rather than as a panel behind the content.
    /// </summary>
    public void ApplyWallpaper()
    {
        var path = Paths.Media(Library.Current.Settings.Wallpaper);
        var active = path.Length > 0 && File.Exists(path);

        WallpaperVideo.Stop();
        WallpaperVideo.Source = null;
        WallpaperImage.Source = null;

        if (!active)
        {
            WallpaperLayer.Visibility = Visibility.Collapsed;
            SidebarPanel.Background = (Brush)FindResource("Sidebar");
            TopBarPanel.Background = (Brush)FindResource("TopBar");
            return;
        }

        WallpaperLayer.Visibility = Visibility.Visible;
        SidebarPanel.Background = (Brush)FindResource("SidebarGlass");
        TopBarPanel.Background = (Brush)FindResource("TopBarGlass");

        if (Wallpaper.IsVideo(path))
        {
            WallpaperImage.Visibility = Visibility.Collapsed;
            WallpaperVideo.Visibility = Visibility.Visible;
            WallpaperVideo.Source = new Uri(path);
            WallpaperVideo.Position = TimeSpan.Zero;
            WallpaperVideo.Play();
        }
        else
        {
            WallpaperVideo.Visibility = Visibility.Collapsed;
            WallpaperImage.Visibility = Visibility.Visible;
            WallpaperImage.Source = ImageLoader.FromFile(path, 3840);
        }
    }

    // ------------------------------------------------------------- rendering

    /// <summary>Rebuilds every region of the window from the current library and filter.</summary>
    private void Refresh()
    {
        UpdateSearchHint();
        BuildNav();
        BuildCategories();
        UpdateStats();

        var games = VisibleGames();
        GameList.ItemsSource = games;
        ApplyView();
        UpdateHero();
        UpdateEmptyState(games.Count);

        _ = ImageLoader.LoadCoversAsync(games, 360, 1400);
    }

    private void UpdateSearchHint() =>
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>@return the games matching the current filter, search text and sort order</summary>
    private List<Game> VisibleGames()
    {
        IEnumerable<Game> list = Library.Current.Games;
        switch (_filterType)
        {
            case "favorite":
                list = list.Where(g => g.Favorite);
                break;
            case "recent":
                list = list.Where(g => !string.IsNullOrEmpty(g.LastPlayedAt));
                break;
            case "category":
                list = list.Where(g => g.Categories.Contains(_filterValue));
                break;
        }

        var query = SearchBox.Text.Trim().ToLowerInvariant();
        if (query.Length > 0)
        {
            list = list.Where(g => ($"{g.Name} {g.Description} {g.Developer} {g.Publisher} " +
                                    $"{string.Join(' ', g.Genres)} {string.Join(' ', g.Categories)}")
                                    .ToLowerInvariant().Contains(query));
        }

        return _sort switch
        {
            "name" => list.OrderBy(g => g.Name, StringComparer.CurrentCulture).ToList(),
            "playtime" => list.OrderByDescending(g => g.PlaytimeMs).ToList(),
            "added" => list.OrderByDescending(g => g.AddedAt).ToList(),
            _ => list.OrderByDescending(g => string.IsNullOrEmpty(g.LastPlayedAt) ? g.AddedAt : g.LastPlayedAt).ToList(),
        };
    }

    private void UpdateStats()
    {
        var total = Library.Current.Games.Count;
        if (total == 0)
        {
            StatLine.Text = "还没有游戏";
            return;
        }
        var hours = Library.Current.Games.Sum(g => g.PlaytimeMs) / 3600000.0;
        StatLine.Text = $"{total} 个游戏 · 共 {hours:0.#} 小时";
    }

    /// <summary>
    /// Rebuilds the top navigation entries. Rebuilding is skipped while the counts are
    /// unchanged, which keeps the radio group and keyboard focus stable across refreshes.
    /// </summary>
    private void BuildNav()
    {
        var all = Library.Current.Games.Count;
        var recent = Library.Current.Games.Count(g => !string.IsNullOrEmpty(g.LastPlayedAt));
        var favorite = Library.Current.Games.Count(g => g.Favorite);
        var signature = $"{all}/{recent}/{favorite}/{_filterType}";
        if (signature == _navSignature) return;
        _navSignature = signature;

        NavPanel.Children.Clear();
        AddNavButton("all", "▦", "全部游戏", all);
        AddNavButton("recent", "◷", "最近游玩", recent);
        AddNavButton("favorite", "★", "收藏", favorite);
    }

    private void AddNavButton(string type, string icon, string label, int count)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconBlock = new TextBlock { Text = icon, Width = 18, VerticalAlignment = VerticalAlignment.Center };
        var labelBlock = new TextBlock { Text = label, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var countBlock = new TextBlock
        {
            Text = count.ToString(),
            FontSize = 12,
            Foreground = (Brush)FindResource("TextDim"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(iconBlock, 0);
        Grid.SetColumn(labelBlock, 1);
        Grid.SetColumn(countBlock, 2);
        row.Children.Add(iconBlock);
        row.Children.Add(labelBlock);
        row.Children.Add(countBlock);

        // IsChecked is set before the handler is attached, so building the list cannot trigger a refresh.
        var button = new RadioButton
        {
            Style = (Style)FindResource("NavRadio"),
            Content = row,
            GroupName = "nav",
            Tag = type,
            IsChecked = _filterType == type,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        button.Checked += (_, _) =>
        {
            if (_filterType == type) return;
            _filterType = type;
            _filterValue = "";
            Refresh();
        };
        NavPanel.Children.Add(button);
    }

    private void BuildCategories()
    {
        var signature = string.Join("|", Library.Current.Document.Categories) + "#" + _filterValue;
        if (signature == _categorySignature) return;
        _categorySignature = signature;

        CategoryPanel.Children.Clear();
        if (Library.Current.Document.Categories.Count == 0)
        {
            CategoryPanel.Children.Add(new TextBlock
            {
                Text = "还没有分类",
                FontSize = 12,
                Foreground = (Brush)FindResource("TextDim"),
                Margin = new Thickness(10, 4, 0, 0),
            });
            return;
        }

        foreach (var name in Library.Current.Document.Categories)
        {
            var count = Library.Current.Games.Count(g => g.Categories.Contains(name));
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var countBlock = new TextBlock
            {
                Text = count.ToString(),
                FontSize = 12,
                Margin = new Thickness(6, 0, 0, 0),
                Foreground = (Brush)FindResource("TextDim"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var remove = new Button
            {
                Content = "✕",
                Style = (Style)FindResource("GhostButton"),
                FontSize = 11,
                Padding = new Thickness(4, 0, 4, 0),
                Margin = new Thickness(4, 0, 0, 0),
                Tag = name,
                ToolTip = "删除分类",
            };
            remove.Click += RemoveCategory_Click;

            Grid.SetColumn(label, 0);
            Grid.SetColumn(countBlock, 1);
            Grid.SetColumn(remove, 2);
            row.Children.Add(label);
            row.Children.Add(countBlock);
            row.Children.Add(remove);

            var button = new RadioButton
            {
                Style = (Style)FindResource("NavRadio"),
                Content = row,
                GroupName = "nav",
                Tag = name,
                IsChecked = _filterType == "category" && _filterValue == name,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0),
            };
            var captured = name;
            button.Checked += (_, _) =>
            {
                if (_filterType == "category" && _filterValue == captured) return;
                _filterType = "category";
                _filterValue = captured;
                Refresh();
            };
            CategoryPanel.Children.Add(button);
        }
    }

    private void ApplyView()
    {
        var list = _view == "list";
        GameList.ItemTemplate = (DataTemplate)FindResource(list ? "RowTemplate" : "CardTemplate");
        GameList.ItemsPanel = (ItemsPanelTemplate)FindResource(list ? "StackPanelTemplate" : "WrapPanelTemplate");
    }

    /// <summary>
    /// Shows the most recently played game as a continue banner.
    ///
    /// The test is on the stored cover path, never on the decoded bitmap: at startup no image
    /// has been decoded yet, so requiring one hid the banner until some unrelated action
    /// happened to refresh the window.
    /// </summary>
    private void UpdateHero()
    {
        _heroGame = Library.Current.Games
            .Where(g => !string.IsNullOrEmpty(g.LastPlayedAt))
            .OrderByDescending(g => g.LastPlayedAt)
            .FirstOrDefault();

        if (_heroGame is null)
        {
            HeroBox.Visibility = Visibility.Collapsed;
            return;
        }

        HeroBox.Visibility = Visibility.Visible;
        HeroTitle.Text = _heroGame.Name;
        HeroText.Text = string.IsNullOrEmpty(_heroGame.Description)
            ? "还没有简介，进入详情可以补全。"
            : _heroGame.Description;
        HeroPlay.Content = Launcher.IsRunning(_heroGame.Id) ? "■  结束运行" : "▶  开始游戏";
        _ = ShowHeroImageAsync(_heroGame);
    }

    /// <summary>
    /// Draws the banner artwork, decoding it off the UI thread.
    ///
    /// The assignment goes straight to the control: writing only to the model left the banner
    /// blank until the next full refresh. The guard keeps a slow decode from overwriting the
    /// artwork after the banner has already moved on to another game.
    ///
    /// @param game the game currently shown in the banner
    /// </summary>
    private async Task ShowHeroImageAsync(Game game)
    {
        HeroImage.Source = game.HeroImage ?? game.CoverImage;
        if (HeroImage.Source is not null) return;

        var image = await Task.Run(() => ImageLoader.FromFile(game.HeroFullPath, 1400));
        if (image is null) return;
        game.HeroImage = image;
        if (ReferenceEquals(game, _heroGame)) HeroImage.Source = image;
    }

    private void UpdateEmptyState(int visible)
    {
        EmptyState.Visibility = visible == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (visible > 0) return;
        var hasAny = Library.Current.Games.Count > 0;
        EmptyTitle.Text = hasAny ? "没有匹配的游戏" : "游戏库还是空的";
        EmptyText.Text = hasAny
            ? "换个关键词，或者选择其他分类试试。"
            : "点「扫描 Steam 库」可以一次性导入本机已安装的 Steam 游戏，也可以用「添加游戏」导入硬盘上任意程序。";
        EmptyActions.Visibility = hasAny ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Shows a transient message in the lower-right corner.</summary>
    private void ShowToast(string message, bool error = false)
    {
        ToastText.Text = message;
        Toast.BorderBrush = (Brush)FindResource(error ? "Danger" : "Line");
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ------------------------------------------------------------- interaction

    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Game game })
        {
            e.Handled = true;
            OpenDetail(game);
        }
    }

    private void Favorite_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Game game })
        {
            e.Handled = true;
            game.Favorite = !game.Favorite;
            Library.Current.Touch(game);
            Refresh();
        }
    }

    private void HeroPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_heroGame is not null) PlayGame(_heroGame);
    }

    private void HeroDetail_Click(object sender, RoutedEventArgs e)
    {
        if (_heroGame is not null) OpenDetail(_heroGame);
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        UpdateSearchHint();
        if (IsLoaded) Refresh();
    }

    private void Sort_Changed(object sender, SelectionChangedEventArgs e)
    {
        _sort = SortBox.SelectedIndex switch { 1 => "added", 2 => "name", 3 => "playtime", _ => "recent" };
        if (IsLoaded) Refresh();
    }

    private void View_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } button && button.IsChecked == true)
        {
            _view = tag;
            ApplyView();
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e) => OpenEdit(null);

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow { Owner = this };
        dialog.ShowDialog();
        SteamApi.ApplyProxy(Library.Current.Settings.Proxy);
        ApplyWallpaper();
        Refresh();
    }

    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ScanWindow { Owner = this };
        dialog.ShowDialog();
        Refresh();
        if (dialog.Imported.Count == 0) return;
        ShowToast($"已导入 {dialog.Imported.Count} 个游戏，正在获取封面…");
        await RunEnrichAsync(dialog.Imported);
    }

    private void RemoveCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name }) return;
        var answer = MessageBox.Show(this, $"删除分类「{name}」？\n\n游戏本身不会被删除。", "一切游戏管理家",
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        Library.Current.RemoveCategory(name);
        if (_filterType == "category" && _filterValue == name)
        {
            _filterType = "all";
            _filterValue = "";
        }
        _categorySignature = "";
        Refresh();
    }

    private void OnSessionsChanged()
    {
        foreach (var game in Library.Current.Games) game.IsRunning = Launcher.IsRunning(game.Id);
        Refresh();
    }

    private void OnSessionEnded(string gameId, TimeSpan elapsed)
    {
        var game = Library.Current.Find(gameId);
        if (game is null) return;
        game.PlaytimeMs += (long)elapsed.TotalMilliseconds;
        game.LastPlayedAt = DateTime.UtcNow.ToString("o");
        Library.Current.Save();
        Refresh();
    }

    // ------------------------------------------------------------- game actions

    private void OpenDetail(Game game)
    {
        var dialog = new DetailWindow(game) { Owner = this };
        dialog.ShowDialog();
        Refresh();
    }

    private void OpenEdit(Game? game)
    {
        var dialog = new EditWindow(game) { Owner = this };
        if (dialog.ShowDialog() == true) Refresh();
    }

    /// <summary>Starts a game, or stops it when this process is already tracking it.</summary>
    private async void PlayGame(Game game)
    {
        if (game.LaunchType != "steam" && string.IsNullOrWhiteSpace(game.ExePath) && !Launcher.IsRunning(game.Id))
        {
            ShowToast("这个游戏还没有设置启动路径，先编辑它填写程序位置。", error: true);
            OpenEdit(game);
            return;
        }

        var (message, error) = await GameActions.ToggleAsync(game);
        Refresh();
        ShowToast(message, error);
    }

    // ------------------------------------------------------------- metadata job

    private async void Enrich_Click(object sender, RoutedEventArgs e)
    {
        var targets = Library.Current.Games.Where(g => !g.IsComplete).ToList();
        if (targets.Count == 0)
        {
            ShowToast("所有游戏都已有简介和封面。");
            return;
        }
        await RunEnrichAsync(targets);
    }

    /// <summary>
    /// Fills in artwork and metadata for a set of games.
    ///
    /// The two halves are handled differently on purpose. Artwork comes from the CDN, which
    /// is reachable far more often than the store API, so it runs first and in parallel:
    /// the library looks right within seconds even when the store is down. Metadata then runs
    /// one game at a time, and is skipped entirely when a single probe shows the store is
    /// unreachable — hammering a dead endpoint for a minute per game helps nobody.
    ///
    /// @param targets games missing artwork or metadata
    /// </summary>
    private async Task RunEnrichAsync(IReadOnlyList<Game> targets)
    {
        BeginJob("补全游戏信息", targets.Count);
        var timeout = Library.Current.Settings.Timeout;
        var storeUp = (await SteamApi.ProbeAsync("Steam 商店", SteamApi.AppDetailsProbeUrl)).Ok;

        // ---- phase 1: artwork, in parallel ----
        var finished = new int[1];
        using (var gate = new SemaphoreSlim(6))
        {
            var work = targets.Select(async game =>
            {
                await gate.WaitAsync();
                try
                {
                    if (game.Cover.Length > 0 && game.Hero.Length > 0) return;
                    var (cover, hero) = await FetchArtworkAsync(game, storeUp, timeout);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (cover.Length > 0) game.Cover = cover;
                        if (hero.Length > 0 && game.Hero.Length == 0) game.Hero = hero;
                    });
                }
                catch (Exception)
                {
                    // Artwork is best effort; a game without a cover still shows its name.
                }
                finally
                {
                    gate.Release();
                    var done = Interlocked.Increment(ref finished[0]);
                    await Dispatcher.InvokeAsync(() => UpdateJob(done, targets.Count));
                }
            });
            await Task.WhenAll(work);
        }
        Library.Current.Save();
        Refresh();

        // ---- phase 2: descriptions, one at a time ----
        var failures = new List<string>();
        var consecutiveFailures = 0;
        foreach (var game in targets)
        {
            if (game.IsComplete) continue;

            // Steam's own Chinese store copy is the best text, but only for apps it lists.
            if (storeUp && game.AppId.Length > 0)
            {
                try
                {
                    await FetchMetadataAsync(game, timeout);
                    consecutiveFailures = 0;
                }
                catch (Exception error)
                {
                    failures.Add($"{game.Name}：{error.Message}");
                    consecutiveFailures++;
                    // Three timeouts in a row means the store is down, not that these games are odd.
                    if (consecutiveFailures >= 3) storeUp = false;
                }
            }

            // Whatever Steam cannot cover is handled by VNDB and 萌娘百科.
            if (game.Description.Length == 0 || game.Developer.Length == 0 || game.ReleaseDate.Length == 0)
                await FetchExternalAsync(game, timeout);

            // A hand-added game that is on Steam but only findable by name.
            if (game.Description.Length == 0 && game.AppId.Length == 0 && storeUp)
            {
                try
                {
                    await FetchMetadataAsync(game, timeout);
                }
                catch (Exception)
                {
                    // The external sources already came up empty; there is nothing left to try.
                }
            }

            await Task.Delay(200);
        }

        EndJob();
        Refresh();
        var missingCover = targets.Count(g => g.Cover.Length == 0);
        var missingText = targets.Count(g => g.Description.Length == 0);
        if (missingCover == 0 && missingText == 0)
        {
            ShowToast("补全完成");
        }
        else
        {
            var parts = new List<string>();
            if (missingCover > 0) parts.Add($"{missingCover} 个没有封面");
            if (missingText > 0) parts.Add($"{missingText} 个没有简介");
            var detail = failures.Count > 0 ? $"（例如 {failures[0]}）" : "";
            ShowToast($"补全完成，还有 {string.Join("、", parts)}{detail}。可以改个名字后再点一次重试，或手动填写。", error: true);
        }
    }

    /// <summary>
    /// Fills in what it can from the sources that cover games the Steam store does not list.
    ///
    /// @param game the game to fill in
    /// @param timeout per-attempt timeout
    /// </summary>
    private static async Task FetchExternalAsync(Game game, int timeout)
    {
        try
        {
            var metadata = await ExternalMetadataSources.LookupAsync(game.Name, timeout);
            if (metadata is null) return;
            if (game.Description.Length == 0 && metadata.Description.Length > 0) game.Description = metadata.Description;
            if (game.Detailed.Length == 0 && metadata.Detailed.Length > 0) game.Detailed = metadata.Detailed;
            if (game.Developer.Length == 0 && metadata.Developer.Length > 0) game.Developer = metadata.Developer;
            if (game.ReleaseDate.Length == 0 && metadata.ReleaseDate.Length > 0) game.ReleaseDate = metadata.ReleaseDate;
            Library.Current.Touch(game);
        }
        catch (Exception)
        {
            // Neither source answered; the game keeps whatever it already had.
        }
    }

    /// <summary>
    /// Finds a cover for one game, trying each source in order of how well it fits.
    ///
    /// Steam's CDN has the best portrait art but only for apps Steam knows about, which by
    /// definition excludes games imported by hand. Those are exactly what SteamGridDB exists
    /// for, and VNDB covers visual novels without needing any key at all.
    ///
    /// @param game the game needing artwork
    /// @param storeUp whether the Steam store answered a probe this run
    /// @param timeout per-attempt timeout
    /// @return stored relative paths for the cover and hero, either of which may be empty
    /// </summary>
    private static async Task<(string Cover, string Hero)> FetchArtworkAsync(Game game, bool storeUp, int timeout)
    {
        // 1. Steam CDN, when this game already has an app id.
        if (game.AppId.Length > 0)
        {
            var steam = await SteamApi.DownloadArtworkAsync(game.AppId, timeout);
            if (steam.Cover.Length > 0 || steam.Hero.Length > 0) return steam;
        }

        // 2. SteamGridDB — its whole purpose is games the Steam store does not list.
        if (Library.Current.Settings.SteamGridDbKey.Length > 0)
        {
            try
            {
                var artwork = await SteamGridDb.FetchForNameAsync(game.Name, game.Id);
                if (artwork.Cover.Length > 0 || artwork.Hero.Length > 0) return (artwork.Cover, artwork.Hero);
            }
            catch (Exception)
            {
                // Not in SteamGridDB, or the key was rejected; VNDB is tried next.
            }
        }

        // 3. VNDB, which is free and whose covers fit portrait cards.
        try
        {
            var vndb = await Vndb.SearchAsync(game.Name, timeout);
            if (vndb is not null && vndb.CoverUrl.Length > 0)
            {
                var relative = $"covers/{game.Id}_vndb.jpg";
                if (await SteamApi.DownloadFileAsync(vndb.CoverUrl, Paths.Media(relative), timeout))
                    return (relative, "");
            }
        }
        catch (Exception)
        {
            // No source matched; the card keeps its letter placeholder.
        }

        // 4. Last resort: look the name up on Steam, accepting only a real name match.
        if (storeUp && game.AppId.Length == 0)
        {
            try
            {
                var match = await SteamApi.ResolveAppIdAsync(game.Name, timeout);
                if (match is not null)
                {
                    var steam = await SteamApi.DownloadArtworkAsync(match.AppId, timeout);
                    return steam;
                }
            }
            catch (Exception)
            {
                // Nothing to fall back to.
            }
        }

        return ("", "");
    }

    /// <summary>
    /// Fetches the store description and credits for one game.
    ///
    /// @param game the game to fill in; its app id must already be resolved
    /// @param timeout per-attempt timeout
    /// </summary>
    private static async Task FetchMetadataAsync(Game game, int timeout)
    {
        var appId = game.AppId;
        if (appId.Length == 0)
        {
            var match = await SteamApi.ResolveAppIdAsync(game.Name, timeout);
            if (match is null) throw new InvalidOperationException("未在 Steam 中找到匹配条目");
            appId = match.AppId;
            game.AppId = appId;
        }

        var details = await SteamApi.FetchAppDetailsAsync(appId, timeout);
        if (!string.IsNullOrEmpty(details.Name)) game.Name = details.Name;
        if (string.IsNullOrEmpty(game.Description)) game.Description = details.Description;
        if (string.IsNullOrEmpty(game.Detailed)) game.Detailed = details.Detailed;
        if (string.IsNullOrEmpty(game.Developer)) game.Developer = details.Developer;
        if (string.IsNullOrEmpty(game.Publisher)) game.Publisher = details.Publisher;
        if (string.IsNullOrEmpty(game.ReleaseDate)) game.ReleaseDate = details.ReleaseDate;
        if (game.Genres.Count == 0) game.Genres = details.Genres;
        Library.Current.Touch(game);

        // Steam carries no Chinese copy for every app, so the text is localized afterwards.
        await MetadataLocalizer.EnsureChineseAsync(game, timeout);
    }

    private void BeginJob(string label, int total)
    {
        JobBar.Visibility = Visibility.Visible;
        JobLabel.Text = label;
        JobProgress.Maximum = total;
        JobProgress.Value = 0;
        JobCount.Text = $"0 / {total}";
    }

    private void UpdateJob(int done, int total)
    {
        JobProgress.Value = done;
        JobCount.Text = $"{done} / {total}";
    }

    private void EndJob()
    {
        JobBar.Visibility = Visibility.Collapsed;
    }
}
