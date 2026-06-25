using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;

namespace SteamRoulette.Services;

public static class ImageService
{
    private static readonly HttpClient Http = new()
    {
        DefaultRequestHeaders = { { "User-Agent", "SteamRoulette/1.0" } },
        Timeout = TimeSpan.FromSeconds(15),
    };

    // Per-file semaphores prevent two concurrent tasks from writing the same
    // cache file simultaneously (e.g. preload + ExcludeGamesWindow both
    // requesting the same icon at the same time).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>
        _fileLocks = new();

    private static SemaphoreSlim LockFor(string path) =>
        _fileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));

    // -------------------------------------------------------------------------
    // Header image — returns the local cache file path (not a decoded bitmap).
    //
    // Fallback order:
    //   1. Our cache dir (previous run)
    //   2. Local Steam appcache\librarycache\   ← new
    //   3. Steam CDN (cloudflare → akamai)
    //   4. SGDB official Valve-uploaded assets  ← new
    //   5. SGDB community grids
    // -------------------------------------------------------------------------
    public static async Task<string?> EnsureHeaderImageAsync(
        string appId, string cacheDir, string gameName = "",
        string steamPath = "", SteamGridDbService? sgdb = null)
    {
        string label     = gameName.Length > 0 ? $"{gameName} ({appId})" : appId;
        string cacheFile = Path.Combine(cacheDir, $"{appId}.jpg");

        // Fast path — already cached (checked before acquiring the lock)
        if (File.Exists(cacheFile)) return cacheFile;

        // Serialise concurrent writes to the same cache file
        var sem = LockFor(cacheFile);
        await sem.WaitAsync().ConfigureAwait(false);
        try
        {
        // ── 1. Re-check inside lock (another task may have written it) ─────
        if (File.Exists(cacheFile)) return cacheFile;

        // ── 2. Local Steam appcache ───────────────────────────────────────────
        var localPath = LocalAssetService.FindLocalHeader(steamPath, appId);
        if (localPath != null)
        {
            // Copy into our cache so subsequent loads bypass the local search
            var copied = await LocalAssetService.CopyToCacheAsync(
                localPath, cacheDir, $"{appId}.jpg").ConfigureAwait(false);
            if (copied != null) return copied;
        }

        // ── 3. Steam CDN ──────────────────────────────────────────────────────
        var steamUrls = new[]
        {
            $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg",
            $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg",
            $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/capsule_616x353.jpg",
            $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/page_bg.jpg",
        };

        foreach (var url in steamUrls)
        {
            var bytes = await TryDownloadAsync(url).ConfigureAwait(false);
            if (bytes != null)
            {
                await File.WriteAllBytesAsync(cacheFile, bytes).ConfigureAwait(false);
                Logger.Log($"[Steam] Downloaded header: {label}");
                return cacheFile;
            }
        }

        if (sgdb != null)
        {
            // ── 4. SGDB official Valve-uploaded assets ────────────────────────
            Logger.Log($"[Image] Steam CDN failed for {label} — trying SGDB official assets…");
            var officialBytes = await sgdb.FetchOfficialHeaderBytesAsync(appId, gameName)
                                          .ConfigureAwait(false);
            if (officialBytes != null)
            {
                await File.WriteAllBytesAsync(cacheFile, officialBytes).ConfigureAwait(false);
                Logger.Log($"[SGDB-Official] Saved header: {label}");
                return cacheFile;
            }

            // ── 5. SGDB community grids ───────────────────────────────────────
            Logger.Log($"[Image] Official assets failed for {label} — trying SGDB community grids…");
            var sgdbBytes = await sgdb.FetchHeaderImageBytesAsync(appId, gameName)
                                      .ConfigureAwait(false);
            if (sgdbBytes != null)
            {
                await File.WriteAllBytesAsync(cacheFile, sgdbBytes).ConfigureAwait(false);
                Logger.Log($"[SGDB] Saved header: {label}");
                return cacheFile;
            }
        }

        Logger.Log($"[Image] All sources failed for {label}.");
        return null;
        } // end try
        finally { sem.Release(); }
    }

    // -------------------------------------------------------------------------
    // Small icon — same pattern: returns path or null
    //
    // Fallback order:
    //   1. Our cache dir (previous run)
    //   2. Local Steam steam\games\{hash}.ico   ← new
    //   3. Steam CDN hash-based URL
    //   4. SGDB official icon assets             ← new
    //   5. SGDB community icons
    //   6. Steam CDN capsule_sm_120 (landscape banner, last resort)
    // -------------------------------------------------------------------------
    public static async Task<string?> EnsureIconAsync(
        string appId, string iconHash, string cacheDir,
        string gameName = "", string steamPath = "",
        SteamGridDbService? sgdb = null)
    {
        if (string.IsNullOrEmpty(appId)) return null;

        string label     = gameName.Length > 0 ? $"{gameName} ({appId})" : appId;
        string cacheFile = Path.Combine(cacheDir, $"icon_{appId}.png");

        if (File.Exists(cacheFile)) return cacheFile;

        var sem = LockFor(cacheFile);
        await sem.WaitAsync().ConfigureAwait(false);
        try
        {
        // ── 1. Re-check inside lock ───────────────────────────────────────────
        if (File.Exists(cacheFile)) return cacheFile;

        // ── 2. Local Steam steam\games\ ───────────────────────────────────────
        if (!string.IsNullOrEmpty(iconHash))
        {
            var localIcon = LocalAssetService.FindLocalIcon(steamPath, iconHash);
            if (localIcon != null)
            {
                // .ico files need converting to a PNG/JPEG WPF can decode directly.
                // We extract via WPF's BitmapDecoder then re-encode as PNG in memory.
                var converted = await ConvertIconToCacheAsync(
                    localIcon, cacheFile, label).ConfigureAwait(false);
                if (converted != null) return converted;
            }
        }

        // ── 3. Steam CDN hash-based URL ───────────────────────────────────────
        if (!string.IsNullOrEmpty(iconHash))
        {
            var base_ = $"https://media.steampowered.com/steamcommunity/public/images/apps/{appId}/{iconHash}";
            foreach (var url in new[] { base_, base_ + ".jpg", base_ + ".png" })
            {
                var bytes = await TryDownloadAsync(url).ConfigureAwait(false);
                if (bytes != null)
                {
                    await File.WriteAllBytesAsync(cacheFile, bytes).ConfigureAwait(false);
                    Logger.Log($"[Steam] Downloaded icon: {label}");
                    return cacheFile;
                }
            }
        }

        if (sgdb != null)
        {
            // ── 4. SGDB official icon assets ──────────────────────────────────
            Logger.Log($"[Icon] Steam CDN failed for {label} — trying SGDB official assets…");
            var officialBytes = await sgdb.FetchOfficialIconBytesAsync(appId, gameName)
                                          .ConfigureAwait(false);
            if (officialBytes != null)
            {
                await File.WriteAllBytesAsync(cacheFile, officialBytes).ConfigureAwait(false);
                Logger.Log($"[SGDB-Official] Saved icon: {label}");
                return cacheFile;
            }

            // ── 5. SGDB community icons ───────────────────────────────────────
            Logger.Log($"[Icon] Official assets failed for {label} — trying SGDB community icons…");
            var sgdbBytes = await sgdb.FetchIconBytesAsync(appId, gameName)
                                      .ConfigureAwait(false);
            if (sgdbBytes != null)
            {
                await File.WriteAllBytesAsync(cacheFile, sgdbBytes).ConfigureAwait(false);
                Logger.Log($"[SGDB] Saved icon: {label}");
                return cacheFile;
            }
        }

        // ── 6. Capsule banner (last resort — landscape art, not a true icon) ──
        foreach (var url in new[]
        {
            $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/capsule_sm_120.jpg",
            $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/capsule_sm_120.jpg",
        })
        {
            var bytes = await TryDownloadAsync(url).ConfigureAwait(false);
            if (bytes != null)
            {
                await File.WriteAllBytesAsync(cacheFile, bytes).ConfigureAwait(false);
                Logger.Log($"[Steam] Downloaded capsule fallback icon: {label}");
                return cacheFile;
            }
        }

        Logger.Log($"[Icon] All sources failed for {label}.");
        return null;
        } // end try
        finally { sem.Release(); }
    }

    // -------------------------------------------------------------------------
    // .ico → PNG conversion
    // WPF's BitmapDecoder handles multi-size .ico files natively. We pick the
    // largest frame and encode it as PNG so it can be cached and reloaded without
    // needing the System.Drawing/WinForms GDI stack.
    // -------------------------------------------------------------------------
    private static async Task<string?> ConvertIconToCacheAsync(
        string icoPath, string destPath, string label)
    {
        try
        {
            return await Task.Run(() =>
            {
                using var stream = File.OpenRead(icoPath);

                BitmapDecoder decoder;
                try { decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad); }
                catch { return null; }

                if (decoder.Frames.Count == 0) return null;

                // Pick the largest frame (best quality)
                var frame = decoder.Frames
                    .OrderByDescending(f => f.PixelWidth * f.PixelHeight)
                    .First();

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(frame));

                using var outStream = File.Open(destPath, FileMode.Create, FileAccess.Write);
                encoder.Save(outStream);

                Logger.Log($"[Local] Converted .ico → PNG: {label}");
                return destPath;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Log($"[Local] ico conversion failed for {label}: {ex.Message}");
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Decode helpers — used by callers that need a BitmapSource immediately
    // -------------------------------------------------------------------------
    public static BitmapSource LoadBitmapFromPath(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption   = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.None;
        bmp.UriSource     = new Uri(path, UriKind.Absolute);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    public static BitmapSource LoadBitmapFromBytes(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption  = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    public static BitmapSource CreatePlaceholderBitmap(int w = 600, int h = 300)
    {
        var bmp    = new WriteableBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null);
        var pixels = new int[w * h];
        Array.Fill(pixels, unchecked((int)0xFF282828));
        bmp.WritePixels(new System.Windows.Int32Rect(0, 0, w, h), pixels, w * 4, 0);
        bmp.Freeze();
        return bmp;
    }

    public static BitmapSource CreatePlaceholderIcon(int size = 20)
    {
        var bmp    = new WriteableBitmap(size, size, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        var pixels = new int[size * size];
        Array.Fill(pixels, unchecked((int)0xDC3C3C41));
        bmp.WritePixels(new System.Windows.Int32Rect(0, 0, size, size), pixels, size * 4, 0);
        bmp.Freeze();
        return bmp;
    }

    // -------------------------------------------------------------------------
    // HTTP helpers
    // -------------------------------------------------------------------------
    private static async Task<byte[]?> TryDownloadAsync(string url)
    {
        try
        {
            using var resp = await Http.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                if (resp.StatusCode != System.Net.HttpStatusCode.NotFound)
                    Logger.Log($"[HTTP] {(int)resp.StatusCode} — {url}");
                return null;
            }

            // Reject HTML error pages (Steam CDN returns 200 + HTML for missing images)
            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                return null;

            var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

            if (bytes.Length < 256 || !IsImageBytes(bytes))
                return null;

            return bytes;
        }
        catch (Exception ex)
        {
            Logger.Log($"[HTTP] Error fetching {url}: {ex.Message}");
            return null;
        }
    }

    private static bool IsImageBytes(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return true; // JPEG
        if (b.Length >= 4 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return true; // PNG
        if (b.Length >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46
                           && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return true; // WebP
        return false;
    }
}
