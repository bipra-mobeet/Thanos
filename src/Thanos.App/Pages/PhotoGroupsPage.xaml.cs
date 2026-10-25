using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;
using Thanos.App.ViewModels;
using Thanos.Core;

namespace Thanos.App.Pages;

public sealed partial class PhotoGroupsPage : Page
{
    private readonly ObservableCollection<PhotoGroupViewModel> _groups = [];
    private readonly ObservableCollection<ScanFolder> _welcomeFolders = [];
    private readonly ObservableCollection<ScanSessionViewModel> _sessions = [];
    private readonly SemaphoreSlim _thumbnailConcurrency = new(4, 4);
    private CancellationTokenSource? _thumbnailCancellation;
    private string? _previewPath;
    private PhotoPreviewWindow? _previewWindow;
    private string? _lastClickedPhotoPath;
    private long _lastPhotoClickMilliseconds;
    private Guid? _activeSessionId;

    public PhotoGroupsPage()
    {
        InitializeComponent();
        GroupList.ItemsSource = _groups;
        WelcomeFolders.ItemsSource = _welcomeFolders;
        PreviousSessionsList.ItemsSource = _sessions;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        AppServices.Scans.ProgressChanged += ScanProgressChanged;
        if (AppServices.Scans.IsRunning && AppServices.Scans.ActiveTask is { } task)
        {
            await ShowNewScanAsync(hasPreviousResults: true);
            WelcomeProgress.Visibility = Visibility.Visible;
            StartScanButton.IsEnabled = false;
            ScanProgressChanged(AppServices.Scans.LastProgress);
            await ObserveScanAsync(task);
            return;
        }
        await ReloadAsync(showSessionChoice: true);
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _thumbnailCancellation?.Cancel();
        AppServices.Scans.ProgressChanged -= ScanProgressChanged;
        _previewWindow?.Close();
        DetachSelectionHandlers();
    }

    private async Task ReloadAsync(bool showSessionChoice)
    {
        _thumbnailCancellation?.Cancel();
        _thumbnailCancellation?.Dispose();
        _thumbnailCancellation = null;
        DetachSelectionHandlers();
        _groups.Clear();

        _sessions.Clear();
        foreach (var session in await AppServices.Repository.GetScanSessionsAsync(CancellationToken.None))
            _sessions.Add(new(session));

        if (_sessions.Count == 0)
        {
            _activeSessionId = null;
            await ShowNewScanAsync(hasPreviousResults: false);
            return;
        }

        var activeSession = _sessions.FirstOrDefault(item => item.Session.Id == _activeSessionId) ?? _sessions[0];
        _activeSessionId = activeSession.Session.Id;
        var groups = await AppServices.Repository.GetGroupsAsync(activeSession.Session.Id, CancellationToken.None);
        foreach (var group in groups)
        {
            var viewModel = new PhotoGroupViewModel(group);
            _groups.Add(viewModel);
            foreach (var item in viewModel.Selection.Items)
                item.PropertyChanged += SelectionItem_PropertyChanged;
        }
        UpdateDeleteSelection();

        UpdateGroupSummary();
        PreviousSessionSummary.Text = _sessions.Count == 1
            ? "1 completed scan is available locally"
            : $"{_sessions.Count:N0} completed scans are available locally";
        if (showSessionChoice)
        {
            ShowSessionChoice();
            return;
        }

        if (_groups.Count == 0)
        {
            await ShowNewScanAsync(hasPreviousResults: _sessions.Any(session => session.CanOpen));
            return;
        }

        ShowGroups();
    }

    private void ShowSessionChoice()
    {
        _thumbnailCancellation?.Cancel();
        WelcomePanel.Visibility = Visibility.Collapsed;
        GroupsPanel.Visibility = Visibility.Collapsed;
        SessionChoicePanel.Visibility = Visibility.Visible;
    }

    private async Task ShowNewScanAsync(bool hasPreviousResults)
    {
        _thumbnailCancellation?.Cancel();
        SessionChoicePanel.Visibility = Visibility.Collapsed;
        GroupsPanel.Visibility = Visibility.Collapsed;
        WelcomePanel.Visibility = Visibility.Visible;
        ReturnToResultsButton.Visibility = hasPreviousResults ? Visibility.Visible : Visibility.Collapsed;

        _welcomeFolders.Clear();
        var folders = await AppServices.Repository.GetFoldersAsync(CancellationToken.None);
        var hasIndexedPhotos = false;
        foreach (var folder in folders)
            hasIndexedPhotos |= await AppServices.Repository.CountPhotosAsync(folder.Id, CancellationToken.None) > 0;

        WelcomeTitle.Text = hasPreviousResults
            ? "Start a New Scan"
            : hasIndexedPhotos ? "No Similar Photos Found" : "Start Your First Scan";
        WelcomeDescription.Text = hasPreviousResults
            ? "Choose folders for a new scan. Your five most recent completed scans remain available in Scan History."
            : hasIndexedPhotos
                ? "The latest scan found no matching groups. Unmatched photos stay hidden."
                : "Select one or more folders to find similar photos, remove duplicates and free up space on your device.";
        StartScanButton.IsEnabled = _welcomeFolders.Count > 0;
    }

    private void ShowGroups()
    {
        SessionChoicePanel.Visibility = Visibility.Collapsed;
        WelcomePanel.Visibility = Visibility.Collapsed;
        GroupsPanel.Visibility = Visibility.Visible;

        _thumbnailCancellation?.Cancel();
        _thumbnailCancellation?.Dispose();
        _thumbnailCancellation = new();
        if (GroupList.SelectedIndex != 0)
            GroupList.SelectedIndex = 0;
        else if (GroupList.SelectedItem is PhotoGroupViewModel selected)
            _ = ShowSelectedGroupAsync(selected);

        _ = PrefetchThumbnailsAsync(_thumbnailCancellation.Token);
    }

    private async Task PrefetchThumbnailsAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var group in _groups.Take(50))
                await PrefetchGroupPreviewAsync(group, cancellationToken);
            foreach (var group in _groups.Skip(50))
                await PrefetchGroupPreviewAsync(group, cancellationToken);
        }
        catch (OperationCanceledException) { }
    }

    private async Task PrefetchGroupPreviewAsync(PhotoGroupViewModel group, CancellationToken cancellationToken)
    {
        await LoadRepresentativeSafeAsync(group, cancellationToken);
        await Task.WhenAll(group.PreviewItems.Select(item => LoadThumbnailSafeAsync(item, cancellationToken)));
    }

    private async Task LoadRepresentativeSafeAsync(PhotoGroupViewModel group, CancellationToken cancellationToken)
    {
        await _thumbnailConcurrency.WaitAsync(cancellationToken);
        try { await group.LoadRepresentativeAsync(cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { Log.Warning(exception, "Could not load group thumbnail {PhotoPath}", group.Selection.Recommended.Photo.FullPath); }
        finally { _thumbnailConcurrency.Release(); }
    }

    private async Task LoadThumbnailSafeAsync(PhotoItemViewModel item, CancellationToken cancellationToken)
    {
        await _thumbnailConcurrency.WaitAsync(cancellationToken);
        try { await item.LoadThumbnailAsync(cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { Log.Warning(exception, "Could not load thumbnail {PhotoPath}", item.Path); }
        finally { _thumbnailConcurrency.Release(); }
    }

    private async void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GroupList.SelectedItem is PhotoGroupViewModel group)
            await ShowSelectedGroupAsync(group);
    }

    private async Task ShowSelectedGroupAsync(PhotoGroupViewModel group)
    {
        SelectedGroupTitle.Text = group.Title;
        PhotoGrid.ItemsSource = group.Items;
        await SetPreviewAsync(group.Selection.Recommended.Photo.FullPath);
        if (_thumbnailCancellation is null) return;
        try
        {
            await Task.WhenAll(group.Items.Select(item => LoadThumbnailSafeAsync(item, _thumbnailCancellation.Token)));
        }
        catch (OperationCanceledException) { }
    }

    private async void PhotoGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PhotoGrid.SelectedItem is PhotoItemViewModel item) await SetPreviewAsync(item.Path);
    }

    private async void IgnoreGroup_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PhotoGroupViewModel group) return;
        foreach (var item in group.Selection.Items) item.PropertyChanged -= SelectionItem_PropertyChanged;
        await AppServices.Repository.RemoveGroupAsync(group.Group.Id, CancellationToken.None);
        _groups.Remove(group);
        UpdateDeleteSelection();
        UpdateGroupSummary();
        if (_groups.Count == 0)
        {
            PhotoGrid.ItemsSource = null;
            PreviewImage.Source = null;
            _previewPath = null;
            await ReloadAsync(showSessionChoice: true);
        }
    }

    private void SelectionItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PhotoSelectionItem.IsSelected)) UpdateDeleteSelection();
    }

    private void UpdateDeleteSelection()
    {
        var count = _groups.Sum(group => group.Selection.SelectedPaths().Count);
        DeleteSelectionSummary.Text = count == 0
            ? "No photos selected for deletion"
            : $"{count:N0} {(count == 1 ? "photo" : "photos")} selected for deletion";
        DeleteButtonText.Text = count == 0 ? "Delete Selected Photos" : $"Delete Selected Photos ({count:N0})";
        DeleteSelectedButton.IsEnabled = count > 0;
    }

    private void DetachSelectionHandlers()
    {
        foreach (var item in _groups.SelectMany(group => group.Selection.Items))
            item.PropertyChanged -= SelectionItem_PropertyChanged;
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = _groups.SelectMany(x => x.Selection.SelectedPaths()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (selected.Length == 0) { await ShowMessageAsync("Nothing selected", "Select one or more photos first."); return; }
        var settings = await AppServices.Settings.LoadAsync(CancellationToken.None);
        if (settings.ConfirmBeforeDelete || settings.DeleteMode == DeleteMode.Permanent)
        {
            var warning = settings.DeleteMode == DeleteMode.Permanent
                ? $"Permanently delete {selected.Length:N0} photos? This cannot be undone."
                : $"Move {selected.Length:N0} photos to the Recycle Bin?";
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Delete selected photos?",
                Content = warning,
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                PrimaryButtonStyle = (Style)Application.Current.Resources["DangerButtonStyle"]
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        var result = await AppServices.Deletion.DeleteAsync(selected, settings.DeleteMode, CancellationToken.None);
        var removed = result.Items.Where(x => x.Success).Select(x => x.Path).ToArray();
        await AppServices.Repository.RemovePhotosAsync(removed, CancellationToken.None);
        await ReloadAsync(showSessionChoice: false);
        if (result.Failed > 0)
            await ShowMessageAsync("Some files could not be deleted", $"Deleted {result.Succeeded:N0}; failed {result.Failed:N0}.");
    }

    private async void StartNewSession_Click(object sender, RoutedEventArgs e) =>
        await ShowNewScanAsync(hasPreviousResults: _sessions.Count > 0);

    private async void OpenSession_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ScanSessionViewModel session || !session.CanOpen) return;
        _activeSessionId = session.Session.Id;
        await ReloadAsync(showSessionChoice: false);
    }

    private void ShowSessionHistory_Click(object sender, RoutedEventArgs e) => ShowSessionChoice();
    private void ReturnToResults_Click(object sender, RoutedEventArgs e) => ShowSessionChoice();

    private async void AddWelcomeFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickerHelpers.PickFolderAsync(App.MainWindow, XamlRoot);
        if (path is null) return;
        var folder = await AppServices.Repository.UpsertFolderAsync(path, true, CancellationToken.None);
        if (_welcomeFolders.All(x => x.Id != folder.Id)) _welcomeFolders.Add(folder);
        StartScanButton.IsEnabled = true;
    }

    private void RemoveWelcomeFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ScanFolder folder) return;
        _welcomeFolders.Remove(folder);
        StartScanButton.IsEnabled = _welcomeFolders.Count > 0;
    }

    private async void StartScan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            WelcomeProgress.Visibility = Visibility.Visible;
            StartScanButton.IsEnabled = false;
            var settings = await AppServices.Settings.LoadAsync(CancellationToken.None);
            await AppServices.Scans.StartAsync(_welcomeFolders.ToArray(), settings.SimilarityThreshold);
            _activeSessionId = null;
            if (IsLoaded) await ReloadAsync(showSessionChoice: false);
        }
        catch (OperationCanceledException) { if (IsLoaded) WelcomeProgressText.Text = "Scan cancelled safely."; }
        catch (Exception exception) { if (IsLoaded) await ShowMessageAsync("Scan could not start", exception.Message); }
        finally
        {
            if (IsLoaded)
            {
                WelcomeProgress.Visibility = Visibility.Collapsed;
                StartScanButton.IsEnabled = _welcomeFolders.Count > 0;
            }
        }
    }

    private async Task ObserveScanAsync(Task<ScanSummary> task)
    {
        try
        {
            await task;
            _activeSessionId = null;
            if (IsLoaded) await ReloadAsync(showSessionChoice: false);
        }
        catch (OperationCanceledException) { if (IsLoaded) WelcomeProgressText.Text = "Scan cancelled safely."; }
        catch (Exception exception) { if (IsLoaded) await ShowMessageAsync("Scan failed", exception.Message); }
        finally { if (IsLoaded) WelcomeProgress.Visibility = Visibility.Collapsed; }
    }

    private void ScanProgressChanged(ScanProgress value) =>
        WelcomeProgressText.Text = value.Total > 0 ? $"{value.Phase}: {value.Processed:N0}/{value.Total:N0}" : value.Phase.ToString();

    private void CancelScan_Click(object sender, RoutedEventArgs e) => AppServices.Scans.Cancel();

    private void UpdateGroupSummary()
    {
        var session = _sessions.FirstOrDefault(item => item.Session.Id == _activeSessionId);
        var completed = session is null ? string.Empty : $" • Scan from {session.Session.CompletedAtUtc.LocalDateTime:g}";
        GroupSummary.Text = $"Found {_groups.Count:N0} groups with similar photos  ({_groups.Sum(x => x.Count):N0} photos total){completed}";
    }

    private async Task SetPreviewAsync(string path)
    {
        _previewPath = path;
        PreviewPath.Text = path;
        try
        {
            var displayPath = await AppServices.Thumbnails.GetThumbnailAsync(path, 1200, CancellationToken.None);
            if (string.Equals(_previewPath, path, StringComparison.OrdinalIgnoreCase))
                PreviewImage.Source = new BitmapImage(new Uri(displayPath)) { DecodePixelWidth = 1200 };
        }
        catch (Exception exception) { Log.Warning(exception, "Could not load preview {PhotoPath}", path); }
    }

    private async void PreviewImage_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_previewPath is not null && IsMouseDoubleClick(_previewPath, e))
            await OpenFullScreenPreviewAsync(_previewPath);
    }

    private async void Photo_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string path && IsMouseDoubleClick(path, e))
            await OpenFullScreenPreviewAsync(path);
    }

    private bool IsMouseDoubleClick(string path, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return false;
        var now = Environment.TickCount64;
        var isDoubleClick = string.Equals(path, _lastClickedPhotoPath, StringComparison.OrdinalIgnoreCase) &&
                            now - _lastPhotoClickMilliseconds <= 500;
        _lastClickedPhotoPath = path;
        _lastPhotoClickMilliseconds = now;
        if (isDoubleClick) e.Handled = true;
        return isDoubleClick;
    }

    private async Task OpenFullScreenPreviewAsync(string path)
    {
        try
        {
            var displayPath = await AppServices.Thumbnails.GetThumbnailAsync(path, 2400, CancellationToken.None);
            _previewWindow?.Close();
            _previewWindow = new PhotoPreviewWindow(displayPath, path);
            _previewWindow.Closed += (_, _) => _previewWindow = null;
            _previewWindow.Activate();
        }
        catch (Exception exception) { await ShowMessageAsync("Photo preview unavailable", exception.Message); }
    }

    private async Task ShowMessageAsync(string title, string message) =>
        await new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = message, CloseButtonText = "Close" }.ShowAsync();
}
