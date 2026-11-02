using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SkyBar.SystemIntegration;

/// <summary>
/// Mengambil ikon/thumbnail resolusi tinggi (hingga 256px) dari Windows Shell,
/// lengkap dengan alpha channel, sehingga tetap tajam saat diperbesar di dock
/// dan di layar HiDPI. Pengganti Icon.ExtractAssociatedIcon yang hanya 32px.
/// </summary>
public static class ShellIconLoader
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    [Flags]
    private enum SIIGBF
    {
        ResizeToFit = 0x00,
        BiggerSizeOk = 0x01,
        IconOnly = 0x04,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DIBSECTION
    {
        public BITMAP dsBm;
        public BITMAPINFOHEADER dsBmih;
        public uint dsBitfields0;
        public uint dsBitfields1;
        public uint dsBitfields2;
        public IntPtr dshSection;
        public uint dsOffset;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        [In] ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, out DIBSECTION lpvObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>Ikon aplikasi/file (tanpa thumbnail konten).</summary>
    public static ImageSource? GetIcon(string path, int size = 256) => Get(path, size, iconOnly: true);

    /// <summary>Thumbnail konten bila tersedia (gambar, video, dll), atau ikon.</summary>
    public static ImageSource? GetThumbnail(string path, int size = 96) => Get(path, size, iconOnly: false);

    private static ImageSource? Get(string path, int size, bool iconOnly)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        string key = $"{(iconOnly ? 'i' : 't')}{size}|{path}";
        if (Cache.TryGetValue(key, out var cached)) return cached;

        ImageSource? result = null;
        try
        {
            result = LoadFromShell(path, size, iconOnly ? SIIGBF.IconOnly | SIIGBF.BiggerSizeOk : SIIGBF.ResizeToFit);
        }
        catch
        {
            // Diabaikan: jatuh ke fallback di bawah.
        }

        result ??= LoadLegacyIcon(path);
        Cache[key] = result;
        return result;
    }

    private static ImageSource? LoadFromShell(string path, int size, SIIGBF flags)
    {
        Guid iid = typeof(IShellItemImageFactory).GUID;
        SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out IShellItemImageFactory factory);

        try
        {
            int hr = factory.GetImage(new SIZE { cx = size, cy = size }, flags, out IntPtr hBitmap);
            if (hr != 0 || hBitmap == IntPtr.Zero) return null;

            try
            {
                return ConvertHBitmapWithAlpha(hBitmap);
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    private static BitmapSource? ConvertHBitmapWithAlpha(IntPtr hBitmap)
    {
        int bytes = GetObject(hBitmap, Marshal.SizeOf<DIBSECTION>(), out DIBSECTION dib);
        if (bytes == 0) return null;

        BITMAP bm = dib.dsBm;
        if (bm.bmBitsPixel != 32 || bm.bmBits == IntPtr.Zero || bm.bmWidth <= 0 || bm.bmHeight <= 0)
        {
            var fallback = Imaging.CreateBitmapSourceFromHBitmap(hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            fallback.Freeze();
            return fallback;
        }

        int width = bm.bmWidth;
        int height = bm.bmHeight;
        int stride = bm.bmWidthBytes;
        bool bottomUp = dib.dsBmih.biHeight > 0;

        var pixels = new byte[stride * height];
        for (int row = 0; row < height; row++)
        {
            int srcRow = bottomUp ? height - 1 - row : row;
            Marshal.Copy(bm.bmBits + srcRow * stride, pixels, row * stride, stride);
        }

        // Ikon lama tanpa alpha channel: jadikan opak agar tidak hilang.
        bool hasAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0) { hasAlpha = true; break; }
        }
        if (!hasAlpha)
        {
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        }

        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }

    private static ImageSource? LoadLegacyIcon(string path)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}

