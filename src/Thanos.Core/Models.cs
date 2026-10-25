namespace Thanos.Core;

public sealed record ScanFolder(
    long Id,
    string FullPath,
    bool IncludeSubfolders = true,
    bool IsEnabled = true,
    DateTimeOffset? LastScanUtc = null);

public sealed record Photo(
    long Id,
    long RootFolderId,
    string FullPath,
    long FileSize,
    DateTimeOffset LastWriteUtc,
    int? Width = null,
    int? Height = null,
    string? ContentHash = null,
    float[]? Embedding = null,
    string? EmbeddingModelVersion = null,
    DateTimeOffset? IndexedAtUtc = null,
    bool IsMissing = false,
    double Sharpness = 0)
{
    public long PixelCount => (long)(Width ?? 0) * (Height ?? 0);
}

public sealed record SimilarityGroupMember(Photo Photo, float SimilarityScore);

public sealed record SimilarityGroup(
    Guid Id,
    long RepresentativePhotoId,
    IReadOnlyList<SimilarityGroupMember> Members,
    float ThresholdUsed,
    DateTimeOffset CreatedAtUtc)
{
    public int MemberCount => Members.Count;
}

public sealed record ScanSession(
    Guid Id,
    DateTimeOffset CompletedAtUtc,
    string FolderSummary,
    int FolderCount,
    int GroupCount,
    int MatchedPhotoCount);

public enum DeleteMode { RecycleBin, Permanent }
public enum AppTheme { System, Light, Dark }
public enum ScanPhase { Idle, Enumerating, Indexing, BuildingSimilarityIndex, Grouping, Completed, Cancelled, Failed }

public sealed record ScanProgress(ScanPhase Phase, int Processed, int Total, string? CurrentPath = null);
public sealed record ScanSummary(Guid SessionId, int Scanned, int Reused, int Embedded, int Groups, int Matched, int HiddenUnmatched);
public sealed record DeletionItemResult(string Path, bool Success, string? Error = null);
public sealed record DeleteResult(IReadOnlyList<DeletionItemResult> Items)
{
    public int Succeeded => Items.Count(x => x.Success);
    public int Failed => Items.Count - Succeeded;
}

public sealed record AppSettings(
    float SimilarityThreshold = 0.90f,
    bool UseGpu = true,
    DeleteMode DeleteMode = DeleteMode.RecycleBin,
    bool ConfirmBeforeDelete = true,
    AppTheme Theme = AppTheme.System);

public sealed record FileCandidate(long RootFolderId, string FullPath, long FileSize, DateTimeOffset LastWriteUtc);
public sealed record VectorNeighbor(long PhotoId, float Similarity);
