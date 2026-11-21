using ClickToSlideshow.App.ViewModels;
using ClickToSlideshow.Core.Model;

namespace ClickToSlideshow.App.Tests;

// BuildRuntimeSummary's branching (audio-section-brief.md §3.6) is easy to get subtly wrong —
// it's pure and needs no live DispatcherQueue, so there's no reason to leave it untested.
public class MainViewModelRuntimeSummaryTests
{
    [Fact]
    public void BuildRuntimeSummary_NoAudio_StatesShowLengthOnly()
    {
        string summary = MainViewModel.BuildRuntimeSummary(AudioMatch.Trim, runtimeMs: 19000, audioLengthMs: 0);

        Assert.Equal("0:19 show, no audio", summary);
    }

    [Fact]
    public void BuildRuntimeSummary_ExactMatch_StatesLengthsMatch()
    {
        string summary = MainViewModel.BuildRuntimeSummary(AudioMatch.Trim, runtimeMs: 19000, audioLengthMs: 19000);

        Assert.Equal("Audio matches the slideshow length exactly (0:19).", summary);
    }

    [Theory]
    [InlineData(19000, 8000)] // audio shorter than the show
    [InlineData(8000, 19000)] // audio longer than the show
    public void BuildRuntimeSummary_FitSlides_AlwaysDescribesStretchingRegardlessOfWhichSideIsLonger(
        int runtimeMs, int audioLengthMs)
    {
        string summary = MainViewModel.BuildRuntimeSummary(AudioMatch.FitSlides, runtimeMs, audioLengthMs);

        Assert.Contains("stretch", summary);
    }

    [Fact]
    public void BuildRuntimeSummary_LoopWithShorterAudio_DescribesRepeatingWithoutClaimingAFadeOut()
    {
        string summary = MainViewModel.BuildRuntimeSummary(AudioMatch.Loop, runtimeMs: 19000, audioLengthMs: 8000);

        // Save (MP4 export) never implemented the fade-out this text used to promise — see
        // MediaCompositionAudioMuxer's own "Not implemented at M1" doc comment. This summary is
        // shown above both Play and Save, so it must not claim a fade only one of them does.
        Assert.Contains("repeat", summary);
        Assert.DoesNotContain("fade", summary);
    }

    [Fact]
    public void BuildRuntimeSummary_TrimWithShorterAudio_DescribesSilenceNotRepeat()
    {
        string summary = MainViewModel.BuildRuntimeSummary(AudioMatch.Trim, runtimeMs: 19000, audioLengthMs: 8000);

        Assert.Contains("silence", summary);
    }

    [Theory]
    [InlineData(AudioMatch.Trim)]
    [InlineData(AudioMatch.Loop)]
    public void BuildRuntimeSummary_AudioLongerThanShow_DescribesCutoffRegardlessOfMode(AudioMatch audioMatch)
    {
        string summary = MainViewModel.BuildRuntimeSummary(audioMatch, runtimeMs: 8000, audioLengthMs: 19000);

        Assert.Contains("cut off", summary);
    }
}
