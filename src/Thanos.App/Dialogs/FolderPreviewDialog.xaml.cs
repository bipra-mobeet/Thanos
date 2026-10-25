using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using Thanos.App.ViewModels;
using Thanos.Infrastructure;

namespace Thanos.App.Dialogs;

public sealed partial class FolderPreviewDialog : ContentDialog
{
    private readonly ObservableCollection<FolderPreviewItemViewModel> _items = [];
    private bool _loaded;

    public string FolderPath { get; }

    public FolderPreviewDialog(string folderPath)
    {
        FolderPath = folderPath;
        InitializeComponent();
        FolderPathText.Text = folderPath;
        ContentGrid.ItemsSource = _items;
    }

    private async void Dialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            var directories = Directory.EnumerateDirectories(FolderPath)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToArray();
            var photos = Directory.EnumerateFiles(FolderPath)
                .Where(ImageFileTypes.IsSupportedExtension)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Take(40)
                .ToArray();
            foreach (var directory in directories) _items.Add(new(directory, true));
            var photoItems = photos.Select(path => new FolderPreviewItemViewModel(path, false)).ToArray();
            foreach (var photo in photoItems) _items.Add(photo);
            SummaryText.Text = $"{directories.Length:N0} subfolders and {photos.Length:N0} photos shown";
            if (_items.Count == 0) SummaryText.Text = "This folder has no supported photos or subfolders at this level.";

            foreach (var photo in photoItems)
            {
                try { await photo.LoadThumbnailAsync(CancellationToken.None); }
                catch (Exception exception) { Log.Debug(exception, "Could not preview folder item {PhotoPath}", photo.FullPath); }
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            SummaryText.Text = "This folder's contents could not be read.";
            IsPrimaryButtonEnabled = false;
        }
    }
}
