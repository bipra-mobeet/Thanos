using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using Thanos.App.Pages;
using Thanos.Core;

namespace Thanos.App;

public sealed partial class MainWindow : Window
{
    private readonly AppWindow _appWindow;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Thanos by Bipra";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        var windowId = Win32Interop.GetWindowIdFromWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Resize(new Windows.Graphics.SizeInt32(1440, 900));
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Thanos.ico");
        if (File.Exists(iconPath)) _appWindow.SetIcon(iconPath);
        RootLayout.ActualThemeChanged += (_, _) => UpdateTitleBarColors();
        UpdateTitleBarColors();
        Navigation.SelectedItem = Navigation.MenuItems[0];
        ContentFrame.Navigate(typeof(PhotoGroupsPage));
        Closed += (_, _) => { Log.Information("App exited"); Log.CloseAndFlush(); };
    }

    public void ApplyTheme(AppTheme theme)
    {
        RootLayout.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        UpdateTitleBarColors();
    }

    private void UpdateTitleBarColors()
    {
        var dark = RootLayout.ActualTheme == ElementTheme.Dark;
        _appWindow.TitleBar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        _appWindow.TitleBar.ButtonInactiveForegroundColor = dark
            ? ColorHelper.FromArgb(255, 160, 174, 193)
            : ColorHelper.FromArgb(255, 75, 85, 99);
        _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        _appWindow.TitleBar.ButtonHoverBackgroundColor = dark
            ? ColorHelper.FromArgb(255, 42, 57, 75)
            : ColorHelper.FromArgb(255, 225, 235, 246);
        _appWindow.TitleBar.ButtonPressedBackgroundColor = dark
            ? ColorHelper.FromArgb(255, 56, 74, 96)
            : ColorHelper.FromArgb(255, 207, 222, 238);
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag) return;
        var page = tag switch
        {
            "groups" => typeof(PhotoGroupsPage),
            "folders" => typeof(ScanFoldersPage),
            "similar" => typeof(SimilarToSelectedPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(PhotoGroupsPage)
        };
        if (ContentFrame.CurrentSourcePageType != page) ContentFrame.Navigate(page);
    }

    private void FooterScanFolders_Click(object sender, RoutedEventArgs e)
    {
        Navigation.SelectedItem = Navigation.MenuItems
            .OfType<NavigationViewItem>()
            .First(item => Equals(item.Tag, "folders"));
    }
}
