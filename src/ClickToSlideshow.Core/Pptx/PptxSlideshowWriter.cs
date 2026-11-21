using System.Runtime.InteropServices.WindowsRuntime;
using System.Xml.Linq;
using ClickToSlideshow.Core.Audio;
using ClickToSlideshow.Core.Imaging;
using ClickToSlideshow.Core.Model;
using ClickToSlideshow.Core.Serialization;
using ClickToSlideshow.Core.Video;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Windows.Graphics.Imaging;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace ClickToSlideshow.Core.Pptx;

/// <summary>
/// Writes a <see cref="SlideshowProject"/> to PPTX (spec §4.8): one full-bleed picture per
/// slide, the §4.6 transition mapping, one background audio track spanning the whole show,
/// per-slide notes, and the project JSON embedded as a custom XML part for round-trip via
/// <see cref="PptxProjectReader"/>. Confirmed against real PowerPoint (2026-09-03) — opens
/// cleanly (no repair prompt), correct slide order/rotation/transition types/advance timing,
/// and the background-audio timing tree survives Slide Show playback without error. See
/// docs/CURRENT_APPLICATION.md's M5 entry for the two real bugs that check caught (neither
/// showed up in <see cref="DocumentFormat.OpenXml.Validation.OpenXmlValidator"/> or the
/// round-trip test). Transition effect duration confirmed via PowerPoint's own object model.
/// </summary>
public static class PptxSlideshowWriter
{
    // 96 DPI, the same assumption Mp4VideoRenderer's profile-derived pixel sizes implicitly make.
    private const double EmuPerPixel = 914400.0 / 96.0;

    // project stays the caller's original, unscaled project throughout — embedded as-is in the
    // custom XML part (see EmbedProjectJson) so reopening restores the user's exact authored
    // values, matching SlideshowProjectFile.Save's contract for JSON. renderProject is the only
    // thing the slide/timing/audio construction below reads, since PPTX is a rendering consumer
    // like the MP4 exporter — its FitSlides-scaled durations are what should actually play back.
    //
    // Progress runs in the opposite order to the MP4's: the soundtrack (Audio) first, because
    // slide 1 carries it, then one Frames step per slide.
    public static async Task WriteAsync(
        SlideshowProject project,
        string outputPath,
        IProgress<RenderProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (project.Slides.Count == 0)
        {
            throw new InvalidOperationException("Cannot write a slideshow with no slides.");
        }

        SlideshowProject renderProject = SlideshowRenderer.EffectiveProject(project);

        MediaEncodingProfile profile = await Mp4Profiles.ForProjectAsync(renderProject);
        long slideWidthEmu = (long)Math.Round(profile.Video.Width * EmuPerPixel);
        long slideHeightEmu = (long)Math.Round(profile.Video.Height * EmuPerPixel);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.Delete(outputPath);

        try
        {
            using PresentationDocument document = PresentationDocument.Create(outputPath, PresentationDocumentType.Presentation);
            PresentationPart presentationPart = document.AddPresentationPart();
            presentationPart.Presentation = new P.Presentation
            {
                SlideSize = new P.SlideSize { Cx = (Int32Value)slideWidthEmu, Cy = (Int32Value)slideHeightEmu },
                NotesSize = new P.NotesSize { Cx = slideHeightEmu, Cy = slideWidthEmu },
            };

            SlideLayoutPart layoutPart = CreateMasterLayoutAndTheme(presentationPart);
            NotesMasterPart notesMasterPart = CreateNotesMaster(presentationPart);

            MediaDataPart? backgroundAudioPart = null;
            if (renderProject.AudioTracks.Count > 0)
            {
                // Reported here, directly, so the save row names this phase from the start: the
                // renderer's own reports come later and through a posted Progress<double>.
                progress?.Report(new RenderProgress(RenderPhase.Audio, 0));
                backgroundAudioPart = await CreateBackgroundAudioMediaPartAsync(
                    document, renderProject, SlideshowRenderer.ForPhase(progress, RenderPhase.Audio), cancellationToken);
            }

            presentationPart.Presentation.SlideIdList = new P.SlideIdList();
            presentationPart.Presentation.SlideMasterIdList = new P.SlideMasterIdList(
                new P.SlideMasterId { Id = 2147483648U, RelationshipId = presentationPart.GetIdOfPart(layoutPart.GetParentParts().OfType<SlideMasterPart>().Single()) });
            presentationPart.Presentation.NotesMasterIdList = new P.NotesMasterIdList(
                new P.NotesMasterId { Id = presentationPart.GetIdOfPart(notesMasterPart) });

            uint nextSlideId = 256;
            for (int i = 0; i < renderProject.Slides.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                SlidePart slidePart = await AddSlideAsync(
                    presentationPart, layoutPart, notesMasterPart, renderProject, i,
                    slideWidthEmu, slideHeightEmu, i == 0 ? backgroundAudioPart : null, cancellationToken);

                presentationPart.Presentation.SlideIdList.Append(new P.SlideId
                {
                    Id = nextSlideId++,
                    RelationshipId = presentationPart.GetIdOfPart(slidePart),
                });

                progress?.Report(new RenderProgress(RenderPhase.Frames, (i + 1) / (double)renderProject.Slides.Count));
            }

            EmbedProjectJson(presentationPart, project);

            presentationPart.Presentation.Save();
        }
        catch
        {
            try
            {
                File.Delete(outputPath);
            }
            catch (Exception)
            {
            }

            throw;
        }
    }

    /// <summary>
    /// The minimal master/layout/theme chain PowerPoint needs to open the file without a
    /// "repair" prompt — no styling beyond the default Office theme, since every slide draws its
    /// own full-bleed content and never touches placeholder formatting.
    /// </summary>
    private static SlideLayoutPart CreateMasterLayoutAndTheme(PresentationPart presentationPart)
    {
        SlideMasterPart masterPart = presentationPart.AddNewPart<SlideMasterPart>();
        ThemePart themePart = masterPart.AddNewPart<ThemePart>();
        themePart.Theme = BuildMinimalTheme();

        SlideLayoutPart layoutPart = masterPart.AddNewPart<SlideLayoutPart>();
        // The master->layout relationship above isn't enough on its own — PowerPoint's loader
        // (not OpenXmlValidator, which stayed silent on this) also requires the reverse
        // layout->master relationship, confirmed by opening a file missing it in real PowerPoint
        // ("PowerPoint found a problem with content ... can attempt to repair").
        layoutPart.AddPart(masterPart);
        layoutPart.SlideLayout = new P.SlideLayout(
            new P.CommonSlideData(new P.ShapeTree(
                new P.NonVisualGroupShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                    new P.NonVisualGroupShapeDrawingProperties(),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.GroupShapeProperties(new A.TransformGroup()))))
        { Type = P.SlideLayoutValues.Blank };

        masterPart.SlideMaster = new P.SlideMaster(
            new P.CommonSlideData(new P.ShapeTree(
                new P.NonVisualGroupShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                    new P.NonVisualGroupShapeDrawingProperties(),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.GroupShapeProperties(new A.TransformGroup()))),
            new P.ColorMap
            {
                Background1 = A.ColorSchemeIndexValues.Light1,
                Text1 = A.ColorSchemeIndexValues.Dark1,
                Background2 = A.ColorSchemeIndexValues.Light2,
                Text2 = A.ColorSchemeIndexValues.Dark2,
                Accent1 = A.ColorSchemeIndexValues.Accent1,
                Accent2 = A.ColorSchemeIndexValues.Accent2,
                Accent3 = A.ColorSchemeIndexValues.Accent3,
                Accent4 = A.ColorSchemeIndexValues.Accent4,
                Accent5 = A.ColorSchemeIndexValues.Accent5,
                Accent6 = A.ColorSchemeIndexValues.Accent6,
                Hyperlink = A.ColorSchemeIndexValues.Hyperlink,
                FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink,
            },
            new P.SlideLayoutIdList(new P.SlideLayoutId { Id = 2147483649U, RelationshipId = masterPart.GetIdOfPart(layoutPart) }));

        return layoutPart;
    }

    private static A.Theme BuildMinimalTheme() => new(
        new A.ThemeElements(
            new A.ColorScheme(
                new A.Dark1Color(new A.SystemColor { Val = A.SystemColorValues.WindowText }),
                new A.Light1Color(new A.SystemColor { Val = A.SystemColorValues.Window }),
                new A.Dark2Color(new A.RgbColorModelHex { Val = "44546A" }),
                new A.Light2Color(new A.RgbColorModelHex { Val = "E7E6E6" }),
                new A.Accent1Color(new A.RgbColorModelHex { Val = "4472C4" }),
                new A.Accent2Color(new A.RgbColorModelHex { Val = "ED7D31" }),
                new A.Accent3Color(new A.RgbColorModelHex { Val = "A5A5A5" }),
                new A.Accent4Color(new A.RgbColorModelHex { Val = "FFC000" }),
                new A.Accent5Color(new A.RgbColorModelHex { Val = "5B9BD5" }),
                new A.Accent6Color(new A.RgbColorModelHex { Val = "70AD47" }),
                new A.Hyperlink(new A.RgbColorModelHex { Val = "0563C1" }),
                new A.FollowedHyperlinkColor(new A.RgbColorModelHex { Val = "954F72" }))
            { Name = "ClickTo: Slideshow" },
            new A.FontScheme(
                new A.MajorFont(new A.LatinFont { Typeface = "Calibri" }, new A.EastAsianFont { Typeface = "" }, new A.ComplexScriptFont { Typeface = "" }),
                new A.MinorFont(new A.LatinFont { Typeface = "Calibri" }, new A.EastAsianFont { Typeface = "" }, new A.ComplexScriptFont { Typeface = "" }))
            { Name = "ClickTo: Slideshow" },
            new A.FormatScheme(
                new A.FillStyleList(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Accent1 }), new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Accent1 }), new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Accent1 })),
                new A.LineStyleList(new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Accent1 })), new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Accent1 })), new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Accent1 }))),
                new A.EffectStyleList(new A.EffectStyle(new A.EffectList()), new A.EffectStyle(new A.EffectList()), new A.EffectStyle(new A.EffectList())),
                new A.BackgroundFillStyleList(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Accent1 }), new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Accent1 }), new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Accent1 })))
            { Name = "ClickTo: Slideshow" }));

    private static NotesMasterPart CreateNotesMaster(PresentationPart presentationPart)
    {
        NotesMasterPart notesMasterPart = presentationPart.AddNewPart<NotesMasterPart>();
        notesMasterPart.NotesMaster = new P.NotesMaster(
            new P.CommonSlideData(new P.ShapeTree(
                new P.NonVisualGroupShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                    new P.NonVisualGroupShapeDrawingProperties(),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.GroupShapeProperties(new A.TransformGroup()))),
            new P.ColorMap
            {
                Background1 = A.ColorSchemeIndexValues.Light1,
                Text1 = A.ColorSchemeIndexValues.Dark1,
                Background2 = A.ColorSchemeIndexValues.Light2,
                Text2 = A.ColorSchemeIndexValues.Dark2,
                Accent1 = A.ColorSchemeIndexValues.Accent1,
                Accent2 = A.ColorSchemeIndexValues.Accent2,
                Accent3 = A.ColorSchemeIndexValues.Accent3,
                Accent4 = A.ColorSchemeIndexValues.Accent4,
                Accent5 = A.ColorSchemeIndexValues.Accent5,
                Accent6 = A.ColorSchemeIndexValues.Accent6,
                Hyperlink = A.ColorSchemeIndexValues.Hyperlink,
                FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink,
            });

        ThemePart notesThemePart = notesMasterPart.AddNewPart<ThemePart>();
        notesThemePart.Theme = BuildMinimalTheme();

        return notesMasterPart;
    }

    private static async Task<SlidePart> AddSlideAsync(
        PresentationPart presentationPart, SlideLayoutPart layoutPart, NotesMasterPart notesMasterPart,
        SlideshowProject project, int slideIndex, long slideWidthEmu, long slideHeightEmu,
        MediaDataPart? backgroundAudioOnThisSlide, CancellationToken cancellationToken)
    {
        Slide slide = project.Slides[slideIndex];
        SlidePart slidePart = presentationPart.AddNewPart<SlidePart>();
        slidePart.AddPart(layoutPart);

        SlideInfo info = await SlideImageInspector.InspectAsync(slide.SourcePath);
        (ImagePart imagePart, string relationshipId) = await EmbedImageAsync(slidePart, slide.SourcePath, cancellationToken);

        PptxSlideGeometry geometry = PptxSlideGeometryCalculator.Compute(
            info.PixelWidth, info.PixelHeight, slide.RotationDegrees, project.FitMode, slideWidthEmu, slideHeightEmu);

        uint nextShapeId = 2;
        var shapeTree = new P.ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                new P.NonVisualGroupShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties(new A.TransformGroup()),
            BuildBackgroundRectangle(nextShapeId++, slideWidthEmu, slideHeightEmu, project.BackgroundColor),
            BuildPicture(nextShapeId++, relationshipId, geometry));

        if (backgroundAudioOnThisSlide is { } mediaDataPart)
        {
            // PowerPoint writes both relationships to the same media part; without the second
            // (the "media" one) it treats the audio as an unloadable link and never plays it.
            AudioReferenceRelationship audioRelationship = slidePart.AddAudioReferenceRelationship(mediaDataPart);
            MediaReferenceRelationship mediaRelationship = slidePart.AddMediaReferenceRelationship(mediaDataPart);

            uint audioShapeId = nextShapeId++;
            ImagePart iconPart = slidePart.AddImagePart(ImagePartType.Png);
            using (var iconStream = new MemoryStream(MinimalTransparentPng))
            {
                iconPart.FeedData(iconStream);
            }

            shapeTree.Append(BuildAudioIconPicture(
                audioShapeId, slidePart.GetIdOfPart(iconPart), audioRelationship.Id!, mediaRelationship.Id!));
            slidePart.Slide = BuildSlide(shapeTree, BuildTransitionForSlide(project, slideIndex), audioShapeId);
        }
        else
        {
            slidePart.Slide = BuildSlide(shapeTree, BuildTransitionForSlide(project, slideIndex), null);
        }

        AddNotesSlide(slidePart, notesMasterPart, layoutPart, slide.SourcePath, info.DateTaken);

        return slidePart;
    }

    private static P.Slide BuildSlide(P.ShapeTree shapeTree, OpenXmlElement? transition, uint? audioShapeIdForTiming)
    {
        var slide = new P.Slide(new P.CommonSlideData(shapeTree));

        if (transition is not null)
        {
            slide.Append(transition);
        }

        if (audioShapeIdForTiming is { } audioShapeId)
        {
            slide.Append(BuildBackgroundAudioTiming(audioShapeId));
        }

        return slide;
    }

    private static P.Shape BuildBackgroundRectangle(uint id, long widthEmu, long heightEmu, Windows.UI.Color color) => new(
        new P.NonVisualShapeProperties(
            new P.NonVisualDrawingProperties { Id = id, Name = $"Background {id}" },
            new P.NonVisualShapeDrawingProperties(),
            new P.ApplicationNonVisualDrawingProperties()),
        new P.ShapeProperties(
            new A.Transform2D(
                new A.Offset { X = 0, Y = 0 },
                new A.Extents { Cx = widthEmu, Cy = heightEmu }),
            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle },
            new A.SolidFill(new A.RgbColorModelHex { Val = $"{color.R:X2}{color.G:X2}{color.B:X2}" }),
            new A.Outline(new A.NoFill())));

    private static P.Picture BuildPicture(uint id, string imageRelationshipId, PptxSlideGeometry geometry)
    {
        var transform = new A.Transform2D(
            new A.Offset { X = (long)Math.Round(geometry.OffsetX), Y = (long)Math.Round(geometry.OffsetY) },
            new A.Extents { Cx = (long)Math.Round(geometry.Width), Cy = (long)Math.Round(geometry.Height) });
        if (geometry.RotationDegrees != 0)
        {
            transform.Rotation = geometry.RotationDegrees * 60000;
        }

        var blipFill = new P.BlipFill(new A.Blip { Embed = imageRelationshipId });
        if (geometry.CropLeft > 0 || geometry.CropTop > 0 || geometry.CropRight > 0 || geometry.CropBottom > 0)
        {
            blipFill.Append(BuildSourceRectangle(geometry));
        }

        blipFill.Append(new A.Stretch(new A.FillRectangle()));

        return new P.Picture(
            new P.NonVisualPictureProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = $"Picture {id}" },
                new P.NonVisualPictureDrawingProperties(new A.PictureLocks { NoChangeAspect = true }),
                new P.ApplicationNonVisualDrawingProperties()),
            blipFill,
            new P.ShapeProperties(transform, new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }));
    }

    private static A.SourceRectangle BuildSourceRectangle(PptxSlideGeometry geometry)
    {
        var rect = new A.SourceRectangle();
        SetPercentAttribute(rect, "l", geometry.CropLeft);
        SetPercentAttribute(rect, "t", geometry.CropTop);
        SetPercentAttribute(rect, "r", geometry.CropRight);
        SetPercentAttribute(rect, "b", geometry.CropBottom);
        return rect;
    }

    // srcRect's l/t/r/b are in thousandths of a percent (100000 = 100%) — not exposed as typed
    // properties by the SDK, confirmed by inspecting SourceRectangle's members directly.
    private static void SetPercentAttribute(OpenXmlElement element, string localName, double fraction) =>
        element.SetAttribute(new OpenXmlAttribute(localName, "", ((int)Math.Round(fraction * 100000)).ToString()));

    /// <summary>
    /// Maps §4.6's transition table onto <c>&lt;p:transition&gt;</c>. The first slide gets no
    /// incoming-transition element (nothing precedes it) but still needs its own display time —
    /// plus, per spec's "global fade-in at the start," a bare fade so the show fades in from
    /// black when the slideshow itself starts (real PowerPoint plays slide 1's own transition on
    /// start — unverified here against a live PowerPoint, see this file's own doc comment).
    /// There is no PPTX equivalent for "fade-out at the end" (no slide follows the last one) —
    /// a deliberate scope cut, not a silent omission.
    /// </summary>
    private static OpenXmlElement BuildTransitionForSlide(SlideshowProject project, int slideIndex)
    {
        int displayMs = project.Slides[slideIndex].DurationMs ?? project.DefaultSlideMs;
        var transition = new P.Transition { AdvanceAfterTime = displayMs.ToString() };

        if (slideIndex == 0)
        {
            transition.Append(new P.FadeTransition());
            return transition;
        }

        OpenXmlElement? effect = BuildTransitionEffectElement(project.TransitionInto(slideIndex));
        if (effect is null)
        {
            return transition;
        }

        transition.Append(effect);
        return WrapInDurationAlternateContent(transition, project.TransitionMs);
    }

    private const string PowerPoint2010Namespace = "http://schemas.microsoft.com/office/powerpoint/2010/main";

    private const string MarkupCompatibilityNamespace = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    // The transition's own length is the p14:dur *attribute* on <p:transition>
    // (MS-PPTX 2.3.2.3), which only PowerPoint 2010+ understands — so PowerPoint itself writes
    // it inside mc:AlternateContent with a plain fallback for older readers, and so do we. An
    // earlier version put a <p14:dur> element in a made-up <p:extLst> instead; PowerPoint ignored
    // it and showed its default 0.5s whatever was configured.
    private static AlternateContent WrapInDurationAlternateContent(P.Transition transition, int transitionMs)
    {
        var timed = (P.Transition)transition.CloneNode(deep: true);
        timed.SetAttribute(new OpenXmlAttribute("p14", "dur", PowerPoint2010Namespace, transitionMs.ToString()));

        var choice = new AlternateContentChoice(timed) { Requires = "p14" };
        choice.AddNamespaceDeclaration("p14", PowerPoint2010Namespace);

        var alternateContent = new AlternateContent(choice, new AlternateContentFallback(transition));
        alternateContent.AddNamespaceDeclaration("mc", MarkupCompatibilityNamespace);
        return alternateContent;
    }

    private static OpenXmlElement? BuildTransitionEffectElement(TransitionKind kind) => kind switch
    {
        TransitionKind.Cut => null,
        TransitionKind.Crossfade => WithAttribute(new P.FadeTransition(), "thruBlk", "0"),
        TransitionKind.FadeThroughBlack => WithAttribute(new P.FadeTransition(), "thruBlk", "1"),
        TransitionKind.PushLeft => WithAttribute(new P.PushTransition(), "dir", "l"),
        TransitionKind.PushRight => WithAttribute(new P.PushTransition(), "dir", "r"),
        TransitionKind.PushUp => WithAttribute(new P.PushTransition(), "dir", "u"),
        TransitionKind.PushDown => WithAttribute(new P.PushTransition(), "dir", "d"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static T WithAttribute<T>(T element, string localName, string value) where T : OpenXmlElement
    {
        element.SetAttribute(new OpenXmlAttribute(localName, "", value));
        return element;
    }

    /// <summary>
    /// All tracks mixed into one show-length MP3 (see <see cref="SoundtrackRenderer"/>), added to
    /// the package's shared media store and referenced from slide 1. Never the source files
    /// themselves: a recipient without a decoder for the original format would get silence.
    /// </summary>
    private static async Task<MediaDataPart> CreateBackgroundAudioMediaPartAsync(
        PresentationDocument document, SlideshowProject project, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        string soundtrackPath = AppTempFolder.NewFilePath(".mp3");
        try
        {
            await SoundtrackRenderer.RenderMp3Async(project, soundtrackPath, progress, cancellationToken);

            // The SDK's own Mp3 member labels the part "audio/mp3"; PowerPoint itself writes the
            // registered "audio/mpeg".
            MediaDataPart mediaDataPart = document.CreateMediaDataPart("audio/mpeg", ".mp3");
            using (Stream soundtrackStream = File.OpenRead(soundtrackPath))
            {
                mediaDataPart.FeedData(soundtrackStream);
            }

            return mediaDataPart;
        }
        finally
        {
            File.Delete(soundtrackPath);
        }
    }

    /// <summary>
    /// PowerPoint's own "play across slides, start automatically, hide during show" timing tree
    /// for the audio shape. Copied from a file PowerPoint itself saved after being set up that way
    /// through its object model (only the shape id substituted): an earlier hand-built,
    /// simplified tree opened without complaint but PowerPoint read none of it as media playback
    /// (PlayOnEntry/StopAfterSlides empty). 999 slides is what its UI writes for "across slides".
    /// </summary>
    private static P.Timing BuildBackgroundAudioTiming(uint audioShapeId) => new(
        $"""
        <p:timing xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"><p:tnLst><p:par><p:cTn id="1" dur="indefinite" restart="never" nodeType="tmRoot"><p:childTnLst><p:seq concurrent="1" nextAc="seek"><p:cTn id="2" dur="indefinite" nodeType="mainSeq"><p:childTnLst><p:par><p:cTn id="3" fill="hold"><p:stCondLst><p:cond delay="indefinite"/><p:cond evt="onBegin" delay="0"><p:tn val="2"/></p:cond></p:stCondLst><p:childTnLst><p:par><p:cTn id="4" fill="hold"><p:stCondLst><p:cond delay="0"/></p:stCondLst><p:childTnLst><p:par><p:cTn id="5" presetID="1" presetClass="mediacall" presetSubtype="0" fill="hold" nodeType="afterEffect"><p:stCondLst><p:cond delay="0"/></p:stCondLst><p:childTnLst><p:cmd type="call" cmd="playFrom(0.0)"><p:cBhvr><p:cTn id="6" dur="1" fill="hold"/><p:tgtEl><p:spTgt spid="{audioShapeId}"/></p:tgtEl></p:cBhvr></p:cmd></p:childTnLst></p:cTn></p:par></p:childTnLst></p:cTn></p:par></p:childTnLst></p:cTn></p:par></p:childTnLst></p:cTn><p:prevCondLst><p:cond evt="onPrev" delay="0"><p:tgtEl><p:sldTgt/></p:tgtEl></p:cond></p:prevCondLst><p:nextCondLst><p:cond evt="onNext" delay="0"><p:tgtEl><p:sldTgt/></p:tgtEl></p:cond></p:nextCondLst></p:seq><p:audio><p:cMediaNode vol="80000" numSld="999" showWhenStopped="0"><p:cTn id="7" fill="hold" display="0"><p:stCondLst><p:cond delay="indefinite"/></p:stCondLst><p:endCondLst><p:cond evt="onStopAudio" delay="0"><p:tgtEl><p:sldTgt/></p:tgtEl></p:cond></p:endCondLst></p:cTn><p:tgtEl><p:spTgt spid="{audioShapeId}"/></p:tgtEl></p:cMediaNode></p:audio></p:childTnLst></p:cTn></p:par></p:tnLst></p:timing>
        """);

    // The shape mirrors what PowerPoint writes for embedded audio: a media hyperlink, the
    // audioFile link, and the p14:media embed (extension DAA4B4D4-...) pointing at the same media
    // part. Not marked hidden — the timing tree's showWhenStopped="0" is PowerPoint's own "hide
    // during show", and the icon image is transparent anyway.
    private static P.Picture BuildAudioIconPicture(
        uint id, string iconImageRelationshipId, string audioRelationshipId, string mediaRelationshipId)
    {
        var nvPr = new P.ApplicationNonVisualDrawingProperties(new A.AudioFromFile { Link = audioRelationshipId });
        nvPr.Append(new P.ApplicationNonVisualDrawingPropertiesExtensionList(
            new P.ApplicationNonVisualDrawingPropertiesExtension(
                new DocumentFormat.OpenXml.Office2010.PowerPoint.Media { Embed = mediaRelationshipId })
            { Uri = "{DAA4B4D4-6D71-4841-9C94-3DE7FCFB9230}" }));

        return new P.Picture(
            new P.NonVisualPictureProperties(
                new P.NonVisualDrawingProperties(new A.HyperlinkOnClick { Id = "", Action = "ppaction://media" })
                { Id = id, Name = "Background audio" },
                new P.NonVisualPictureDrawingProperties(new A.PictureLocks { NoChangeAspect = true }),
                nvPr),
            new P.BlipFill(new A.Blip { Embed = iconImageRelationshipId }, new A.Stretch(new A.FillRectangle())),
            new P.ShapeProperties(
                new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = 406400, Cy = 406400 }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }));
    }

    /// <summary>Original filename plus Date Taken, self-documenting the archive per spec §4.8.</summary>
    private static void AddNotesSlide(
        SlidePart slidePart, NotesMasterPart notesMasterPart, SlideLayoutPart layoutPart, string sourcePath, DateTimeOffset? dateTaken)
    {
        NotesSlidePart notesSlidePart = slidePart.AddNewPart<NotesSlidePart>();
        notesSlidePart.AddPart(notesMasterPart);

        string fileName = Path.GetFileName(sourcePath);
        string dateText = dateTaken is { } taken ? taken.ToString("yyyy-MM-dd HH:mm") : "not available";

        notesSlidePart.NotesSlide = new P.NotesSlide(
            new P.CommonSlideData(new P.ShapeTree(
                new P.NonVisualGroupShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                    new P.NonVisualGroupShapeDrawingProperties(),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.GroupShapeProperties(new A.TransformGroup()),
                new P.Shape(
                    new P.NonVisualShapeProperties(
                        new P.NonVisualDrawingProperties { Id = 2, Name = "Notes" },
                        new P.NonVisualShapeDrawingProperties(),
                        new P.ApplicationNonVisualDrawingProperties(new P.PlaceholderShape { Type = P.PlaceholderValues.Body })),
                    new P.ShapeProperties(),
                    new P.TextBody(
                        new A.BodyProperties(),
                        new A.ListStyle(),
                        new A.Paragraph(new A.Run(new A.Text($"{fileName} — captured {dateText}"))))))));
    }

    private static async Task<(ImagePart Part, string RelationshipId)> EmbedImageAsync(
        SlidePart slidePart, string sourcePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        PartTypeInfo? passthroughType = extension switch
        {
            ".jpg" or ".jpeg" => ImagePartType.Jpeg,
            ".png" => ImagePartType.Png,
            ".gif" => ImagePartType.Gif,
            ".bmp" => ImagePartType.Bmp,
            ".tif" or ".tiff" => ImagePartType.Tiff,
            _ => null,
        };

        if (passthroughType is { } imagePartType)
        {
            ImagePart part = slidePart.AddImagePart(imagePartType);
            using Stream sourceStream = File.OpenRead(sourcePath);
            part.FeedData(sourceStream);
            return (part, slidePart.GetIdOfPart(part));
        }

        // PowerPoint's native image part doesn't reliably support this format (heic/webp/avif,
        // all of which this app otherwise accepts — see M2's Known Issues) — decode and
        // re-encode to PNG. Rotation is untouched by this: it's applied later via the picture
        // shape's own rot, never baked into pixels here.
        ImagePart pngPart = slidePart.AddImagePart(ImagePartType.Png);
        byte[] pngBytes = await EncodeAsPngIgnoringRotationAsync(sourcePath);
        using (var pngStream = new MemoryStream(pngBytes))
        {
            pngPart.FeedData(pngStream);
        }

        return (pngPart, slidePart.GetIdOfPart(pngPart));
    }

    private static async Task<byte[]> EncodeAsPngIgnoringRotationAsync(string path)
    {
        BitmapDecoder decoder = await SlideImageLoader.OpenDecoderAsync(path);
        using SoftwareBitmap softwareBitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);

        using var memoryStream = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memoryStream);
        encoder.SetSoftwareBitmap(softwareBitmap);
        await encoder.FlushAsync();

        memoryStream.Seek(0);
        var buffer = new byte[memoryStream.Size];
        await memoryStream.ReadAsync(buffer.AsBuffer(), (uint)buffer.Length, InputStreamOptions.None);
        return buffer;
    }

    /// <summary>
    /// Embeds the project JSON as a custom XML part (spec §4.8) so <see cref="PptxProjectReader"/>
    /// can reconstruct the project on Open. Uses the *original* project, not any
    /// <c>EffectiveProject</c>-scaled snapshot the caller rendered slides from, so reopening
    /// restores the user's exact authored values — the same contract <see cref="SlideshowProjectFile.Save"/>
    /// makes for JSON. Sources are stored as bare file names, not relative paths: a deck gets
    /// shared, and a relative path discloses folder names (an absolute one, when the photos are on
    /// another drive, discloses the user name). The cost is that reopening finds the sources only
    /// when they sit beside the .pptx; missing ones are reported like any moved file.
    /// </summary>
    private static void EmbedProjectJson(PresentationPart presentationPart, SlideshowProject project)
    {
        string json = SlideshowProjectFile.ToJson(project, Path.GetFileName);

        // Wrapped in a real XML element (not raw JSON bytes typed as XML) so the part is
        // well-formed and identifiable by PptxProjectReader — see PptxProjectXmlContract.
        var element = new XElement(PptxProjectXmlContract.RootElementName, json);

        CustomXmlPart customXmlPart = presentationPart.AddCustomXmlPart(CustomXmlPartType.CustomXml);
        using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(element.ToString(SaveOptions.DisableFormatting))))
        {
            customXmlPart.FeedData(stream);
        }
    }

    // A 1x1 transparent PNG, generated once via System.Drawing and pasted here as bytes — the
    // hidden background-audio shape needs *some* image part to satisfy PPTX's picture schema
    // even though it's never actually seen.
    private static readonly byte[] MinimalTransparentPng =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x01, 0x73, 0x52, 0x47, 0x42, 0x00, 0xAE, 0xCE, 0x1C, 0xE9, 0x00, 0x00,
        0x00, 0x04, 0x67, 0x41, 0x4D, 0x41, 0x00, 0x00, 0xB1, 0x8F, 0x0B, 0xFC, 0x61, 0x05, 0x00, 0x00,
        0x00, 0x09, 0x70, 0x48, 0x59, 0x73, 0x00, 0x00, 0x0E, 0xC3, 0x00, 0x00, 0x0E, 0xC3, 0x01, 0xC7,
        0x6F, 0xA8, 0x64, 0x00, 0x00, 0x00, 0x0B, 0x49, 0x44, 0x41, 0x54, 0x18, 0x57, 0x63, 0x60, 0x00,
        0x02, 0x00, 0x00, 0x05, 0x00, 0x01, 0xAA, 0xD5, 0xC8, 0x51, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45,
        0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82,
    ];
}
