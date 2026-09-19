using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace GameVault;

/// <summary>
/// The add/edit form for one game.
///
/// The form edits a copy of the values and only touches the library when the user
/// saves, so cancelling never leaves a half-applied change behind.
/// </summary>
public partial class EditWindow : Window
{
    private readonly Game? _existing;
    private readonly string _gameId;
    private readonly HashSet<string> _categories = new();
    private List<SteamSearchResult> _results = new();

    /// <summary>Stored relative cover path that will be kept when the form is saved.</summary>
    private string _cover = "";

    /// <summary>A cover the user picked from disk, imported only once the form is saved.</summary>
    private string? _pendingCover;

    /// <summary>@param game the game to edit, or null to create a new one</summary>
    public EditWindow(Game? game)
    {
        InitializeComponent();
        _existing = game;
        _gameId = game?.Id ?? Library.NewId();
        Title = game is null ? "添加游戏" : "编辑游戏";
        HeaderTitle.Text = Title;
        DeleteButton.Visibility = game is null ? Visibility.Collapsed : Visibility.Visible;

        Load(game);
    }

    /// <summary>Fills every control from the game being edited, or from empty defaults.</summary>
    private void Load(Game? game)
    {
        NameBox.Text = game?.Name ?? "";
        ExeBox.Text = game?.ExePath ?? "";
        AppIdBox.Text = game?.AppId ?? "";
        ArgsBox.Text = game?.Args ?? "";
        WorkDirBox.Text = game?.WorkDir ?? "";
        DescriptionBox.Text = game?.Description ?? "";
        DeveloperBox.Text = game?.Developer ?? "";
        PublisherBox.Text = game?.Publisher ?? "";
        ReleaseBox.Text = game?.ReleaseDate ?? "";
        GenresBox.Text = string.Join(", ", game?.Genres ?? new List<string>());
        FavoriteBox.IsChecked = game?.Favorite ?? false;
        _cover = game?.Cover ?? "";
        foreach (var category in game?.Categories ?? new List<string>()) _categories.Add(category);

        var mode = game?.LaunchType ?? "exe";
        ModeSteam.IsChecked = mode == "steam";
        ModeShell.IsChecked = mode == "shell";
        ModeExe.IsChecked = mode == "exe";
        ModeExe.Checked += (_, _) => SyncMode();
        ModeSteam.Checked += (_, _) => SyncMode();
        ModeShell.Checked += (_, _) => SyncMode();
        SyncMode();

        BuildChips();
        ShowCover();
    }

    /// <summary>Shows the fields that belong to the selected launch mode.</summary>
    private void SyncMode()
    {
        var steam = ModeSteam.IsChecked == true;
        AppIdSection.Visibility = steam ? Visibility.Visible : Visibility.Collapsed;
        ExeSection.Visibility = steam ? Visibility.Collapsed : Visibility.Visible;
        ExeLabel.Text = ModeShell.IsChecked == true ? "文件 / 快捷方式 / 网址" : "可执行文件";
    }

    /// <summary>Draws the cover preview from the pending file or the stored path.</summary>
    private void ShowCover()
    {
        var path = _pendingCover ?? Paths.Media(_cover);
        var image = string.IsNullOrEmpty(path) ? null : ImageLoader.FromFile(path, 420);
        CoverImage.Source = image;
        CoverPlaceholder.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildChips()
    {
        var names = Library.Current.Document.Categories
            .Union(_categories)
            .OrderBy(n => n, StringComparer.CurrentCulture)
            .ToList();

        var items = new List<UIElement>();
        foreach (var name in names)
        {
            var toggle = new ToggleButton
            {
                Content = name,
                Style = (Style)FindResource("Chip"),
                IsChecked = _categories.Contains(name),
                Tag = name,
            };
            toggle.Checked += Chip_Toggled;
            toggle.Unchecked += Chip_Toggled;
            items.Add(toggle);
        }
        CategoryChips.ItemsSource = items;
    }

    private void Chip_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string name } toggle) return;
        if (toggle.IsChecked == true) _categories.Add(name);
        else _categories.Remove(name);
    }

    // ------------------------------------------------------------- steam lookup

    private async void Lookup_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "请先填写游戏名称。", "一切游戏管理家", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        LookupButton.IsEnabled = false;
        LookupButton.Content = "获取中…";
        try
        {
            try
            {
                _results = await SteamApi.SearchStoreAsync(name, Library.Current.Settings.Timeout);
            }
            catch (Exception)
            {
                // The store is unreachable; the sources below may still know this game.
                _results = new List<SteamSearchResult>();
            }

            if (_results.Count == 0)
            {
                await ApplyExternalAsync(name);
                return;
            }

            ResultList.ItemsSource = _results.Select(BuildResultRow).ToList();
            ResultHint.Text = $"找到 {_results.Count} 个结果，正在填写最接近的一个；也可以点下面其他结果替换。";
            ResultPanel.Visibility = Visibility.Visible;

            var target = SteamApi.NormalizeName(name);
            var best = _results.FirstOrDefault(r => SteamApi.NormalizeName(r.Name) == target)
                       ?? _results.FirstOrDefault(r => SteamApi.NormalizeName(r.Name).StartsWith(target, StringComparison.Ordinal))
                       ?? _results[0];
            await ApplyDetailsAsync(best.AppId);
        }
        catch (Exception error)
        {
            ResultHint.Text = $"获取失败：{error.Message}";
            ResultList.ItemsSource = null;
            ResultPanel.Visibility = Visibility.Visible;
        }
        finally
        {
            LookupButton.IsEnabled = true;
            LookupButton.Content = "自动获取信息";
        }
    }

    /// <summary>
    /// Fills the form from the sources that cover games the Steam store does not list.
    ///
    /// @param name the title typed into the form
    /// </summary>
    private async Task ApplyExternalAsync(string name)
    {
        var timeout = Library.Current.Settings.Timeout;
        var metadata = await ExternalMetadataSources.LookupAsync(name, timeout);
        ResultList.ItemsSource = null;
        ResultPanel.Visibility = Visibility.Visible;

        if (metadata is null)
        {
            ResultHint.Text = "Steam、VNDB 和萌娘百科都没有找到这个游戏。可以换个名称再试，或者直接手动填写下面的内容。";
            return;
        }

        if (metadata.Description.Length > 0) DescriptionBox.Text = metadata.Description;
        if (metadata.Developer.Length > 0) DeveloperBox.Text = metadata.Developer;
        if (metadata.ReleaseDate.Length > 0) ReleaseBox.Text = metadata.ReleaseDate;

        if (_cover.Length == 0 && _pendingCover is null && metadata.CoverUrl.Length > 0)
        {
            var relative = $"covers/{_gameId}_ext.jpg";
            if (await SteamApi.DownloadFileAsync(metadata.CoverUrl, Paths.Media(relative), timeout))
            {
                _cover = relative;
                ShowCover();
            }
        }

        ResultHint.Text = $"Steam 没有收录这个游戏，已从 {metadata.Source} 获取信息。保存后即可生效。";
    }

    /// <summary>Builds one search-result row with its thumbnail, which loads in the background.</summary>
    private UIElement BuildResultRow(SteamSearchResult result)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var thumb = new Border
        {
            Width = 96,
            Height = 44,
            CornerRadius = new CornerRadius(4),
            Background = (Brush)FindResource("Panel2"),
            ClipToBounds = true,
        };
        var image = new Image { Stretch = Stretch.UniformToFill };
        thumb.Child = image;
        Grid.SetColumn(thumb, 0);

        var name = new TextBlock
        {
            Text = result.Name,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 1);

        var appId = new TextBlock
        {
            Text = $"#{result.AppId}",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Foreground = (Brush)FindResource("TextDim"),
        };
        Grid.SetColumn(appId, 2);

        grid.Children.Add(thumb);
        grid.Children.Add(name);
        grid.Children.Add(appId);
        grid.Tag = result;

        _ = LoadThumbAsync(image, result.Image);
        return grid;
    }

    private static async Task LoadThumbAsync(Image target, string url)
    {
        var bitmap = await ImageLoader.FromUrlAsync(url, 200);
        if (bitmap is not null) target.Source = bitmap;
    }

    private async void Result_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (ResultList.SelectedItem is not Grid { Tag: SteamSearchResult result }) return;
        try
        {
            await ApplyDetailsAsync(result.AppId);
            ResultHint.Text = $"已填写「{result.Name}」。";
        }
        catch (Exception error)
        {
            ResultHint.Text = $"获取失败：{error.Message}";
        }
        ResultList.SelectedItem = null;
    }

    /// <summary>Fills the form from Steam, downloading the cover when the form has none.</summary>
    private async Task ApplyDetailsAsync(string appId)
    {
        var timeout = Library.Current.Settings.Timeout;
        var details = await SteamApi.FetchAppDetailsAsync(appId, timeout);
        AppIdBox.Text = appId;
        if (details.Name.Length > 0) NameBox.Text = details.Name;
        if (details.Description.Length > 0) DescriptionBox.Text = details.Description;
        DeveloperBox.Text = details.Developer;
        PublisherBox.Text = details.Publisher;
        ReleaseBox.Text = details.ReleaseDate;
        GenresBox.Text = string.Join(", ", details.Genres);

        // Without a path the game can only be started through Steam, so switch the mode for the user.
        if (ModeExe.IsChecked == true && ExeBox.Text.Trim().Length == 0)
        {
            ModeSteam.IsChecked = true;
            SyncMode();
        }

        if (_cover.Length == 0 && _pendingCover is null)
        {
            var (cover, hero) = await SteamApi.DownloadArtworkAsync(appId, timeout);
            if (cover.Length > 0) _cover = cover;
            if (hero.Length > 0) _cover = _cover.Length > 0 ? _cover : hero;
            ShowCover();
        }

        await LocalizeDescriptionAsync(timeout);
    }

    /// <summary>
    /// Replaces an English or Japanese description with Chinese, from a wiki or by translation.
    ///
    /// @param timeout per-attempt timeout
    /// </summary>
    private async Task LocalizeDescriptionAsync(int timeout)
    {
        var current = DescriptionBox.Text.Trim();
        if (current.Length == 0 || Translator.LooksChinese(current)) return;

        var chinese = await MetadataLocalizer.FindChineseAsync(new[] { NameBox.Text.Trim() }, timeout);
        if (chinese.Length == 0) chinese = await Translator.ToChineseAsync(current, timeout);
        if (chinese.Length > 0) DescriptionBox.Text = chinese;
    }

    // ------------------------------------------------------------- cover / paths

    private void PickCover_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择封面图片",
            Filter = "图片 (*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp)|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        _pendingCover = dialog.FileName;
        ShowCover();
    }

    private async void SteamCover_Click(object sender, RoutedEventArgs e)
    {
        var appId = AppIdBox.Text.Trim();
        if (appId.Length == 0)
        {
            MessageBox.Show(this, "请先填写 AppID，或点「自动获取信息」从名称解析。", "一切游戏管理家",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var (cover, hero) = await SteamApi.DownloadArtworkAsync(appId, Library.Current.Settings.Timeout);
            if (cover.Length == 0 && hero.Length == 0)
            {
                MessageBox.Show(this, "没有下载到封面，请检查网络或代理设置。", "一切游戏管理家", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _cover = cover.Length > 0 ? cover : hero;
            _pendingCover = null;
            ShowCover();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, $"获取封面失败：{error.Message}", "一切游戏管理家", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void GridDbCover_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "请先填写游戏名称。", "一切游戏管理家", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var (cover, hero, matched) = await SteamGridDb.FetchForNameAsync(name, _gameId);
            if (cover.Length > 0) _cover = cover;
            else if (hero.Length > 0) _cover = hero;
            _pendingCover = null;
            ShowCover();
            MessageBox.Show(this, $"已获取「{matched}」的封面。", "一切游戏管理家", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, $"获取失败：{error.Message}", "一切游戏管理家", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearCover_Click(object sender, RoutedEventArgs e)
    {
        _cover = "";
        _pendingCover = null;
        ShowCover();
    }

    private void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择游戏程序",
            Filter = "可执行文件 (*.exe)|*.exe|快捷方式 (*.lnk)|*.lnk|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        ExeBox.Text = dialog.FileName;
        if (WorkDirBox.Text.Trim().Length == 0) WorkDirBox.Text = Path.GetDirectoryName(dialog.FileName) ?? "";
        if (NameBox.Text.Trim().Length == 0) NameBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
    }

    private void BrowseDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择工作目录" };
        if (dialog.ShowDialog(this) != true) return;
        WorkDirBox.Text = dialog.FolderName;
    }

    private void AddCategory_Click(object sender, RoutedEventArgs e)
    {
        var name = NewCategoryBox.Text.Trim();
        if (name.Length == 0) return;
        Library.Current.EnsureCategory(name);
        _categories.Add(name);
        NewCategoryBox.Text = "";
        BuildChips();
    }

    // ------------------------------------------------------------- save / delete

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "请填写游戏名称。", "一切游戏管理家", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var mode = ModeSteam.IsChecked == true ? "steam" : ModeShell.IsChecked == true ? "shell" : "exe";
        if (mode == "steam" && AppIdBox.Text.Trim().Length == 0)
        {
            MessageBox.Show(this, "通过 Steam 启动需要填写 AppID。", "一切游戏管理家", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (mode == "exe" && ExeBox.Text.Trim().Length == 0)
        {
            MessageBox.Show(this, "请选择可执行文件，或把启动方式改成「通过 Steam 启动」。", "一切游戏管理家",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var target = _existing ?? new Game { Id = _gameId, AddedAt = DateTime.UtcNow.ToString("o") };
            var previousCover = target.Cover;

            target.Name = name;
            target.LaunchType = mode;
            target.ExePath = ExeBox.Text.Trim();
            target.AppId = AppIdBox.Text.Trim();
            target.Args = ArgsBox.Text.Trim();
            target.WorkDir = WorkDirBox.Text.Trim();
            target.Description = DescriptionBox.Text.Trim();
            target.Developer = DeveloperBox.Text.Trim();
            target.Publisher = PublisherBox.Text.Trim();
            target.ReleaseDate = ReleaseBox.Text.Trim();
            target.Favorite = FavoriteBox.IsChecked == true;
            target.Genres = GenresBox.Text.Split(',').Select(g => g.Trim()).Where(g => g.Length > 0).Distinct().ToList();
            target.Categories = _categories.OrderBy(c => c, StringComparer.CurrentCulture).ToList();

            if (_pendingCover is not null)
            {
                var extension = Path.GetExtension(_pendingCover);
                var relative = $"covers/{_gameId}_{DateTime.UtcNow.Ticks}{extension}";
                File.Copy(_pendingCover, Paths.Media(relative), overwrite: true);
                target.Cover = relative;
            }
            else
            {
                target.Cover = _cover;
            }

            if (_existing is null) Library.Current.Add(target);
            else Library.Current.Touch(target);

            // The replaced cover is no longer referenced by anything.
            if (previousCover.Length > 0 && previousCover != target.Cover) Library.Current.DeleteMedia(previousCover);

            DialogResult = true;
            Close();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, $"保存失败：{error.Message}", "一切游戏管理家", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_existing is null) return;
        var answer = MessageBox.Show(this,
            $"确定要从仓库中删除「{_existing.Name}」吗？\n\n只会删除仓库里的记录和已导入的图片，不会删除游戏本体。",
            "一切游戏管理家", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;
        Library.Current.Remove(_existing);
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Moves the window when the user drags its header, which replaces the caption.</summary>
    private void Chrome_Drag(object sender, MouseButtonEventArgs e) => FramelessWindow.Drag(this, e);
}
