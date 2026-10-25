using Thanos.Core;
using Thanos.Infrastructure;

namespace Thanos.App;

public static class AppServices
{
    public static SqliteRepository Repository { get; } = new(AppPaths.Database);
    public static IFileEnumerationService Files { get; } = new FileEnumerationService();
    public static IImageMetadataService Metadata { get; } = new ImageMetadataService();
    public static IThumbnailService Thumbnails { get; } = new ThumbnailService();
    public static IPhotoDeletionService Deletion { get; } = new WindowsPhotoDeletionService();
    public static ILogViewerService Logs { get; } = new LogViewerService();
    public static ISettingsRepository Settings => Repository;
    public static ScanCoordinator Scans { get; } = new();
    private static IImageEmbeddingService? _embedding;
    private static readonly SemaphoreSlim EmbeddingGate = new(1, 1);

    public static Task InitializeAsync() => Repository.InitializeAsync(CancellationToken.None);

    public static async Task<IScanService> CreateScanServiceAsync()
    {
        var embedding = await GetEmbeddingAsync();
        return new ScanService(Files, Metadata, embedding, Repository, new HnswVectorIndex(), new SimilarityGroupingService());
    }

    public static async Task<IPhotoSearchService> CreateSearchServiceAsync() =>
        new PhotoSearchService(Files, await GetEmbeddingAsync());

    private static async Task<IImageEmbeddingService> GetEmbeddingAsync()
    {
        if (_embedding is not null) return _embedding;
        await EmbeddingGate.WaitAsync();
        try
        {
            if (_embedding is not null) return _embedding;
            var settings = await Settings.LoadAsync(CancellationToken.None);
            var modelPath = Path.Combine(AppContext.BaseDirectory, "models", "dinov2-small.onnx");
            _embedding = new OnnxImageEmbeddingService(modelPath, settings.UseGpu);
            return _embedding;
        }
        finally { EmbeddingGate.Release(); }
    }
}
