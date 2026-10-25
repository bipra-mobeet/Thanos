using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Thanos.App.ViewModels;
using Thanos.Core;

namespace Thanos.App.Pages;

public sealed partial class ScanFoldersPage : Page
{
    private readonly ObservableCollection<FolderRowViewModel> _folders = [];

    public ScanFoldersPage()
    {
        InitializeComponent();
        FolderList.ItemsSource = _folders;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        AppServices.Scans.ProgressChanged += ScanProgressChanged;
        await ReloadAsync();
        if (AppServices.Scans.IsRunning && AppServices.Scans.ActiveTask is { } task)
        {
            ProgressPanel.Visibility = Visibility.Visible;
            ScanProgressChanged(AppServices.Scans.LastProgress);
            await ObserveScanAsync(task);
        }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e) => AppServices.Scans.ProgressChanged -= ScanProgressChanged;

    private async Task ReloadAsync()
    {
        _folders.Clear();
        foreach (var folder in await AppServices.Repository.GetFoldersAsync(CancellationToken.None))
            _folders.Add(new(folder, await AppServices.Repository.CountPhotosAsync(folder.Id, CancellationToken.None)));
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickerHelpers.PickFolderAsync(App.MainWindow, XamlRoot);
        if (path is null) return;
        await AppServices.Repository.UpsertFolderAsync(path, true, CancellationToken.None);
        await ReloadAsync();
    }

    private async void FolderState_Changed(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not FolderRowViewModel row) return;
        await AppServices.Repository.SetFolderStateAsync(row.Folder.Id, row.IncludeSubfolders, row.IsEnabled, CancellationToken.None);
    }

    private async void ScanAll_Click(object sender, RoutedEventArgs e) => await ScanAsync(_folders.Where(x => x.IsEnabled).Select(x => x.CurrentFolder).ToArray());
    private async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is FolderRowViewModel row) await ScanAsync([row.CurrentFolder]);
    }

    private async Task ScanAsync(IReadOnlyCollection<ScanFolder> folders)
    {
        if (folders.Count == 0) { ShowStatus("Add or include at least one folder.", InfoBarSeverity.Warning); return; }
        try
        {
            ProgressPanel.Visibility = Visibility.Visible;
            var settings = await AppServices.Settings.LoadAsync(CancellationToken.None);
            var summary = await AppServices.Scans.StartAsync(folders, settings.SimilarityThreshold);
            if (IsLoaded)
            {
                ShowStatus($"{summary.Groups:N0} groups · {summary.Matched:N0} matched photos · {summary.HiddenUnmatched:N0} unmatched hidden", InfoBarSeverity.Success);
                await ReloadAsync();
            }
        }
        catch (OperationCanceledException) { if (IsLoaded) ShowStatus("Scan cancelled safely.", InfoBarSeverity.Informational); }
        catch (Exception exception) { if (IsLoaded) ShowStatus(exception.Message, InfoBarSeverity.Error); }
        finally { if (IsLoaded) ProgressPanel.Visibility = Visibility.Collapsed; }
    }

    private async Task ObserveScanAsync(Task<ScanSummary> task)
    {
        try
        {
            var summary = await task;
            if (IsLoaded)
            {
                ShowStatus($"{summary.Groups:N0} groups · {summary.Matched:N0} matched photos · {summary.HiddenUnmatched:N0} unmatched hidden", InfoBarSeverity.Success);
                await ReloadAsync();
            }
        }
        catch (OperationCanceledException) { if (IsLoaded) ShowStatus("Scan cancelled safely.", InfoBarSeverity.Informational); }
        catch (Exception exception) { if (IsLoaded) ShowStatus(exception.Message, InfoBarSeverity.Error); }
        finally { if (IsLoaded) ProgressPanel.Visibility = Visibility.Collapsed; }
    }

    private void ScanProgressChanged(ScanProgress value)
    {
        ProgressText.Text = value.Total > 0 ? $"{value.Phase} — {value.Processed:N0} of {value.Total:N0}" : value.Phase.ToString();
        ScanProgress.IsIndeterminate = value.Total == 0;
        ScanProgress.Maximum = Math.Max(1, value.Total);
        ScanProgress.Value = value.Processed;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => AppServices.Scans.Cancel();

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not FolderRowViewModel row) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Remove folder from Thanos?",
            Content = "Its local index data will be removed. Your photos will not be deleted.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await AppServices.Repository.RemoveFolderAsync(row.Folder.Id, CancellationToken.None);
        await ReloadAsync();
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }
}
