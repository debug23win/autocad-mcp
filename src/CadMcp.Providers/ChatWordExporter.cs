using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.Text.RegularExpressions;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace CadMcp.Providers;

public static class ChatWordExporter
{
    public static void Save(string path, IReadOnlyList<ChatLine> messages)
    {
        if (messages.Count == 0) throw new InvalidOperationException("В диалоге ещё нет сообщений");
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp.docx";
        try
        {
        using (var document = WordprocessingDocument.Create(temporary, WordprocessingDocumentType.Document))
        {
        var main = document.AddMainDocumentPart();
        main.Document = new Document(new Body());
        var body = main.Document.Body!;
        var styles=main.AddNewPart<StyleDefinitionsPart>();
        styles.Styles=new Styles(new DocDefaults(new RunPropertiesDefault(new RunPropertiesBaseStyle(new RunFonts{Ascii="Calibri",HighAnsi="Calibri"},new Color{Val="000000"},new FontSize{Val="22"}))),
            new Style(new StyleName{Val="Title"},new StyleParagraphProperties(new SpacingBetweenLines{After="240"}),new StyleRunProperties(new Color{Val="000000"},new FontSize{Val="32"})){Type=StyleValues.Paragraph,StyleId="Title"});
        var title=Paragraph("Диалог CAD MCP");title.ParagraphProperties=new ParagraphProperties(new ParagraphStyleId{Val="Title"});body.Append(title);
        uint imageId = 1;
        foreach (var message in messages)
        {
            string label = message.Role switch { "user" => "Вы", "assistant" => "Ассистент", "progress" => "Ход работы", _ => "История" };
            if (!string.IsNullOrWhiteSpace(message.Model)) label += " · " + message.Model;
            body.Append(Paragraph(label, bold: true, size: "24"));
            AppendContent(body,message.Text);
            if (!string.IsNullOrWhiteSpace(message.ReasoningSummary))
            {
                body.Append(Paragraph("Краткий ход рассуждений", bold: true));
                foreach (string line in message.ReasoningSummary.Replace("\r\n", "\n").Split('\n')) body.Append(Paragraph(line));
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
                    long height = image.Width > 0 && image.Height > 0 ? width * image.Height / image.Width : 3_600_000;
                    if(height>7_000_000){width=width*7_000_000/height;height=7_000_000;}
                    body.Append(new Paragraph(new Run(new DrawingElement(main.GetIdOfPart(part), image.Name, imageId++, width, height).Create())));
                    body.Append(Paragraph(image.Name));
                }
        }
        body.Append(new SectionProperties(new PageSize{Width=12240U,Height=15840U},new PageMargin{Top=1440,Bottom=1440,Left=1440U,Right=1440U}));
        main.Document.Save();
        }
        File.Move(temporary,path,true);
        }
        finally { if(File.Exists(temporary))File.Delete(temporary); }
    }

    private static readonly Regex DisplayMath=new(@"\$\$(?<body>[\s\S]+?)\$\$|\\\[(?<body>[\s\S]+?)\\\]",RegexOptions.CultureInvariant);
    private static readonly Regex InlineMath=new(@"\$(?<body>[^$\r\n]+)\$|\\\((?<body>[\s\S]+?)\\\)",RegexOptions.CultureInvariant);
    private static void Inline(Paragraph paragraph,string text,bool bold=false)
    {
        int at=0;
        foreach(Match match in InlineMath.Matches(text))
        {
            if(match.Index>at)paragraph.Append(TextRun(text[at..match.Index],bold));
            paragraph.Append(WordMath.Parse(match.Groups["body"].Value));at=match.Index+match.Length;
        }
        if(at<text.Length)paragraph.Append(TextRun(text[at..],bold));
    }
    private static Run TextRun(string text,bool bold=false)=>bold?new Run(new RunProperties(new Bold()),new Text(text){Space=SpaceProcessingModeValues.Preserve}):new Run(new Text(text){Space=SpaceProcessingModeValues.Preserve});
    private static void Formatted(Paragraph paragraph,string text,bool heading=false)
    {
        int at=0;foreach(Match match in Regex.Matches(text,@"\*\*(.+?)\*\*"))
        {Inline(paragraph,text[at..match.Index],heading);Inline(paragraph,match.Groups[1].Value,true);at=match.Index+match.Length;}
        Inline(paragraph,text[at..],heading);
    }
    private static void AppendContent(Body body,string text)
    {
        var equations=new List<DocumentFormat.OpenXml.Math.OfficeMath>();
        text=DisplayMath.Replace(text,match=>{int id=equations.Count;equations.Add(WordMath.Parse(match.Groups["body"].Value));return "\n@@CADMCPMATH"+id+"@@\n";});
        var lines=text.Replace("\r\n","\n").Split('\n');bool code=false;
        for(int n=0;n<lines.Length;n++)
        {
            string line=lines[n];if(!code&&string.IsNullOrWhiteSpace(line))continue;if(line.StartsWith("```",StringComparison.Ordinal)){code=!code;continue;}
            var display=Regex.Match(line,@"^@@CADMCPMATH(\d+)@@$");
            if(display.Success){body.Append(new Paragraph(new ParagraphProperties(new Justification{Val=JustificationValues.Center}),equations[int.Parse(display.Groups[1].Value)]));continue;}
            if(!code&&line.Trim().StartsWith('|')&&n+1<lines.Length&&Regex.IsMatch(lines[n+1],@"^\s*\|?[\s:|\-]+\|?\s*$"))
            {
                var table=new Table(new TableProperties(new TableWidth{Type=TableWidthUnitValues.Pct,Width="5000"},new TableBorders(new TopBorder{Val=BorderValues.Single,Size=4},new LeftBorder{Val=BorderValues.Single,Size=4},new BottomBorder{Val=BorderValues.Single,Size=4},new RightBorder{Val=BorderValues.Single,Size=4},new InsideHorizontalBorder{Val=BorderValues.Single,Size=4},new InsideVerticalBorder{Val=BorderValues.Single,Size=4})));
                table.Append(new TableGrid(line.Trim().Trim('|').Split('|').Select(_=>new GridColumn{Width=(9360/line.Trim().Trim('|').Split('|').Length).ToString(System.Globalization.CultureInfo.InvariantCulture)})));
                void Row(string value,bool header=false){var row=new TableRow();foreach(var cell in value.Trim().Trim('|').Split('|')){var para=new Paragraph();Formatted(para,cell.Trim(),header);row.Append(new TableCell(para));}table.Append(row);}
                Row(line,true);n++;while(n+1<lines.Length&&lines[n+1].Trim().StartsWith('|'))Row(lines[++n]);body.Append(table);continue;
            }
            bool heading=!code&&Regex.IsMatch(line,@"^#{1,6}\s+");if(heading)line=Regex.Replace(line,@"^#{1,6}\s+","");
            if(!code)line=Regex.Replace(line,@"^\s*[-*]\s+","• ");
            var paragraph=new Paragraph();
            if(code)paragraph.Append(new Run(new RunProperties(new RunFonts{Ascii="Consolas",HighAnsi="Consolas"}),new Text(line){Space=SpaceProcessingModeValues.Preserve}));
            else Formatted(paragraph,line,heading);
            body.Append(paragraph);
        }
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
