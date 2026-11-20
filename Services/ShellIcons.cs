using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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

    // 提取串行化：Shell 图标缓存在首次提取时并发调用会偶发返回空句柄，保证同一时刻只有一个提取在跑
    private static readonly object ExtractGate = new();

    // 单消费者后台通道：避免界面首次加载时几十个 Task.Run 争锁超时导致兜底图残留
    private readonly record struct IconRequest(string Path, bool IsDirectory, Action<ImageSource?>? Callback);
    private static readonly Channel<IconRequest> Queue = Channel.CreateUnbounded<IconRequest>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private static int _workerStarted;

    private static ImageSource? _fallbackFolder;
    private static ImageSource? _fallbackFile;
    private static volatile uint _iconPixelSize = 48;

    /// <summary>在 UI 线程初始化或更新图标像素边长（避免后台线程跨线程读取 MainWindow 抛异常）。</summary>
    public static void InitializeDpi(Visual visual)
    {
        try
        {
            var dip = VisualTreeHelper.GetDpi(visual).PixelsPerDip;
            _iconPixelSize = (uint)Math.Clamp((int)Math.Round(48 * dip), 32, 256);
        }
        catch
        {
            _iconPixelSize = 48;
        }
    }

    /// <summary>清空内存图标缓存（手动刷新时调用，确保外部程序更新图标后能重新读取）。</summary>
    public static void ClearCache()
    {
        lock (Gate)
        {
            Cache.Clear();
        }
    }

    /// <summary>异步入队加载图标；不会因并发锁超时丢弃请求。</summary>
    public static void Enqueue(string path, bool isDirectory, Action<ImageSource?> callback)
    {
        EnsureWorkerStarted();
        Queue.Writer.TryWrite(new IconRequest(path, isDirectory, callback));
    }

    /// <summary>后台预热图标（与 UI 加载共用同一串行队列，无锁竞争）。</summary>
    public static void EnqueueWarm(string path, bool isDirectory)
    {
        if (TryGetCached(path, isDirectory, out _)) return;
        EnsureWorkerStarted();
        Queue.Writer.TryWrite(new IconRequest(path, isDirectory, null));
    }

    private static void EnsureWorkerStarted()
    {
        if (Interlocked.CompareExchange(ref _workerStarted, 1, 0) != 0) return;

        var thread = new Thread(ProcessQueue)
        {
            IsBackground = true,
            Name = "ShortcutManager.IconWorker",
        };
        try { thread.SetApartmentState(ApartmentState.STA); } catch { /* ignore */ }
        thread.Start();
    }

    private static void ProcessQueue()
    {
        var reader = Queue.Reader;
        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (reader.TryRead(out var req))
            {
                ImageSource? img = null;
                try
                {
                    img = Get(req.Path, req.IsDirectory);
                }
                catch { /* ignore */ }

                if (req.Callback is not null)
                {
                    try { req.Callback(img); }
                    catch { /* ignore */ }
                }
            }
        }
    }

    /// <summary>同步获取图标（供后台队列与自检/诊断使用）。</summary>
    public static ImageSource Get(string path, bool isDirectory)
    {
        var key = CacheKey(path, isDirectory);
        if (TryGetCached(path, isDirectory, out var hit)) return hit;

        ImageSource? image = null;
        lock (ExtractGate)
        {
            // 双重检查：排队期间可能已被前一个请求加载进缓存
            if (TryGetCached(path, isDirectory, out var secondHit)) return secondHit;
            image = Load(path, isDirectory) ?? ExtractFromFile(path);
        }

        // 提取失败时不缓存兜底图：下次还有机会拿到真实图标
        if (image is null) return Fallback(isDirectory);

        lock (Gate)
        {
            Cache[key] = image;
        }
        return image;
    }

    /// <summary>缓存命中时立刻返回，避免 UI 线程去提图标。</summary>
    public static bool TryGetCached(string path, bool isDirectory, out ImageSource image)
    {
        var key = CacheKey(path, isDirectory);
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var hit))
            {
                image = hit;
                return true;
            }
        }
        image = Fallback(isDirectory);
        return false;
    }

    /// <summary>提取未完成时先显示的占位图（与提取失败兜底图相同，且不入缓存）。</summary>
    public static ImageSource Placeholder(bool isDirectory) => Fallback(isDirectory);

    /// <summary>是不是程序自带的兜底图标（用于判断「这张图不是真实图标」）。</summary>
    public static bool IsBuiltInFallback(ImageSource? image, bool isDirectory)
        => image is not null && ReferenceEquals(image, Fallback(isDirectory));

    private static string CacheKey(string path, bool isDirectory)
        => $"{(isDirectory ? "D" : "F")}:{_iconPixelSize}:{path.ToLowerInvariant()}";

    private static ImageSource? Load(string path, bool isDirectory)
    {
        var sized = ExtractAtDpi(path);
        if (sized is not null) return sized;

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
        sb.AppendLine($"dpiSize    : {_iconPixelSize}");
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

    /// <summary>按 DPI 从文件抽更大的图标。关联图标/文件夹会失败，再走 SHGetFileInfo。</summary>
    private static ImageSource? ExtractAtDpi(string path)
    {
        try
        {
            var size = _iconPixelSize;
            var packed = size | (size << 16);
            var hr = SHDefExtractIcon(path, 0, 0, out var large, out var small, packed);
            if (hr != 0 || large == IntPtr.Zero)
            {
                if (small != IntPtr.Zero) DestroyIcon(small);
                return null;
            }

            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(large, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally
            {
                DestroyIcon(large);
                if (small != IntPtr.Zero) DestroyIcon(small);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>直接从文件的图标资源里提取（绕开 Shell 缓存/类型关联），失败返回 null。</summary>
    private static ImageSource? ExtractFromFile(string path)
    {
        try
        {
            using var src = System.Drawing.Icon.ExtractAssociatedIcon(path);
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

    private static ImageSource Fallback(bool isDirectory)
        => isDirectory
            ? _fallbackFolder ??= BuildFallback(true)
            : _fallbackFile ??= BuildFallback(false);

    private static ImageSource BuildFallback(bool isDirectory)
    {
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
