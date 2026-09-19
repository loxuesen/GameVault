using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GameVault;

/// <summary>
/// The about box: what the program is, who made it, where its data comes from,
/// and the disclaimers that go with redistributing other people's game information.
/// </summary>
public partial class AboutWindow : Window
{
    /// <summary>
    /// The disclaimers, in the order they are shown.
    ///
    /// They are kept as data rather than markup so the wording can be reviewed as prose.
    /// </summary>
    private static readonly string[] Disclaimers =
    {
        "本程序是免费的个人作品，按「现状」提供，不附带任何明示或暗示的担保。使用本程序产生的任何后果由使用者自行承担。",

        "本程序与 Valve、Steam 以及任何游戏开发商、发行商均无关联，也未获得其授权、赞助或认可。" +
        "Steam 及相关标识是 Valve Corporation 的商标，此处仅用于说明程序的用途。",

        "本程序不提供、不下载、不分发任何游戏本体，也不包含任何破解或绕过正版验证的功能。" +
        "它只负责启动你自己电脑上已经存在的程序，游戏本身请通过正规渠道获取。",

        "游戏中显示的名称、封面、简介、开发商等资料来自 Steam 公开接口、VNDB、萌娘百科、SteamGridDB 等第三方站点，" +
        "版权归各自权利人所有，本程序仅出于识别和介绍游戏的目的取用。若权利人认为有不妥，可联系移除。",

        "当找不到中文简介时，程序会调用公开的机器翻译接口自动翻译。译文可能与原文有出入，仅供参考，不代表原作者的表述。",

        "启动功能是按你填写的路径调用系统去运行程序。因使用该功能产生的问题" +
        "（包括但不限于反作弊判定、账号封禁、存档损坏、程序冲突），作者不承担责任。",

        "游戏库、封面和壁纸都保存在程序目录下的 data 文件夹中，请自行做好备份。" +
        "因误删、硬盘故障或程序缺陷造成的数据丢失，作者不承担责任。",

        "「开机自动启动」会在当前用户的注册表启动项里写入一条记录，不涉及系统级修改，可随时在设置中取消。",
    };

    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"版本 {Version} · Windows · .NET 8 · WPF";
        DataModeText.Text = Paths.IsPortable ? "便携模式：数据保存在程序旁边" : "安装模式：数据保存在用户目录";
        DataPathText.Text = Paths.DataDir;
        BuildDisclaimers();
    }

    /// <summary>@return the assembly version, falling back to a fixed string</summary>
    private static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version is { } version
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "1.0.0";

    /// <summary>Renders the numbered disclaimers into the scrolling body.</summary>
    private void BuildDisclaimers()
    {
        for (var index = 0; index < Disclaimers.Length; index++)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var number = new TextBlock
            {
                Text = $"{index + 1}.",
                FontSize = 12.5,
                LineHeight = 21,
                Width = 22,
                Foreground = (Brush)FindResource("TextDim"),
                VerticalAlignment = VerticalAlignment.Top,
            };
            var text = new TextBlock
            {
                Text = Disclaimers[index],
                FontSize = 12.5,
                LineHeight = 21,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0xA9, 0xB8, 0xC6)),
            };
            Grid.SetColumn(number, 0);
            Grid.SetColumn(text, 1);
            row.Children.Add(number);
            row.Children.Add(text);
            DisclaimerList.Children.Add(row);
        }
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => Launcher.OpenExternal(Paths.DataDir);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Moves the window when the user drags its header, which replaces the caption.</summary>
    private void Chrome_Drag(object sender, MouseButtonEventArgs e) => FramelessWindow.Drag(this, e);
}
