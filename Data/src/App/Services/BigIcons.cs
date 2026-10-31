using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MultiWindowSwitcher.Models;
using static MultiWindowSwitcher.Native.Win32;

namespace MultiWindowSwitcher.Services;

/// <summary>
/// Крупный значок программы — из системного списка «огромных» значков (до 256 px) по пути к exe.
/// У старых программ значок там нарисован маленьким в углу: обрезаем по непрозрачной части.
/// Нет exe (окна UWP и т. п.) — берём обычный значок окна.
/// </summary>
public static class BigIcons
{
    private const uint SHGFI_SYSICONINDEX = 0x4000;
    private const int SHIL_JUMBO = 4, ILD_TRANSPARENT = 1;
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Of(WindowItem item)
    {
        if (string.IsNullOrEmpty(item.ExePath))
        {
            return item.Icon;
        }

        if (!Cache.TryGetValue(item.ExePath, out var image))
        {
            image = Load(item.ExePath);
            Cache[item.ExePath] = image;
        }

        return image ?? item.Icon;
    }

    private static ImageSource? Load(string path)
    {
        try
        {
            var info = new SHFILEINFO();

            if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_SYSICONINDEX) == IntPtr.Zero)
            {
                return null;
            }

            var iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");

            if (SHGetImageList(SHIL_JUMBO, ref iid, out var list) != 0 || list is null)
            {
                return null;
            }

            var icon = IntPtr.Zero;

            if (list.GetIcon(info.iIcon, ILD_TRANSPARENT, ref icon) != 0 || icon == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                var cropped = CropToContent(source);
                cropped.Freeze();
                return cropped;
            }
            finally
            {
                DestroyIcon(icon);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Крупный значок не получен: " + path + " — " + ex.Message);
            return null;
        }
    }

    /// <summary>Обрезать прозрачные поля: маленький значок в углу большого холста становится во весь размер.</summary>
    private static BitmapSource CropToContent(BitmapSource source)
    {
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        bgra.CopyPixels(pixels, stride, 0);

        int left = w, top = h, right = -1, bottom = -1;

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (pixels[y * stride + x * 4 + 3] > 8)
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }
        }

        if (right < 0)
        {
            return bgra;
        }

        // квадрат, чтобы значок не сплющился
        var side = Math.Max(right - left + 1, bottom - top + 1);
        left = Math.Clamp(left - (side - (right - left + 1)) / 2, 0, Math.Max(0, w - side));
        top = Math.Clamp(top - (side - (bottom - top + 1)) / 2, 0, Math.Max(0, h - side));
        side = Math.Min(side, Math.Min(w - left, h - top));

        return side >= w ? bgra : new CroppedBitmap(bgra, new Int32Rect(left, top, side, side));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [ComImport]
    [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig] int Add(IntPtr image, IntPtr mask, ref int index);
        [PreserveSig] int ReplaceIcon(int i, IntPtr icon, ref int index);
        [PreserveSig] int SetOverlayImage(int image, int overlay);
        [PreserveSig] int Replace(int i, IntPtr image, IntPtr mask);
        [PreserveSig] int AddMasked(IntPtr image, int mask, ref int index);
        [PreserveSig] int Draw(IntPtr parameters);
        [PreserveSig] int Remove(int i);
        [PreserveSig] int GetIcon(int i, int flags, ref IntPtr icon);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("shell32.dll")]
    private static extern int SHGetImageList(int list, ref Guid iid, out IImageList? result);
}
