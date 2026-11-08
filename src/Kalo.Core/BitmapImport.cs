using System.Diagnostics;

namespace Kalo.Core;

/// <summary>
/// Turns a picture into a bitmap tag: writes it as a TIFF under the workspace's data folder (what HREK's tool.exe reads),
/// then runs tool.exe, which makes the .bitmap next to it under tags.
/// </summary>
public static class BitmapImport
{
    /// <summary>Returns the bitmap tag path (levels\mods\...\name.bitmap), or throws with tool.exe's last lines.</summary>
    public static async Task<string> ImportAsync(KaloPaths paths, string folder, string name, int width, int height, byte[] rgb, CancellationToken ct = default)
    {
        string relative = Path.Combine(folder, name);
        string tiff = Path.Combine(paths.Workspace, "data", relative + ".tif");
        Directory.CreateDirectory(Path.GetDirectoryName(tiff)!);
        File.WriteAllBytes(tiff, Tiff(width, height, rgb));

        var start = new ProcessStartInfo(Path.Combine(paths.Workspace, "tool.exe"))
        {
            WorkingDirectory = paths.Workspace,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("bitmap_single");
        start.ArgumentList.Add(relative + ".tif");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("tool.exe did not start.");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var errors = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        string bitmap = relative + ".bitmap";
        if (!File.Exists(Path.Combine(paths.WorkspaceTags, bitmap)))
            throw new InvalidOperationException("tool.exe made no bitmap: " + string.Join(" / ", (await output + await errors).Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(4)).Trim());
        return bitmap;
    }

    /// <summary>An uncompressed 8-bit RGB TIFF, one strip.</summary>
    public static byte[] Tiff(int width, int height, byte[] rgb)
    {
        const int entries = 10, ifd = 8, bits = ifd + 2 + entries * 12 + 4, pixels = bits + 6;
        var data = new byte[pixels + rgb.Length];
        using var w = new BinaryWriter(new MemoryStream(data));
        w.Write((byte)'I'); w.Write((byte)'I'); w.Write((ushort)42); w.Write(ifd);
        w.Write((ushort)entries);
        void Entry(ushort tag, ushort type, int count, int value)
        {
            w.Write(tag); w.Write(type); w.Write(count);
            if (type == 3 && count == 1) { w.Write((ushort)value); w.Write((ushort)0); } else w.Write(value);
        }
        Entry(256, 4, 1, width);
        Entry(257, 4, 1, height);
        Entry(258, 3, 3, bits);       // bits per sample: at an offset, there are three
        Entry(259, 3, 1, 1);          // no compression
        Entry(262, 3, 1, 2);          // RGB
        Entry(273, 4, 1, pixels);     // where the strip is
        Entry(277, 3, 1, 3);          // samples per pixel
        Entry(278, 4, 1, height);     // rows per strip
        Entry(279, 4, 1, rgb.Length); // strip size
        Entry(284, 3, 1, 1);          // interleaved
        w.Write(0);                   // no next directory
        w.Write((ushort)8); w.Write((ushort)8); w.Write((ushort)8);
        w.Write(rgb);
        return data;
    }
}
