using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace GameVault;

/// <summary>
/// Full-screen detail view for one game: hero art, metadata, description, own images
/// and the launch controls.
/// </summary>
public partial class DetailWindow : Window
{
    private readonly Game _game;

    /// <summary>@param game the game to show; the instance is edited in place</summary>
    public DetailWindow(Game game)
    {
        InitializeComponent();
        _game = game;
        // The window stays open while the game runs, so it must react when the process exits.
        Launcher.SessionsChanged += OnSessionsChanged;
        Closed += (_, _) => Launcher.SessionsChanged -= OnSessionsChanged;
        Render();
    }

    /// <summary>
    /// Updates the parts of the dialog that depend on whether the game is still running.
    ///
    /// A full re-render is avoided: it would reload every screenshot from disk and jump the
    /// scroll position, for a change that only affects the play button and the playtime line.
    /// </summary>
    private void OnSessionsChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(OnSessionsChanged);
            return;
        }
        if (!IsLoaded) return;
        UpdatePlayState();
        RenderMeta();
    }

    /// <summary>Rebuilds the whole dialog from the current state of the game.</summary>
    private void Render()
    {
        Title = _game.Name;
        TitleText.Text = _game.Name;
        LoadHero();
        RenderMeta();

        DescriptionText.Text = string.IsNullOrEmpty(_game.Description)
            ? "还没有简介。点「编辑」可以手动填写，或用「自动获取信息」从 Steam 抓取。"
            : _game.Description;

        DetailedText.Text = _game.Detailed;
        DetailSection.Visibility = string.IsNullOrEmpty(_game.Detailed) ? Visibility.Collapsed : Visibility.Visible;
        // When the summary had to be translated, this section holds the untranslated original.
        DetailSectionTitle.Text = Translator.LooksChinese(_game.Detailed) ? "详细简介" : "原文（未翻译）";

        CategoryList.ItemsSource = _game.Categories.Select(BuildChip).ToList();
        CategorySection.Visibility = _game.Categories.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        UpdatePlayState();
        FavButton.Content = _game.Favorite ? "★ 取消收藏" : "☆ 收藏";
        RevealButton.Visibility = string.IsNullOrEmpty(_game.ExePath) ? Visibility.Collapsed : Visibility.Visible;

        BuildShots();
    }

    /// <summary>Refreshes the metadata line, which changes when a session ends.</summary>
    private void RenderMeta()
    {
        MetaPanel.Children.Clear();
        AddMeta("开发商", _game.Developer);
        AddMeta("发行商", _game.Publisher);
        AddMeta("发行日期", _game.ReleaseDate);
        AddMeta("类型", string.Join(" / ", _game.Genres));
        AddMeta("游玩时长", _game.PlaytimeText);
        AddMeta("启动次数", _game.LaunchCount > 0 ? $"{_game.LaunchCount} 次" : "");
        AddMeta("上次游玩", FormatRelative(_game.LastPlayedAt));
        AddMeta("Steam AppID", _game.AppId);
    }

    /// <summary>Points the play control at the action that is currently possible.</summary>
    private void UpdatePlayState() =>
        PlayButton.Content = Launcher.IsRunning(_game.Id) ? "■  结束运行" : "▶  开始游戏";

    /// <summary>Decodes the hero image, falling back to the cover when none is stored.</summary>
    private void LoadHero()
    {
        var source = _game.HeroImage ?? _game.CoverImage;
        if (source is null)
        {
            source = ImageLoader.FromFile(_game.HeroFullPath, 1200) ?? ImageLoader.FromFile(_game.CoverFullPath, 1200);
            if (source is not null) _game.HeroImage = source;
        }
        HeroImage.Source = source;
    }

    private void AddMeta(string label, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 4) };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = (Brush)FindResource("TextDim"),
            Margin = new Thickness(0, 0, 6, 0),
        });
        panel.Children.Add(new TextBlock { Text = value, FontSize = 12.5, Foreground = (Brush)FindResource("Text") });
        MetaPanel.Children.Add(panel);
    }

    private Border BuildChip(string text) => new()
    {
        Background = (Brush)FindResource("AccentDeep"),
        CornerRadius = new CornerRadius(13),
        Padding = new Thickness(12, 4, 12, 4),
        Margin = new Thickness(0, 0, 6, 6),
        Child = new TextBlock { Text = text, FontSize = 12, Foreground = Brushes.White },
    };

    /// <summary>Draws the user's own screenshots, each removable with a click.</summary>
    private void BuildShots()
    {
        var items = new List<UIElement>();
        foreach (var shot in _game.Screenshots)
        {
            var image = ImageLoader.FromFile(Paths.Media(shot), 260);
            var border = new Border
            {
                Height = 104,
                Margin = new Thickness(0, 0, 8, 8),
                CornerRadius = new CornerRadius(5),
                ClipToBounds = true,
                Cursor = Cursors.Hand,
                Background = (Brush)FindResource("Panel2"),
                Tag = shot,
                ToolTip = "点击删除这张图片",
                Child = image is null
                    ? new TextBlock { Text = "无法读取", Foreground = (Brush)FindResource("TextDim"), Margin = new Thickness(10) }
                    : new Image { Source = image, Stretch = Stretch.Uniform },
            };
            border.MouseLeftButtonUp += RemoveShot_Click;
            items.Add(border);
        }

        var add = new Button
        {
            Content = "＋ 添加图片",
            Style = (Style)FindResource("TinyButton"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
        };
        add.Click += AddShot_Click;
        items.Add(add);

        ShotList.ItemsSource = items;
    }

    private static string FormatRelative(string iso)
    {
        if (string.IsNullOrEmpty(iso) || !DateTime.TryParse(iso, out var time)) return "";
        var span = DateTime.UtcNow - time.ToUniversalTime();
        if (span.TotalMinutes < 60) return "刚刚";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} 小时前";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays} 天前";
        return time.ToLocalTime().ToString("yyyy-MM-dd");
    }

    // ------------------------------------------------------------- commands

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_game.LaunchType != "steam" && string.IsNullOrWhiteSpace(_game.ExePath) && !Launcher.IsRunning(_game.Id))
        {
            MessageBox.Show(this, "这个游戏还没有设置启动路径，请先编辑它填写程序位置。", "一切游戏管理家",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Edit_Click(sender, e);
            return;
        }
        var (message, error) = await GameActions.ToggleAsync(_game);
        Render();
        if (error) MessageBox.Show(this, message, "一切游戏管理家", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new EditWindow(_game) { Owner = this };
        if (dialog.ShowDialog() == true) Render();
    }

    private void Fav_Click(object sender, RoutedEventArgs e)
    {
        _game.Favorite = !_game.Favorite;
        Library.Current.Touch(_game);
        Render();
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        var target = File.Exists(_game.ExePath) ? _game.ExePath : _game.WorkDir;
        if (string.IsNullOrEmpty(target)) return;
        Launcher.RevealInExplorer(target);
    }

    private void AddShot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择图片",
            Filter = "图片 (*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp)|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var extension = Path.GetExtension(dialog.FileName);
            var relative = $"shots/{_game.Id}_{DateTime.UtcNow.Ticks}{extension}";
            File.Copy(dialog.FileName, Paths.Media(relative), overwrite: true);
            _game.Screenshots.Add(relative);
            Library.Current.Touch(_game);
            BuildShots();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, $"添加图片失败：{error.Message}", "一切游戏管理家", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RemoveShot_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: string shot }) return;
        if (MessageBox.Show(this, "删除这张图片？", "一切游戏管理家", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _game.Screenshots.Remove(shot);
        Library.Current.DeleteMedia(shot);
        Library.Current.Touch(_game);
        BuildShots();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            $"确定要从仓库中删除「{_game.Name}」吗？\n\n只会删除仓库里的记录和已导入的图片，不会删除游戏本体。",
            "一切游戏管理家", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;
        Library.Current.Remove(_game);
        DialogResult = true;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Moves the window when the user drags its artwork.
    ///
    /// The window has no title bar, so this replaces the drag the caption used to provide.
    /// Buttons inside the artwork handle their own clicks and never reach this handler.
    /// </summary>
    private void Chrome_Drag(object sender, MouseButtonEventArgs e) => FramelessWindow.Drag(this, e);
}
