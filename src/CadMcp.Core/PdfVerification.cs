using UglyToad.PdfPig;
namespace CadMcp.Core;
public static class PdfVerification
{
    private const double PointsToMillimetres = 25.4 / 72;
    /// <summary>
    /// Strict parse of a produced PDF. With <paramref name="expectedMillimetres"/> (the plotted paper size), each
    /// page must have that size in either orientation; pages without any text, image or vector path are reported
    /// as empty. Size and emptiness findings make the state review_required; a wrong page count fails.
    /// </summary>
    public static object Check(string path,int expectedPages=1,(double Width,double Height)? expectedMillimetres=null)
    {
        using var document=PdfDocument.Open(path,new ParsingOptions{UseLenientParsing=false});
        if(document.NumberOfPages!=expectedPages)throw new CadFault("PDF_PAGE_COUNT","Expected "+expectedPages+" pages; produced "+document.NumberOfPages);
        var warnings=new List<object>();
        var pages=document.GetPages().Select(p=>
        {
            string text=string.Concat(p.Letters.Select(l=>l.Value));
            int paths=p.Paths.Count;
            double width=Math.Round(p.Width*PointsToMillimetres,1),height=Math.Round(p.Height*PointsToMillimetres,1);
            if(p.Letters.Count==0&&p.NumberOfImages==0&&paths==0)
                warnings.Add(new{code="PDF_PAGE_EMPTY",page=p.Number,detail="The page has no text, images or vector paths; check the layout, viewports and plot area"});
            if(expectedMillimetres is {} expected&&!SameSize(width,height,expected.Width,expected.Height))
                warnings.Add(new{code="PDF_PAGE_SIZE_MISMATCH",page=p.Number,page_size_mm=new[]{width,height},expected_size_mm=new[]{Math.Round(expected.Width,1),Math.Round(expected.Height,1)},
                    detail="The PDF page does not have the plotted paper size; check the media and plot settings of the layout"});
            return new{number=p.Number,width_points=p.Width,height_points=p.Height,width_mm=width,height_mm=height,orientation=width>=height?"landscape":"portrait",
                letters=p.Letters.Count,images=p.NumberOfImages,vector_paths=paths,text=text.Substring(0,Math.Min(2048,text.Length))};
        }).ToArray();
        if(pages.Any(p=>p.width_points<=0||p.height_points<=0))throw new CadFault("PDF_INVALID_MEDIA","PDF page size invalid");
        return new{state=warnings.Count==0?"passed":"review_required",page_count=document.NumberOfPages,pages,warnings,
            expected_size_mm=expectedMillimetres is {} size?new[]{Math.Round(size.Width,1),Math.Round(size.Height,1)}:null,
            verification="strict_PDF_parser, expected page count"+(expectedMillimetres is null?"":", paper size")+" and non-empty content",
            appearance="requires_rendered_review; vector-only text may not be extractable"};
    }
    /// <summary>Same paper in either orientation, within 1 mm or 0.5 %.</summary>
    public static bool SameSize(double width,double height,double expectedWidth,double expectedHeight)
    {
        static bool Close(double a,double b)=>Math.Abs(a-b)<=Math.Max(1,Math.Max(a,b)*0.005);
        return Close(width,expectedWidth)&&Close(height,expectedHeight)||Close(width,expectedHeight)&&Close(height,expectedWidth);
    }
}
