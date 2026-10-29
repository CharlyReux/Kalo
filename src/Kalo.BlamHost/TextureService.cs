using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Bungie.Tags;
using Kalo.Protocol;

namespace Kalo.BlamHost
{
    /// <summary>
    /// Bitmaps for the map view: the first image of a bitmap tag, at a mip level no larger than MaxSize, decoded from
    /// its processed pixel data (DXT1/3/5, DXT5A, DXN, plain 8/16/32-bit formats) to RGBA and written as .ktex
    /// ("KTEX", width, height, then width*height*4 bytes, top row first).
    /// </summary>
    internal static class TextureService
    {
        private const uint Magic = 0x5845544B; // "KTEX"

        public static ExportTexturesResult Export(List<string> bitmaps, string folder, int maxSize)
        {
            var sw = Stopwatch.StartNew();
            Directory.CreateDirectory(folder);
            var result = new ExportTexturesResult { Textures = new List<TextureInfo>() };
            foreach (string bitmap in (bitmaps ?? new List<string>()).Where(b => !string.IsNullOrEmpty(b)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var info = new TextureInfo { Bitmap = bitmap, File = Path.Combine(folder, TextureNames.FileName(bitmap)) };
                try
                {
                    using (var tag = new TagFile(TagService.TagPathOf(bitmap)))
                        Decode(tag, info, maxSize);
                }
                catch (Exception ex)
                {
                    info.Error = ex.Message;
                }
                result.Textures.Add(info);
            }
            result.Ms = sw.ElapsedMilliseconds;
            return result;
        }

        private static void Decode(TagFile tag, TextureInfo info, int maxSize)
        {
            var images = tag.SelectField("Block:bitmaps") as TagFieldBlock;
            if (images == null || images.Elements.Count == 0) throw new InvalidDataException("no images");
            const string b = "Block:bitmaps[0]/";
            int w = Int(tag.SelectField(b + "ShortInteger:width"));
            int h = Int(tag.SelectField(b + "ShortInteger:height"));
            int depth = Math.Max(1, Int(tag.SelectField(b + "CharInteger:depth")));
            int type = Int(tag.SelectField(b + "CharEnum:type"));
            int mips = Math.Max(0, Int(tag.SelectField(b + "CharInteger:mipmap count")));
            int offset = Int(tag.SelectField(b + "LongInteger:pixels offset"));
            var formatField = tag.SelectField(b + "ShortEnum:format") as TagFieldEnum;
            string format = formatField == null ? "?" : SafeName(formatField);
            info.Format = format;
            if (type != 0 || depth != 1) throw new InvalidDataException("not a 2D texture (" + type + ")");
            var data = tag.SelectField("Data:processed pixel data") as TagFieldData;
            byte[] pixels = data == null ? null : data.GetData();
            if (pixels == null || pixels.Length == 0) throw new InvalidDataException("no pixel data");

            // Pick the mip level: the first one that fits in maxSize.
            int level = 0;
            while (level < mips && (Math.Max(1, w >> level) > maxSize || Math.Max(1, h >> level) > maxSize)) level++;
            for (int i = 0; i < level; i++) offset += LevelSize(format, Math.Max(1, w >> i), Math.Max(1, h >> i));
            int lw = Math.Max(1, w >> level), lh = Math.Max(1, h >> level);
            byte[] rgba = DecodeLevel(format, pixels, offset, lw, lh);
            info.Width = lw;
            info.Height = lh;
            string tmp = info.File + ".tmp";
            using (var wr = new BinaryWriter(File.Create(tmp)))
            {
                wr.Write(Magic);
                wr.Write(lw);
                wr.Write(lh);
                wr.Write(rgba);
            }
            if (File.Exists(info.File)) File.Delete(info.File);
            File.Move(tmp, info.File);
        }

        private static string SafeName(TagFieldEnum e)
        {
            try { return e.Items[e.Value].EnumName; } catch (Exception) { return "#" + e.Value; }
        }

        private static int BlockBytes(string f)
        {
            switch (f)
            {
                case "dxt1": case "dxt3a": case "dxt5a": case "ctx1": case "dxt3a_mono": case "dxt3a_alpha": case "dxt5a_mono": case "dxt5a_alpha": case "dxt3a_1111": return 8;
                case "dxt3": case "dxt5": case "dxn": case "dxn_mono_alpha": return 16;
                default: return 0;
            }
        }

        private static int PixelBytes(string f)
        {
            switch (f)
            {
                case "a8": case "y8": case "ay8": case "r8": return 1;
                case "a8y8": case "r5g6b5": case "a1r5g5b5": case "a4r4g4b4": case "v8u8": case "g8b8": return 2;
                case "x8r8g8b8": case "a8r8g8b8": case "q8w8v8u8": case "a2r10g10b10": return 4;
                case "a16b16g16r16f": case "a16b16g16r16": return 8;
                case "a32b32g32r32f": return 16;
                default: return 0;
            }
        }

        private static int LevelSize(string f, int w, int h)
        {
            int block = BlockBytes(f);
            if (block > 0) return Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * block;
            int px = PixelBytes(f);
            if (px > 0) return w * h * px;
            throw new NotSupportedException("format " + f);
        }

        private static byte[] DecodeLevel(string f, byte[] src, int offset, int w, int h)
        {
            var dst = new byte[w * h * 4];
            int block = BlockBytes(f);
            if (block > 0)
            {
                int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
                var c = new byte[16 * 4];
                for (int by = 0; by < bh; by++)
                    for (int bx = 0; bx < bw; bx++)
                    {
                        int o = offset + (by * bw + bx) * block;
                        if (o + block > src.Length) continue;
                        DecodeBlock(f, src, o, c);
                        for (int py = 0; py < 4; py++)
                            for (int px = 0; px < 4; px++)
                            {
                                int x = bx * 4 + px, y = by * 4 + py;
                                if (x >= w || y >= h) continue;
                                Buffer.BlockCopy(c, (py * 4 + px) * 4, dst, (y * w + x) * 4, 4);
                            }
                    }
                return dst;
            }
            int bpp = PixelBytes(f);
            for (int i = 0; i < w * h; i++)
            {
                int o = offset + i * bpp;
                if (o + bpp > src.Length) break;
                byte r = 0, g = 0, bl = 0, a = 255;
                switch (f)
                {
                    case "a8": r = g = bl = 255; a = src[o]; break;
                    case "y8": case "r8": r = g = bl = src[o]; break;
                    case "ay8": r = g = bl = a = src[o]; break;
                    case "a8y8": r = g = bl = src[o]; a = src[o + 1]; break;
                    case "r5g6b5": { int v = src[o] | (src[o + 1] << 8); r = (byte)((v >> 11 & 31) * 255 / 31); g = (byte)((v >> 5 & 63) * 255 / 63); bl = (byte)((v & 31) * 255 / 31); break; }
                    case "a1r5g5b5": { int v = src[o] | (src[o + 1] << 8); a = (byte)((v >> 15) * 255); r = (byte)((v >> 10 & 31) * 255 / 31); g = (byte)((v >> 5 & 31) * 255 / 31); bl = (byte)((v & 31) * 255 / 31); break; }
                    case "a4r4g4b4": { int v = src[o] | (src[o + 1] << 8); a = (byte)((v >> 12 & 15) * 17); r = (byte)((v >> 8 & 15) * 17); g = (byte)((v >> 4 & 15) * 17); bl = (byte)((v & 15) * 17); break; }
                    case "x8r8g8b8": bl = src[o]; g = src[o + 1]; r = src[o + 2]; break;
                    case "a8r8g8b8": bl = src[o]; g = src[o + 1]; r = src[o + 2]; a = src[o + 3]; break;
                    default: r = g = bl = 128; break;
                }
                dst[i * 4] = r; dst[i * 4 + 1] = g; dst[i * 4 + 2] = bl; dst[i * 4 + 3] = a;
            }
            return dst;
        }

        /// <summary>Decodes one 4×4 block to 16 RGBA pixels.</summary>
        private static void DecodeBlock(string f, byte[] s, int o, byte[] c)
        {
            switch (f)
            {
                case "dxt1":
                    Color(s, o, c, true);
                    break;
                case "dxt3":
                    Color(s, o + 8, c, false);
                    for (int i = 0; i < 16; i++) c[i * 4 + 3] = (byte)(((s[o + i / 2] >> (i % 2 * 4)) & 15) * 17);
                    break;
                case "dxt5":
                    Color(s, o + 8, c, false);
                    Alpha(s, o, c, 3);
                    break;
                case "dxn":
                case "dxn_mono_alpha":
                {
                    // Two-channel normal map: show it as a neutral grey shaded by its X channel.
                    Alpha(s, o, c, 0);
                    Alpha(s, o + 8, c, 1);
                    for (int i = 0; i < 16; i++) { c[i * 4 + 2] = 255; c[i * 4 + 3] = 255; }
                    break;
                }
                case "dxt5a":
                case "dxt5a_mono":
                case "dxt5a_alpha":
                    Alpha(s, o, c, 0);
                    for (int i = 0; i < 16; i++) { c[i * 4 + 1] = c[i * 4 + 2] = c[i * 4]; c[i * 4 + 3] = 255; }
                    break;
                default:
                    for (int i = 0; i < 16; i++) { c[i * 4] = c[i * 4 + 1] = c[i * 4 + 2] = 128; c[i * 4 + 3] = 255; }
                    break;
            }
        }

        private static void Color(byte[] s, int o, byte[] c, bool oneBitAlpha)
        {
            int c0 = s[o] | (s[o + 1] << 8), c1 = s[o + 2] | (s[o + 3] << 8);
            var p = new int[4, 4];
            Unpack(c0, out p[0, 0], out p[0, 1], out p[0, 2]);
            Unpack(c1, out p[1, 0], out p[1, 1], out p[1, 2]);
            bool four = !oneBitAlpha || c0 > c1;
            for (int k = 0; k < 3; k++)
            {
                if (four)
                {
                    p[2, k] = (2 * p[0, k] + p[1, k]) / 3;
                    p[3, k] = (p[0, k] + 2 * p[1, k]) / 3;
                }
                else
                {
                    p[2, k] = (p[0, k] + p[1, k]) / 2;
                    p[3, k] = 0;
                }
            }
            uint bits = (uint)(s[o + 4] | (s[o + 5] << 8) | (s[o + 6] << 16) | (s[o + 7] << 24));
            for (int i = 0; i < 16; i++)
            {
                int idx = (int)(bits >> (2 * i) & 3);
                c[i * 4] = (byte)p[idx, 0];
                c[i * 4 + 1] = (byte)p[idx, 1];
                c[i * 4 + 2] = (byte)p[idx, 2];
                c[i * 4 + 3] = (byte)(!four && idx == 3 ? 0 : 255);
            }
        }

        private static void Unpack(int v, out int r, out int g, out int b)
        {
            r = (v >> 11 & 31) * 255 / 31;
            g = (v >> 5 & 63) * 255 / 63;
            b = (v & 31) * 255 / 31;
        }

        /// <summary>DXT5-style 8-byte alpha block into channel <paramref name="channel"/> of the 16 pixels.</summary>
        private static void Alpha(byte[] s, int o, byte[] c, int channel)
        {
            int a0 = s[o], a1 = s[o + 1];
            var a = new int[8];
            a[0] = a0; a[1] = a1;
            if (a0 > a1) for (int i = 1; i < 7; i++) a[i + 1] = ((7 - i) * a0 + i * a1) / 7;
            else
            {
                for (int i = 1; i < 5; i++) a[i + 1] = ((5 - i) * a0 + i * a1) / 5;
                a[6] = 0; a[7] = 255;
            }
            ulong bits = 0;
            for (int i = 0; i < 6; i++) bits |= (ulong)s[o + 2 + i] << (8 * i);
            for (int i = 0; i < 16; i++) c[i * 4 + channel] = (byte)a[(int)(bits >> (3 * i) & 7)];
        }

        private static int Int(TagField f)
        {
            var i = f as TagFieldElementInteger;
            if (i != null) return (int)i.Data;
            var e = f as TagFieldEnum;
            return e != null ? e.Value : 0;
        }
    }
}
