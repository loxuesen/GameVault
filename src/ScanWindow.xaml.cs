using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GameVault;

/// <summary>
/// Lists the Steam titles installed on this machine and imports the selected ones.
///
/// The scan reads Steam's own library files, so it works without the network; only the
/// thumbnails and the metadata fetched afterwards need a connection.
/// </summary>
public partial class ScanWindow : Window
{
    private readonly List<ScanRow> _rows = new();

    /// <summary>Games created by this dialog, so the caller can fetch their metadata.</summary>
    public List<Game> Imported { get; } = new();

    public ScanWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Rescan();
    }

    /// <summary>Reads the local Steam libraries and rebuilds the list.</summary>
    private void Rescan()
    {
        StatusText.Text = "正在扫描 Steam 库…";
        GameList.ItemsSource = null;
        _rows.Clear();

        List<InstalledGame> installed;
        try
        {
            installed = SteamApi.ScanInstalledGames();
        }
        catch (Exception error)
        {
            StatusText.Text = $"扫描失败：{error.Message}";
            return;
        }

        var known = Library.Current.Games.Select(g => g.AppId).Where(id => id.Length > 0).ToHashSet();
        foreach (var game in installed)
        {
            game.Imported = known.Contains(game.AppId);
            _rows.Add(new ScanRow(game));
        }

        if (_rows.Count == 0)
        {
            StatusText.Text = "没有找到已安装的 Steam 游戏。请确认 Steam 已安装并至少下载过一个游戏，" +
                              "或者改用主界面的「添加游戏」手动导入。";
            return;
        }

        GameList.ItemsSource = _rows;
        StatusText.Visibility = Visibility.Collapsed;
        UpdateCounts();
        _ = LoadThumbsAsync();
    }

    private async Task LoadThumbsAsync()
    {
        foreach (var row in _rows.ToList())
        {
            var bitmap = await ImageLoader.FromUrlAsync(row.Game.Image, 200);
            if (bitmap is not null) row.Thumb = bitmap;
        }
    }

    private void UpdateCounts()
    {
        var selectable = _rows.Count(r => r.CanSelect);
        var selected = _rows.Count(r => r.Selected && r.CanSelect);
        CountText.Text = $"共 {_rows.Count} 个，已选中 {selected} 个";
        SelectAll.IsChecked = selectable > 0 && selected == selectable;
        ImportButton.IsEnabled = selected > 0;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var check = SelectAll.IsChecked == true;
        foreach (var row in _rows.Where(r => r.CanSelect)) row.Selected = check;
        UpdateCounts();
    }

    private void Rescan_Click(object sender, RoutedEventArgs e) => Rescan();

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _rows.Where(r => r.Selected && r.CanSelect).ToList();
        if (chosen.Count == 0) return;

        foreach (var row in chosen)
        {
            var game = new Game
            {
                Id = Library.NewId(),
                Name = row.Game.Name,
                AppId = row.Game.AppId,
                LaunchType = "steam",
                ExePath = row.Game.InstallDir,
                WorkDir = row.Game.InstallDir,
                Source = "steam-scan",
                AddedAt = DateTime.UtcNow.ToString("o"),
            };
            Library.Current.Add(game);
            Imported.Add(game);
        }

        DialogResult = true;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Moves the window when the user drags its header, which replaces the caption.</summary>
    private void Chrome_Drag(object sender, MouseButtonEventArgs e) => FramelessWindow.Drag(this, e);
}
