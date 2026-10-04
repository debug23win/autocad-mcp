using UglyToad.PdfPig;
namespace CadMcp.Core;
public static class PdfVerification
{
    public static object Check(string path,int expectedPages=1)
    {
        using var document=PdfDocument.Open(path,new ParsingOptions{UseLenientParsing=false});
        if(document.NumberOfPages!=expectedPages)throw new CadFault("PDF_PAGE_COUNT","Expected "+expectedPages+" pages; produced "+document.NumberOfPages);
        var pages=document.GetPages().Select(p=>new{number=p.Number,width_points=p.Width,height_points=p.Height,letters=p.Letters.Count,images=p.NumberOfImages,
            text=string.Concat(p.Letters.Select(l=>l.Value)).Substring(0,Math.Min(2048,string.Concat(p.Letters.Select(l=>l.Value)).Length))}).ToArray();
        if(pages.Any(p=>p.width_points<=0||p.height_points<=0))throw new CadFault("PDF_INVALID_MEDIA","PDF page size invalid");
        return new{state="passed",page_count=document.NumberOfPages,pages,verification="strict_PDF_parser_and_expected_page_count",appearance="requires_rendered_review; vector-only text may not be extractable"};
    }
}
