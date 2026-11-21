using System.Xml.Linq;

namespace ClickToSlideshow.App.Tests;

// The app's English resw read straight from the source tree: ResourceLoader needs a packaged or
// WinUI-hosted process, so tests that check real wording look the strings up here instead.
internal static class AppResw
{
    public static IReadOnlyDictionary<string, string> Strings => LazyStrings.Value;

    private static readonly Lazy<Dictionary<string, string>> LazyStrings = new(() => XDocument
        .Load(SourceTree.Find("src", "ClickToSlideshow.App", "Strings", "en-us", "Resources.resw"))
        .Root!.Elements("data")
        .ToDictionary(data => (string)data.Attribute("name")!, data => (string)data.Element("value")!));

    public static string Get(string key) => Strings.TryGetValue(key, out string? value) ? value : "";
}
