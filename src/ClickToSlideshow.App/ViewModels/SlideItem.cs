using ClickToSlideshow.Core.Imaging;
using ClickToSlideshow.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;

namespace ClickToSlideshow.App.ViewModels;

// One tile in the reorder grid. Created immediately with just the underlying Slide so the
// window can show a placeholder before any decode happens. Unlike ClickToPDF's PageItem, rotation
// is NOT derived here — MainViewModel.LoadFolderAsync already resolves it via
// SlideImageLoader.DetectExifRotationDegreesAsync before constructing the Slide (mirroring
// ClickToSlideshow.Cli/Program.cs), so Slide.RotationDegrees is already correct at construction
// time; this class only adjusts it further via RotateClockwise/RotateCounterClockwise and keeps
// it in sync with the underlying domain object. Dimensions and the thumbnail arrive later via
// ApplySlideInfo/ThumbnailSource once MainViewModel has decoded them.
public partial class SlideItem : ObservableObject
{
    public SlideItem(Slide slide)
    {
        Slide = slide;
        FileName = Path.GetFileName(slide.SourcePath);
        FileModifiedUtc = File.GetLastWriteTimeUtc(slide.SourcePath);
        _rotationDegrees = slide.RotationDegrees;
        _durationMs = slide.DurationMs;
        _transitionIn = slide.TransitionIn;
    }

    public Guid Id { get; } = Guid.NewGuid();

    // The domain object this tile edits in place — MainViewModel reads project.Slides directly
    // off of these rather than re-projecting SlideItem back into Slide on every save/render.
    public Slide Slide { get; }

    public string SourcePath => Slide.SourcePath;

    public string FileName { get; }

    // Cheap synchronous file-system stat, available immediately at construction — used as the
    // "Date modified" sort key and as the fallback for "Date taken" while EXIF hasn't been read
    // yet (ApplySlideInfo runs later, asynchronously).
    public DateTimeOffset FileModifiedUtc { get; }

    public DateTimeOffset? DateTaken { get; private set; }

    public int PixelWidth { get; private set; }

    public int PixelHeight { get; private set; }

    public int ExifOrientation { get; private set; }

    [ObservableProperty]
    private ImageSource? _thumbnailSource;

    [ObservableProperty]
    private int _rotationDegrees;

    partial void OnRotationDegreesChanged(int value) => Slide.RotationDegrees = value;

    // Null means "use the project default" — the tile's duration badge shows the resolved value
    // either way, but only a non-null override is written back to the saved project.
    [ObservableProperty]
    private int? _durationMs;

    partial void OnDurationMsChanged(int? value)
    {
        Slide.DurationMs = value;
        OnPropertyChanged(nameof(DurationMsOrZero));
    }

    // NumberBox binds to a plain double, not int? — 0 (its Minimum) doubles as "use the project
    // default" so the set-duration flyout doesn't need a separate "use default" control.
    public double DurationMsOrZero => DurationMs ?? 0;

    // Null means "use the project's default transition" (Slide.TransitionIn semantics, see
    // SlideshowProject.TransitionInto) — the tile's leading-edge glyph reflects the resolved
    // transition either way.
    [ObservableProperty]
    private TransitionKind? _transitionIn;

    partial void OnTransitionInChanged(TransitionKind? value) => Slide.TransitionIn = value;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private bool _hasError;

    // Populated only when HasError is set, for the tile's tooltip — e.g. a missing HEIF codec
    // gets an actionable message instead of a bare "Can't load".
    [ObservableProperty]
    private string? _errorDetail;

    // Non-null only when ErrorDetail names a Store-installable codec fix for this file's own
    // extension — the tile's error flyout uses this to show a direct link instead of asking the
    // user to search the Store by name.
    [ObservableProperty]
    private Uri? _errorStoreLinkUri;

    // Kept in sync by MainViewModel after every reorder/remove — not authoritative on its own,
    // just a bindable mirror of the item's current position in the slide collection.
    [ObservableProperty]
    private int _slideNumber;

    // Mirrors GridView.SelectedItems — not authoritative on its own. MainWindow's
    // SelectionChanged handler is the only writer; this exists purely so the tile's own
    // DataTemplate can render a selection highlight, since a GridViewItem ControlTemplate has
    // no visual reach into the content its ContentPresenter hosts.
    [ObservableProperty]
    private bool _isSelected;

    public void ApplySlideInfo(SlideInfo info)
    {
        PixelWidth = info.PixelWidth;
        PixelHeight = info.PixelHeight;
        ExifOrientation = info.ExifOrientation;
        DateTaken = info.DateTaken;
    }

    public void RotateClockwise() => RotationDegrees = (RotationDegrees + 90) % 360;

    public void RotateCounterClockwise() => RotationDegrees = (RotationDegrees + 270) % 360;
}
