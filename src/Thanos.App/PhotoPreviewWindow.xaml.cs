using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace Thanos.App;

public sealed partial class PhotoPreviewWindow : Window
{
    public PhotoPreviewWindow(string displayImagePath, string originalPhotoPath)
    {
        InitializeComponent();
        Title = $"Preview - {Path.GetFileName(originalPhotoPath)}";
        FullScreenImage.Source = new BitmapImage(new Uri(displayImagePath));
        PhotoPathText.Text = originalPhotoPath;
        var windowId = Win32Interop.GetWindowIdFromWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        AppWindow.GetFromWindowId(windowId).SetPresenter(AppWindowPresenterKind.FullScreen);
        PreviewRoot.Loaded += (_, _) => PreviewRoot.Focus(FocusState.Programmatic);
    }

    private void PreviewRoot_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Space or VirtualKey.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
