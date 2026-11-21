using System.Text;
using System.Xml.Linq;
using ClickToSlideshow.Core.Model;
using ClickToSlideshow.Core.Pptx;
using ClickToSlideshow.Core.Serialization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using P = DocumentFormat.OpenXml.Presentation;

namespace ClickToSlideshow.Core.Tests;

public class PptxProjectReaderTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "ClickToSlideshowTests", Guid.NewGuid().ToString());

    public PptxProjectReaderTests() => Directory.CreateDirectory(_tempDirectory);

    public void Dispose() => Directory.Delete(_tempDirectory, recursive: true);

    [Fact]
    public void ReadEmbeddedProject_PptxWithNoEmbeddedProjectData_ThrowsActionableMessage()
    {
        // A .pptx not saved by this app (or a stray customXml part from something else entirely,
        // e.g. PowerPoint's own SmartArt data) has no matching part for
        // PptxProjectXmlContract.RootElementName to find — this must fail clearly, not with a
        // NullReferenceException or a raw deserialization error.
        string path = Path.Combine(_tempDirectory, "no-project-data.pptx");
        using (PresentationDocument document = PresentationDocument.Create(path, PresentationDocumentType.Presentation))
        {
            PresentationPart presentationPart = document.AddPresentationPart();
            presentationPart.Presentation = new P.Presentation();
            presentationPart.Presentation.Save();
        }

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => PptxProjectReader.ReadEmbeddedProject(path));
        Assert.Contains("no embedded", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Matches PptxProjectXmlContract.RootElementName (internal, so duplicated here as the public
    // wire contract a black-box test is allowed to know) rather than reaching into internals.
    private static readonly XName ProjectRootElementName =
        XNamespace.Get("https://clicktoslideshow.app/xml/project/1") + "project";

    [Fact]
    public void ReadEmbeddedProject_MultipleCustomXmlParts_FindsTheRightOneAmongForeignAndMalformedParts()
    {
        // A customXml part is also how PowerPoint stores its own SmartArt/data-binding XML, and a
        // part can be malformed XML entirely — FindProjectJson has to skip both and keep looking
        // rather than stopping at (or crashing on) the first part it opens.
        string path = Path.Combine(_tempDirectory, "multiple-parts.pptx");
        string imagePath = Path.Combine(_tempDirectory, "photo.jpg");
        string json = SlideshowProjectFile.ToJson(
            new SlideshowProject { Slides = { new Slide { SourcePath = imagePath, DurationMs = 2500 } } },
            _tempDirectory);

        using (PresentationDocument document = PresentationDocument.Create(path, PresentationDocumentType.Presentation))
        {
            PresentationPart presentationPart = document.AddPresentationPart();
            presentationPart.Presentation = new P.Presentation();
            presentationPart.Presentation.Save();

            AddCustomXmlPart(presentationPart, "<not valid xml");
            AddCustomXmlPart(presentationPart, "<foreignRoot xmlns=\"urn:example:other\">not ours</foreignRoot>");
            AddCustomXmlPart(presentationPart, new XElement(ProjectRootElementName, json).ToString(SaveOptions.DisableFormatting));
        }

        SlideshowProject reloaded = PptxProjectReader.ReadEmbeddedProject(path);

        Assert.Single(reloaded.Slides);
        Assert.Equal(imagePath, reloaded.Slides[0].SourcePath);
        Assert.Equal(2500, reloaded.Slides[0].DurationMs);
    }

    [Fact]
    public void ReadEmbeddedProject_EmbeddedPartHasInvalidJson_ThrowsActionableMessage()
    {
        string path = Path.Combine(_tempDirectory, "corrupted-project-data.pptx");
        using (PresentationDocument document = PresentationDocument.Create(path, PresentationDocumentType.Presentation))
        {
            PresentationPart presentationPart = document.AddPresentationPart();
            presentationPart.Presentation = new P.Presentation();
            presentationPart.Presentation.Save();

            AddCustomXmlPart(presentationPart, new XElement(ProjectRootElementName, "{ not valid json").ToString(SaveOptions.DisableFormatting));
        }

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => PptxProjectReader.ReadEmbeddedProject(path));
        Assert.Contains("edited by another tool", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddCustomXmlPart(PresentationPart presentationPart, string content)
    {
        CustomXmlPart part = presentationPart.AddCustomXmlPart(CustomXmlPartType.CustomXml);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        part.FeedData(stream);
    }
}
