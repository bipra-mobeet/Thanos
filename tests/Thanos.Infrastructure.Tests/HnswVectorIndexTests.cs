using Thanos.Infrastructure;

namespace Thanos.Infrastructure.Tests;

public sealed class HnswVectorIndexTests
{
    [Fact]
    public void Search_returns_nearest_vectors_and_excludes_requested_photo()
    {
        var index = new HnswVectorIndex();
        index.Add(1, [1, 0]);
        index.Add(2, [.99f, .1f]);
        index.Add(3, [0, 1]);
        var result = index.Search([1, 0], 2, 1);
        Assert.Equal(2, result[0].PhotoId);
        Assert.DoesNotContain(result, x => x.PhotoId == 1);
    }
}
