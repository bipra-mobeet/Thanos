using Thanos.Core;

namespace Thanos.Infrastructure;

public sealed class HnswVectorIndex : IVectorIndexService
{
    private sealed class Node(long id, float[] vector, int level)
    {
        public long Id { get; } = id;
        public float[] Vector { get; } = vector;
        public int Level { get; } = level;
        public Dictionary<int, HashSet<long>> Neighbors { get; } = Enumerable.Range(0, level + 1).ToDictionary(x => x, _ => new HashSet<long>());
    }

    private readonly Dictionary<long, Node> _nodes = [];
    private readonly int _maxConnections;
    private readonly int _efConstruction;
    private long? _entryPoint;
    private int _maxLevel = -1;

    public HnswVectorIndex(int maxConnections = 16, int efConstruction = 100)
    {
        if (maxConnections < 2) throw new ArgumentOutOfRangeException(nameof(maxConnections));
        _maxConnections = maxConnections;
        _efConstruction = Math.Max(efConstruction, maxConnections);
    }

    public void Clear()
    {
        _nodes.Clear();
        _entryPoint = null;
        _maxLevel = -1;
    }

    public void Add(long photoId, ReadOnlySpan<float> normalizedVector)
    {
        if (_nodes.ContainsKey(photoId)) throw new InvalidOperationException($"Photo {photoId} is already indexed.");
        var vector = SimilarityMath.Normalize(normalizedVector);
        var level = RandomLevel(photoId);
        var node = new Node(photoId, vector, level);
        _nodes.Add(photoId, node);
        if (_entryPoint is null)
        {
            _entryPoint = photoId;
            _maxLevel = level;
            return;
        }

        var entry = _nodes[_entryPoint.Value];
        for (var layer = _maxLevel; layer > level; layer--)
            entry = GreedyClosest(vector, entry, layer);

        for (var layer = Math.Min(level, _maxLevel); layer >= 0; layer--)
        {
            var candidates = SearchLayer(vector, [entry], _efConstruction, layer);
            foreach (var neighbor in candidates.Take(_maxConnections))
            {
                node.Neighbors[layer].Add(neighbor.Id);
                neighbor.Neighbors[layer].Add(node.Id);
                Prune(neighbor, layer);
            }
            if (candidates.Count > 0) entry = candidates[0];
        }

        if (level > _maxLevel)
        {
            _entryPoint = photoId;
            _maxLevel = level;
        }
    }

    public IReadOnlyList<VectorNeighbor> Search(ReadOnlySpan<float> normalizedVector, int count, long? excludedPhotoId = null)
    {
        if (count <= 0 || _entryPoint is null) return [];
        var vector = SimilarityMath.Normalize(normalizedVector);
        var entry = _nodes[_entryPoint.Value];
        for (var layer = _maxLevel; layer > 0; layer--) entry = GreedyClosest(vector, entry, layer);

        return SearchLayer(vector, [entry], Math.Max(64, count * 4), 0)
            .Where(x => x.Id != excludedPhotoId)
            .Take(count)
            .Select(x => new VectorNeighbor(x.Id, SimilarityMath.Cosine(vector, x.Vector)))
            .ToArray();
    }

    private Node GreedyClosest(ReadOnlySpan<float> query, Node entry, int layer)
    {
        var best = entry;
        var bestScore = SimilarityMath.Cosine(query, best.Vector);
        bool improved;
        do
        {
            improved = false;
            if (!best.Neighbors.TryGetValue(layer, out var neighbors)) break;
            foreach (var id in neighbors)
            {
                var candidate = _nodes[id];
                var score = SimilarityMath.Cosine(query, candidate.Vector);
                if (score <= bestScore) continue;
                best = candidate;
                bestScore = score;
                improved = true;
            }
        } while (improved);
        return best;
    }

    private List<Node> SearchLayer(ReadOnlySpan<float> query, IEnumerable<Node> entries, int ef, int layer)
    {
        var candidates = new PriorityQueue<Node, float>();
        var best = new PriorityQueue<Node, float>();
        var visited = new HashSet<long>();
        foreach (var entry in entries)
        {
            var similarity = SimilarityMath.Cosine(query, entry.Vector);
            visited.Add(entry.Id);
            candidates.Enqueue(entry, -similarity);
            best.Enqueue(entry, similarity);
        }

        while (candidates.TryDequeue(out var current, out var negativeSimilarity))
        {
            var similarity = -negativeSimilarity;
            best.TryPeek(out _, out var worstSimilarity);
            if (best.Count >= ef && similarity < worstSimilarity) break;
            if (!current.Neighbors.TryGetValue(layer, out var neighbors)) continue;
            foreach (var id in neighbors)
            {
                if (!visited.Add(id)) continue;
                var candidate = _nodes[id];
                var candidateSimilarity = SimilarityMath.Cosine(query, candidate.Vector);
                best.TryPeek(out _, out worstSimilarity);
                if (best.Count >= ef && candidateSimilarity <= worstSimilarity) continue;
                candidates.Enqueue(candidate, -candidateSimilarity);
                best.Enqueue(candidate, candidateSimilarity);
                if (best.Count > ef) best.Dequeue();
            }
        }

        return best.UnorderedItems
            .OrderByDescending(x => x.Priority)
            .ThenBy(x => x.Element.Id)
            .Select(x => x.Element)
            .ToList();
    }

    private void Prune(Node node, int layer)
    {
        var neighbors = node.Neighbors[layer];
        if (neighbors.Count <= _maxConnections) return;
        var keep = neighbors.Select(id => _nodes[id])
            .OrderByDescending(x => SimilarityMath.Cosine(node.Vector, x.Vector))
            .ThenBy(x => x.Id)
            .Take(_maxConnections)
            .Select(x => x.Id)
            .ToHashSet();
        foreach (var removed in neighbors.Except(keep).ToArray())
        {
            neighbors.Remove(removed);
            if (_nodes[removed].Neighbors.TryGetValue(layer, out var reverse)) reverse.Remove(node.Id);
        }
    }

    private static int RandomLevel(long id)
    {
        var value = unchecked((ulong)id + 0x9E3779B97F4A7C15UL);
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        value ^= value >> 31;
        var level = 0;
        while ((value & 15UL) == 0 && level < 16) { level++; value >>= 4; }
        return level;
    }
}
