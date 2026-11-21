using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Bureau.Common;
using Bureau.Common.Help;
using Bureau.Common.Messages;
using ClickToSlideshow.App.Diagnostics;
using ClickToSlideshow.App.State;
using ClickToSlideshow.App.ViewModels;
using ClickToSlideshow.Core;
using ClickToSlideshow.Core.Imaging;
using ClickToSlideshow.Core.Model;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;

namespace ClickToSlideshow.App;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    private static readonly ResourceLoader ResourceLoader = new();

    private SlideItem? _previewItem;
    private CancellationTokenSource? _saveCts;

    public MainWindow(string? folderPath)
    {
        ViewModel = InitializeCommon();

        if (!string.IsNullOrWhiteSpace(folderPath))
        {
            _ = ViewModel.LoadFolderAsync(folderPath);
        }
    }

    // --list/--skipped launch path (App.xaml.cs) — an explicit file selection rather than a whole
    // folder, the real M6 shell-extension contract. skippedNames are files the selection
    // included but the shell extension excluded as unsupported.
    public MainWindow(IReadOnlyList<string> filePaths, IReadOnlyList<string> skippedNames)
    {
        ViewModel = InitializeCommon();

        if (filePaths.Count > 0)
        {
            _ = ViewModel.LoadFilesAsync(filePaths, skippedNames);
        }
    }

    private MainViewModel InitializeCommon()
    {
        InitializeComponent();

        // GridView's own default keyboard handling claims every arrow key (with or without Ctrl)
        // for its built-in cell-to-cell focus navigation and marks the event Handled before a
        // plain XAML KeyDown="..." bubble handler ever sees it — confirmed via UI Automation that
        // this silently killed both Ctrl+Left/Right (MoveSelected) and Left/Right-to-step while
        // the preview overlay is open; only non-arrow shortcuts (R, Delete, Ctrl+Z, Ctrl+A) were
        // ever actually reachable. handledEventsToo:true makes SlidesGridView_KeyDown run
        // regardless, alongside (not instead of) GridView's own navigation.
        SlidesGridView.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(SlidesGridView_KeyDown), true);

        // handledEventsToo for the same kind of reason: the slide grid and audio list take part in
        // every drag over them for their own reorder, and a drop from File Explorer must still
        // reach these when it lands on either list.
        ContentGrid.AddHandler(UIElement.DragOverEvent, new DragEventHandler(ContentGrid_DragOver), true);
        ContentGrid.AddHandler(UIElement.DropEvent, new DragEventHandler(ContentGrid_Drop), true);

        Title = ResourceLoader.GetString("WindowTitle");
        AboutDialog.ProductName = ResourceLoader.GetString("WindowTitle");
        AboutDialog.Description = ResourceLoader.GetString("AppDescriptionText");
        AboutDialog.LogDirectoryPath = AppLog.LogDirectoryPath;
        var viewModel = new MainViewModel(DispatcherQueue);
        viewModel.MessageShown += message => Messages.Show(message.Key, message.Severity, message.Text, [.. message.Actions]);
        viewModel.MessageClosed += Messages.Close;
        RootGrid.DataContext = viewModel;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.BackgroundColor))
            {
                foreach (Button swatch in _backgroundColorSwatchButtons)
                {
                    UpdateBackgroundColorSwatchSelection(swatch);
                }
            }
        };

        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        // Package.appxmanifest's Square44x44Logo only covers the Start tile/pinned-shortcut
        // icon and (when packaged) the taskbar — the running window's own titlebar icon needs
        // this explicit call regardless of packaging state, or it falls back to WinUI's
        // generic default icon.
        Microsoft.UI.WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId).SetIcon("Assets\\ClickToSlideshow.ico");

        TitleBarTheme.Follow(this, RootGrid);

        // Editor only: PlayerWindow gets no F1, so a playing show is never interrupted (decided
        // 2026-10-28).
        HelpKey.Attach(RootGrid, ShowHelp);
        AttachUndoKey();

        AppSettingsData settings = AppSettings.Load();
        ZoomSlider.Value = settings.ZoomValue;
        viewModel.CurrentSortOrder = settings.LastSortOrder;

        // Save-on-close rather than save-on-every-drag-tick: a continuous Slider.ValueChanged
        // during a drag would otherwise hammer disk I/O for no benefit — the setting only needs
        // to survive across sessions, not live-sync mid-drag.
        Closed += (_, _) =>
        {
            AppSettings.Save(new AppSettingsData(ZoomSlider.Value, viewModel.CurrentSortOrder));
            viewModel.ReleasePreviewPlayer();
        };

        return viewModel;
    }

    // Source of truth stays GridView.SelectedItems; this just mirrors it onto each SlideItem so
    // the DataTemplate can draw its own selection highlight (a container-level template can't
    // reach into the ContentPresenter's own templated content).
    private void SlidesGridView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (SlideItem item in e.RemovedItems.OfType<SlideItem>())
        {
            item.IsSelected = false;
        }

        foreach (SlideItem item in e.AddedItems.OfType<SlideItem>())
        {
            item.IsSelected = true;
        }
    }

    // Handled on the tile's root Grid rather than per-button: PointerEntered/Exited only fire at
    // the boundary of the element they're attached to, so attaching them here means moving the
    // pointer between the tile and its own buttons never fires Exited.
    private void Tile_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (((FrameworkElement)sender).FindName("ChromePill") is Border pill)
        {
            pill.Opacity = 1.0;
        }
    }

    private void Tile_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (((FrameworkElement)sender).FindName("ChromePill") is Border pill)
        {
            pill.Opacity = 0.55;
        }
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e) => ViewModel.Undo();

    // On the root rather than in SlidesGridView_KeyDown, where it only worked while a slide had
    // focus. HelpKey has already hidden the root's accelerator tooltips.
    private void AttachUndoKey()
    {
        var accelerator = new KeyboardAccelerator { Key = VirtualKey.Z, Modifiers = VirtualKeyModifiers.Control };
        accelerator.Invoked += UndoAccelerator_Invoked;
        RootGrid.KeyboardAccelerators.Add(accelerator);
    }

    // A text box (the duration boxes are NumberBoxes, which hold one) keeps Ctrl+Z for its own
    // text: leaving Handled false lets the key go on to it.
    private void UndoAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FocusManager.GetFocusedElement(RootGrid.XamlRoot) is TextBox)
        {
            return;
        }

        args.Handled = true;
        ViewModel.Undo();
    }

    private void SlidesGridView_DragItemsStarting(object sender, DragItemsStartingEventArgs e) => ViewModel.BeginSlideDrag();

    private void SlidesGridView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) =>
        ViewModel.EndSlideDrag();

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Slides.Count == 0 || !ViewModel.CanEdit)
        {
            return;
        }

        // A track preview and the real Play button must never overlap (brief §3.4).
        ViewModel.StopPreview();

        // PlayerWindow activates itself during construction (needs to be shown before it can
        // switch to a fullscreen presenter) — nothing further to do here.
        _ = new PlayerWindow(ViewModel.CreatePlaybackSnapshot());
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Slides.Count > 0 || ViewModel.AudioTracks.Count > 0)
        {
            DiscardChangesDialog.XamlRoot = RootGrid.XamlRoot;
            if (await DiscardChangesDialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SettingsIdentifier = "OpenProjectFolder",
        };
        picker.FileTypeFilter.Add(".json");
        picker.FileTypeFilter.Add(".pptx");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        await ViewModel.LoadProjectAsync(file.Path);
        SyncOptionsControlsFromViewModel();
    }

    // Save's main part saves a video: nothing in the spec named a default, and MP4 is the format
    // that plays anywhere. A fixed default rather than the last format used, so the tooltip can say
    // exactly what a click does.
    private async void SaveButton_Click(SplitButton sender, SplitButtonClickEventArgs args) => await SaveVideoAsync();

    private async void SaveVideo_Click(object sender, RoutedEventArgs e) => await SaveVideoAsync();

    private async Task SaveVideoAsync()
    {
        if (ViewModel.Slides.Count == 0 || !ViewModel.CanEdit)
        {
            return;
        }

        StorageFile? file = await PickSaveFileAsync(PickerLocationId.VideosLibrary, "SaveVideoFileTypeLabel", ".mp4");
        if (file is not null)
        {
            await SaveWithProgressAsync(file, (progress, token) => ViewModel.RenderVideoAsync(file.Path, progress, token));
        }
    }

    private async void SavePptx_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Slides.Count == 0 || !ViewModel.CanEdit)
        {
            return;
        }

        StorageFile? file = await PickSaveFileAsync(PickerLocationId.DocumentsLibrary, "SavePptxFileTypeLabel", ".pptx");
        if (file is not null)
        {
            await SaveWithProgressAsync(file, (progress, token) => ViewModel.RenderPptxAsync(file.Path, progress, token));
        }
    }

    // Video and PowerPoint: the progress row with Cancel while it renders, then the Saved row and
    // the rating prompt, or the error.
    private async Task SaveWithProgressAsync(StorageFile file, Func<IProgress<RenderProgress>, CancellationToken, Task> render)
    {
        Messages.Close(MessageKeys.Saved);
        SaveProgressPanel.Visibility = Visibility.Visible;
        ShowSaveProgress(new RenderProgress(RenderPhase.Frames, 0));

        _saveCts = new CancellationTokenSource();
        var progress = new Progress<RenderProgress>(report => DispatcherQueue.TryEnqueue(() => ShowSaveProgress(report)));
        bool saved = false;

        try
        {
            await ViewModel.SaveLockedAsync(() => render(progress, _saveCts.Token));
            ShowSaved(file);
            saved = true;
        }
        catch (OperationCanceledException)
        {
            // SlideshowRenderer.RenderAsync and PptxSlideshowWriter.WriteAsync both delete their
            // partial output on cancellation, so nothing is left for this layer to delete.
        }
        catch (Exception ex)
        {
            AppLog.LogError($"Save {file.FileType}", ex);
            ViewModel.ShowError(ex.Message);
        }
        finally
        {
            SaveProgressPanel.Visibility = Visibility.Collapsed;
            _saveCts.Dispose();
            _saveCts = null;
        }

        if (saved)
        {
            await OfferRatingIfSessionCleanAsync();
        }
    }

    // The success row carries Open and Show in folder (app-conventions.md → Message bar).
    private void ShowSaved(StorageFile file)
    {
        string path = file.Path;
        Messages.Show(
            MessageKeys.Saved,
            MessageSeverity.Success,
            string.Format(ResourceLoader.GetString("SavedFormat"), file.Name),
            new MessageAction(
                ResourceLoader.GetString("SavedOpenAction"),
                () => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })),
            new MessageAction(
                ResourceLoader.GetString("SavedShowInFolderAction"),
                () => Process.Start("explorer.exe", $"/select,\"{path}\"")));
    }

    private void ShowSaveProgress(RenderProgress report)
    {
        int percent = (int)Math.Round(report.Fraction * 100);
        string formatKey = report.Phase == RenderPhase.Audio ? "SaveProgressAudioFormat" : "SaveProgressFramesFormat";
        SaveProgressFill.Width = SaveProgressTrack.Width * Math.Clamp(report.Fraction, 0, 1);
        SaveProgressText.Text = string.Format(ResourceLoader.GetString(formatKey), percent);
    }

    // Only after a finished video or PowerPoint, not a project-file save, which is a checkpoint
    // mid-work. And only when nothing went wrong this session (MainViewModel.HadProblemThisSession):
    // a photo that failed to decode (a missing codec included), unreadable or missing audio, or an
    // earlier failed load or save means the app let the user down, and the Store prompt must never
    // reach them then (Bureau.Common RatingPrompt). A skipped unsupported file doesn't count.
    // Called after the save's finally, so the progress row is already gone.
    private async Task OfferRatingIfSessionCleanAsync()
    {
        if (!ViewModel.HadProblemThisSession)
        {
            await RatingPrompt.RequestOnceAsync(this);
        }
    }

    private void SaveProjectJson_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Slides.Count == 0 || !ViewModel.CanEdit)
        {
            return;
        }

        _ = SaveProjectJsonAsync();
    }

    private async Task SaveProjectJsonAsync()
    {
        StorageFile? file = await PickSaveFileAsync(PickerLocationId.DocumentsLibrary, "SaveProjectFileTypeLabel", ".json");
        if (file is null)
        {
            return;
        }

        Messages.Close(MessageKeys.Saved);
        try
        {
            ViewModel.SaveProjectJson(file.Path);
            ShowSaved(file);
        }
        catch (Exception ex)
        {
            AppLog.LogError("SaveProjectJson_Click", ex);
            ViewModel.ShowError(ex.Message);
        }
    }

    // Shared by SaveVideo_Click and SaveProjectJsonAsync — same picker/naming shape, differing
    // only in the library the picker opens to, the file-type label, and the extension.
    private async Task<StorageFile?> PickSaveFileAsync(PickerLocationId startLocation, string fileTypeLabelResourceKey, string extension)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = startLocation,
            SettingsIdentifier = "SaveFolder",
        };
        picker.FileTypeChoices.Add(ResourceLoader.GetString(fileTypeLabelResourceKey), new List<string> { extension });

        string desiredName = SaveFileNaming.ComputeDefaultFileName(extension);
        string resolvedName = ViewModel.FolderPath is not null
            ? SaveFileNaming.ResolveCollision(ViewModel.FolderPath, desiredName)
            : desiredName;
        picker.SuggestedFileName = Path.GetFileNameWithoutExtension(resolvedName);

        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return await picker.PickSaveFileAsync();
    }

    private void CancelSaveButton_Click(object sender, RoutedEventArgs e) => _saveCts?.Cancel();

    private void HelpButton_Click(object sender, RoutedEventArgs e) => ShowHelp();

    private void ShowHelp() => HelpWindow.Show(
        this,
        ResourceLoader.GetString("WindowTitle"),
        HelpTopics.Load(ResourceLoader.GetString, HelpTopicKeys.InOrder));

    private async void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        AboutDialog.XamlRoot = RootGrid.XamlRoot;
        await AboutDialog.ShowAsync();
    }

    private bool _optionsExpanded;

    private void OptionsHeaderButton_Click(object sender, RoutedEventArgs e)
    {
        _optionsExpanded = !_optionsExpanded;
        OptionsBody.Visibility = _optionsExpanded ? Visibility.Visible : Visibility.Collapsed;
        OptionsHeaderChevron.Glyph = _optionsExpanded ? "" : "";
    }

    // Sort's main part repeats the last order chosen (name until one is), as ClickToSpeech's does
    // — Gavin, 2026-11-03.
    private void SortButton_Click(SplitButton sender, SplitButtonClickEventArgs args) =>
        ApplySortAndPersist(ViewModel.CurrentSortOrder);

    private void SortByName_Click(object sender, RoutedEventArgs e) => ApplySortAndPersist(SortOrder.NameNatural);

    private void SortByDateTaken_Click(object sender, RoutedEventArgs e) => ApplySortAndPersist(SortOrder.DateTaken);

    private void SortByDateModified_Click(object sender, RoutedEventArgs e) => ApplySortAndPersist(SortOrder.DateModified);

    private void ReverseOrder_Click(object sender, RoutedEventArgs e) => ViewModel.ReverseOrder();

    private void ApplySortAndPersist(SortOrder order)
    {
        ViewModel.ApplySort(order);
        AppSettings.Save(new AppSettingsData(ZoomSlider.Value, order));
    }

    private void RotateButton_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is SlideItem item)
        {
            ViewModel.RotateSelected([item], clockwise: true);
        }
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is SlideItem item)
        {
            ViewModel.RemoveSelected([item]);
        }
    }

    private void TileTransition_Click(object sender, RoutedEventArgs e)
    {
        var menuItem = (MenuFlyoutItem)sender;
        if (menuItem.Tag is SlideItem item)
        {
            ViewModel.SetTransitionIn(
                [item], menuItem.CommandParameter is string kindName ? Enum.Parse<TransitionKind>(kindName) : null);
        }
    }

    // NumberBox's Minimum=0 doubles as "use the project default" (SlideItem.DurationMsOrZero) —
    // 0 or NaN both clear the override rather than writing a nonsensical zero-length slide.
    // ValueChanged fires on a committed value (Enter, leaving the box, a spin button), not per
    // keystroke, so each is one undo step; Ctrl+Z inside the box still undoes the typing.
    private void DurationNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (sender.Tag is SlideItem item)
        {
            ViewModel.SetDuration([item], double.IsNaN(args.NewValue) || args.NewValue <= 0 ? null : (int)args.NewValue);
        }
    }

    // ValueChanged handlers, not TwoWay x:Bind: NumberBox.Value is a double and both VM
    // properties are int — TwoWay x:Bind needs an implicit conversion in both directions, and
    // double-to-int narrowing isn't one. OneWay display binding (XAML) + this handler for the
    // write-back avoids the compile failure that would otherwise cause.
    private void DefaultSlideMsNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (ViewModel is not null && !double.IsNaN(args.NewValue))
        {
            ViewModel.DefaultSlideMs = (int)args.NewValue;
        }
    }

    private void TransitionMsNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (ViewModel is not null && !double.IsNaN(args.NewValue))
        {
            ViewModel.TransitionMs = (int)args.NewValue;
        }
    }

    private async void Tile_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // DoubleTapped is routed and bubbles from the chrome buttons up through this tile's root
        // Grid — a Button's Click handler doesn't mark it handled, so two quick clicks on
        // Preview/Rotate/Remove/set-duration also opened the preview underneath. Ignore
        // double-taps that originated inside a button rather than on the tile/image itself.
        if (e.OriginalSource is DependencyObject originalSource && HasButtonAncestor(originalSource))
        {
            return;
        }

        if (((FrameworkElement)sender).Tag is SlideItem item)
        {
            await ShowPreviewAsync(item);
        }
    }

    private static bool HasButtonAncestor(DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Button)
            {
                return true;
            }
        }

        return false;
    }

    private async void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is SlideItem item)
        {
            await ShowPreviewAsync(item);
        }
    }

    private async Task ShowPreviewAsync(SlideItem item)
    {
        try
        {
            byte[] bytes = await SlideImageInspector.GetThumbnailAsync(item.SourcePath, requestedLongEdge: 1600);

            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);

            _previewItem = item;
            PreviewImage.Source = bitmap;
            PreviewImageRotation.Angle = item.RotationDegrees;
            PreviewOverlay.Visibility = Visibility.Visible;
            UpdatePreviewIndicator();
        }
        catch (Exception)
        {
            // Preview is best-effort — leave the overlay closed on failure rather than crash.
        }
    }

    // Left/Right while the overlay is open (wired from SlidesGridView_KeyDown) — clamps at the
    // ends rather than wrapping, the less surprising choice for stepping through a finite order.
    private async Task StepPreviewAsync(int delta)
    {
        if (_previewItem is null)
        {
            return;
        }

        int index = ViewModel.Slides.IndexOf(_previewItem);
        int target = index + delta;
        if (index < 0 || target < 0 || target >= ViewModel.Slides.Count)
        {
            return;
        }

        await ShowPreviewAsync(ViewModel.Slides[target]);
    }

    private void UpdatePreviewIndicator()
    {
        if (_previewItem is null)
        {
            return;
        }

        int index = ViewModel.Slides.IndexOf(_previewItem);
        PreviewIndicatorText.Text = string.Format(
            ResourceLoader.GetString("PreviewIndicatorFormat"), index + 1, ViewModel.Slides.Count);
    }

    private void PreviewOverlay_Tapped(object sender, TappedRoutedEventArgs e) => ClosePreview();

    private void PreviewCloseButton_Click(object sender, RoutedEventArgs e) => ClosePreview();

    private void PreviewRotateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_previewItem is not null)
        {
            ViewModel.RotateSelected([_previewItem], clockwise: true);
            PreviewImageRotation.Angle = _previewItem.RotationDegrees;
        }
    }

    // The overlay's own Background is Tapped-enabled to close on click-outside — since Tapped
    // bubbles, a control placed inside the overlay (close button, rotate button) needs to stop
    // that bubble or every click on them would also close the overlay it just acted on.
    private void StopTappedPropagation(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    private void ClosePreview()
    {
        PreviewOverlay.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
        _previewItem = null;
    }

    // ComboBox's SelectedIndex="0" in XAML fires SelectionChanged synchronously during
    // InitializeComponent() — before the constructor below has assigned ViewModel — so these
    // guard against a null ViewModel rather than relying on ordering.
    //
    // Read via each ComboBoxItem's Tag rather than SelectedIndex — the Tag names the enum member
    // directly, so reordering or inserting a ComboBoxItem in XAML can't silently desync from the
    // enum the way a positional index cast could. Each ComboBox's item order is chosen so index 0
    // matches SlideshowProject's own default for that property (see MainWindow.xaml).
    private static T SelectedOption<T>(ComboBox combo) where T : struct, Enum =>
        Enum.Parse<T>((string)((ComboBoxItem)combo.SelectedItem).Tag);

    // Reverse of SelectedOption<T> — used only after a project JSON load, to bring these four
    // un-bound ComboBoxes back in sync with the values LoadProjectAsync just wrote into
    // ViewModel (DefaultSlideMs/TransitionMs/BackgroundColor are x:Bind and update themselves).
    private static void SetSelectedOption<T>(ComboBox combo, T value) where T : struct, Enum
    {
        string tag = value.ToString();
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().First(item => (string)item.Tag == tag);
    }

    private void SyncOptionsControlsFromViewModel()
    {
        SetSelectedOption(TransitionKindCombo, ViewModel.TransitionKind);
        SetSelectedOption(ResolutionCombo, ViewModel.Resolution);
        SetSelectedOption(FitModeCombo, ViewModel.FitMode);
        SetSelectedOption(AudioMatchCombo, ViewModel.AudioMatch);
    }

    private void TransitionKindCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.TransitionKind = SelectedOption<TransitionKind>(TransitionKindCombo);
        }
    }

    private void ResolutionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.Resolution = SelectedOption<Resolution>(ResolutionCombo);
        }
    }

    private void FitModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.FitMode = SelectedOption<FitMode>(FitModeCombo);
        }
    }

    // Swatch buttons carry their colour as a "#AARRGGBB" Tag (ColorHex's format, shared with
    // project-JSON) rather than a bound Color object — x:Bind can't convert a Tag to
    // Windows.UI.Color without a converter, and these ten values never change at runtime. The
    // Tag is the single source of truth for both the fill (set here) and the click handler below
    // — no separate hex literal on Background to drift out of sync with it.
    private readonly List<Button> _backgroundColorSwatchButtons = new();

    private void BackgroundColorSwatch_Loaded(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.Background = new SolidColorBrush(ColorHex.Parse((string)button.Tag));
        _backgroundColorSwatchButtons.Add(button);
        UpdateBackgroundColorSwatchSelection(button);
    }

    private void BackgroundColorSwatch_Click(object sender, RoutedEventArgs e) =>
        ViewModel.BackgroundColor = ColorHex.Parse((string)((Button)sender).Tag);

    // Ring highlight rather than a checkmark glyph: a checkmark would need a per-swatch
    // contrasting foreground (black on yellow, white on black, ...) to stay visible, while a
    // border colour works the same against every fill. Also re-run for every swatch whenever
    // BackgroundColor changes from any source, including the ColorPicker flyout below, which
    // updates it via TwoWay bind rather than through this click handler.
    private void UpdateBackgroundColorSwatchSelection(Button swatch)
    {
        bool isSelected = ColorHex.Parse((string)swatch.Tag).Equals(ViewModel.BackgroundColor);
        if (isSelected)
        {
            swatch.BorderBrush = (Brush)Application.Current.Resources["AccentButtonBackground"];
            swatch.BorderThickness = new Thickness(3);
        }
        else
        {
            swatch.ClearValue(Control.BorderBrushProperty);
            swatch.ClearValue(Control.BorderThicknessProperty);
        }
    }

    private void AudioMatchCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.AudioMatch = SelectedOption<AudioMatch>(AudioMatchCombo);
        }
    }

    private async void ChooseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SettingsIdentifier = "ChooseFolder",
        };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");

        StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            await ViewModel.LoadFolderAsync(folder.Path);
        }
    }

    // The empty state's picker takes photos and music together, so it is handed on as a drop
    // would be: same split into slides and tracks, same order, same skipped-file messages.
    private async void ChooseFilesButton_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<StorageFile> files = await PickMultipleFilesAsync(
            PickerViewMode.Thumbnail, PickerLocationId.PicturesLibrary, "ChooseFilesFolder",
            [.. SupportedFiles.ImageExtensions, .. SupportedFiles.AudioExtensions]);
        if (files.Count > 0)
        {
            await ViewModel.AddDroppedAsync(files.Select(f => f.Path).ToList());
        }
    }

    private async void AddAudioButton_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<StorageFile> files = await PickMultipleFilesAsync(
            PickerViewMode.List, PickerLocationId.MusicLibrary, "AddAudioFolder", SupportedFiles.AudioExtensions);
        if (files.Count > 0)
        {
            await ViewModel.AddAudioTracksAsync(files.Select(f => f.Path).ToList());
        }
    }

    // Slides' own add button (brief §3.10) — previously the only "add" affordance was Open…,
    // which opens a saved project, not raw images; this is a genuinely new capability.
    private async void AddImagesButton_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<StorageFile> files = await PickMultipleFilesAsync(
            PickerViewMode.Thumbnail, PickerLocationId.PicturesLibrary, "AddPhotosFolder", SupportedFiles.ImageExtensions);
        if (files.Count > 0)
        {
            await ViewModel.AddImagesAsync(files.Select(f => f.Path).ToList());
        }
    }

    // Only a drag carrying files or folders is taken, and none while a save runs. The grid's and
    // audio list's own reorder drags carry none, so they are left to the lists and keep working.
    private void ContentGrid_DragOver(object sender, DragEventArgs e)
    {
        if (ViewModel.CanEdit && e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = ResourceLoader.GetString("DropCaption");
        }
    }

    // The data is read before the deferral completes, as the drag source may let it go after. An
    // item with no path on disk is passed by its name, so it is listed as unreadable rather than
    // vanishing. Not a phone's photo through Phone Link: that arrives with a path and adds
    // normally (Gavin, 2026-11-04).
    private async void ContentGrid_Drop(object sender, DragEventArgs e)
    {
        if (!ViewModel.CanEdit || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        DragOperationDeferral deferral = e.GetDeferral();
        IReadOnlyList<IStorageItem> items;
        try
        {
            items = await e.DataView.GetStorageItemsAsync();
        }
        finally
        {
            deferral.Complete();
        }

        await ViewModel.AddDroppedAsync(items.Select(item => string.IsNullOrEmpty(item.Path) ? item.Name : item.Path).ToList());
    }

    // SettingsIdentifier is scoped per call site (not one shared value) so photos and audio each
    // reopen to their own last-used folder rather than clobbering each other's — same reasoning
    // PickSaveFileAsync's own "SaveFolder" identifier documents.
    private Task<IReadOnlyList<StorageFile>> PickMultipleFilesAsync(
        PickerViewMode viewMode, PickerLocationId startLocation, string settingsIdentifier, IEnumerable<string> extensions)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = viewMode,
            SuggestedStartLocation = startLocation,
            SettingsIdentifier = settingsIdentifier,
        };
        foreach (string extension in extensions)
        {
            picker.FileTypeFilter.Add(extension);
        }
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return picker.PickMultipleFilesAsync().AsTask();
    }

    private void PlayPauseAudioButton_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is AudioTrackItem item)
        {
            ViewModel.TogglePreview(item);
        }
    }

    // Feeds the runtime footer's actual measured bar-track width back into the view model so
    // RuntimeBarWidth/AudioBarWidth stay proportional at any window size (brief §3.6) — debounced
    // on the MainViewModel side since SizeChanged fires continuously during a live resize drag.
    private void RuntimeBarTrack_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ViewModel.SetAudioAvailableWidth(e.NewSize.Width);

    private void WaveformSeekSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var surface = (FrameworkElement)sender;
        if (surface.Tag is AudioTrackItem item &&
            e.GetCurrentPoint(surface).Properties.IsLeftButtonPressed &&
            surface.CapturePointer(e.Pointer))
        {
            ViewModel.ScrubPreview(item, PointerFraction(surface, e));

            // Keeps the list's drag-to-reorder from starting under a scrub.
            e.Handled = true;
        }
    }

    private void WaveformSeekSurface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var surface = (FrameworkElement)sender;
        if (surface.Tag is AudioTrackItem item && surface.PointerCaptures is { Count: > 0 })
        {
            ViewModel.ScrubPreview(item, PointerFraction(surface, e));
        }
    }

    private void WaveformSeekSurface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var surface = (FrameworkElement)sender;
        if (surface.Tag is AudioTrackItem item && surface.PointerCaptures is { Count: > 0 })
        {
            ViewModel.SeekPreview(item, PointerFraction(surface, e));
            surface.ReleasePointerCapture(e.Pointer);
        }
    }

    private void WaveformSeekSurface_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is AudioTrackItem item)
        {
            ViewModel.CancelScrub(item);
        }
    }

    private static double PointerFraction(FrameworkElement surface, PointerRoutedEventArgs e) =>
        Math.Clamp(e.GetCurrentPoint(surface).Position.X / surface.ActualWidth, 0.0, 1.0);

    private void RemoveAudioTrackButton_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is AudioTrackItem item)
        {
            ViewModel.RemoveAudioTrack(item);
        }
    }

    private void SlidesGridView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(CoreVirtualKeyStates.Down);
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(CoreVirtualKeyStates.Down);

        var selected = SlidesGridView.SelectedItems.OfType<SlideItem>().ToList();

        switch (e.Key)
        {
            case VirtualKey.Escape:
                ClosePreview();
                e.Handled = true;
                break;

            case VirtualKey.Left when !ctrl && PreviewOverlay.Visibility == Visibility.Visible:
                _ = StepPreviewAsync(-1);
                e.Handled = true;
                break;

            case VirtualKey.Right when !ctrl && PreviewOverlay.Visibility == Visibility.Visible:
                _ = StepPreviewAsync(1);
                e.Handled = true;
                break;

            case VirtualKey.Left when ctrl:
                ViewModel.MoveSelected(selected, -1);
                e.Handled = true;
                break;

            case VirtualKey.Right when ctrl:
                ViewModel.MoveSelected(selected, 1);
                e.Handled = true;
                break;

            case VirtualKey.R:
                ViewModel.RotateSelected(selected, clockwise: !shift);
                e.Handled = true;
                break;

            case VirtualKey.Delete:
                ViewModel.RemoveSelected(selected);
                e.Handled = true;
                break;

            case VirtualKey.A when ctrl:
                SlidesGridView.SelectAll();
                e.Handled = true;
                break;
        }
    }
}
