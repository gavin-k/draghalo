using DropHalo.Core.Conversion;
using DropHalo.Core.Wheel;
using DropHalo.Engines.Archive;
using DropHalo.Engines.Document;
using DropHalo.Engines.Image;
using DropHalo.Engines.Media;
using DropHalo.Engines.Ocr;
using DropHalo.Engines.Pdf;
using DropHalo.Engines.Sheet;
using DropHalo.Engines.Subtitle;
using DropHalo.Engines.Tools;

namespace DropHalo.App;

/// <summary>
/// The converters DropHalo ships, registered into one graph, and the built-in tools made from them. The app and
/// the tool command line (<see cref="Program"/>) both build this, so a recipe an agent validates from the console
/// is checked against the same steps and the same conversions the wheel will run.
/// </summary>
internal sealed class ConverterSet
{
    public ConverterSet()
    {
        Graph.Register(new MagickImageConverter());
        Graph.Register(new SubtitleConverter());
        Graph.Register(Archives);
        Graph.Register(Ffmpeg);
        Graph.Register(new PdfConverter());
        Graph.Register(new DocumentConverter());
        Graph.Register(new SheetConverter());
    }

    public ConversionGraph Graph { get; } = new();

    public SharpCompressArchiveConverter Archives { get; } = new();

    public FfmpegConverter Ffmpeg { get; } = new();

    /// <summary>The built-in tools; the file actions are the app's, since most need its windows.</summary>
    public IReadOnlyList<IWheelOperation> Tools(ITextRecognizer recognizer) => EngineTools.Create(Archives, Ffmpeg, recognizer: recognizer);
}
