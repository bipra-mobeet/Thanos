using Serilog;
using Thanos.Core;

namespace Thanos.Infrastructure;

public sealed class ScanService(
    IFileEnumerationService files,
    IImageMetadataService metadata,
    IImageEmbeddingService embeddings,
    IScanRepository repository,
    IVectorIndexService index,
    ISimilarityGroupingService grouping) : IScanService
{
    public async Task<ScanSummary> ScanAsync(
        IReadOnlyCollection<ScanFolder> folders,
        float threshold,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var runId = Guid.NewGuid();
        Log.Information("Scan started. RunId={RunId} Folders={FolderCount} Threshold={Threshold}", runId, folders.Count, threshold);
        try
        {
            progress?.Report(new(ScanPhase.Enumerating, 0, 0));
            var candidates = new List<FileCandidate>();
            await foreach (var candidate in files.EnumerateAsync(folders, cancellationToken)) candidates.Add(candidate);
            Log.Information("Enumerated {CandidateCount} candidate images", candidates.Count);

            var reused = 0;
            var embedded = 0;
            for (var i = 0; i < candidates.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = candidates[i];
                progress?.Report(new(ScanPhase.Indexing, i, candidates.Count, candidate.FullPath));
                var current = await repository.FindPhotoAsync(candidate.FullPath, cancellationToken);
                if (current is { IsMissing: false, Embedding: not null } &&
                    current.FileSize == candidate.FileSize &&
                    current.LastWriteUtc.UtcDateTime == candidate.LastWriteUtc.UtcDateTime &&
                    current.EmbeddingModelVersion == embeddings.ModelVersion)
                {
                    if (current.RootFolderId != candidate.RootFolderId)
                        await repository.UpsertPhotoAsync(current with { RootFolderId = candidate.RootFolderId }, cancellationToken);
                    reused++;
                    continue;
                }

                try
                {
                    var size = await metadata.ReadAsync(candidate.FullPath, cancellationToken);
                    var vector = await embeddings.EmbedAsync(candidate.FullPath, cancellationToken);
                    var photo = new Photo(current?.Id ?? 0, candidate.RootFolderId, candidate.FullPath, candidate.FileSize,
                        candidate.LastWriteUtc, size.Width, size.Height, current?.ContentHash, vector,
                        embeddings.ModelVersion, DateTimeOffset.UtcNow);
                    await repository.UpsertPhotoAsync(photo, cancellationToken);
                    embedded++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    Log.Warning(exception, "Could not index image {ImagePath}", candidate.FullPath);
                }
            }

            foreach (var folder in folders)
            {
                var paths = candidates.Where(x => x.RootFolderId == folder.Id).Select(x => x.FullPath).ToArray();
                await repository.MarkMissingAsync(folder.Id, paths, cancellationToken);
                await repository.SetFolderScanCompletedAsync(folder.Id, DateTimeOffset.UtcNow, cancellationToken);
            }

            progress?.Report(new(ScanPhase.BuildingSimilarityIndex, 0, candidates.Count));
            var indexed = await repository.GetIndexedPhotosAsync(folders.Select(folder => folder.Id).ToArray(), cancellationToken);
            index.Clear();
            foreach (var photo in indexed) index.Add(photo.Id, photo.Embedding!);

            progress?.Report(new(ScanPhase.Grouping, indexed.Count, indexed.Count));
            var groups = grouping.CreateGroups(indexed, index, threshold);
            var matched = groups.Sum(x => x.MemberCount);
            var session = new ScanSession(runId, DateTimeOffset.UtcNow, BuildFolderSummary(folders), folders.Count, groups.Count, matched);
            await repository.SaveScanSessionAsync(session, groups, cancellationToken);
            var summary = new ScanSummary(runId, candidates.Count, reused, embedded, groups.Count, matched, Math.Max(0, indexed.Count - matched));
            progress?.Report(new(ScanPhase.Completed, candidates.Count, candidates.Count));
            Log.Information("Scan complete. Groups={Groups} MatchedPhotos={Matched} HiddenUnmatched={Hidden} Reused={Reused} Embedded={Embedded}",
                summary.Groups, summary.Matched, summary.HiddenUnmatched, reused, embedded);
            return summary;
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new(ScanPhase.Cancelled, 0, 0));
            Log.Information("Scan cancelled. RunId={RunId}", runId);
            throw;
        }
        catch (Exception exception)
        {
            progress?.Report(new(ScanPhase.Failed, 0, 0));
            Log.Error(exception, "Scan failed. RunId={RunId}", runId);
            throw;
        }
    }

    private static string BuildFolderSummary(IEnumerable<ScanFolder> folders)
    {
        var names = folders.Select(folder =>
            Path.GetFileName(folder.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } name
                ? name
                : folder.FullPath)
            .ToArray();
        if (names.Length <= 2) return string.Join(", ", names);
        return $"{string.Join(", ", names.Take(2))} +{names.Length - 2} more";
    }
}

public sealed class PhotoSearchService(
    IFileEnumerationService files,
    IImageEmbeddingService embeddings) : IPhotoSearchService
{
    public async Task<IReadOnlyList<SimilarityGroupMember>> FindSimilarAsync(
        string referencePath,
        IReadOnlyCollection<ScanFolder> folders,
        float threshold,
        CancellationToken cancellationToken)
    {
        var canonicalReference = Path.GetFullPath(referencePath);
        var reference = await embeddings.EmbedAsync(canonicalReference, cancellationToken);
        var results = new List<SimilarityGroupMember>();
        await foreach (var candidate in files.EnumerateAsync(folders, cancellationToken))
        {
            if (string.Equals(Path.GetFullPath(candidate.FullPath), canonicalReference, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var vector = await embeddings.EmbedAsync(candidate.FullPath, cancellationToken);
                var similarity = SimilarityMath.Cosine(reference, vector);
                if (similarity < threshold) continue;
                results.Add(new(new Photo(0, candidate.RootFolderId, candidate.FullPath, candidate.FileSize, candidate.LastWriteUtc,
                    Embedding: vector, EmbeddingModelVersion: embeddings.ModelVersion), similarity));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { Log.Warning(exception, "Could not compare image {ImagePath}", candidate.FullPath); }
        }
        return results.OrderByDescending(x => x.SimilarityScore).ThenBy(x => x.Photo.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
