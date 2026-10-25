using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Thanos.App.ViewModels;
using Thanos.Core;

namespace Thanos.App.Pages;

public sealed partial class SimilarToSelectedPage : Page
{
    private readonly ObservableCollection<ScanFolder> _folders = [];
    private readonly ObservableCollection<SearchResultViewModel> _results = [];
    private string? _referencePath;
    private CancellationTokenSource? _cancellation;

    public SimilarToSelectedPage()
    {
        InitializeComponent();
        SearchFolders.ItemsSource = _folders;
        ResultsGrid.ItemsSource = _results;
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e) => _cancellation?.Cancel();

    private async void ChooseReference_Click(object sender, RoutedEventArgs e)
    {
        _referencePath = await PickerHelpers.PickImageAsync(App.MainWindow);
        ReferencePathText.Text = _referencePath ?? "No photo selected";
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickerHelpers.PickFolderAsync(App.MainWindow, XamlRoot);
        if (path is null || _folders.Any(x => string.Equals(x.FullPath, path, StringComparison.OrdinalIgnoreCase))) return;
        _folders.Add(new ScanFolder(-(_folders.Count + 1), path));
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ScanFolder folder) _folders.Remove(folder);
    }

    private async void Find_Click(object sender, RoutedEventArgs e)
    {
        if (_referencePath is null || _folders.Count == 0)
        {
            ResultSummary.Text = "Choose one reference photo and at least one search folder.";
            return;
        }
        try
        {
            SetBusy(true);
            _results.Clear();
            _cancellation = new();
            var settings = await AppServices.Settings.LoadAsync(_cancellation.Token);
            var matches = await Task.Run(async () =>
            {
                var service = await AppServices.CreateSearchServiceAsync();
                return await service.FindSimilarAsync(_referencePath, _folders.ToArray(), settings.SimilarityThreshold, _cancellation.Token);
            }, _cancellation.Token);
            foreach (var match in matches) _results.Add(new(match));
            ResultSummary.Text = $"{_results.Count:N0} matches at or above {settings.SimilarityThreshold:P0}, highest similarity first.";
            foreach (var result in _results)
            {
                try { await result.LoadThumbnailAsync(_cancellation.Token); }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }
        catch (OperationCanceledException) { ResultSummary.Text = "Search cancelled."; }
        catch (Exception exception) { await ShowAsync("Search failed", exception.Message); }
        finally { SetBusy(false); _cancellation?.Dispose(); _cancellation = null; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cancellation?.Cancel();

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var paths = _results.Where(x => x.IsSelected).Select(x => x.FullPath).ToArray();
        if (paths.Length == 0) { await ShowAsync("Nothing selected", "Select one or more matches first."); return; }
        var settings = await AppServices.Settings.LoadAsync(CancellationToken.None);
        if (settings.ConfirmBeforeDelete || settings.DeleteMode == DeleteMode.Permanent)
        {
            var content = settings.DeleteMode == DeleteMode.Permanent
                ? $"Permanently delete {paths.Length:N0} matches? This cannot be undone."
                : $"Move {paths.Length:N0} matches to the Recycle Bin?";
            var confirm = new ContentDialog { XamlRoot = XamlRoot, Title = "Delete selected matches?", Content = content,
                PrimaryButtonText = "Delete", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
                PrimaryButtonStyle = (Style)Application.Current.Resources["DangerButtonStyle"] };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        }
        var result = await AppServices.Deletion.DeleteAsync(paths, settings.DeleteMode, CancellationToken.None);
        foreach (var deleted in result.Items.Where(x => x.Success).Select(x => x.Path).ToArray())
        {
            var item = _results.FirstOrDefault(x => string.Equals(x.FullPath, deleted, StringComparison.OrdinalIgnoreCase));
            if (item is not null) _results.Remove(item);
        }
        ResultSummary.Text = $"Deleted {result.Succeeded:N0}; failed {result.Failed:N0}.";
    }

    private void SetBusy(bool busy)
    {
        SearchProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        FindButton.IsEnabled = !busy;
    }

    private async Task ShowAsync(string title, string message) =>
        await new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = message, CloseButtonText = "Close" }.ShowAsync();
}
