using Thanos.Core;

namespace Thanos.Core.Tests;

public sealed class SimilarityTests
{
    [Fact]
    public void Cosine_returns_expected_value()
    {
        var value = SimilarityMath.Cosine([1, 0], [.8f, .6f]);
        Assert.Equal(.8f, value, 5);
    }

    [Fact]
    public void Threshold_rejects_below_threshold_and_hides_singletons()
    {
        var photos = new[] { Photo(1, [1, 0]), Photo(2, [.8f, .6f]), Photo(3, [0, 1]) };
        var groups = new SimilarityGroupingService().CreateGroups(photos, ExactIndex(photos), .81f);
        Assert.Empty(groups);
    }

    [Fact]
    public void Connected_photos_form_a_group()
    {
        var photos = new[] { Photo(1, [1, 0]), Photo(2, [.98f, .2f]), Photo(3, [0, 1]) };
        var groups = new SimilarityGroupingService().CreateGroups(photos, ExactIndex(photos), .9f);
        var group = Assert.Single(groups);
        Assert.Equal(new long[] { 1, 2 }, group.Members.Select(x => x.Photo.Id).Order().ToArray());
    }

    [Fact]
    public void Chained_neighbors_do_not_create_a_broad_group()
    {
        var photos = new[] { Photo(1, [1, 0]), Photo(2, [.8f, .6f]), Photo(3, [.28f, .96f]) };
        var group = Assert.Single(new SimilarityGroupingService().CreateGroups(photos, ExactIndex(photos), .75f));
        Assert.Equal(2, group.MemberCount);
        Assert.DoesNotContain(group.Members, x => x.Photo.Id == 3);
    }

    [Fact]
    public void Representative_prefers_resolution_then_sharpness_then_path()
    {
        var low = Photo(1, [1, 0]) with { Width = 100, Height = 100, Sharpness = 20 };
        var sharp = Photo(2, [1, 0]) with { Width = 200, Height = 200, Sharpness = 80 };
        var blurry = Photo(3, [1, 0]) with { Width = 200, Height = 200, Sharpness = 5 };
        Assert.Equal(sharp, SimilarityGroupingService.ChooseRepresentative([low, blurry, sharp]));
    }

    private static Photo Photo(long id, float[] vector) => new(id, 1, $"C:\\photos\\{id}.jpg", 1, DateTimeOffset.UnixEpoch,
        Embedding: SimilarityMath.Normalize(vector), EmbeddingModelVersion: "test");

    private static IVectorIndexService ExactIndex(IEnumerable<Photo> photos)
    {
        var index = new ExactVectorIndex();
        foreach (var photo in photos) index.Add(photo.Id, photo.Embedding!);
        return index;
    }

    private sealed class ExactVectorIndex : IVectorIndexService
    {
        private readonly Dictionary<long, float[]> _vectors = [];
        public void Clear() => _vectors.Clear();
        public void Add(long photoId, ReadOnlySpan<float> normalizedVector) => _vectors[photoId] = normalizedVector.ToArray();
        public IReadOnlyList<VectorNeighbor> Search(ReadOnlySpan<float> vector, int count, long? excludedPhotoId = null)
        {
            var query = vector.ToArray();
            return _vectors
            .Where(x => x.Key != excludedPhotoId)
            .Select(x => new VectorNeighbor(x.Key, SimilarityMath.Cosine(query, x.Value)))
            .OrderByDescending(x => x.Similarity).Take(count).ToArray();
        }
    }
}
