using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace CadMcp.Providers;

public static class ChatWordExporter
{
    public static void Save(string path, IReadOnlyList<ChatLine> messages)
    {
        if (messages.Count == 0) throw new InvalidOperationException("В диалоге ещё нет сообщений");
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        main.Document = new Document(new Body());
        var body = main.Document.Body!;
        body.Append(Paragraph("CAD MCP — диалог", bold: true, size: "32"));
        uint imageId = 1;
        foreach (var message in messages)
        {
            string label = message.Role switch { "user" => "Вы", "assistant" => "Ассистент", "progress" => "Ход работы", _ => "История" };
            if (!string.IsNullOrWhiteSpace(message.Model)) label += " · " + message.Model;
            body.Append(Paragraph(label, bold: true, size: "24"));
            foreach (string line in message.Text.Replace("\r\n", "\n").Split('\n')) body.Append(Paragraph(line));
            if (!string.IsNullOrWhiteSpace(message.ReasoningSummary))
            {
                body.Append(Paragraph("Краткий ход рассуждений", bold: true));
                foreach (string line in message.ReasoningSummary.Replace("\r\n", "\n").Split('\n')) body.Append(Paragraph(line));
            }
            if (message.Steps is { Count: > 0 })
            {
                body.Append(Paragraph("Действия", bold: true));
                foreach (var step in message.Steps) body.Append(Paragraph("• " + step));
            }
            if (message.Images is { Count: > 0 })
                foreach (var image in message.Images)
                {
                    if (!File.Exists(image.Path)) { body.Append(Paragraph("Изображение недоступно: " + image.Name)); continue; }
                    PartTypeInfo? type = Path.GetExtension(image.Path).ToLowerInvariant() switch
                    {
                        ".png" => ImagePartType.Png, ".jpg" or ".jpeg" => ImagePartType.Jpeg,
                        ".gif" => ImagePartType.Gif, _ => null
                    };
                    if (type is null) { body.Append(Paragraph("Изображение: " + image.Name + " (формат не поддерживается Word)")); continue; }
                    var part = main.AddImagePart(type.Value);
                    using (var source = File.OpenRead(image.Path)) part.FeedData(source);
                    long width = 5_400_000;
                    long height = image.Width > 0 && image.Height > 0 ? Math.Clamp(width * image.Height / image.Width, 100_000, 7_000_000) : 3_600_000;
                    body.Append(new Paragraph(new Run(new DrawingElement(main.GetIdOfPart(part), image.Name, imageId++, width, height).Create())));
                    body.Append(Paragraph(image.Name));
                }
        }
        body.Append(new SectionProperties());
        main.Document.Save();
    }

    private static Paragraph Paragraph(string text, bool bold = false, string? size = null)
    {
        var run = new Run();
        if (bold || size is not null)
        {
            var properties = new RunProperties();
            if (bold) properties.Append(new Bold());
            if (size is not null) properties.Append(new FontSize { Val = size });
            run.Append(properties);
        }
        run.Append(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return new Paragraph(run);
    }

    private sealed record DrawingElement(string RelationshipId, string Name, uint Id, long Width, long Height)
    {
        public DocumentFormat.OpenXml.Wordprocessing.Drawing Create() => new(
            new DW.Inline(
                new DW.Extent { Cx = Width, Cy = Height },
                new DW.DocProperties { Id = Id, Name = Name },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(new A.GraphicData(
                    new PIC.Picture(
                        new PIC.NonVisualPictureProperties(
                            new PIC.NonVisualDrawingProperties { Id = 0U, Name = Name },
                            new PIC.NonVisualPictureDrawingProperties()),
                        new PIC.BlipFill(new A.Blip { Embed = RelationshipId }, new A.Stretch(new A.FillRectangle())),
                        new PIC.ShapeProperties(
                            new A.Transform2D(new A.Offset { X = 0L, Y = 0L }, new A.Extents { Cx = Width, Cy = Height }),
                            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
                { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
            { DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U });
    }
}
