using Bureau.Common.Help;

namespace ClickToSlideshow.App.Tests;

// HelpTopics.Load leaves out a topic whose Title or Body string is missing, so a mistyped key
// would make a topic quietly vanish from Help. This reads the resw itself to catch that.
public class HelpTopicKeysTests
{
    [Fact]
    public void EveryHelpTopic_HasItsTitleAndBodyInTheResw()
    {
        IReadOnlyList<HelpTopic> topics = HelpTopics.Load(AppResw.Get, HelpTopicKeys.InOrder);

        Assert.Equal(HelpTopicKeys.InOrder.Count, topics.Count);
    }

    // Named one by one so that dropping a topic from InOrder fails here, and a missing string
    // names the topic it belongs to.
    [Theory]
    [InlineData("HelpGettingStarted")]
    [InlineData("HelpSupportedFiles")]
    [InlineData("HelpSkippedFiles")]
    [InlineData("HelpArrangingSlides")]
    [InlineData("HelpDurations")]
    [InlineData("HelpTransitions")]
    [InlineData("HelpMusic")]
    [InlineData("HelpRuntimeBar")]
    [InlineData("HelpOptions")]
    [InlineData("HelpPlaying")]
    [InlineData("HelpSaving")]
    [InlineData("HelpReopening")]
    [InlineData("HelpKeyboardShortcuts")]
    [InlineData("HelpPrivacy")]
    public void HelpTopic_IsListedAndResolvesToText(string key)
    {
        Assert.Contains(key, HelpTopicKeys.InOrder);
        Assert.False(string.IsNullOrWhiteSpace(AppResw.Get(key + "Title")), $"{key}Title");
        Assert.False(string.IsNullOrWhiteSpace(AppResw.Get(key + "Body")), $"{key}Body");
    }

    // HelpBody turns any line holding " | " into a shortcut-table row, so a stray one in prose
    // would render as a key cap. Only the shortcuts topic is meant to have a table.
    [Fact]
    public void OnlyTheShortcutsTopic_HasTableRows()
    {
        List<string> topicsWithRows = HelpTopicKeys.InOrder
            .Where(key => AppResw.Get(key + "Body").Contains(" | ", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(["HelpKeyboardShortcuts"], topicsWithRows);
    }

    // The codec messages on a slide that can't load and the Help topic about them must give the
    // same fix. They once said "reload this file", which the app has no way to do.
    [Theory]
    [InlineData("HeifCodecMissingMessage")]
    [InlineData("RawCodecMissingMessage")]
    [InlineData("WebpCodecMissingMessage")]
    public void CodecMessage_GivesTheFixHelpGives(string messageKey)
    {
        Assert.Contains("remove this slide and add the photo again", AppResw.Get(messageKey), StringComparison.Ordinal);
        Assert.DoesNotContain("reload", AppResw.Get(messageKey), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SkippedFilesTopic_GivesTheFixTheCodecMessagesGive() =>
        Assert.Contains("remove the slide and add the photo again", AppResw.Get("HelpSkippedFilesBody"), StringComparison.Ordinal);
}
