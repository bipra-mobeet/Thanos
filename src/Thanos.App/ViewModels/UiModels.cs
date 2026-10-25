using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Thanos.Core;

namespace Thanos.App.ViewModels;

public abstract class Bindable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Changed(name);
        return true;
    }
}

public sealed class FolderRowViewModel(ScanFolder folder, int photoCount) : Bindable
{
    private bool _isEnabled = folder.IsEnabled;
    private bool _includeSubfolders = folder.IncludeSubfolders;
    public ScanFolder Folder { get; private set; } = folder;
    public ScanFolder CurrentFolder => Folder with { IsEnabled = IsEnabled, IncludeSubfolders = IncludeSubfolders };
    public string FullPath => Folder.FullPath;
    public string PhotoCountText => $"{photoCount:N0} photos";
    public string LastScannedText => Folder.LastScanUtc is null ? "Not scanned" : $"Scanned {Folder.LastScanUtc.Value.LocalDateTime:g}";
    public bool IsEnabled { get => _isEnabled; set => Set(ref _isEnabled, value); }
    public bool IncludeSubfolders { get => _includeSubfolders; set => Set(ref _includeSubfolders, value); }
    public void Update(ScanFolder value) { Folder = value; Changed(nameof(LastScannedText)); }
}

public sealed class ScanSessionViewModel
{
    public ScanSession Session { get; }
    public string Title => Session.CompletedAtUtc.LocalDateTime.ToString("MMM d, yyyy • h:mm tt");
    public string Summary => $"{Session.GroupCount:N0} groups • {Session.MatchedPhotoCount:N0} matched photos";
    public string Folders => Session.FolderCount == 1
        ? $"1 folder: {Session.FolderSummary}"
        : $"{Session.FolderCount:N0} folders: {Session.FolderSummary}";
    public bool CanOpen => Session.GroupCount > 0;

    public ScanSessionViewModel(ScanSession session) => Session = session;
}

public sealed class PhotoItemViewModel(PhotoSelectionItem selection) : Bindable
{
    private readonly SemaphoreSlim _thumbnailGate = new(1, 1);
    private BitmapImage? _thumbnail;
    private Visibility _thumbnailErrorVisibility = Visibility.Collapsed;
    public PhotoSelectionItem Selection { get; } = selection;
    public string Name => System.IO.Path.GetFileName(Selection.Photo.FullPath);
    public string Path => Selection.Photo.FullPath;
    public string SimilarityText => $"{Selection.Similarity:P0}";
    public Visibility RecommendedVisibility => Selection.IsRecommended ? Visibility.Visible : Visibility.Collapsed;
    public BitmapImage? Thumbnail { get => _thumbnail; private set => Set(ref _thumbnail, value); }
    public Visibility ThumbnailErrorVisibility { get => _thumbnailErrorVisibility; private set => Set(ref _thumbnailErrorVisibility, value); }

    public async Task LoadThumbnailAsync(CancellationToken cancellationToken)
    {
        if (Thumbnail is not null) return;
        await _thumbnailGate.WaitAsync(cancellationToken);
        try
        {
            if (Thumbnail is not null) return;
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    var path = await AppServices.Thumbnails.GetThumbnailAsync(Path, 128, cancellationToken);
                    Thumbnail = new BitmapImage(new Uri(path)) { DecodePixelWidth = 128 };
                    ThumbnailErrorVisibility = Visibility.Collapsed;
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch when (attempt < 2) { await Task.Delay(150, cancellationToken); }
                catch
                {
                    ThumbnailErrorVisibility = Visibility.Visible;
                    throw;
                }
            }
        }
        finally { _thumbnailGate.Release(); }
    }
}

public sealed class PhotoGroupViewModel : Bindable
{
    private BitmapImage? _thumbnail;
    public SimilarityGroup Group { get; }
    public PhotoGroupSelection Selection { get; }
    public ObservableCollection<PhotoItemViewModel> Items { get; }
    public IReadOnlyList<PhotoItemViewModel> PreviewItems { get; }
    public int Count => Group.MemberCount;
    public string Title => $"{Count:N0} similar photos";
    public BitmapImage? Thumbnail { get => _thumbnail; private set => Set(ref _thumbnail, value); }

    public PhotoGroupViewModel(SimilarityGroup group)
    {
        Group = group;
        Selection = new(group, preselectOthers: true);
        Items = new(Selection.Items.Select(x => new PhotoItemViewModel(x)));
        PreviewItems = Items.Take(6).ToArray();
    }

    public async Task LoadRepresentativeAsync(CancellationToken cancellationToken)
    {
        var path = Selection.Recommended.Photo.FullPath;
        var thumbnail = await AppServices.Thumbnails.GetThumbnailAsync(path, 160, cancellationToken);
        Thumbnail = new BitmapImage(new Uri(thumbnail)) { DecodePixelWidth = 160 };
    }
}

public sealed class SearchResultViewModel(SimilarityGroupMember member) : Bindable
{
    private bool _isSelected;
    private BitmapImage? _thumbnail;
    public SimilarityGroupMember Member { get; } = member;
    public string Name => Path.GetFileName(Member.Photo.FullPath);
    public string FullPath => Member.Photo.FullPath;
    public string SimilarityText => $"{Member.SimilarityScore:P0}";
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public BitmapImage? Thumbnail { get => _thumbnail; private set => Set(ref _thumbnail, value); }
    public async Task LoadThumbnailAsync(CancellationToken cancellationToken)
    {
        var path = await AppServices.Thumbnails.GetThumbnailAsync(FullPath, 220, cancellationToken);
        Thumbnail = new BitmapImage(new Uri(path)) { DecodePixelWidth = 220 };
    }
}

public sealed class FolderPreviewItemViewModel(string fullPath, bool isFolder) : Bindable
{
    private BitmapImage? _thumbnail;
    public string FullPath { get; } = fullPath;
    public string Name => System.IO.Path.GetFileName(FullPath);
    public bool IsFolder { get; } = isFolder;
    public Visibility FolderVisibility => IsFolder ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PhotoVisibility => IsFolder ? Visibility.Collapsed : Visibility.Visible;
    public BitmapImage? Thumbnail { get => _thumbnail; private set => Set(ref _thumbnail, value); }

    public async Task LoadThumbnailAsync(CancellationToken cancellationToken)
    {
        if (IsFolder || Thumbnail is not null) return;
        var path = await AppServices.Thumbnails.GetThumbnailAsync(FullPath, 160, cancellationToken);
        Thumbnail = new BitmapImage(new Uri(path)) { DecodePixelWidth = 160 };
    }
}
