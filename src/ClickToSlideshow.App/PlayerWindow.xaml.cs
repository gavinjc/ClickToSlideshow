using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using ClickToSlideshow.App.ViewModels;
using ClickToSlideshow.Core.Model;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.System;

namespace ClickToSlideshow.App;

/// <summary>
/// The fullscreen player (M3, spec §4.7) — the one sanctioned second window (spec §2). Renders
/// through <see cref="PlayerViewModel.RenderFrame"/> into an offscreen <see cref="CanvasRenderTarget"/>
/// (the same Win2D surface type <see cref="Core.Video.Mp4VideoRenderer"/> already uses for
/// export) and presents each frame as a <see cref="WriteableBitmap"/> on <c>PlayerImage</c> — not
/// Win2D's own <c>CanvasAnimatedControl</c>/<c>CanvasControl</c> XAML controls, which turned out
/// not to be resolvable in this unpackaged WinUI3 app (confirmed by trying: the types exist in
/// the package's metadata but neither the XAML nor C# compiler can see them here, apparently a
/// UWP-only limitation of this Win2D version). A <see cref="DispatcherTimer"/> throttled to the
/// spec's own 30fps target drives the loop, rather than uncapped <c>CompositionTarget.Rendering</c>
/// — deliberately conservative given <see cref="CanvasRenderTarget.GetPixelBytes()"/>'s per-frame
/// GPU→CPU readback cost. Measured against real hardware (2026-09-22, see
/// docs/CURRENT_APPLICATION.md's M3 entry): comfortably under the 33.3ms/frame budget even at
/// this machine's native 2560×1600 (average 7.8ms, max 14.9ms, zero frames over budget), so the
/// documented CanvasSwapChain/SwapChainPanel fallback isn't needed.
/// </summary>
public sealed partial class PlayerWindow : Window
{
    private const string PlayGlyph = "\uE768";
    private const string PauseGlyph = "\uE769";

    private static readonly TimeSpan ChromeHideDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FrameInterval = TimeSpan.FromSeconds(1.0 / 30.0);

    public PlayerViewModel ViewModel { get; }

    private readonly AppWindow _appWindow;
    private readonly DispatcherTimer _chromeHideTimer;
    private readonly DispatcherTimer _renderTimer;
    private readonly CanvasDevice _device = CanvasDevice.GetSharedDevice();

    private CanvasRenderTarget? _renderTarget;
    private WriteableBitmap? _writeableBitmap;

    public PlayerWindow(SlideshowProject project)
    {
        InitializeComponent();

        ViewModel = new PlayerViewModel(project, DispatcherQueue);
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.Ended += ViewModel_Ended;

        _chromeHideTimer = new DispatcherTimer { Interval = ChromeHideDelay };
        _chromeHideTimer.Tick += (_, _) =>
        {
            _chromeHideTimer.Stop();
            ChromeOverlay.Visibility = Visibility.Collapsed;
        };

        _renderTimer = new DispatcherTimer { Interval = FrameInterval };
        _renderTimer.Tick += RenderTimer_Tick;
        _renderTimer.Start();

        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        // Activate before switching presenters — SetPresenter(FullScreen) on a window that has
        // never been shown leaves it fullscreen-configured but never actually visible (confirmed
        // by testing: the window and its MediaPlayer existed but IsWindowVisible was false).
        Activate();
        EnterFullScreenOnBestMonitor();

        RootGrid.Loaded += (_, _) => RootGrid.Focus(FocusState.Programmatic);
        Closed += (_, _) =>
        {
            _chromeHideTimer.Stop();
            _renderTimer.Stop();
            _renderTarget?.Dispose();
            ViewModel.Cleanup();
        };

        ViewModel.TogglePlayPause();
        RestartChromeHideTimer();
    }

    // Multi-monitor default (spec §4.7): prefer the first non-primary display so the editor stays
    // visible on the primary. AppWindow.Move must happen before SetPresenter(FullScreen) — the
    // presenter fullscreens on whichever monitor the window already overlaps.
    //
    // Indexed loop, not LINQ: DisplayArea.FindAll()'s IReadOnlyList<DisplayArea> throws
    // InvalidCastException from FirstOrDefault() (confirmed via a live crash log — WinRT.
    // IObjectReference.As<T> fails resolving IEnumerable<DisplayArea> off this specific
    // projected collection, even though indexer/Count access work fine). Exactly the class of
    // CsWinRT issue the M0 spike's own notes warned to watch for.
    private void EnterFullScreenOnBestMonitor()
    {
        IReadOnlyList<DisplayArea> displayAreas = DisplayArea.FindAll();
        DisplayArea target = DisplayArea.Primary;
        for (int i = 0; i < displayAreas.Count; i++)
        {
            if (!displayAreas[i].IsPrimary)
            {
                target = displayAreas[i];
                break;
            }
        }

        _appWindow.Move(new Windows.Graphics.PointInt32(target.OuterBounds.X, target.OuterBounds.Y));
        _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
    }

    private void ToggleFullScreen()
    {
        _appWindow.SetPresenter(_appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen
            ? AppWindowPresenterKind.Default
            : AppWindowPresenterKind.FullScreen);
    }

    // Render surfaces are sized in DIPs at a fixed 96 DPI, not the window's actual rasterization
    // scale — simpler than reconciling Win2D's DPI-aware CanvasRenderTarget with a manually
    // copied WriteableBitmap, at the cost of a soft (not crisp) image on a >100%-scaled display.
    // Flagged rather than solved: not a functional gap, just an unaddressed quality one.
    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        int width = Math.Max(1, (int)e.NewSize.Width);
        int height = Math.Max(1, (int)e.NewSize.Height);

        _renderTarget?.Dispose();
        _renderTarget = new CanvasRenderTarget(_device, width, height, 96);
        _writeableBitmap = new WriteableBitmap(width, height);
        PlayerImage.Source = _writeableBitmap;
    }

    private void RenderTimer_Tick(object? sender, object e)
    {
        if (_renderTarget is null || _writeableBitmap is null)
        {
            return;
        }

        using (CanvasDrawingSession session = _renderTarget.CreateDrawingSession())
        {
            ViewModel.RenderFrame(_device, session, new Size(_renderTarget.Size.Width, _renderTarget.Size.Height));
        }

        byte[] pixels = _renderTarget.GetPixelBytes();
        using (Stream pixelStream = _writeableBitmap.PixelBuffer.AsStream())
        {
            pixelStream.Write(pixels, 0, pixels.Length);
        }

        _writeableBitmap.Invalidate();
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => ViewModel.TogglePlayPause();

    private void PreviousButton_Click(object sender, RoutedEventArgs e) => ViewModel.PreviousSlide();

    private void NextButton_Click(object sender, RoutedEventArgs e) => ViewModel.NextSlide();

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.IsPlaying))
        {
            PlayPauseIcon.Glyph = ViewModel.IsPlaying ? PauseGlyph : PlayGlyph;
        }
    }

    // Playback reached the end of the show on its own — reveal chrome rather than leaving the
    // user stuck looking at a frozen, hidden-chrome screen (ViewModel already paused itself).
    private void ViewModel_Ended(object? sender, EventArgs e) => ShowChrome();

    private void ShowChrome()
    {
        ChromeOverlay.Visibility = Visibility.Visible;
        RestartChromeHideTimer();
    }

    private void RestartChromeHideTimer()
    {
        _chromeHideTimer.Stop();
        _chromeHideTimer.Start();
    }

    private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e) => ShowChrome();

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Space:
                ViewModel.TogglePlayPause();
                ShowChrome();
                e.Handled = true;
                break;

            case VirtualKey.Left:
                ViewModel.PreviousSlide();
                ShowChrome();
                e.Handled = true;
                break;

            case VirtualKey.Right:
                ViewModel.NextSlide();
                ShowChrome();
                e.Handled = true;
                break;

            case VirtualKey.Escape:
                Close();
                e.Handled = true;
                break;

            case VirtualKey.F:
                ToggleFullScreen();
                e.Handled = true;
                break;
        }
    }
}
