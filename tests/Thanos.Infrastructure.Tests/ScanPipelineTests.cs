using Microsoft.Data.Sqlite;
using Thanos.Core;
using Thanos.Infrastructure;

namespace Thanos.Infrastructure.Tests;

public sealed class ScanPipelineTests
{
    private static readonly byte[] FixtureImage = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public async Task Scan_caches_unchanged_files_reembeds_changes_marks_missing_and_hides_singletons()
    {
        var directory = Directory.CreateTempSubdirectory("thanos-scan-");
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await CreateFixturesAsync(directory.FullName, cancellationToken);
            var repository = new SqliteRepository(Path.Combine(directory.FullName, "index.db"));
            await repository.InitializeAsync(cancellationToken);
            var folder = await repository.UpsertFolderAsync(directory.FullName, true, cancellationToken);
            await using var embedding = new FixtureEmbeddingService();
            var service = new ScanService(new FileEnumerationService(), new ImageMetadataService(), embedding,
                repository, new HnswVectorIndex(), new SimilarityGroupingService());

            var first = await service.ScanAsync([folder], .9f, null, cancellationToken);
            Assert.Equal(1, first.Groups);
            Assert.Equal(5, first.Matched);
            Assert.Equal(1, first.HiddenUnmatched);
            Assert.Equal(6, embedding.Calls);
            var firstSession = Assert.Single(await repository.GetScanSessionsAsync(cancellationToken));
            Assert.Equal(first.SessionId, firstSession.Id);
            var visible = Assert.Single(await repository.GetGroupsAsync(first.SessionId, cancellationToken));
            Assert.Equal(5, visible.MemberCount);
            await repository.RemoveGroupAsync(visible.Id, cancellationToken);
            Assert.Empty(await repository.GetGroupsAsync(first.SessionId, cancellationToken));
            Assert.Equal(0, (await repository.GetScanSessionsAsync(cancellationToken)).Single(x => x.Id == first.SessionId).GroupCount);

            var second = await service.ScanAsync([folder], .9f, null, cancellationToken);
            Assert.Equal(6, second.Reused);
            Assert.Equal(6, embedding.Calls);
            Assert.Single(await repository.GetGroupsAsync(second.SessionId, cancellationToken));

            var unrelated = Path.Combine(directory.FullName, "unrelated.jpg");
            File.SetLastWriteTimeUtc(unrelated, File.GetLastWriteTimeUtc(unrelated).AddSeconds(5));
            var third = await service.ScanAsync([folder], .9f, null, cancellationToken);
            Assert.Equal(1, third.Embedded);
            Assert.Equal(7, embedding.Calls);

            File.Delete(unrelated);
            await service.ScanAsync([folder], .9f, null, cancellationToken);
            Assert.Null((await repository.GetIndexedPhotosAsync([folder.Id], cancellationToken)).SingleOrDefault(x => x.FullPath == unrelated));

            await service.ScanAsync([folder], .9f, null, cancellationToken);
            await service.ScanAsync([folder], .9f, null, cancellationToken);
            var retained = await repository.GetScanSessionsAsync(cancellationToken);
            Assert.Equal(5, retained.Count);
            Assert.DoesNotContain(retained, session => session.Id == first.SessionId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Scan_sessions_only_group_photos_from_their_selected_folders()
    {
        var root = Directory.CreateTempSubdirectory("thanos-independent-scans-");
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var firstDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, "first"));
            var secondDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, "second"));
            await CreatePairAsync(firstDirectory.FullName, cancellationToken);
            await CreatePairAsync(secondDirectory.FullName, cancellationToken);

            var repository = new SqliteRepository(Path.Combine(root.FullName, "index.db"));
            await repository.InitializeAsync(cancellationToken);
            var firstFolder = await repository.UpsertFolderAsync(firstDirectory.FullName, true, cancellationToken);
            var secondFolder = await repository.UpsertFolderAsync(secondDirectory.FullName, true, cancellationToken);
            await using var embedding = new FixtureEmbeddingService();
            var service = new ScanService(new FileEnumerationService(), new ImageMetadataService(), embedding,
                repository, new HnswVectorIndex(), new SimilarityGroupingService());

            var firstScan = await service.ScanAsync([firstFolder], .9f, null, cancellationToken);
            var secondScan = await service.ScanAsync([secondFolder], .9f, null, cancellationToken);
            await repository.SetFolderStateAsync(firstFolder.Id, true, false, cancellationToken);

            var firstGroup = Assert.Single(await repository.GetGroupsAsync(firstScan.SessionId, cancellationToken));
            var secondGroup = Assert.Single(await repository.GetGroupsAsync(secondScan.SessionId, cancellationToken));
            Assert.All(firstGroup.Members, member => Assert.Equal(firstFolder.Id, member.Photo.RootFolderId));
            Assert.All(secondGroup.Members, member => Assert.Equal(secondFolder.Id, member.Photo.RootFolderId));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(true);
        }
    }

    [Fact]
    public async Task Rescanning_an_overlapping_child_folder_reuses_photos_in_that_session()
    {
        var root = Directory.CreateTempSubdirectory("thanos-overlapping-scans-");
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var child = Directory.CreateDirectory(Path.Combine(root.FullName, "child"));
            await CreatePairAsync(child.FullName, cancellationToken);
            var repository = new SqliteRepository(Path.Combine(root.FullName, "index.db"));
            await repository.InitializeAsync(cancellationToken);
            var parentFolder = await repository.UpsertFolderAsync(root.FullName, true, cancellationToken);
            var childFolder = await repository.UpsertFolderAsync(child.FullName, true, cancellationToken);
            await using var embedding = new FixtureEmbeddingService();
            var service = new ScanService(new FileEnumerationService(), new ImageMetadataService(), embedding,
                repository, new HnswVectorIndex(), new SimilarityGroupingService());

            await service.ScanAsync([parentFolder], .9f, null, cancellationToken);
            var childScan = await service.ScanAsync([childFolder], .9f, null, cancellationToken);

            Assert.Equal(2, childScan.Reused);
            var childGroup = Assert.Single(await repository.GetGroupsAsync(childScan.SessionId, cancellationToken));
            Assert.All(childGroup.Members, member => Assert.Equal(childFolder.Id, member.Photo.RootFolderId));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(true);
        }
    }

    [Fact]
    public async Task File_enumeration_includes_standard_and_windows_codec_image_extensions()
    {
        var directory = Directory.CreateTempSubdirectory("thanos-formats-");
        try
        {
            foreach (var name in new[] { "one.jpg", "two.heic", "three.avif", "four.tiff", "five.dng", "six.cr3", "ignore.txt", "clip.mp4", "movie.mov" })
                await File.WriteAllTextAsync(Path.Combine(directory.FullName, name), "fixture", TestContext.Current.CancellationToken);

            var candidates = new List<FileCandidate>();
            await foreach (var candidate in new FileEnumerationService().EnumerateAsync(
                               [new ScanFolder(1, directory.FullName)], TestContext.Current.CancellationToken))
                candidates.Add(candidate);

            Assert.Equal(6, candidates.Count);
            Assert.DoesNotContain(candidates, item => item.FullPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(candidates, item => item.FullPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(candidates, item => item.FullPath.EndsWith(".mov", StringComparison.OrdinalIgnoreCase));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task Windows_codec_reads_metadata_and_creates_a_thumbnail()
    {
        var directory = Directory.CreateTempSubdirectory("thanos-wic-");
        string? thumbnail = null;
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var source = Path.Combine(directory.FullName, "source.png");
            await File.WriteAllBytesAsync(source, FixtureImage, cancellationToken);

            var dimensions = await new ImageMetadataService().ReadAsync(source, cancellationToken);
            thumbnail = await new ThumbnailService().GetThumbnailAsync(source, 64, cancellationToken);

            Assert.Equal((1, 1), dimensions);
            Assert.True(File.Exists(thumbnail));
            Assert.True(new FileInfo(thumbnail).Length > 0);
        }
        finally
        {
            if (thumbnail is not null) File.Delete(thumbnail);
            directory.Delete(true);
        }
    }

    private static async Task CreatePairAsync(string directory, CancellationToken cancellationToken)
    {
        await File.WriteAllBytesAsync(Path.Combine(directory, "one.jpg"), FixtureImage, cancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(directory, "two.jpg"), FixtureImage, cancellationToken);
    }

    private static async Task CreateFixturesAsync(string directory, CancellationToken cancellationToken)
    {
        var originalPath = Path.Combine(directory, "original.jpg");
        await File.WriteAllBytesAsync(originalPath, FixtureImage, cancellationToken);
        File.Copy(originalPath, Path.Combine(directory, "exact-copy.jpg"));
        await File.WriteAllBytesAsync(Path.Combine(directory, "recompressed.jpg"), FixtureImage, cancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(directory, "cropped.jpg"), FixtureImage, cancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(directory, "similar-different.jpg"), FixtureImage, cancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(directory, "unrelated.jpg"), FixtureImage, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, "corrupt.jpg"), "not an image", cancellationToken);
    }

    private sealed class FixtureEmbeddingService : IImageEmbeddingService
    {
        public int Calls { get; private set; }
        public int Dimension => 2;
        public string ModelVersion => "fixture-v1";
        public string Provider => "test";
        public Task<float[]> EmbedAsync(string imagePath, CancellationToken cancellationToken)
        {
            Calls++;
            var file = Path.GetFileName(imagePath);
            var value = file switch
            {
                "similar-different.jpg" => new[] { .95f, .31f },
                "unrelated.jpg" => new[] { 0f, 1f },
                _ => new[] { 1f, .01f }
            };
            return Task.FromResult(SimilarityMath.Normalize(value));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
