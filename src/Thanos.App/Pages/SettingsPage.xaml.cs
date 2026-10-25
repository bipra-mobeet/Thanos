using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Thanos.Core;

namespace Thanos.App.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage() => InitializeComponent();

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        var settings = await AppServices.Settings.LoadAsync(CancellationToken.None);
        ThresholdBox.Value = Math.Round(settings.SimilarityThreshold * 100);
        GpuToggle.IsOn = settings.UseGpu;
        RecycleBinOption.IsChecked = settings.DeleteMode == DeleteMode.RecycleBin;
        PermanentOption.IsChecked = settings.DeleteMode == DeleteMode.Permanent;
        ConfirmDeleteCheck.IsChecked = settings.ConfirmBeforeDelete;
        ThemeBox.SelectedIndex = (int)settings.Theme;
        PermanentWarning.IsOpen = settings.DeleteMode == DeleteMode.Permanent;
    }

    private void PermanentOption_Checked(object sender, RoutedEventArgs e) => PermanentWarning.IsOpen = PermanentOption.IsChecked == true;

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var threshold = double.IsNaN(ThresholdBox.Value) ? 90 : ThresholdBox.Value;
        var settings = new AppSettings(
            (float)Math.Clamp(threshold / 100d, .01, 1),
            GpuToggle.IsOn,
            PermanentOption.IsChecked == true ? DeleteMode.Permanent : DeleteMode.RecycleBin,
            ConfirmDeleteCheck.IsChecked == true,
            (AppTheme)Math.Clamp(ThemeBox.SelectedIndex, 0, 2));
        await AppServices.Settings.SaveAsync(settings, CancellationToken.None);
        App.MainWindow.ApplyTheme(settings.Theme);
        SavedBar.IsOpen = true;
    }

    private async void ViewLogs_Click(object sender, RoutedEventArgs e)
    {
        var text = await AppServices.Logs.ReadNewestAsync(CancellationToken.None);
        var viewer = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), MinWidth = 720, MinHeight = 460 };
        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Newest Thanos log",
            Content = new ScrollViewer { Content = viewer, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            CloseButtonText = "Close"
        }.ShowAsync();
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => AppServices.Logs.OpenFolder();

    private async void ClearLogs_Click(object sender, RoutedEventArgs e)
    {
        var count = AppServices.Logs.ClearOlderThan(TimeSpan.FromDays(7));
        await new ContentDialog { XamlRoot = XamlRoot, Title = "Old logs cleared", Content = $"Removed {count:N0} log files older than 7 days.", CloseButtonText = "Close" }.ShowAsync();
    }
}
