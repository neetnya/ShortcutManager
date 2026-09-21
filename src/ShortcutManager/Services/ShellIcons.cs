using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShortcutManager.Models;

namespace ShortcutManager.Services;

/// <summary>通过 Win32 Shell API 取文件/文件夹的系统图标，并转成 WPF ImageSource。</summary>
public static class ShellIcons
{
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int SHDefExtractIcon(string pszIconFile, int iIndex, uint uFlags, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIconSize);

    // 后端缓存：ImageSource 被 Freeze 后可跨线程共享
    private static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    private static ImageSource? _fallbackFolder;
    private static ImageSource? _fallbackFile;

    /// <summary>同步获取图标（供后台线程预热使用）。</summary>
    public static ImageSource Get(string path, bool isDirectory)
    {
        var key = (isDirectory ? "D:" : "F:") + path.ToLowerInvariant();
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var hit)) return hit;
        }

        var image = Load(path, isDirectory) ?? Fallback(isDirectory);

        lock (Gate)
        {
            Cache[key] = image;
        }
        return image;
    }

    private static ImageSource? Load(string path, bool isDirectory)
    {
        var info = new SHFILEINFO();
        uint flags = SHGFI_ICON | SHGFI_LARGEICON;

        // 目标不存在时也给出扩展名对应的图标（避免整格空白）
        bool exists = isDirectory ? Directory.Exists(path) : File.Exists(path);
        if (!exists) flags |= SHGFI_USEFILEATTRIBUTES;

        uint attrs = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
        var res = SHGetFileInfo(path, attrs, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        if (res == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch
        {
            return null;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    /// <summary>程序自带的兜底图标（Shell 也失败时使用）。</summary>
    private static ImageSource Fallback(bool isDirectory)
    {
        if (isDirectory)
            return _fallbackFolder ??= BuildFallback(isDirectory);

        // 优先用 exe 自身图标作为“文件”占位
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var ico = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (ico is not null)
                {
                    var src = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
            }
        }
        catch { /* ignore */ }

        return _fallbackFile ??= BuildFallback(isDirectory);
    }

    private static ImageSource BuildFallback(bool isDirectory)
    {
        // 画一个简单的圆角方块：文件夹=琥珀色，文件=灰蓝色
        var color = isDirectory ? Color.FromRgb(0xF2, 0xB0, 0x2E) : Color.FromRgb(0x8A, 0x96, 0xA8);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), 1);
            pen.Freeze();
            dc.DrawRoundedRectangle(brush, pen, new Rect(4, 8, 24, 20), 4, 4);
            if (isDirectory)
            {
                var tab = new SolidColorBrush(Color.FromArgb(255, 0xD9, 0x9A, 0x1E));
                tab.Freeze();
                dc.DrawRoundedRectangle(tab, null, new Rect(4, 3, 12, 8), 3, 3);
            }
        }
        var bmp = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }
}
