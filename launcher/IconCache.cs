using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlazeLauncher;

/// <summary>
/// Loads shell icons (for AppsFolder items, exe paths, or known folders) on the shared STA worker and caches
/// them as frozen bitmaps, so the UI thread never touches the shell.
/// </summary>
internal sealed class IconCache(StaWorker worker)
{
    private const int IconPx = 48; // rendered at 26 DIP; 48px stays crisp up to ~180% scaling

    private readonly object _gate = new();
    private readonly Dictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Action<ImageSource>>> _waiting = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGet(string parsingPath, out ImageSource? icon, Guid? knownFolderId = null)
    {
        lock (_gate) return _cache.TryGetValue(CacheKey(parsingPath, knownFolderId), out icon);
    }

    /// <summary>Calls <paramref name="onLoaded"/> (on the loader thread) once the icon is ready.</summary>
    public void Request(string parsingPath, Action<ImageSource>? onLoaded = null, Guid? knownFolderId = null)
    {
        var key = CacheKey(parsingPath, knownFolderId);
        ImageSource? cached;
        lock (_gate)
        {
            if (!_cache.TryGetValue(key, out cached))
            {
                if (!_waiting.TryGetValue(key, out var callbacks))
                {
                    _waiting[key] = callbacks = [];
                    worker.Post(() => LoadAndPublish(key, parsingPath, knownFolderId));
                }
                if (onLoaded is not null) callbacks.Add(onLoaded);
                return;
            }
        }
        if (cached is not null) onLoaded?.Invoke(cached);
    }

    private void LoadAndPublish(string key, string parsingPath, Guid? knownFolderId)
    {
        ImageSource? image = null;
        try { image = Load(parsingPath, knownFolderId); }
        catch (Exception error) { Log.Error($"icon {parsingPath}", error); }

        List<Action<ImageSource>>? callbacks;
        lock (_gate)
        {
            _cache[key] = image;
            _waiting.Remove(key, out callbacks);
        }
        if (image is not null && callbacks is not null)
            foreach (var callback in callbacks) callback(image);
    }

    private static string CacheKey(string parsingPath, Guid? knownFolderId) => knownFolderId is { } folderId
        ? $"known-folder:{folderId:D}|{parsingPath}"
        : parsingPath;

    private static ImageSource? Load(string parsingPath, Guid? knownFolderId)
    {
        var iid = typeof(Native.IShellItemImageFactory).GUID;
        if (knownFolderId is { } folderId
            && Native.SHGetKnownFolderItem(in folderId, 0, IntPtr.Zero, in iid, out var folder) == 0 && folder is not null
            && GetIcon(folder, parsingPath) is { } icon)
            return icon;

        if (Native.SHCreateItemFromParsingName(parsingPath, IntPtr.Zero, ref iid, out var factory) != 0 || factory is null)
            return null;
        return GetIcon(factory, parsingPath);
    }

    private static ImageSource? GetIcon(Native.IShellItemImageFactory factory, string parsingPath)
    {
        IntPtr hbm = IntPtr.Zero;
        try
        {
            var size = new Native.SIZE { cx = IconPx, cy = IconPx };
            if (factory.GetImage(size, Native.SIIGBF_BIGGERSIZEOK | Native.SIIGBF_ICONONLY, out hbm) != 0 || hbm == IntPtr.Zero)
                return null;
            return FromHBitmap(hbm);
        }
        catch (Exception e)
        {
            Log.Error($"icon {parsingPath}", e);
            return null;
        }
        finally
        {
            if (hbm != IntPtr.Zero) Native.DeleteObject(hbm);
            System.Runtime.InteropServices.Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>Copies a 32bpp HBITMAP into a frozen BitmapSource, keeping its alpha channel
    /// (Imaging.CreateBitmapSourceFromHBitmap drops it).</summary>
    private static BitmapSource? FromHBitmap(IntPtr hbm)
    {
        if (Native.GetObject(hbm, System.Runtime.InteropServices.Marshal.SizeOf<Native.BITMAP>(), out var bmp) == 0) return null;
        int w = bmp.bmWidth, h = Math.Abs(bmp.bmHeight);
        if (w <= 0 || h <= 0) return null;

        var bi = new Native.BITMAPINFOHEADER
        {
            biSize = 40, biWidth = w, biHeight = -h /* top-down */, biPlanes = 1, biBitCount = 32, biCompression = 0,
        };
        var pixels = new byte[w * h * 4];
        var hdc = Native.GetDC(IntPtr.Zero);
        try
        {
            if (Native.GetDIBits(hdc, hbm, 0, (uint)h, pixels, ref bi, 0) == 0) return null;
        }
        finally { Native.ReleaseDC(IntPtr.Zero, hdc); }

        bool anyAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        var src = BitmapSource.Create(w, h, 96, 96, anyAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgra32, null, pixels, w * 4);
        src.Freeze();
        return src;
    }
}
