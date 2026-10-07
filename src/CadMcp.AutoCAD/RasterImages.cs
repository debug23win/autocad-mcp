using System.IO;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadMcp.Core;

namespace CadMcp.AutoCAD;

internal static class RasterImages
{
    public static (RasterImage Image, object Registration) Attach(Database db, Transaction tr, JsonElement operation)
    {
        var supplied = EditPlan.RequiredText(operation, "path");
        if (!Path.IsPathFullyQualified(supplied)) throw new CadFault("INVALID_IMAGE_PATH", "Use an absolute image file path");
        var path = Path.GetFullPath(supplied);
        if (!File.Exists(path)) throw new CadFault("IMAGE_NOT_FOUND", path);
        if (new FileInfo(path).Length > 200 * 1024 * 1024)
            throw new CadFault("IMAGE_TOO_LARGE", "Raster image is larger than 200 MB");
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".tif" or ".tiff" or ".bmp"))
            throw new CadFault("INVALID_IMAGE_FORMAT", "Use PNG, JPEG, TIFF or BMP");
        int width, height;
        try { using var bitmap = System.Drawing.Image.FromFile(path); width = bitmap.Width; height = bitmap.Height; }
        catch (System.Exception e) { throw new CadFault("IMAGE_READ_FAILED", e.Message); }
        if (width > 30000 || height > 30000 || (long)width * height > 250_000_000)
            throw new CadFault("IMAGE_TOO_LARGE", "Image pixel dimensions exceed the supported limit");
        var fit = ImageRegistration.Fit(operation.GetProperty("control_points"), width, height);
        string name = operation.Text("name") ?? Path.GetFileNameWithoutExtension(path);
        SymbolUtilityServices.ValidateSymbolName(name, false);
        var imageDictionaryId = RasterImageDef.GetImageDictionary(db);
        if (imageDictionaryId.IsNull) imageDictionaryId = RasterImageDef.CreateImageDictionary(db);
        var dictionary = (DBDictionary)tr.GetObject(imageDictionaryId, OpenMode.ForWrite);
        if (dictionary.Contains(name)) throw new CadFault("IMAGE_NAME_EXISTS", "Use a unique image definition name: " + name);
        var definition = new RasterImageDef { SourceFileName = path };
        definition.Load();
        dictionary.SetAt(name, definition); tr.AddNewlyCreatedDBObject(definition, true);
        var origin = fit.PixelToWorld(0, height);
        var right = fit.PixelToWorld(width, height);
        var top = fit.PixelToWorld(0, 0);
        var image = new RasterImage { ImageDefId = definition.ObjectId };
        image.Orientation = new CoordinateSystem3d(new Point3d(origin[0], origin[1], origin[2]),
            new Vector3d(right[0] - origin[0], right[1] - origin[1], 0),
            new Vector3d(top[0] - origin[0], top[1] - origin[1], 0));
        return (image, new { source_path = path, image_name = name, pixel_width = width, pixel_height = height,
            pixel_origin = "top_left", coordinate_system = "WCS", pixel_to_world_x = fit.X, pixel_to_world_y = fit.Y,
            world_z = fit.Z, control_points = fit.ControlPointCount, rms_error = fit.RmsError, max_error = fit.MaxError,
            fitted_model = "2D_affine" });
    }

    public static void Associate(RasterImage image, Transaction tr)
    {
        RasterImage.EnableReactors(true);
        // Association adds a reactor to the definition, which needs write access.
        image.AssociateRasterDef((RasterImageDef)tr.GetObject(image.ImageDefId, OpenMode.ForWrite));
    }
}
