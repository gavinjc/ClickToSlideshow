using System.Xml.Linq;

namespace ClickToSlideshow.Core.Pptx;

/// <summary>
/// The shape of the custom XML part <see cref="PptxSlideshowWriter"/> embeds and
/// <see cref="PptxProjectReader"/> reads back (spec §4.8): the project JSON wrapped in one
/// identifiable XML element, rather than raw JSON bytes typed as XML content — a customXml part
/// is also how PowerPoint stores its own SmartArt/data-binding XML, so the reader needs a real
/// way to recognise this app's part among others rather than taking whichever comes first.
/// </summary>
internal static class PptxProjectXmlContract
{
    public static readonly XName RootElementName = XNamespace.Get("https://clicktoslideshow.app/xml/project/1") + "project";
}
