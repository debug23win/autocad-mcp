namespace CadMcp.Core;

public sealed record SteelSection(string Code,string Shape,double HeightMm,double WidthMm,double WebMm,double FlangeMm,double RootRadiusMm,string Source)
{
    public double AreaMm2 => Shape=="pipe"?Math.PI*WebMm*(HeightMm-WebMm):2*WidthMm*FlangeMm+(HeightMm-2*FlangeMm)*WebMm+4*(1-Math.PI/4)*RootRadiusMm*RootRadiusMm;
    public double TheoreticalKgPerM => AreaMm2*.00785;
}

/// <summary>Nominal section dimensions, independent of the model provider. Never infer a steel grade from a profile.</summary>
public static class SteelSections
{
    public static IReadOnlyList<SteelSection> Catalog { get; } = [
        new("20Б1","i",200,100,5.5,8,11,"https://ese.pro/assortments/i-beams-produced/"),
        new("25Б1","i",248,124,5,8,12,"https://ese.pro/assortments/i-beams-produced/22985/"),
        new("30Б1","i",298,149,5.5,8,13,"https://ese.pro/assortments/beams/23322/"),
        new("35Б1","i",346,174,6,9,14,"https://ese.pro/assortments/i-beams-produced/23676/"),
        new("40Б1","i",396,199,7,11,16,"https://ese.pro/assortments/i-beams-produced/23975/"),
        new("25К1","i",246,249,8,12,16,"https://ese.pro/assortments/i-beams-produced/"),
        .. new[]{(57d,3.5),(89d,4d),(108d,4d),(133d,4d),(159d,4.5),(219d,6d)}.Select(p=>new SteelSection(
            "PIPE-"+p.Item1.ToString(System.Globalization.CultureInfo.InvariantCulture)+"x"+p.Item2.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "pipe",p.Item1,p.Item1,p.Item2,0,0,"nominal circular tube; theoretical annulus area; confirm order specification"))];
    public static SteelSection Get(string code)=>Catalog.FirstOrDefault(s=>string.Equals(s.Code,code,StringComparison.OrdinalIgnoreCase))
        ?? throw new CadFault("UNKNOWN_STEEL_PROFILE","Choose an exact code from cad_steel_catalog");
    public static double MetersPerUnit(string units)=>units switch {
        "Millimeters"=>.001,"Centimeters"=>.01,"Meters"=>1,"Inches"=>.0254,"Feet"=>.3048,
        _=>throw new CadFault("UNITS_REQUIRED","Define mm/cm/m/in/ft before using a nominal steel profile or mass")};
}
