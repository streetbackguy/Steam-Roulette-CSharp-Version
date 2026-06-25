using System.Windows.Media.Imaging;

namespace SteamRoulette.Services;

/// <summary>
/// Two-level image cache:
///   Level 1 — file paths on disk (persists across sessions, unlimited size).
///   Level 2 — decoded BitmapSource objects in memory (LRU, capped at maxDecoded).
///
/// This keeps memory proportional to what's actually on screen rather than
/// to the total library size. A 200-game library fully pre-loaded used to hold
/// ~200 × 600×300×4 bytes ≈ 144 MB of decoded bitmaps. With an LRU cap of 30
/// that ceiling drops to ~22 MB regardless of library size.
/// </summary>
public sealed class ImageCache
{
    private readonly int _maxDecoded;

    // Disk paths — populated during pre-load, never evicted
    private readonly Dictionary<string, string> _paths = new();

    // Decoded bitmaps — LRU eviction
    private readonly Dictionary<string, BitmapSource> _decoded = new();
    private readonly LinkedList<string> _lruOrder = new();

    public ImageCache(int maxDecoded = 30)
    {
        _maxDecoded = maxDecoded;
    }

    // -------------------------------------------------------------------------
    // Register a cached file path (called once the file is on disk)
    // -------------------------------------------------------------------------
    public void RegisterPath(string appId, string filePath)
    {
        lock (_paths) _paths[appId] = filePath;
    }

    public bool HasPath(string appId)
    {
        lock (_paths) return _paths.ContainsKey(appId);
    }

    public string? GetPath(string appId)
    {
        lock (_paths) return _paths.TryGetValue(appId, out var p) ? p : null;
    }

    // -------------------------------------------------------------------------
    // Get a decoded bitmap — loads from disk if not in the decoded cache
    // -------------------------------------------------------------------------
    public BitmapSource? GetDecoded(string appId)
    {
        lock (_decoded)
        {
            if (_decoded.TryGetValue(appId, out var bmp))
            {
                // Move to front of LRU
                _lruOrder.Remove(appId);
                _lruOrder.AddFirst(appId);
                return bmp;
            }
        }

        // Not decoded yet — load from disk path if available
        string? path;
        lock (_paths) _paths.TryGetValue(appId, out path);

        if (path == null || !System.IO.File.Exists(path))
            return null;

        try
        {
            var bmp = LoadBitmapFromPath(path);
            StoreDecoded(appId, bmp);
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Store a freshly-decoded bitmap, evicting LRU entries if over cap
    // -------------------------------------------------------------------------
    public void StoreDecoded(string appId, BitmapSource bmp)
    {
        lock (_decoded)
        {
            if (_decoded.ContainsKey(appId))
            {
                _lruOrder.Remove(appId);
            }
            else if (_decoded.Count >= _maxDecoded)
            {
                // Evict least-recently-used
                var lru = _lruOrder.Last!.Value;
                _lruOrder.RemoveLast();
                _decoded.Remove(lru);
                Logger.Log($"[Cache] Evicted decoded bitmap for appId {lru}");
            }

            _decoded[appId] = bmp;
            _lruOrder.AddFirst(appId);
        }
    }

    // -------------------------------------------------------------------------
    // Clear decoded bitmaps only (paths stay so disk cache still works)
    // -------------------------------------------------------------------------
    public void ClearDecoded()
    {
        lock (_decoded)
        {
            _decoded.Clear();
            _lruOrder.Clear();
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------
    private static BitmapSource LoadBitmapFromPath(string path)
    {
        // Use BitmapCreateOptions.None + BitmapCacheOption.OnLoad so the file
        // handle is released immediately and the decoded pixels are held by the
        // BitmapImage itself. This avoids holding the stream open.
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption  = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.None;
        bmp.UriSource    = new Uri(path, UriKind.Absolute);
        bmp.EndInit();
        bmp.Freeze();   // makes it shareable across threads without marshalling
        return bmp;
    }

    public int DecodedCount { get { lock (_decoded) return _decoded.Count; } }
    public int PathCount    { get { lock (_paths)   return _paths.Count;   } }
}
