using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Thanos.Core;

public sealed class PhotoSelectionItem(Photo photo, float similarity, bool selected) : INotifyPropertyChanged
{
    private bool _isSelected = selected;
    private bool _isRecommended;

    public Photo Photo { get; } = photo;
    public float Similarity { get; } = similarity;
    public bool IsRecommended
    {
        get => _isRecommended;
        internal set { if (_isRecommended == value) return; _isRecommended = value; OnPropertyChanged(); }
    }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class PhotoGroupSelection
{
    public ObservableCollection<PhotoSelectionItem> Items { get; }
    public PhotoSelectionItem Recommended => Items.Single(x => x.IsRecommended);

    public PhotoGroupSelection(SimilarityGroup group, bool preselectOthers = true)
    {
        if (group.Members.Count < 2) throw new ArgumentException("A visible group needs at least two photos.", nameof(group));
        Items = new(group.Members.Select(x => new PhotoSelectionItem(x.Photo, x.SimilarityScore, false)));
        var recommended = Items.Single(x => x.Photo.Id == group.RepresentativePhotoId);
        recommended.IsRecommended = true;
        if (preselectOthers)
            foreach (var item in Items.Where(x => !x.IsRecommended)) item.IsSelected = true;
    }

    public IReadOnlyList<string> SelectedPaths() => Items.Where(x => x.IsSelected).Select(x => x.Photo.FullPath).ToArray();
}
