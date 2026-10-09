using System.Buffers.Binary;
using System.IO.Compression;

namespace Agent.Common.Os;

/// <summary>
/// A minimal PNG writer (8-bit gray or 8-bit RGB, filter 0, zlib) so a screenshot can be
/// encoded without WPF imaging or System.Drawing — neither exists in plain net10.0 — and
/// without a package. Compression is <see cref="ZLibStream"/>; the CRC is the PNG one.
/// </summary>
public static class PngEncoder
{
    /// <param name="pixels">Row-major, top-down: 1 byte per pixel when <paramref name="gray"/>, else 3 (R,G,B).</param>
    public static byte[] Encode(byte[] pixels, int width, int height, bool gray)
    {
        var channels = gray ? 1 : 3;
        var stride = width * channels;
        if (pixels.Length < stride * height) throw new ArgumentException("pixel buffer too small");

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;                     // bit depth
        ihdr[9] = (byte)(gray ? 0 : 2);  // colour type: gray / truecolour
        WriteChunk(png, "IHDR", ihdr);

        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
            {
                for (var y = 0; y < height; y++)
                {
                    z.WriteByte(0);   // filter: none
                    z.Write(pixels, y * stride, stride);
                }
            }
            WriteChunk(png, "IDAT", raw.ToArray());
        }
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = Crc32(typeBytes, data);
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc);
        s.Write(c);
    }

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(byte[] a, byte[] b)
    {
        var c = 0xFFFFFFFFu;
        foreach (var x in a) c = Table[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in b) c = Table[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
