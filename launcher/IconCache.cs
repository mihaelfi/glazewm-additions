using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlazeLauncher;

/// <summary>
/// Loads shell icons (for AppsFolder items or exe paths) on the shared STA worker and caches
/// them as frozen bitmaps, so the UI thread never touches the shell.
/// </summary>
internal sealed class IconCache(StaWorker worker)
{
    private const int IconPx = 48; // rendered at 26 DIP; 48px stays crisp up to ~180% scaling

    private readonly ConcurrentDictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<Action<ImageSource>>> _waiting = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGet(string parsingPath, out ImageSource? icon) => _cache.TryGetValue(parsingPath, out icon);

    /// <summary>Calls <paramref name="onLoaded"/> (on the loader thread) once the icon is ready.</summary>
    public void Request(string parsingPath, Action<ImageSource>? onLoaded = null)
    {
        if (_cache.TryGetValue(parsingPath, out var cached))
        {
            if (cached is not null) onLoaded?.Invoke(cached);
            return;
        }

        bool first = false;
        var list = _waiting.GetOrAdd(parsingPath, _ => { first = true; return new List<Action<ImageSource>>(); });
        lock (list) if (onLoaded is not null) list.Add(onLoaded);
        if (!first) return;

        worker.Post(() =>
        {
            var img = Load(parsingPath);
            _cache[parsingPath] = img;
            if (_waiting.TryRemove(parsingPath, out var callbacks) && img is not null)
            {
                lock (callbacks) foreach (var cb in callbacks) cb(img);
            }
        });
    }

    private static ImageSource? Load(string parsingPath)
    {
        var iid = typeof(Native.IShellItemImageFactory).GUID;
        if (Native.SHCreateItemFromParsingName(parsingPath, IntPtr.Zero, ref iid, out var factory) != 0 || factory is null)
            return null;
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
