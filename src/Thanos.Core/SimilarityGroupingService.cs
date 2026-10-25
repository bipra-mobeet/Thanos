namespace Thanos.Core;

public sealed class SimilarityGroupingService : ISimilarityGroupingService
{
    public IReadOnlyList<SimilarityGroup> CreateGroups(
        IReadOnlyCollection<Photo> photos,
        IVectorIndexService index,
        float threshold,
        int neighborCount = 50)
    {
        if (threshold is < -1 or > 1) throw new ArgumentOutOfRangeException(nameof(threshold));
        var usable = photos.Where(x => !x.IsMissing && x.Embedding is not null).OrderBy(x => x.Id).ToArray();
        var byId = usable.ToDictionary(x => x.Id);
        var edges = usable.ToDictionary(x => x.Id, _ => new HashSet<long>());

        foreach (var photo in usable)
        {
            foreach (var neighbor in index.Search(photo.Embedding!, neighborCount + 1, photo.Id))
            {
                if (neighbor.Similarity < threshold || !byId.ContainsKey(neighbor.PhotoId)) continue;
                edges[photo.Id].Add(neighbor.PhotoId);
                edges[neighbor.PhotoId].Add(photo.Id);
            }
        }

        var visited = new HashSet<long>();
        var groups = new List<SimilarityGroup>();
        foreach (var photo in usable)
        {
            if (!visited.Add(photo.Id) || edges[photo.Id].Count == 0) continue;
            var component = Traverse(photo.Id, edges, visited).Select(id => byId[id]).ToArray();
            foreach (var validated in SplitChainedComponent(component, threshold))
            {
                if (validated.Length < 2) continue;
                var representative = ChooseRepresentative(validated);
                var members = validated
                    .Select(x => new SimilarityGroupMember(x, SimilarityMath.Cosine(representative.Embedding!, x.Embedding!)))
                    .OrderByDescending(x => x.SimilarityScore)
                    .ThenBy(x => x.Photo.FullPath, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                groups.Add(new SimilarityGroup(Guid.NewGuid(), representative.Id, members, threshold, DateTimeOffset.UtcNow));
            }
        }

        return groups.OrderByDescending(x => x.MemberCount).ThenBy(x => x.RepresentativePhotoId).ToArray();
    }

    public static Photo ChooseRepresentative(IEnumerable<Photo> photos) => photos
        .OrderByDescending(x => x.PixelCount)
        .ThenByDescending(x => x.Sharpness)
        .ThenBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase)
        .First();

    private static IEnumerable<long> Traverse(long start, IReadOnlyDictionary<long, HashSet<long>> edges, HashSet<long> visited)
    {
        var queue = new Queue<long>();
        queue.Enqueue(start);
        while (queue.TryDequeue(out var current))
        {
            yield return current;
            foreach (var next in edges[current].Order())
                if (visited.Add(next)) queue.Enqueue(next);
        }
    }

    // Requiring every member to match a medoid prevents broad A~B~C chains.
    private static IEnumerable<Photo[]> SplitChainedComponent(Photo[] component, float threshold)
    {
        var remaining = new HashSet<long>(component.Select(x => x.Id));
        var byId = component.ToDictionary(x => x.Id);
        while (remaining.Count > 0)
        {
            var seed = remaining.Select(id => byId[id])
                .OrderByDescending(candidate => remaining.Count(id => SimilarityMath.Cosine(candidate.Embedding!, byId[id].Embedding!) >= threshold))
                .ThenBy(x => x.Id)
                .First();
            var cluster = new List<Photo> { seed };
            foreach (var candidate in remaining.Select(id => byId[id])
                         .Where(x => x.Id != seed.Id && SimilarityMath.Cosine(seed.Embedding!, x.Embedding!) >= threshold)
                         .OrderByDescending(x => SimilarityMath.Cosine(seed.Embedding!, x.Embedding!))
                         .ThenBy(x => x.Id))
            {
                if (cluster.All(member => SimilarityMath.Cosine(member.Embedding!, candidate.Embedding!) >= threshold))
                    cluster.Add(candidate);
            }
            foreach (var photo in cluster) remaining.Remove(photo.Id);
            yield return cluster.ToArray();
        }
    }
}
