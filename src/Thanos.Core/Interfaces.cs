namespace Thanos.Core;

public interface IImageEmbeddingService : IAsyncDisposable
{
    int Dimension { get; }
    string ModelVersion { get; }
    string Provider { get; }
    Task<float[]> EmbedAsync(string imagePath, CancellationToken cancellationToken);
}

public interface IFileEnumerationService
{
    IAsyncEnumerable<FileCandidate> EnumerateAsync(
        IReadOnlyCollection<ScanFolder> folders,
        CancellationToken cancellationToken);
}

public interface IImageMetadataService
{
    Task<(int Width, int Height)> ReadAsync(string imagePath, CancellationToken cancellationToken);
}

public interface IThumbnailService
{
    Task<string> GetThumbnailAsync(string imagePath, int size, CancellationToken cancellationToken);
}

public interface IVectorIndexService
{
    void Clear();
    void Add(long photoId, ReadOnlySpan<float> normalizedVector);
    IReadOnlyList<VectorNeighbor> Search(ReadOnlySpan<float> normalizedVector, int count, long? excludedPhotoId = null);
}

public interface ISimilarityGroupingService
{
    IReadOnlyList<SimilarityGroup> CreateGroups(
        IReadOnlyCollection<Photo> photos,
        IVectorIndexService index,
        float threshold,
        int neighborCount = 50);
}

public interface IScanRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ScanFolder>> GetFoldersAsync(CancellationToken cancellationToken);
    Task<ScanFolder> UpsertFolderAsync(string path, bool includeSubfolders, CancellationToken cancellationToken);
    Task SetFolderStateAsync(long folderId, bool includeSubfolders, bool isEnabled, CancellationToken cancellationToken);
    Task SetFolderScanCompletedAsync(long folderId, DateTimeOffset completedAt, CancellationToken cancellationToken);
    Task<int> CountPhotosAsync(long folderId, CancellationToken cancellationToken);
    Task RemoveFolderAsync(long folderId, CancellationToken cancellationToken);
    Task<Photo?> FindPhotoAsync(string fullPath, CancellationToken cancellationToken);
    Task<IReadOnlyList<Photo>> GetIndexedPhotosAsync(IReadOnlyCollection<long> rootFolderIds, CancellationToken cancellationToken);
    Task<Photo> UpsertPhotoAsync(Photo photo, CancellationToken cancellationToken);
    Task MarkMissingAsync(long rootFolderId, IReadOnlyCollection<string> existingPaths, CancellationToken cancellationToken);
    Task SaveScanSessionAsync(ScanSession session, IReadOnlyCollection<SimilarityGroup> groups, CancellationToken cancellationToken);
    Task<IReadOnlyList<ScanSession>> GetScanSessionsAsync(CancellationToken cancellationToken);
    Task SetGroupRepresentativeAsync(Guid groupId, long photoId, CancellationToken cancellationToken);
    Task RemoveGroupAsync(Guid groupId, CancellationToken cancellationToken);
    Task<IReadOnlyList<SimilarityGroup>> GetGroupsAsync(Guid scanSessionId, CancellationToken cancellationToken);
    Task RemovePhotosAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken);
}

public interface ISettingsRepository
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);
}

public interface IPhotoDeletionService
{
    Task<DeleteResult> DeleteAsync(IReadOnlyCollection<string> paths, DeleteMode mode, CancellationToken cancellationToken);
}

public interface IScanService
{
    Task<ScanSummary> ScanAsync(
        IReadOnlyCollection<ScanFolder> folders,
        float threshold,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IPhotoSearchService
{
    Task<IReadOnlyList<SimilarityGroupMember>> FindSimilarAsync(
        string referencePath,
        IReadOnlyCollection<ScanFolder> folders,
        float threshold,
        CancellationToken cancellationToken);
}

public interface ILogViewerService
{
    Task<string> ReadNewestAsync(CancellationToken cancellationToken);
    void OpenFolder();
    int ClearOlderThan(TimeSpan age);
}
