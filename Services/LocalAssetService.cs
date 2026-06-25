using System.IO;

namespace SteamRoulette.Services;

/// <summary>
/// Looks up game images that are already installed locally by Steam,
/// so we never need to download something the user already has on disk.
///
/// Steam stores two relevant sets of local assets:
///
///   Header images (library capsules):
///     {steamPath}\appcache\librarycache\{appId}_header.jpg       ← preferred
///     {steamPath}\appcache\librarycache\{appId}_library_hero.jpg
///     {steamPath}\appcache\librarycache\{appId}_library_600x900.jpg
///     {steamPath}\appcache\librarycache\{appId}_logo.png
///
///   Game icons:
///     {steamPath}\steam\games\{iconHash}.ico    ← installed by the game
///     {steamPath}\steam\games\{iconHash}.jpg
///     {steamPath}\steam\games\{iconHash}.png
///
/// All paths are relative to the Steam install root (e.g. C:\Program Files (x86)\Steam).
/// </summary>
public static class LocalAssetService
{
    // -------------------------------------------------------------------------
    // Header image lookup
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns a path to a locally cached header/library image for the given
    /// appId, or null if Steam has not downloaded it yet.
    /// </summary>
    public static string? FindLocalHeader(string steamPath, string appId)
    {
        if (string.IsNullOrEmpty(steamPath) || string.IsNullOrEmpty(appId))
            return null;

        var libraryCache = Path.Combine(steamPath, "appcache", "librarycache");
        if (!Directory.Exists(libraryCache))
            return null;

        // Steam writes several sizes; prefer the landscape header.
        var candidates = new[]
        {
            Path.Combine(libraryCache, $"{appId}_header.jpg"),
            Path.Combine(libraryCache, $"{appId}_library_hero.jpg"),
            Path.Combine(libraryCache, $"{appId}_library_600x900.jpg"),
            Path.Combine(libraryCache, $"{appId}_logo.png"),
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                Logger.Log($"[Local] Found header for appId {appId}: {Path.GetFileName(path)}");
                return path;
            }
        }

        return null;
    }

    // -------------------------------------------------------------------------
    // Icon lookup
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns a path to the locally installed game icon (.ico / .jpg / .png),
    /// or null if Steam hasn't cached it.
    /// iconHash is the value from the ACF manifest (img_icon_url field).
    /// </summary>
    public static string? FindLocalIcon(string steamPath, string iconHash)
    {
        if (string.IsNullOrEmpty(steamPath) || string.IsNullOrEmpty(iconHash))
            return null;

        // Strip any extension the hash might already carry
        var hashBase = Path.GetFileNameWithoutExtension(iconHash);
        var gamesDir = Path.Combine(steamPath, "steam", "games");

        if (!Directory.Exists(gamesDir))
            return null;

        // Steam stores icons as .ico; some games ship .jpg/.png
        var candidates = new[]
        {
            Path.Combine(gamesDir, $"{hashBase}.ico"),
            Path.Combine(gamesDir, $"{hashBase}.jpg"),
            Path.Combine(gamesDir, $"{hashBase}.png"),
            Path.Combine(gamesDir, iconHash),          // already has extension
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                Logger.Log($"[Local] Found icon {hashBase}: {Path.GetFileName(path)}");
                return path;
            }
        }

        return null;
    }

    // -------------------------------------------------------------------------
    // Copy a local asset into our cache directory
    // -------------------------------------------------------------------------

    /// <summary>
    /// Copies a local file into the image cache directory, returning the
    /// destination path. Returns null if the copy fails.
    /// Skips the copy if the destination already exists.
    /// </summary>
    public static async Task<string?> CopyToCacheAsync(
        string sourcePath, string cacheDir, string destFileName)
    {
        var dest = Path.Combine(cacheDir, destFileName);
        if (File.Exists(dest)) return dest;

        try
        {
            var bytes = await File.ReadAllBytesAsync(sourcePath).ConfigureAwait(false);

            // Use a FileStream with FileShare.None so two threads can't write
            // the same file at the same time. If we lose the race, the other
            // writer already wrote valid bytes — just return the path.
            await using var fs = new FileStream(
                dest, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, useAsync: true);
            await fs.WriteAsync(bytes).ConfigureAwait(false);

            Logger.Log($"[Local] Copied to cache: {destFileName}");
            return dest;
        }
        catch (IOException) when (File.Exists(dest))
        {
            // Another task won the race and already wrote the file — that's fine
            return dest;
        }
        catch (Exception ex)
        {
            Logger.Log($"[Local] Copy failed for {destFileName}: {ex.Message}");
            return null;
        }
    }
}
