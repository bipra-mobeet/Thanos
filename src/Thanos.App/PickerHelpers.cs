using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Thanos.App.Dialogs;
using Thanos.Infrastructure;
using Windows.Storage.Pickers;

namespace Thanos.App;

public static class PickerHelpers
{
    public static async Task<string?> PickFolderAsync(Window window, XamlRoot xamlRoot)
    {
        while (true)
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
            var path = (await picker.PickSingleFolderAsync())?.Path;
            if (path is null) return null;
            var preview = new FolderPreviewDialog(path) { XamlRoot = xamlRoot };
            var result = await preview.ShowAsync();
            if (result == ContentDialogResult.Primary) return path;
            if (result != ContentDialogResult.Secondary) return null;
        }
    }

    public static async Task<string?> PickImageAsync(Window window)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        foreach (var extension in ImageFileTypes.Extensions.Order(StringComparer.OrdinalIgnoreCase)) picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        return (await picker.PickSingleFileAsync())?.Path;
    }
}
