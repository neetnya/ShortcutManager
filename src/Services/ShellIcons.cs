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

    // 提取串行化：Shell 图标缓存在首次提取时并发调用会偶发返回空句柄，
    // 一旦拿到空结果就会退化成兜底图标，所以这里保证同一时刻只有一个提取在跑。
    private static readonly object ExtractGate = new();

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

        // 提取串行化：Shell 图标缓存对同一路径的并发首次提取会偶发返回空句柄，
        // 结果就是「图标显示成兜底图」。但不能让 UI 线程无限等锁（目标可能在慢速网络盘上），
        // 抢不到锁就先给兜底图 —— 兜底图不入缓存，下次重绘/刷新会再试一次。
        ImageSource? image = null;
        var locked = false;
        try
        {
            locked = Monitor.TryEnter(ExtractGate, TimeSpan.FromMilliseconds(1500));
            if (locked) image = Load(path, isDirectory) ?? ExtractFromFile(path);
        }
        finally
        {
            if (locked) Monitor.Exit(ExtractGate);
        }

        // 提取失败时不缓存兜底图：下次还有机会拿到真实图标
        if (image is null) return Fallback(isDirectory);

        lock (Gate)
        {
            Cache[key] = image;
        }
        return image;
    }

    /// <summary>是不是程序自带的兜底图标（用于判断「这张图不是真实图标」）。</summary>
    public static bool IsBuiltInFallback(ImageSource? image, bool isDirectory)
        => image is not null && ReferenceEquals(image, Fallback(isDirectory));

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

    /// <summary>
    /// 诊断用（--icon &lt;路径&gt;）：逐步记录一次图标提取的结果，
    /// 用于排查「某个文件的图标为什么显示成兜底图标」。
    /// </summary>
    public static string Diagnose(string path, bool isDirectory)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"path       : {path}");
        sb.AppendLine($"isDir      : {isDirectory}");
        sb.AppendLine($"fileExists : {File.Exists(path)}   dirExists: {Directory.Exists(path)}");

        var info = new SHFILEINFO();
        uint flags = SHGFI_ICON | SHGFI_LARGEICON;
        bool exists = isDirectory ? Directory.Exists(path) : File.Exists(path);
        if (!exists) flags |= SHGFI_USEFILEATTRIBUTES;
        uint attrs = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;

        var res = SHGetFileInfo(path, attrs, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        sb.AppendLine($"SHGetFileInfo: res={res} hIcon={info.hIcon} iIcon={info.iIcon} flags=0x{flags:X} type='{info.szTypeName}'");

        if (info.hIcon != IntPtr.Zero)
        {
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                sb.AppendLine($"FromHIcon  : OK {src.Width}x{src.Height} {src.GetType().Name} fmt={src.Format}");
                src.Freeze();
                sb.AppendLine("Freeze     : OK");
            }
            catch (Exception ex)
            {
                sb.AppendLine("FromHIcon  : THREW " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }

        var viaExtract = ExtractFromFile(path);
        sb.AppendLine($"PrivateExtractIcons: {(viaExtract is null ? "null" : viaExtract.Width + "x" + viaExtract.Height)}");

        var final = Get(path, isDirectory);
        sb.AppendLine($"final      : {final.Width}x{final.Height} {final.GetType().Name}"
                      + (IsBuiltInFallback(final, isDirectory) ? "  <= 兜底图标（提取失败）" : ""));
        return sb.ToString();
    }

    /// <summary>直接从文件的图标资源里提取（绕开 Shell 缓存/类型关联），失败返回 null。</summary>
    private static ImageSource? ExtractFromFile(string path)
    {
        try
        {
            var src = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (src is null) return null;
            var result = Imaging.CreateBitmapSourceFromHIcon(src.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            result.Freeze();
            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 程序自带的兜底图标：Shell 与文件资源都提取不到时才用。
    /// 刻意不用本程序 exe 自身的图标做「文件」占位 —— 那会让用户以为
    /// 「我的图标被换成了这个工具的图标」，也分不清是提取失败还是本来就这样。
    /// </summary>
    private static ImageSource Fallback(bool isDirectory)
        => isDirectory
            ? _fallbackFolder ??= BuildFallback(true)
            : _fallbackFile ??= BuildFallback(false);

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
