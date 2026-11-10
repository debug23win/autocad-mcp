using System.Buffers.Binary;
using System.IO.Compression;
namespace CadMcp.Core;

public sealed record PngInspectionResult(bool Decoded, int Width, int Height, bool NonUniform, string? Error = null);
public static class PngInspection
{
    public static PngInspectionResult Read(byte[] png)
    {
        try
        {
            if (png.Length < 33 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw new InvalidDataException("Invalid PNG signature");
            int width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)), height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
            if (width is < 1 or > 4096 || height is < 1 or > 4096 || (long)width * height > 12_000_000 || png[24] != 8 || png[28] != 0) throw new InvalidDataException("Unsupported PNG dimensions/depth/interlacing");
            int channels = png[25] switch { 0 => 1, 2 => 3, 4 => 2, 6 => 4, _ => throw new InvalidDataException("Unsupported PNG color type") };
            using var compressed = new MemoryStream(); int position = 8; bool ended = false;
            while (position + 12 <= png.Length)
            {
                int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(position, 4));
                if (length < 0 || (long)position + length + 12 > png.Length) throw new InvalidDataException("Invalid PNG chunk");
                var type = png.AsSpan(position + 4, 4);
                if (type.SequenceEqual("IDAT"u8)) compressed.Write(png, position + 8, length);
                if (type.SequenceEqual("IEND"u8)) { ended = true; break; }
                position += length + 12;
            }
            if (!ended || compressed.Length == 0) throw new InvalidDataException("Incomplete PNG");
            compressed.Position = 0; using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
            byte[] previous = new byte[width * channels], row = new byte[width * channels];
            uint? firstColor = null; bool varied = false;
            static int Paeth(int a, int b, int c) { int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c); return pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }
            for (int y = 0; y < height; y++)
            {
                int filter = inflater.ReadByte(); if (filter is < 0 or > 4) throw new InvalidDataException("Invalid PNG filter");
                inflater.ReadExactly(row);
                for (int i = 0; i < row.Length; i++)
                {
                    int a = i >= channels ? row[i - channels] : 0, b = previous[i], c = i >= channels ? previous[i - channels] : 0;
                    row[i] = unchecked((byte)(row[i] + (filter switch { 1 => a, 2 => b, 3 => (a + b) / 2, 4 => Paeth(a, b, c), _ => 0 })));
                }
                for (int x = 0; x < width; x++)
                {
                    int at = x * channels; byte r = row[at], g = channels >= 3 ? row[at + 1] : r, b = channels >= 3 ? row[at + 2] : r, alpha = channels is 2 or 4 ? row[at + channels - 1] : (byte)255;
                    // Composite onto white so invisible RGB under alpha=0 cannot fake nonblank content.
                    uint color = (uint)(((r * alpha + 255 * (255 - alpha)) / 255) << 16 | ((g * alpha + 255 * (255 - alpha)) / 255) << 8 | (b * alpha + 255 * (255 - alpha)) / 255);
                    if (firstColor is null) firstColor = color; else varied |= firstColor.Value != color;
                }
                (previous, row) = (row, previous);
            }
            if (inflater.ReadByte() != -1) throw new InvalidDataException("Unexpected extra scanlines");
            return new(true, width, height, varied);
        }
        catch (Exception error) when (error is InvalidDataException or EndOfStreamException or IOException or ArgumentException)
        { return new(false, 0, 0, false, error.Message); }
    }
}
