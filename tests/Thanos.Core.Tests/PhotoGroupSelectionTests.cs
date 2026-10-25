using Thanos.Core;

namespace Thanos.Core.Tests;

public sealed class PhotoGroupSelectionTests
{
    [Fact]
    public void Default_selection_keeps_recommended_and_selects_every_other_photo_for_deletion()
    {
        var selection = CreateSelection();

        Assert.False(selection.Recommended.IsSelected);
        Assert.All(selection.Items.Where(item => !item.IsRecommended), item => Assert.True(item.IsSelected));
        Assert.DoesNotContain(selection.Recommended.Photo.FullPath, selection.SelectedPaths());
    }

    [Fact]
    public void Recommended_photo_is_identified_but_remains_user_selectable()
    {
        var selection = CreateSelection();
        selection.Recommended.IsSelected = true;
        Assert.True(selection.Recommended.IsSelected);
        Assert.Contains(selection.Recommended.Photo.FullPath, selection.SelectedPaths());
        Assert.Single(selection.Items, x => x.IsRecommended);
    }

    [Fact]
    public void Selected_paths_can_include_every_photo_when_the_user_chooses_it()
    {
        var selection = CreateSelection();
        foreach (var item in selection.Items) item.IsSelected = true;
        Assert.Equal(selection.Items.Count, selection.SelectedPaths().Count);
    }

    private static PhotoGroupSelection CreateSelection()
    {
        var first = new Photo(1, 1, "C:\\a.jpg", 1, DateTimeOffset.UnixEpoch, Embedding: [1, 0]);
        var second = new Photo(2, 1, "C:\\b.jpg", 1, DateTimeOffset.UnixEpoch, Embedding: [1, 0]);
        return new(new SimilarityGroup(Guid.NewGuid(), 1,
            [new(first, 1), new(second, .99f)], .9f, DateTimeOffset.UtcNow));
    }
}
