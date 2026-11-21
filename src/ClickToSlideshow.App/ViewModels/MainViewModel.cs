using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Runtime.InteropServices.WindowsRuntime;
using Bureau.Common.Messages;
using ClickToSlideshow.App.Diagnostics;
using ClickToSlideshow.App.State;
using ClickToSlideshow.Core;
using ClickToSlideshow.Core.Audio;
using ClickToSlideshow.Core.Imaging;
using ClickToSlideshow.Core.Model;
using ClickToSlideshow.Core.Pptx;
using ClickToSlideshow.Core.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ClickToSlideshow.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const int MaxConcurrentThumbnailLoads = 4;

    // WIC's "no codec registered for this container" HRESULT — see SupportedFiles.ImageExtensions.
    private const int WinCodecErrComponentNotFound = unchecked((int)0x88982F50);

    private static readonly ResourceLoader ResourceLoader = new();

    private readonly SlideshowProject _project = new();
    private readonly UndoStack<Action> _undoStack = new(capacity: 20);
    private readonly SemaphoreSlim _thumbnailSemaphore = new(MaxConcurrentThumbnailLoads);
    private readonly DispatcherQueue _dispatcherQueue;

    // Set around every programmatic Slides/AudioTracks mutation that already records its own undo
    // entry, so the CollectionChanged handlers below don't double-record it. Left false only while
    // a list's own drag-reorder is mutating Slides or AudioTracks directly — that's the one case
    // this VM didn't initiate itself.
    private bool _suppressCollectionTracking;

    // Between BeginSlideDrag and EndSlideDrag; _slideDragStepRecorded is set only while this is.
    private bool _slideDragInProgress;
    private bool _slideDragStepRecorded;

    public MainViewModel(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
        Slides.CollectionChanged += OnSlidesCollectionChanged;
        AudioTracks.CollectionChanged += OnAudioTracksCollectionChanged;

        // Constructor-time only — assigning the backing fields directly (not the generated
        // property setters) so this doesn't fire RecomputeRuntime before Slides/AudioTracks exist.
        _defaultSlideMs = _project.DefaultSlideMs;
        _transitionKind = _project.TransitionKind;
        _transitionMs = _project.TransitionMs;
        _resolution = _project.Resolution;
        _fitMode = _project.FitMode;
        _backgroundColor = _project.BackgroundColor;
        _audioMatch = _project.AudioMatch;
    }

    public ObservableCollection<SlideItem> Slides { get; } = new();

    public ObservableCollection<AudioTrackItem> AudioTracks { get; } = new();

    [ObservableProperty]
    private bool _isLoading;

    // Editing is locked for the whole of a video or PowerPoint save, as in ClickToPDF (Gavin,
    // 2026-11-04): the controls bind to CanEdit, and the edit methods below return early while it
    // is false, so a key or drop that reaches them changes nothing either.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isSaving;

    public bool CanEdit => !IsSaving;

    // Unlocks editing however the save ends; a cancellation or failure still reaches the caller.
    public async Task SaveLockedAsync(Func<Task> save)
    {
        IsSaving = true;
        try
        {
            await save();
        }
        finally
        {
            IsSaving = false;
        }
    }

    // The message bar is a control in MainWindow, so the view model says what it should show
    // through these rather than holding the bar itself.
    public event Action<Message>? MessageShown;

    public event Action<string>? MessageClosed;

    // Holds back the Store rating prompt (app-conventions.md → Rating prompt, Gavin 2026-10-31):
    // set by any error, or a file of a supported type that couldn't be read, and never cleared, so
    // a later clean load or a closed message doesn't hide it. A skipped unsupported file doesn't
    // set it: turning away a file the app never claimed to take isn't the app failing.
    public bool HadProblemThisSession { get; private set; }

    public void ShowError(string text)
    {
        HadProblemThisSession = true;
        ShowMessage(MessageKeys.Error, MessageSeverity.Error, text);
    }

    private void ShowMessage(string key, MessageSeverity severity, string text) =>
        MessageShown?.Invoke(new Message(key, severity, text, []));

    // Each load or add starts by closing what the previous one reported, so the bar describes the
    // latest action. The Saved row stays: it is about a file, not about what is loaded.
    private void ClosePreviousReports()
    {
        foreach (string key in (string[])[
            MessageKeys.Error, MessageKeys.SkippedFiles, MessageKeys.UnreadableFiles, MessageKeys.MissingAudio, MessageKeys.UnreadableAudio])
        {
            MessageClosed?.Invoke(key);
        }
    }

    // Skipped unsupported files are a Warning row, as are music files that couldn't be found or
    // read; only the last two count against the rating prompt, and their callers set that.
    // formatKeyStem names a resw pair: "{stem}One" for a single file and "{stem}Many" for more.
    private void ReportSkipped(IReadOnlyList<string> names, string key, string formatKeyStem)
    {
        if (names.Count > 0)
        {
            string text = SkippedFilesText.Format(
                names, ResourceLoader.GetString(formatKeyStem + "One"), ResourceLoader.GetString(formatKeyStem + "Many"));
            ShowMessage(key, MessageSeverity.Warning, text);
        }
    }

    // Grid empty state — Slides only, deliberately not ANDed with AudioTracks: "an audio-only
    // selection opens the editor with the track loaded and an empty grid" is a real acceptance
    // criterion, so an audio-only load must still show the grid's empty state.
    [ObservableProperty]
    private bool _showEmptyState = true;

    // Audio strip's own empty state — separate from the grid's, since the two are independent
    // (a slides-only load shows a populated grid next to an empty audio strip, and vice versa).
    [ObservableProperty]
    private bool _showEmptyAudioState = true;

    // Project-settings proxies (Options expander, spec §4.3) — each write-through to the held
    // SlideshowProject and recompute the runtime bar. TransitionMs < DefaultSlideMs (spec §4.2)
    // is enforced by MainWindow's options-expander UI, not here.
    [ObservableProperty]
    private int _defaultSlideMs;

    partial void OnDefaultSlideMsChanged(int value)
    {
        _project.DefaultSlideMs = value;
        RecomputeRuntime();
    }

    [ObservableProperty]
    private TransitionKind _transitionKind;

    partial void OnTransitionKindChanged(TransitionKind value) => _project.TransitionKind = value;

    [ObservableProperty]
    private int _transitionMs;

    partial void OnTransitionMsChanged(int value)
    {
        _project.TransitionMs = value;
        RecomputeRuntime();
    }

    [ObservableProperty]
    private Resolution _resolution;

    partial void OnResolutionChanged(Resolution value) => _project.Resolution = value;

    [ObservableProperty]
    private FitMode _fitMode;

    partial void OnFitModeChanged(FitMode value) => _project.FitMode = value;

    [ObservableProperty]
    private Windows.UI.Color _backgroundColor;

    partial void OnBackgroundColorChanged(Windows.UI.Color value) => _project.BackgroundColor = value;

    [ObservableProperty]
    private AudioMatch _audioMatch;

    partial void OnAudioMatchChanged(AudioMatch value)
    {
        _project.AudioMatch = value;
        RecomputeRuntime();
    }

    // Runtime bar (spec §4.3) — recomputed at every mutation that could change either value:
    // slide add/remove/reorder, duration edits, transition timing, and audio track changes.
    // Bar widths and the summary text are computed here rather than in MainWindow's XAML: x:Bind
    // function/converter bindings are unreliable with a Window (not FrameworkElement) code-behind
    // root (see MainWindow.xaml's primary-actions comment for the same limitation), so the
    // view stays a plain {Binding} consumer of already-computed display values instead.
    //
    // The footer is full-width and responsive (audio-section-brief.md §3.6), so unlike the old
    // fixed 400px bars, the max width now comes from the footer's own measured size via
    // SetAudioAvailableWidth, debounced against SizeChanged firing continuously during a resize.
    private double _runtimeBarMaxWidth = 400.0;

    // Lazily created (not in the constructor) — MainViewModelReorderTests constructs this view
    // model with a null DispatcherQueue, and DispatcherQueue.CreateTimer() throws immediately on
    // a null receiver. Nothing needs this timer to exist until SetAudioAvailableWidth is first
    // called, which those tests never do.
    private DispatcherQueueTimer? _audioWidthDebounceTimer;

    [ObservableProperty]
    private int _runtimeMs;

    [ObservableProperty]
    private int _audioLengthMs;

    [ObservableProperty]
    private double _runtimeBarWidth;

    [ObservableProperty]
    private double _audioBarWidth;

    [ObservableProperty]
    private string _runtimeSummaryText = "";

    [ObservableProperty]
    private string _formattedRuntimeText = "";

    [ObservableProperty]
    private string _formattedAudioLengthText = "";

    [ObservableProperty]
    private string _slidesHeaderSummaryText = "";

    [ObservableProperty]
    private string _audioHeaderSummaryText = "";

    public void SetAudioAvailableWidth(double width)
    {
        if (width <= 0 || Math.Abs(width - _runtimeBarMaxWidth) < 0.5)
        {
            return;
        }

        _runtimeBarMaxWidth = width;

        if (_audioWidthDebounceTimer is null)
        {
            _audioWidthDebounceTimer = _dispatcherQueue.CreateTimer();
            _audioWidthDebounceTimer.Interval = TimeSpan.FromMilliseconds(80);
            _audioWidthDebounceTimer.IsRepeating = false;
            _audioWidthDebounceTimer.Tick += (_, _) => RecomputeRuntime();
        }

        _audioWidthDebounceTimer.Stop();
        _audioWidthDebounceTimer.Start();
    }

    // Remembered last-used choice — set once at startup from AppSettings by MainWindow before any
    // load call, and re-applied after the initial (always-natural) load completes. See LoadSlidesAsync.
    [ObservableProperty]
    private SortOrder _currentSortOrder = SortOrder.NameNatural;

    public string? FolderPath { get; private set; }

    // Snapshot for the M3 player: _project stays private and keeps being mutated in place by
    // RenumberSlides/RenumberAudioTracks, so a still-open editor's later edits must never reach
    // an in-flight playback session. SlideshowRenderer.EffectiveProject clones (and applies
    // FitSlides scaling identically to the MP4 exporter).
    internal SlideshowProject CreatePlaybackSnapshot() => SlideshowRenderer.EffectiveProject(_project);

    // For project-JSON save: unlike CreatePlaybackSnapshot, this must NOT apply FitSlides scaling
    // — a saved project has to round-trip the user's own stored per-slide durations exactly
    // (spec §4.2), not the values scaled for the current audio track.
    internal SlideshowProject CreateSaveSnapshot() => SlideshowRenderer.Clone(_project);

    internal Task RenderVideoAsync(string outputPath, IProgress<RenderProgress>? progress, CancellationToken cancellationToken) =>
        SlideshowRenderer.RenderAsync(CreatePlaybackSnapshot(), outputPath, progress: progress, cancellationToken: cancellationToken);

    internal void SaveProjectJson(string outputPath) => SlideshowProjectFile.Save(CreateSaveSnapshot(), outputPath);

    // Unscaled, like SaveProjectJson — WriteAsync applies EffectiveProject internally for its own
    // rendering (so the deck's timings still match what Play/Save Video would show) but embeds
    // this original snapshot in its custom XML part, so reopening the PPTX restores the user's
    // exact authored durations rather than whatever FitSlides scaled them to (spec §4.2: "never
    // rewrites the stored per-slide values" — the same invariant JSON save already honours).
    internal Task RenderPptxAsync(string outputPath, IProgress<RenderProgress>? progress, CancellationToken cancellationToken) =>
        PptxSlideshowWriter.WriteAsync(CreateSaveSnapshot(), outputPath, progress, cancellationToken);

    // Replaces the current project outright (Slides/AudioTracks cleared first) — unlike
    // LoadSlidesAsync, which only ever runs against a fresh, empty VM, Open can be clicked with a
    // project already loaded. MainWindow.OpenButton_Click confirms with the user first whenever
    // there's an existing slide or audio track to lose; this method itself still has no
    // unsaved-changes awareness, so anything that calls it directly bypasses that guard.
    // Accepts both project-JSON and PPTX (round-trip via its embedded custom XML part, spec §4.8) —
    // dispatched by extension since both reconstruct the same SlideshowProject shape.
    internal async Task LoadProjectAsync(string projectPath)
    {
        if (!CanEdit)
        {
            return;
        }

        IsLoading = true;
        ClosePreviousReports();
        try
        {
            SlideshowProject loaded = string.Equals(Path.GetExtension(projectPath), ".pptx", StringComparison.OrdinalIgnoreCase)
                ? PptxProjectReader.ReadEmbeddedProject(projectPath)
                : SlideshowProjectFile.Load(projectPath);
            FolderPath = Path.GetDirectoryName(projectPath);

            DefaultSlideMs = loaded.DefaultSlideMs;
            TransitionKind = loaded.TransitionKind;
            TransitionMs = loaded.TransitionMs;
            Resolution = loaded.Resolution;
            FitMode = loaded.FitMode;
            BackgroundColor = loaded.BackgroundColor;
            AudioMatch = loaded.AudioMatch;

            List<AudioTrack> foundAudioTracks = [];
            List<string> missingAudioNames = [];
            List<string> undecodableAudioNames = [];
            foreach (AudioTrack track in loaded.AudioTracks)
            {
                if (!File.Exists(track.SourcePath))
                {
                    missingAudioNames.Add(Path.GetFileName(track.SourcePath));
                    continue;
                }

                try
                {
                    foundAudioTracks.Add(new AudioTrack
                    {
                        SourcePath = track.SourcePath,
                        DurationMs = track.DurationMs,
                        PlayablePath = await OggAudioTranscoder.EnsurePlayablePathAsync(track.SourcePath),
                    });
                }
                catch (Exception ex) when (OggAudioTranscoder.IsOggFamily(track.SourcePath))
                {
                    AppLog.LogWarning("LoadProjectAsync", $"{track.SourcePath}: {ex}");
                    undecodableAudioNames.Add(Path.GetFileName(track.SourcePath));
                }
            }

            if (missingAudioNames.Count > 0)
            {
                HadProblemThisSession = true;
                ReportSkipped(missingAudioNames, MessageKeys.MissingAudio, "MissingAudio");
            }

            ReportUndecodableAudio(undecodableAudioNames);

            _suppressCollectionTracking = true;
            Slides.Clear();
            AudioTracks.Clear();
            foreach (Slide slide in loaded.Slides)
            {
                Slides.Add(new SlideItem(slide));
            }

            foreach (AudioTrack track in foundAudioTracks)
            {
                AudioTracks.Add(CreateAudioTrackItem(track));
            }
            _suppressCollectionTracking = false;
            RenumberSlides();
            RenumberAudioTracks();
            _undoStack.Clear();

            // Missing image sources surface here as a per-tile HasError (same codec-missing path
            // LoadSlideAsync already handles) — no separate upfront existence check needed.
            await Task.WhenAll(Slides.Select(LoadSlideAsync));
        }
        catch (Exception ex)
        {
            AppLog.LogError("LoadProjectAsync", ex);
            ShowError(ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task LoadFolderAsync(string folderPath)
    {
        if (!CanEdit)
        {
            return;
        }

        FolderPath = folderPath;
        ClosePreviousReports();

        // Directory.EnumerateFiles validates the path eagerly (throws synchronously at the call
        // site, not lazily during enumeration) — catch it here rather than letting it escape the
        // fire-and-forget call in MainWindow's constructor.
        List<string> paths;
        try
        {
            paths = Directory.EnumerateFiles(folderPath).ToList();
        }
        catch (Exception ex)
        {
            AppLog.LogError("LoadFolderAsync", ex);
            ShowError(ex.Message);
            return;
        }

        await LoadSlidesAsync(paths);
    }

    // Explicit file list from the shell extension's temp-file handshake (M6) or a hand-built
    // .list file for dev testing. Mixed images and audio, per the decisions doc's combined
    // image+audio extension table.
    public Task LoadFilesAsync(IReadOnlyList<string> filePaths, IReadOnlyList<string> skippedNames)
    {
        FolderPath = filePaths.Count > 0 ? Path.GetDirectoryName(filePaths[0]) : null;
        ClosePreviousReports();
        ReportSkipped(skippedNames, MessageKeys.SkippedFiles, "SkippedFiles");
        return LoadSlidesAsync(filePaths);
    }

    // A plain folder load (the dev launch path and Choose folder…) leaves out unsupported files
    // without reporting them; the shell extension reports its own skips through LoadFilesAsync.
    private async Task LoadSlidesAsync(IEnumerable<string> paths)
    {
        IsLoading = true;
        try
        {
            List<string> imagePaths = paths.Where(SupportedFiles.IsImage).OrderBy(p => p, NaturalSortComparer.Instance).ToList();
            List<string> audioPaths = paths.Where(SupportedFiles.IsAudio).ToList();

            _suppressCollectionTracking = true;
            foreach (string path in imagePaths)
            {
                int rotation = await SlideImageLoader.DetectExifRotationDegreesAsync(path);
                var slide = new Slide { SourcePath = path, RotationDegrees = rotation };
                Slides.Add(new SlideItem(slide));
            }

            foreach (AudioTrack track in await BuildAudioTracksSkippingUndecodableAsync(audioPaths))
            {
                AudioTracks.Add(CreateAudioTrackItem(track));
            }
            _suppressCollectionTracking = false;
            RenumberSlides();
            RenumberAudioTracks();
            _undoStack.Clear();

            await Task.WhenAll(Slides.Select(LoadSlideAsync));

            // Initial order is always natural sort; re-apply a remembered non-default choice now
            // that per-slide metadata (dates) exists. Skipped for NameNatural to avoid a redundant
            // no-op resort (and an unnecessary undo-stack entry) on every load.
            if (CurrentSortOrder != SortOrder.NameNatural)
            {
                ApplySort(CurrentSortOrder);
            }
        }
        catch (Exception ex)
        {
            AppLog.LogError("LoadSlidesAsync", ex);
            ShowError(ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static async Task<AudioTrack> BuildAudioTrackAsync(string path)
    {
        // Duration comes from the playable file: for .ogg/.opus Windows can't read the original.
        string playablePath = await OggAudioTranscoder.EnsurePlayablePathAsync(path);
        StorageFile file = await StorageFile.GetFileFromPathAsync(playablePath);
        var musicProperties = await file.Properties.GetMusicPropertiesAsync();
        return new AudioTrack
        {
            SourcePath = path,
            PlayablePath = playablePath,
            DurationMs = (int)musicProperties.Duration.TotalMilliseconds,
        };
    }

    // An undecodable Ogg/Opus file is skipped and reported rather than failing the whole batch
    // (or, from the picker's async-void handler, the app). Only Ogg-family files can fail here for
    // that reason; any other failure still propagates as before.
    private async Task<List<AudioTrack>> BuildAudioTracksSkippingUndecodableAsync(IEnumerable<string> paths)
    {
        List<AudioTrack> built = [];
        List<string> undecodableNames = [];
        foreach (string path in paths)
        {
            try
            {
                built.Add(await BuildAudioTrackAsync(path));
            }
            catch (Exception ex) when (OggAudioTranscoder.IsOggFamily(path))
            {
                AppLog.LogWarning("BuildAudioTracksSkippingUndecodableAsync", $"{path}: {ex}");
                undecodableNames.Add(Path.GetFileName(path));
            }
        }

        ReportUndecodableAudio(undecodableNames);
        return built;
    }

    // Its own row, beside any unsupported-file or missing-track row, since the cause differs.
    private void ReportUndecodableAudio(IReadOnlyList<string> names)
    {
        if (names.Count > 0)
        {
            HadProblemThisSession = true;
            ReportSkipped(names, MessageKeys.UnreadableAudio, "UnreadableAudio");
        }
    }

    // Single choke point for constructing an AudioTrackItem so every call site gets its waveform
    // kicked off the same way, matching RenumberSlides/RenumberAudioTracks' own "one place" habit.
    private AudioTrackItem CreateAudioTrackItem(AudioTrack track)
    {
        var item = new AudioTrackItem(track);
        _ = LoadPeaksAsync(item);
        return item;
    }

    // Fire-and-forget by design (brief §3.3: "the card must render immediately with a flat
    // placeholder and fill in when ready") — decode runs fully async off the UI thread via
    // AudioPeakExtractor's own MediaTranscoder step, and only the final assignment is marshaled
    // back. A failed decode keeps the flat placeholder rather than surfacing an error; waveform
    // is decoration, never a save blocker.
    private async Task LoadPeaksAsync(AudioTrackItem item)
    {
        float[] peaks;
        try
        {
            peaks = await AudioPeakExtractor.ExtractPeaksAsync(item.PlayablePath, AudioTrackItem.PeakBucketCount);
        }
        catch (Exception ex)
        {
            AppLog.LogWarning("LoadPeaksAsync", $"{item.FileName}: {ex}");
            return;
        }

        _dispatcherQueue.TryEnqueue(() => item.Peaks = peaks);
    }

    public void ApplySort(SortOrder order)
    {
        if (!CanEdit)
        {
            return;
        }

        CurrentSortOrder = order;
        ApplyOrder(ComputeSortedOrder(order));
    }

    private List<SlideItem> ComputeSortedOrder(SortOrder order)
    {
        IOrderedEnumerable<SlideItem> ordered = order switch
        {
            SortOrder.DateTaken => Slides.OrderBy(s => s.DateTaken ?? s.FileModifiedUtc),
            SortOrder.DateModified => Slides.OrderBy(s => s.FileModifiedUtc),
            _ => Slides.OrderBy(s => s.FileName, NaturalSortComparer.Instance),
        };
        return ordered.ToList();
    }

    public void ReverseOrder()
    {
        if (CanEdit)
        {
            ApplyOrder(Slides.Reverse().ToList());
        }
    }

    // Shared by ApplySort/ReverseOrder — same pattern as RemoveSelected/MoveSelected: clear and
    // re-add under _suppressCollectionTracking so the whole reorder is one undo step, not one
    // per moved item.
    private void ApplyOrder(List<SlideItem> newOrder)
    {
        Action restorePrevious = RestoreOrder(Slides, Slides.ToList(), RenumberSlides);

        _suppressCollectionTracking = true;
        Slides.Clear();
        foreach (SlideItem item in newOrder)
        {
            Slides.Add(item);
        }
        _suppressCollectionTracking = false;
        RenumberSlides();

        _undoStack.Push(restorePrevious);
    }

    // Returns an action that puts items back in order.
    private Action RestoreOrder<T>(ObservableCollection<T> items, List<T> order, Action renumber)
    {
        return () =>
        {
            _suppressCollectionTracking = true;
            items.Clear();
            foreach (T item in order)
            {
                items.Add(item);
            }
            _suppressCollectionTracking = false;
            renumber();
        };
    }

    // A list's own drag-reorder is the only untracked change, and it arrives as Remove then Insert,
    // never Move. The Remove starts it, so the order before the drag is the list now with the
    // removed item put back; the Insert records nothing. Taken from the change itself rather than
    // from DragItemsStarting, which the audio list never raised (Gavin's 2026-11-04 retest: undoing
    // a track drag undid the add before it). Returns whether it recorded a step.
    private bool RecordDragStep<T>(ObservableCollection<T> items, NotifyCollectionChangedEventArgs e, Action renumber)
    {
        if (e.Action != NotifyCollectionChangedAction.Remove || e.OldItems is null)
        {
            return false;
        }

        List<T> before = items.ToList();
        before.InsertRange(e.OldStartingIndex, e.OldItems.Cast<T>());
        _undoStack.Push(RestoreOrder(items, before, renumber));
        return true;
    }

    // The slide grid's Extended selection drags every selected tile, a Remove/Insert pair per tile,
    // so only the first Remove of a drag may record a step. MainWindow calls these from the grid's
    // DragItemsStarting and DragItemsCompleted, which the grid does raise; the track list needs
    // neither, since its SelectionMode="None" drags one track at a time.
    // Outside a Begin/End pair, every Remove records its own step.
    public void BeginSlideDrag()
    {
        _slideDragInProgress = true;
        _slideDragStepRecorded = false;
    }

    public void EndSlideDrag()
    {
        _slideDragInProgress = false;
        _slideDragStepRecorded = false;
    }

    // A tile's duration box and transition menu: one undo step per committed change, however many
    // slides it covers. A slide that already has the value records nothing, which also keeps the
    // duration box's ValueChanged, raised when an undo puts its bound value back, from adding a step.
    public void SetDuration(IReadOnlyList<SlideItem> slides, int? durationMs) =>
        SetPerSlide(slides, slide => slide.DurationMs, (slide, value) => slide.DurationMs = value, durationMs);

    public void SetTransitionIn(IReadOnlyList<SlideItem> slides, TransitionKind? kind) =>
        SetPerSlide(slides, slide => slide.TransitionIn, (slide, value) => slide.TransitionIn = value, kind);

    private void SetPerSlide<T>(IReadOnlyList<SlideItem> slides, Func<SlideItem, T> get, Action<SlideItem, T> set, T value)
    {
        List<(SlideItem Item, T Previous)> changed = slides
            .Where(slide => !EqualityComparer<T>.Default.Equals(get(slide), value))
            .Select(slide => (slide, get(slide)))
            .ToList();
        if (changed.Count == 0 || !CanEdit)
        {
            return;
        }

        foreach ((SlideItem item, _) in changed)
        {
            set(item, value);
        }
        RecomputeRuntime();

        _undoStack.Push(() =>
        {
            foreach ((SlideItem item, T previous) in changed)
            {
                set(item, previous);
            }
            RecomputeRuntime();
        });
    }

    public void RotateSelected(IReadOnlyList<SlideItem> selected, bool clockwise)
    {
        if (selected.Count == 0 || !CanEdit)
        {
            return;
        }

        Dictionary<SlideItem, int> previous = selected.ToDictionary(item => item, item => item.RotationDegrees);
        foreach (SlideItem item in selected)
        {
            if (clockwise)
            {
                item.RotateClockwise();
            }
            else
            {
                item.RotateCounterClockwise();
            }
        }

        _undoStack.Push(() =>
        {
            foreach ((SlideItem item, int degrees) in previous)
            {
                item.RotationDegrees = degrees;
            }
        });
    }

    public void RemoveSelected(IReadOnlyList<SlideItem> selected)
    {
        if (!CanEdit)
        {
            return;
        }

        List<(SlideItem Item, int Index)> removed = selected
            .Select(item => (Item: item, Index: Slides.IndexOf(item)))
            .Where(x => x.Index >= 0)
            .OrderBy(x => x.Index)
            .ToList();

        if (removed.Count == 0)
        {
            return;
        }

        _suppressCollectionTracking = true;
        foreach ((SlideItem item, _) in removed)
        {
            Slides.Remove(item);
        }
        _suppressCollectionTracking = false;
        RenumberSlides();

        _undoStack.Push(() =>
        {
            _suppressCollectionTracking = true;
            foreach ((SlideItem item, int index) in removed)
            {
                Slides.Insert(Math.Min(index, Slides.Count), item);
            }
            _suppressCollectionTracking = false;
            RenumberSlides();
        });
    }

    // Ctrl+Left/Right: shifts the whole selection by one slot. If any selected tile is already at
    // the boundary in that direction, the whole move is a no-op rather than partially shifting.
    public void MoveSelected(IReadOnlyList<SlideItem> selected, int delta)
    {
        if (delta is not (-1 or 1) || !CanEdit)
        {
            return;
        }

        List<int> indices = selected
            .Select(item => Slides.IndexOf(item))
            .Where(i => i >= 0)
            .OrderBy(i => i)
            .ToList();

        if (indices.Count == 0)
        {
            return;
        }

        if (delta > 0)
        {
            indices.Reverse();
        }

        foreach (int index in indices)
        {
            int target = index + delta;
            if (target < 0 || target >= Slides.Count)
            {
                return;
            }
        }

        var moves = new List<(int From, int To)>();
        _suppressCollectionTracking = true;
        foreach (int index in indices)
        {
            int target = index + delta;
            Slides.Move(index, target);
            moves.Add((index, target));
        }
        _suppressCollectionTracking = false;
        RenumberSlides();

        _undoStack.Push(() =>
        {
            _suppressCollectionTracking = true;
            for (int i = moves.Count - 1; i >= 0; i--)
            {
                Slides.Move(moves[i].To, moves[i].From);
            }
            _suppressCollectionTracking = false;
            RenumberSlides();
        });
    }

    // The + beside SLIDES (brief §3.10). Its picker only offers photos, so a skip here is rare.
    public Task AddImagesAsync(IReadOnlyList<string> paths)
    {
        if (!CanEdit)
        {
            return Task.CompletedTask;
        }

        ClosePreviousReports();
        ReportSkipped(paths.Where(p => !SupportedFiles.IsImage(p)).Select(Path.GetFileName).ToList()!, MessageKeys.SkippedFiles, "SkippedFiles");
        return AppendAsync(paths.Where(SupportedFiles.IsImage).ToList(), []);
    }

    // The + beside AUDIO.
    public Task AddAudioTracksAsync(IReadOnlyList<string> paths)
    {
        if (!CanEdit)
        {
            return Task.CompletedTask;
        }

        ClosePreviousReports();
        ReportSkipped(paths.Where(p => !SupportedFiles.IsAudio(p)).Select(Path.GetFileName).ToList()!, MessageKeys.SkippedFiles, "SkippedFiles");
        return AppendAsync([], paths.Where(SupportedFiles.IsAudio).ToList());
    }

    // Files and folders dragged in from File Explorer. Photos go in natural name order, as from the
    // right-click menu, since the order Explorer hands over is just the order they were clicked.
    public Task AddDroppedAsync(IReadOnlyList<string> droppedPaths)
    {
        if (!CanEdit)
        {
            return Task.CompletedTask;
        }

        ExpandedSelection selection = ExpandedSelection.Of(droppedPaths);
        ClosePreviousReports();
        ReportSkipped(selection.SkippedNames, MessageKeys.SkippedFiles, "SkippedFiles");
        if (selection.UnreadableNames.Count > 0)
        {
            HadProblemThisSession = true;
            ReportSkipped(selection.UnreadableNames, MessageKeys.UnreadableFiles, "UnreadableFiles");
        }

        return AppendAsync(
            selection.SupportedPaths.Where(SupportedFiles.IsImage).OrderBy(p => p, NaturalSortComparer.Instance).ToList(),
            selection.SupportedPaths.Where(SupportedFiles.IsAudio).ToList());
    }

    // Adding never replaces (app-conventions.md → Add button): photos and music go after what is
    // loaded, keeping its edits, as one undo step however many of each there are. The callers
    // report what they left out first.
    private async Task AppendAsync(IReadOnlyList<string> imagePaths, IReadOnlyList<string> audioPaths)
    {
        var addedSlides = new List<SlideItem>();
        foreach (string path in imagePaths)
        {
            int rotation = await SlideImageLoader.DetectExifRotationDegreesAsync(path);
            addedSlides.Add(new SlideItem(new Slide { SourcePath = path, RotationDegrees = rotation }));
        }

        List<AudioTrackItem> addedTracks = [.. (await BuildAudioTracksSkippingUndecodableAsync(audioPaths)).Select(CreateAudioTrackItem)];
        if (addedSlides.Count == 0 && addedTracks.Count == 0)
        {
            return;
        }

        _suppressCollectionTracking = true;
        foreach (SlideItem item in addedSlides)
        {
            Slides.Add(item);
        }

        foreach (AudioTrackItem item in addedTracks)
        {
            AudioTracks.Add(item);
        }
        _suppressCollectionTracking = false;
        RenumberSlides();
        RenumberAudioTracks();

        _undoStack.Push(() =>
        {
            _suppressCollectionTracking = true;
            foreach (SlideItem item in addedSlides)
            {
                Slides.Remove(item);
            }

            foreach (AudioTrackItem item in addedTracks)
            {
                AudioTracks.Remove(item);
            }
            _suppressCollectionTracking = false;
            RenumberSlides();
            RenumberAudioTracks();
        });

        await Task.WhenAll(addedSlides.Select(LoadSlideAsync));
    }

    public void RemoveAudioTrack(AudioTrackItem item)
    {
        int index = AudioTracks.IndexOf(item);
        if (index < 0 || !CanEdit)
        {
            return;
        }

        if (_previewingTrack == item)
        {
            StopPreview();
        }

        _suppressCollectionTracking = true;
        AudioTracks.RemoveAt(index);
        _suppressCollectionTracking = false;
        RenumberAudioTracks();

        _undoStack.Push(() =>
        {
            _suppressCollectionTracking = true;
            AudioTracks.Insert(Math.Min(index, AudioTracks.Count), item);
            _suppressCollectionTracking = false;
            RenumberAudioTracks();
        });
    }

    // Per-track preview (brief §3.4) — only one track is loaded at a time. Play/Pause on the loaded
    // track resumes from where it paused; StopPreview unloads it (another track starting, the
    // track ending, removal), so that track's next Play starts from the beginning. Starting the
    // slideshow itself (MainWindow's PlayButton_Click) also calls StopPreview first — a track
    // preview and the real Play button must never overlap.
    private MediaPlayer? _previewPlayer;
    private AudioTrackItem? _previewingTrack;
    private AudioTrackItem? _scrubbingTrack;
    private TimeSpan _startOffsetOnOpen;
    private DispatcherQueueTimer? _previewProgressTimer;

    public void TogglePreview(AudioTrackItem item)
    {
        if (_previewingTrack != item)
        {
            StartPreview(item, TimeSpan.Zero);
        }
        else if (item.IsPlaying)
        {
            _previewProgressTimer?.Stop();
            _previewPlayer?.Pause();
            item.IsPlaying = false;
        }
        else
        {
            _previewPlayer?.Play();
            item.IsPlaying = true;
            _previewProgressTimer?.Start();
        }
    }

    // While the pointer is down on a card's waveform the indicator follows it but the audio doesn't
    // move (seeking on every pointer-move stutters); SeekPreview applies the final position on release.
    public void ScrubPreview(AudioTrackItem item, double fraction)
    {
        _scrubbingTrack = item;
        item.HasPlaybackPosition = true;
        item.PlaybackProgress = Math.Clamp(fraction, 0.0, 1.0);
    }

    // For a pointer that was cancelled or lost capture mid-scrub: puts the indicator back where
    // the audio actually is (or hides it if this track isn't loaded).
    public void CancelScrub(AudioTrackItem item)
    {
        if (_scrubbingTrack != item)
        {
            return;
        }

        _scrubbingTrack = null;
        if (_previewingTrack == item)
        {
            UpdatePreviewProgress();
        }
        else
        {
            item.HasPlaybackPosition = false;
            item.PlaybackProgress = 0.0;
        }
    }

    // Seeking a track that isn't loaded starts it playing from that point.
    public void SeekPreview(AudioTrackItem item, double fraction)
    {
        if (item.DurationMs <= 0)
        {
            CancelScrub(item);
            return;
        }

        _scrubbingTrack = null;
        fraction = Math.Clamp(fraction, 0.0, 1.0);
        TimeSpan target = TimeSpan.FromMilliseconds(fraction * item.DurationMs);
        if (_previewingTrack != item || _previewPlayer is null)
        {
            StartPreview(item, target);
            return;
        }

        // Also the offset OnPreviewOpened applies, so a seek made while the media is still
        // opening isn't overwritten by the earlier start offset.
        _startOffsetOnOpen = target;
        _previewPlayer.PlaybackSession.Position = target;
        item.PlaybackProgress = fraction;
    }

    public void StopPreview()
    {
        if (_previewingTrack is null)
        {
            return;
        }

        _previewProgressTimer?.Stop();
        _previewPlayer?.Pause();
        _previewingTrack.IsPlaying = false;
        _previewingTrack.HasPlaybackPosition = false;
        _previewingTrack.PlaybackProgress = 0.0;
        _previewingTrack = null;
    }

    private void StartPreview(AudioTrackItem item, TimeSpan startAt)
    {
        StopPreview();

        _previewPlayer ??= CreatePreviewPlayer();

        // Applied in OnPreviewOpened rather than here as a precaution — not verified whether a
        // position set before the media opens would be kept.
        _startOffsetOnOpen = startAt;
        _previewPlayer.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(item.PlayablePath));
        _previewPlayer.Play();

        _previewingTrack = item;
        item.IsPlaying = true;
        item.HasPlaybackPosition = true;
        item.PlaybackProgress = item.DurationMs > 0 ? startAt.TotalMilliseconds / item.DurationMs : 0.0;

        _previewProgressTimer ??= CreatePreviewProgressTimer();
        _previewProgressTimer.Start();
    }

    // Called from MainWindow's Closed handler — MediaPlayer holds a native resource that outlives
    // GC otherwise, same reasoning PlayerViewModel's own audio player disposal documents.
    public void ReleasePreviewPlayer()
    {
        if (_previewPlayer is null)
        {
            return;
        }

        _previewProgressTimer?.Stop();
        _previewProgressTimer = null;
        _previewPlayer.MediaEnded -= OnPreviewEnded;
        _previewPlayer.MediaOpened -= OnPreviewOpened;
        _previewPlayer.Dispose();
        _previewPlayer = null;
        _previewingTrack = null;
    }

    private MediaPlayer CreatePreviewPlayer()
    {
        var player = new MediaPlayer();
        // Stay out of Windows' media controls: a pause from there would bypass the preview button's state.
        player.CommandManager.IsEnabled = false;
        player.MediaEnded += OnPreviewEnded;
        player.MediaOpened += OnPreviewOpened;
        return player;
    }

    private void OnPreviewOpened(MediaPlayer sender, object args)
    {
        TimeSpan startAt = _startOffsetOnOpen;
        _startOffsetOnOpen = TimeSpan.Zero;
        if (startAt > TimeSpan.Zero)
        {
            sender.PlaybackSession.Position = startAt;
        }
    }

    // 30fps, matching PlayerWindow's own DispatcherTimer throttle (M3) — polling PlaybackSession.Position
    // rather than PositionChanged, which fires at whatever cadence the OS happens to raise it at, not a
    // steady rate a moving indicator needs.
    private DispatcherQueueTimer CreatePreviewProgressTimer()
    {
        DispatcherQueueTimer timer = _dispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(33);
        timer.Tick += (_, _) => UpdatePreviewProgress();
        return timer;
    }

    private void UpdatePreviewProgress()
    {
        // Position reads 0 until the media opens, which would yank an indicator that was just
        // placed by a seek back to the start.
        if (_previewPlayer is null || _previewingTrack is null || _previewingTrack.DurationMs <= 0 ||
            _scrubbingTrack == _previewingTrack ||
            _previewPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Opening)
        {
            return;
        }

        double fraction = _previewPlayer.PlaybackSession.Position.TotalMilliseconds / _previewingTrack.DurationMs;
        _previewingTrack.PlaybackProgress = Math.Clamp(fraction, 0.0, 1.0);
    }

    // Fires on a WinRT callback thread, not the UI thread (same caveat PlayerViewModel's own
    // OnAudioTrackEnded documents) — StopPreview flips an ObservableProperty the UI binds to, so
    // it has to be marshaled back.
    private void OnPreviewEnded(MediaPlayer sender, object args) =>
        _dispatcherQueue.TryEnqueue(StopPreview);

    public void Undo()
    {
        if (CanEdit && _undoStack.TryPop(out Action? action))
        {
            action?.Invoke();
        }
    }

    private void OnSlidesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_suppressCollectionTracking)
        {
            return;
        }

        if (!_slideDragStepRecorded)
        {
            _slideDragStepRecorded = RecordDragStep(Slides, e, RenumberSlides) && _slideDragInProgress;
        }
        RenumberSlides();
    }

    private void OnAudioTracksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_suppressCollectionTracking)
        {
            return;
        }

        RecordDragStep(AudioTracks, e, RenumberAudioTracks);
        RenumberAudioTracks();
    }

    // The one place called after every single Slides mutation (load, remove, sort, reorder, and
    // each operation's undo) — the single choke point for keeping ShowEmptyState and the held
    // SlideshowProject's Slides list in sync with the ObservableCollection's current order.
    private void RenumberSlides()
    {
        ShowEmptyState = Slides.Count == 0;

        for (int i = 0; i < Slides.Count; i++)
        {
            Slides[i].SlideNumber = i + 1;
        }

        _project.Slides.Clear();
        _project.Slides.AddRange(Slides.Select(s => s.Slide));
        RecomputeRuntime();
    }

    private void RenumberAudioTracks()
    {
        ShowEmptyAudioState = AudioTracks.Count == 0;

        for (int i = 0; i < AudioTracks.Count; i++)
        {
            AudioTracks[i].TrackNumber = i + 1;
        }

        _project.AudioTracks.Clear();
        _project.AudioTracks.AddRange(AudioTracks.Select(a => a.Track));
        RecomputeRuntime();
    }

    private void RecomputeRuntime()
    {
        RuntimeMs = (int)_project.Runtime.TotalMilliseconds;
        AudioLengthMs = AudioTracks.Sum(t => t.DurationMs);

        int longer = Math.Max(RuntimeMs, AudioLengthMs);
        RuntimeBarWidth = longer == 0 ? 0 : _runtimeBarMaxWidth * RuntimeMs / longer;
        AudioBarWidth = longer == 0 ? 0 : _runtimeBarMaxWidth * AudioLengthMs / longer;

        RuntimeSummaryText = BuildRuntimeSummary(AudioMatch, RuntimeMs, AudioLengthMs);
        FormattedRuntimeText = FormatDuration(RuntimeMs);
        FormattedAudioLengthText = FormatDuration(AudioLengthMs);
        SlidesHeaderSummaryText = $"{Slides.Count} · {FormatDuration(RuntimeMs)} total";
        AudioHeaderSummaryText = AudioTracks.Count == 1
            ? $"1 track · {FormatDuration(AudioLengthMs)} total"
            : $"{AudioTracks.Count} tracks · {FormatDuration(AudioLengthMs)} total";
    }

    // Plain-English consequence of the current AudioMatch mode (brief §3.6) rather than just the
    // two numbers. FitSlides is checked first regardless of which side is longer — it scales
    // slide durations to match the audio (SlideshowRenderer.EffectiveProject), so the real
    // consequence is "timing changes", not a loop/trim/cutoff outcome. Loop only pads with more
    // plays when audio is the shorter side (MediaCompositionAudioMuxer.AddLoopedToFillAsync);
    // every other case (Trim, or Loop with audio already the longer side) hard-cuts at the
    // runtime boundary, matching AddSequentiallyAsync's own truncate-at-video-length behaviour.
    // This text is shown above both Play and Save — don't claim the fade-out here, since only
    // the fullscreen player (PlayerViewModel.UpdateFadeVolume) actually implements one; the MP4
    // export path (MediaCompositionAudioMuxer) hard-cuts, per its own "Not implemented at M1"
    // doc comment.
    //
    // Public (not private) so it's directly unit-testable without InternalsVisibleTo, matching
    // AudioPeakExtractor.ComputePeaksFromWav's precedent for a pure branch pulled out of a view
    // model otherwise reachable only via a live DispatcherQueue.
    public static string BuildRuntimeSummary(AudioMatch audioMatch, int runtimeMs, int audioLengthMs)
    {
        if (audioLengthMs == 0)
        {
            return $"{FormatDuration(runtimeMs)} show, no audio";
        }

        int diffMs = Math.Abs(runtimeMs - audioLengthMs);
        if (diffMs == 0)
        {
            return $"Audio matches the slideshow length exactly ({FormatDuration(runtimeMs)}).";
        }

        if (audioMatch == AudioMatch.FitSlides)
        {
            return $"Slide timing will stretch to match the audio's {FormatDuration(audioLengthMs)} length.";
        }

        return audioMatch == AudioMatch.Loop && audioLengthMs < runtimeMs
            ? $"Audio is {FormatDuration(diffMs)} short — it will repeat to fill the slideshow."
            : audioLengthMs < runtimeMs
                ? $"Audio is {FormatDuration(diffMs)} short — the slideshow continues in silence after it ends."
                : $"Audio is {FormatDuration(diffMs)} longer — it will cut off when the slideshow ends.";
    }

    internal static string FormatDuration(int milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(milliseconds);
        return span.Hours > 0
            ? $"{span.Hours}:{span.Minutes:D2}:{span.Seconds:D2}"
            : $"{span.Minutes}:{span.Seconds:D2}";
    }

    private async Task LoadSlideAsync(SlideItem item)
    {
        await _thumbnailSemaphore.WaitAsync();
        try
        {
            SlideInfo info = await SlideImageInspector.InspectAsync(item.SourcePath);
            byte[] thumbnailBytes = await SlideImageInspector.GetThumbnailAsync(item.SourcePath);

            _dispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    var stream = new InMemoryRandomAccessStream();
                    await stream.WriteAsync(thumbnailBytes.AsBuffer());
                    stream.Seek(0);

                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(stream);

                    item.ApplySlideInfo(info);
                    item.ThumbnailSource = bitmap;
                    item.IsLoading = false;
                }
                catch (Exception)
                {
                    item.HasError = true;
                    HadProblemThisSession = true;
                    item.IsLoading = false;
                }
            });
        }
        catch (Exception ex)
        {
            // Per-tile error state, not a crash — corrupt/unreadable source files shouldn't take
            // down the whole load. Warning, not error: an individual bad file is expected
            // user-data noise, not an app defect worth treating the same as an unhandled exception.
            AppLog.LogWarning("LoadSlideAsync", $"{item.FileName}: {ex}");
            (string detail, Uri? storeLinkUri) = DescribeLoadError(ex, item.SourcePath);
            _dispatcherQueue.TryEnqueue(() =>
            {
                item.HasError = true;
                HadProblemThisSession = true;
                item.ErrorDetail = detail;
                item.ErrorStoreLinkUri = storeLinkUri;
                item.IsLoading = false;
            });
        }
        finally
        {
            _thumbnailSemaphore.Release();
        }
    }

    private static (string Message, Uri? StoreLinkUri) DescribeLoadError(Exception ex, string sourcePath)
    {
        // Checked directly (not by exception type) since a reopened project makes "the source
        // moved or was deleted" a first-class scenario, and WinRT's own exception shape for a
        // missing file isn't consistent enough to pattern-match on reliably.
        if (!File.Exists(sourcePath))
        {
            return (ResourceLoader.GetString("SourceFileMissingMessage"), null);
        }

        if (ex.HResult == WinCodecErrComponentNotFound &&
            CodecFixLinks.GetFixLink(Path.GetExtension(sourcePath)) is (string messageKey, Uri storeLinkUri))
        {
            return (ResourceLoader.GetString(messageKey), storeLinkUri);
        }

        return (ex.Message, null);
    }
}
