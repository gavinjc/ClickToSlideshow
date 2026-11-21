using System.Text.Json;
using System.Xml.Linq;
using ClickToSlideshow.Core.Model;
using ClickToSlideshow.Core.Serialization;
using DocumentFormat.OpenXml.Packaging;

namespace ClickToSlideshow.Core.Pptx;

/// <summary>
/// The read half of the PPTX round-trip (spec §4.8, §6 M5): extracts the project JSON
/// <see cref="PptxSlideshowWriter"/> embeds as a custom XML part and hands it to
/// <see cref="SlideshowProjectFile.FromJson"/> — the exact same DTO mapping the plain JSON
/// loader uses, so a PPTX and a JSON project resolve relative source paths identically.
/// </summary>
public static class PptxProjectReader
{
    public static SlideshowProject ReadEmbeddedProject(string pptxPath)
    {
        using PresentationDocument document = PresentationDocument.Open(pptxPath, isEditable: false);
        PresentationPart presentationPart = document.PresentationPart
            ?? throw new InvalidOperationException($"'{pptxPath}' has no presentation part.");

        string json = FindProjectJson(presentationPart)
            ?? throw new InvalidOperationException(
                $"'{pptxPath}' has no embedded ClickTo: Slideshow project data — it wasn't saved by this app.");

        string baseDirectory = Path.GetDirectoryName(Path.GetFullPath(pptxPath))!;
        try
        {
            return SlideshowProjectFile.FromJson(json, baseDirectory);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(
                $"'{pptxPath}' has embedded project data, but it isn't valid — it may have been edited by another tool.");
        }
    }

    // A customXml part is also how PowerPoint stores its own SmartArt/data-binding XML, so this
    // can't just take the first part — it has to recognise this app's own element (see
    // PptxProjectXmlContract) among whatever else the deck's customXml parts might hold.
    private static string? FindProjectJson(PresentationPart presentationPart)
    {
        foreach (CustomXmlPart part in presentationPart.CustomXmlParts)
        {
            using Stream stream = part.GetStream();
            XElement? element;
            try
            {
                element = XElement.Load(stream);
            }
            catch (System.Xml.XmlException)
            {
                continue;
            }

            if (element.Name == PptxProjectXmlContract.RootElementName)
            {
                return element.Value;
            }
        }

        return null;
    }
}
