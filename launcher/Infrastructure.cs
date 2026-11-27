using System.Collections.Concurrent;

namespace GlazeLauncher;

internal static class Paths
{
    public static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlazeLauncher");

    static Paths() => Directory.CreateDirectory(DataDir);

    public static string File(string name) => Path.Combine(DataDir, name);
}

/// <summary>Tiny append-only log in %LOCALAPPDATA%\GlazeLauncher\launcher.log (trimmed at startup).</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static readonly string LogPath = Paths.File("launcher.log");

    public static void Init()
    {
        try
        {
            var fi = new FileInfo(LogPath);
            if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
        }
        catch { /* ignore */ }
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Error(string msg, Exception? e = null) => Write("ERROR", e is null ? msg : $"{msg}: {e}");

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Gate) System.IO.File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {msg}{Environment.NewLine}");
        }
        catch { /* logging must never throw */ }
    }
}

/// <summary>A single STA thread for shell COM work (AppsFolder enumeration, icon extraction).</summary>
internal sealed class StaWorker
{
    private readonly BlockingCollection<Action> _queue = new();

    public StaWorker(string name)
    {
        var t = new Thread(Run) { IsBackground = true, Name = name, Priority = ThreadPriority.BelowNormal };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    private void Run()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try { work(); }
            catch (Exception e) { Log.Error("sta worker", e); }
        }
    }

    public void Post(Action work) => _queue.Add(work);

    public Task<T> Invoke<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception e) { tcs.SetException(e); }
        });
        return tcs.Task;
    }
}
