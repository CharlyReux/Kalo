using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;
using Kalo.Spike0;

namespace Kalo.Spike1
{
    /// <summary>
    /// Figures out what ViewPort.GetRenderSurface() returns and, if it is a Direct3D surface or texture,
    /// copies its pixels into a PNG. Every pointer is checked with VirtualQuery before it is read, so an
    /// unexpected value (a handle, not a COM object) is reported instead of crashing.
    /// </summary>
    internal static class SurfaceProbe
    {
        private static readonly Guid IID_ID3D11Texture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        private static readonly Guid IID_ID3D11Resource = new Guid("dc8e63f3-d12b-4952-b47b-5e45026a862d");
        private static readonly Guid IID_IDXGIResource = new Guid("035f3ab4-482e-4e50-b41f-8a7f8bd8960b");
        private static readonly Guid IID_IDirect3DSurface9 = new Guid("0cfbaf3a-9ff6-429a-99b3-a2796af8b89b");
        private static readonly Guid IID_IDirect3DTexture9 = new Guid("85c31227-3de5-4f00-9b3a-f11ac38c18b5");
        private static readonly Guid IID_ID3D12Resource = new Guid("696442be-a72e-4059-bc79-5b5c98040fad");

        /// <summary>Describes the surface and tries to save it. Returns true when a PNG was written.</summary>
        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        public static bool Capture(IntPtr surface, string pngPath, Report report)
        {
            try
            {
                report.Metric("Render surface pointer", "0x" + surface.ToInt64().ToString("X"));
                if (surface == IntPtr.Zero) { report.Metric("Render surface", "null (no surface yet)"); return false; }
                if (!Readable(surface, IntPtr.Size)) { report.Metric("Render surface", "not readable memory: probably a handle, not an object"); return false; }

                IntPtr vtable = Marshal.ReadIntPtr(surface);
                if (!Readable(vtable, 3 * IntPtr.Size) || !Executable(Marshal.ReadIntPtr(vtable)))
                {
                    report.Metric("Render surface", "readable but not a COM object");
                    return false;
                }

                string kinds = "";
                if (Has(surface, IID_ID3D11Texture2D)) kinds += " ID3D11Texture2D";
                if (Has(surface, IID_ID3D11Resource)) kinds += " ID3D11Resource";
                if (Has(surface, IID_IDXGIResource)) kinds += " IDXGIResource";
                if (Has(surface, IID_IDirect3DSurface9)) kinds += " IDirect3DSurface9";
                if (Has(surface, IID_IDirect3DTexture9)) kinds += " IDirect3DTexture9";
                if (Has(surface, IID_ID3D12Resource)) kinds += " ID3D12Resource";
                report.Metric("Render surface is a COM object implementing", kinds.Length == 0 ? "none of the Direct3D interfaces tried" : kinds.Trim());

                IntPtr p;
                if (Query(surface, IID_ID3D11Texture2D, out p))
                {
                    try { return CaptureD3D11(p, pngPath, report); }
                    finally { Marshal.Release(p); }
                }
                if (Query(surface, IID_IDirect3DSurface9, out p))
                {
                    try { return CaptureD3D9(p, pngPath, report); }
                    finally { Marshal.Release(p); }
                }
                return false;
            }
            catch (Exception ex)
            {
                report.Metric("Surface capture failed", ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        // ---- Direct3D 11 ----------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct D3D11_TEXTURE2D_DESC
        {
            public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CPUAccessFlags, MiscFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3D11_MAPPED_SUBRESOURCE
        {
            public IntPtr pData;
            public uint RowPitch;
            public uint DepthPitch;
        }

        private delegate void GetDeviceFn(IntPtr self, out IntPtr device);
        private delegate void TexGetDescFn(IntPtr self, out D3D11_TEXTURE2D_DESC desc);
        private delegate int CreateTexture2DFn(IntPtr self, ref D3D11_TEXTURE2D_DESC desc, IntPtr initialData, out IntPtr texture);
        private delegate void GetImmediateContextFn(IntPtr self, out IntPtr context);
        private delegate void CopyResourceFn(IntPtr self, IntPtr dst, IntPtr src);
        private delegate int MapFn(IntPtr self, IntPtr resource, uint subresource, int mapType, uint flags, out D3D11_MAPPED_SUBRESOURCE mapped);
        private delegate void UnmapFn(IntPtr self, IntPtr resource, uint subresource);

        private static bool CaptureD3D11(IntPtr texture, string pngPath, Report report)
        {
            D3D11_TEXTURE2D_DESC desc;
            Fn<TexGetDescFn>(texture, 10)(texture, out desc);
            report.Metric("D3D11 texture", desc.Width + " x " + desc.Height + ", format " + desc.Format + " (" + FormatName(desc.Format) + "), samples " + desc.SampleCount + ", bind 0x" + desc.BindFlags.ToString("X") + ", misc 0x" + desc.MiscFlags.ToString("X"));
            if (desc.SampleCount > 1) { report.Metric("Capture", "skipped: multisampled texture"); return false; }

            IntPtr device, context = IntPtr.Zero, staging = IntPtr.Zero;
            Fn<GetDeviceFn>(texture, 3)(texture, out device);
            try
            {
                Fn<GetImmediateContextFn>(device, 40)(device, out context);
                var sdesc = desc;
                sdesc.MipLevels = 1; sdesc.ArraySize = 1; sdesc.SampleCount = 1; sdesc.SampleQuality = 0;
                sdesc.Usage = 3;               // D3D11_USAGE_STAGING
                sdesc.BindFlags = 0;
                sdesc.CPUAccessFlags = 0x20000; // D3D11_CPU_ACCESS_READ
                sdesc.MiscFlags = 0;
                int hr = Fn<CreateTexture2DFn>(device, 5)(device, ref sdesc, IntPtr.Zero, out staging);
                if (hr < 0) { report.Metric("Capture", "CreateTexture2D failed 0x" + hr.ToString("X8")); return false; }

                Fn<CopyResourceFn>(context, 47)(context, staging, texture);
                D3D11_MAPPED_SUBRESOURCE mapped;
                hr = Fn<MapFn>(context, 14)(context, staging, 0, 1 /* READ */, 0, out mapped);
                if (hr < 0) { report.Metric("Capture", "Map failed 0x" + hr.ToString("X8")); return false; }
                try
                {
                    return SavePixels(mapped.pData, (int)mapped.RowPitch, (int)desc.Width, (int)desc.Height, desc.Format, pngPath, report);
                }
                finally
                {
                    Fn<UnmapFn>(context, 15)(context, staging, 0);
                }
            }
            finally
            {
                if (staging != IntPtr.Zero) Marshal.Release(staging);
                if (context != IntPtr.Zero) Marshal.Release(context);
                if (device != IntPtr.Zero) Marshal.Release(device);
            }
        }

        // ---- Direct3D 9 -----------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct D3DSURFACE_DESC
        {
            public uint Format, Type, Usage, Pool, MultiSampleType, MultiSampleQuality, Width, Height;
        }

        private delegate int Surf9GetDescFn(IntPtr self, out D3DSURFACE_DESC desc);
        private delegate int Surf9GetDCFn(IntPtr self, out IntPtr hdc);
        private delegate int Surf9ReleaseDCFn(IntPtr self, IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int sx, int sy, uint rop);

        private static bool CaptureD3D9(IntPtr surface, string pngPath, Report report)
        {
            D3DSURFACE_DESC desc;
            int hr = Fn<Surf9GetDescFn>(surface, 12)(surface, out desc);
            report.Metric("D3D9 surface", desc.Width + " x " + desc.Height + ", format " + desc.Format + ", usage 0x" + desc.Usage.ToString("X") + ", pool " + desc.Pool + " (hr 0x" + hr.ToString("X8") + ")");

            IntPtr hdc;
            hr = Fn<Surf9GetDCFn>(surface, 15)(surface, out hdc);
            if (hr < 0) { report.Metric("Capture", "GetDC failed 0x" + hr.ToString("X8")); return false; }
            try
            {
                using (var bmp = new Bitmap((int)desc.Width, (int)desc.Height, PixelFormat.Format32bppRgb))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    IntPtr dst = g.GetHdc();
                    BitBlt(dst, 0, 0, (int)desc.Width, (int)desc.Height, hdc, 0, 0, 0x00CC0020 /* SRCCOPY */);
                    g.ReleaseHdc(dst);
                    bmp.Save(pngPath, ImageFormat.Png);
                }
                report.Metric("Saved frame", System.IO.Path.GetFileName(pngPath));
                return true;
            }
            finally
            {
                Fn<Surf9ReleaseDCFn>(surface, 16)(surface, hdc);
            }
        }

        // ---- Pixels -> PNG --------------------------------------------------------------------

        private static bool SavePixels(IntPtr src, int pitch, int w, int h, uint format, string pngPath, Report report)
        {
            bool rgba8 = format == 28 || format == 29;     // R8G8B8A8_UNORM(_SRGB)
            bool bgra8 = format == 87 || format == 91 || format == 88 || format == 93; // B8G8R8A8 / B8G8R8X8
            bool r10 = format == 24;                         // R10G10B10A2_UNORM
            bool f16 = format == 10;                         // R16G16B16A16_FLOAT
            if (!rgba8 && !bgra8 && !r10 && !f16)
            {
                report.Metric("Capture", "format " + format + " not converted (add it if this is the real one)");
                return false;
            }

            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb))
            {
                BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                var row = new byte[pitch];
                var outRow = new byte[w * 4];
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(new IntPtr(src.ToInt64() + (long)y * pitch), row, 0, Math.Min(pitch, row.Length));
                    for (int x = 0; x < w; x++)
                    {
                        byte r, g, b;
                        if (bgra8) { b = row[x * 4]; g = row[x * 4 + 1]; r = row[x * 4 + 2]; }
                        else if (rgba8) { r = row[x * 4]; g = row[x * 4 + 1]; b = row[x * 4 + 2]; }
                        else if (r10)
                        {
                            uint v = BitConverter.ToUInt32(row, x * 4);
                            r = (byte)((v & 0x3FF) >> 2); g = (byte)(((v >> 10) & 0x3FF) >> 2); b = (byte)(((v >> 20) & 0x3FF) >> 2);
                        }
                        else
                        {
                            r = Tone(Half(BitConverter.ToUInt16(row, x * 8)));
                            g = Tone(Half(BitConverter.ToUInt16(row, x * 8 + 2)));
                            b = Tone(Half(BitConverter.ToUInt16(row, x * 8 + 4)));
                        }
                        outRow[x * 4] = b; outRow[x * 4 + 1] = g; outRow[x * 4 + 2] = r; outRow[x * 4 + 3] = 255;
                    }
                    Marshal.Copy(outRow, 0, new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), outRow.Length);
                }
                bmp.UnlockBits(bd);
                bmp.Save(pngPath, ImageFormat.Png);
            }
            report.Metric("Saved frame", System.IO.Path.GetFileName(pngPath));
            return true;
        }

        private static float Half(ushort h)
        {
            int sign = (h >> 15) & 1, exp = (h >> 10) & 0x1F, mant = h & 0x3FF;
            float v = exp == 0 ? (float)(mant / 1024.0 * Math.Pow(2, -14))
                    : exp == 31 ? float.PositiveInfinity
                    : (float)((1 + mant / 1024.0) * Math.Pow(2, exp - 15));
            return sign == 1 ? -v : v;
        }

        private static byte Tone(float linear)
        {
            if (float.IsNaN(linear) || linear <= 0) return 0;
            double c = linear / (1 + linear);           // simple tone map for HDR targets
            return (byte)Math.Min(255, Math.Pow(c, 1 / 2.2) * 255 + 0.5);
        }

        private static string FormatName(uint f)
        {
            switch (f)
            {
                case 10: return "R16G16B16A16_FLOAT";
                case 24: return "R10G10B10A2_UNORM";
                case 28: return "R8G8B8A8_UNORM";
                case 29: return "R8G8B8A8_UNORM_SRGB";
                case 87: return "B8G8R8A8_UNORM";
                case 88: return "B8G8R8X8_UNORM";
                case 91: return "B8G8R8A8_UNORM_SRGB";
                case 93: return "B8G8R8X8_UNORM_SRGB";
                default: return "other";
            }
        }

        // ---- COM and memory helpers -----------------------------------------------------------

        private static T Fn<T>(IntPtr comObject, int slot) where T : class
        {
            IntPtr vtable = Marshal.ReadIntPtr(comObject);
            IntPtr fn = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer(fn, typeof(T)) as T;
        }

        private static bool Has(IntPtr obj, Guid iid)
        {
            IntPtr p;
            if (!Query(obj, iid, out p)) return false;
            Marshal.Release(p);
            return true;
        }

        private static bool Query(IntPtr obj, Guid iid, out IntPtr result)
        {
            int hr = Marshal.QueryInterface(obj, ref iid, out result);
            return hr >= 0 && result != IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        [DllImport("kernel32.dll")]
        private static extern UIntPtr VirtualQuery(IntPtr address, out MEMORY_BASIC_INFORMATION info, UIntPtr length);

        private static bool Readable(IntPtr address, int bytes)
        {
            MEMORY_BASIC_INFORMATION mbi;
            if (VirtualQuery(address, out mbi, (UIntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION))) == UIntPtr.Zero) return false;
            const uint MEM_COMMIT = 0x1000, PAGE_NOACCESS = 0x01, PAGE_GUARD = 0x100;
            if (mbi.State != MEM_COMMIT || (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) != 0) return false;
            long end = mbi.BaseAddress.ToInt64() + mbi.RegionSize.ToInt64();
            return address.ToInt64() + bytes <= end;
        }

        private static bool Executable(IntPtr address)
        {
            MEMORY_BASIC_INFORMATION mbi;
            if (VirtualQuery(address, out mbi, (UIntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION))) == UIntPtr.Zero) return false;
            return mbi.State == 0x1000 && (mbi.Protect & 0xF0) != 0; // PAGE_EXECUTE*
        }
    }
}
