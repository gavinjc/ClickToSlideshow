using ClickToSlideshow.Core.Imaging;
using ClickToSlideshow.Core.Model;
using Windows.Media.MediaProperties;

namespace ClickToSlideshow.Core.Video;

/// <summary>
/// The MP4 encoding profile for a project's <see cref="Resolution"/>. Every consumer of the
/// output size (the video pass, the audio mux pass, the PPTX slide size) goes through here, so
/// they never disagree on it.
/// </summary>
internal static class Mp4Profiles
{
    private const long HdArea = 1920L * 1080;

    public static async Task<MediaEncodingProfile> ForProjectAsync(SlideshowProject project) => project.Resolution switch
    {
        Resolution.HD1080p => MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p),
        Resolution.UHD2160p => MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Uhd2160p),
        Resolution.Original => await ForOriginalAsync(project),
        _ => throw new ArgumentOutOfRangeException(nameof(project), project.Resolution, null),
    };

    /// <summary>
    /// A profile for an arbitrary frame size, with the bitrate Windows' own preset gives the
    /// nearest standard size (1080p up to its area, 2160p above), scaled by pixel area. At exactly
    /// 1920x1080 or 3840x2160 this is the preset itself.
    /// </summary>
    public static MediaEncodingProfile ForFrameSize(uint width, uint height)
    {
        long area = (long)width * height;
        MediaEncodingProfile profile = MediaEncodingProfile.CreateMp4(
            area <= HdArea ? VideoEncodingQuality.HD1080p : VideoEncodingQuality.Uhd2160p);
        long presetArea = (long)profile.Video.Width * profile.Video.Height;

        profile.Video.Bitrate = (uint)Math.Max(1, Math.Round(profile.Video.Bitrate * (double)area / presetArea));
        profile.Video.Width = width;
        profile.Video.Height = height;
        return profile;
    }

    private static async Task<MediaEncodingProfile> ForOriginalAsync(SlideshowProject project)
    {
        var sizes = new List<SlidePixelSize>();
        foreach (Slide slide in project.Slides)
        {
            try
            {
                SlideInfo info = await SlideImageInspector.InspectAsync(slide.SourcePath);
                sizes.Add(new SlidePixelSize(info.PixelWidth, info.PixelHeight, slide.RotationDegrees));
            }
            catch (Exception)
            {
                // An unreadable slide has no size to offer. It still fails the render later, at
                // decode, with its own error; choosing a frame size isn't the place to report it.
            }
        }

        (int width, int height) = OriginalFrameSize.Choose(sizes) ?? (1920, 1080);
        return ForFrameSize((uint)width, (uint)height);
    }
}
