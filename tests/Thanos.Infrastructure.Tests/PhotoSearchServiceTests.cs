using Thanos.Core;
using Thanos.Infrastructure;

namespace Thanos.Infrastructure.Tests;

public sealed class PhotoSearchServiceTests
{
    [Fact]
    public async Task Exact_reference_path_is_excluded_but_copy_can_match()
    {
        var reference = Path.GetFullPath("reference.jpg");
        var copy = Path.GetFullPath("copy.jpg");
        var files = new FakeFiles([reference, copy, Path.GetFullPath("other.jpg")]);
        await using var embeddings = new FakeEmbeddings(reference, copy);
        var matches = await new PhotoSearchService(files, embeddings).FindSimilarAsync(reference, [new(1, Environment.CurrentDirectory)], .9f, TestContext.Current.CancellationToken);
        var match = Assert.Single(matches);
        Assert.Equal(copy, match.Photo.FullPath);
    }

    private sealed class FakeFiles(string[] paths) : IFileEnumerationService
    {
        public async IAsyncEnumerable<FileCandidate> EnumerateAsync(IReadOnlyCollection<ScanFolder> folders,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var path in paths) { yield return new(1, path, 1, DateTimeOffset.UnixEpoch); await Task.Yield(); }
        }
    }

    private sealed class FakeEmbeddings(string reference, string copy) : IImageEmbeddingService
    {
        public int Dimension => 2;
        public string ModelVersion => "test";
        public string Provider => "test";
        public Task<float[]> EmbedAsync(string imagePath, CancellationToken cancellationToken) =>
            Task.FromResult(string.Equals(imagePath, reference, StringComparison.OrdinalIgnoreCase) || string.Equals(imagePath, copy, StringComparison.OrdinalIgnoreCase)
                ? new[] { 1f, 0f } : new[] { 0f, 1f });
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
