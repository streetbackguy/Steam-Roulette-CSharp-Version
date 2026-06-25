using System.IO;
using Newtonsoft.Json;

namespace SteamRoulette.Services;

public static class SettingsService
{
    private static string ExeDir =>
        Path.GetDirectoryName(
            System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
            ?? AppContext.BaseDirectory)
        ?? AppContext.BaseDirectory;

    private static string DataPath(string filename) => Path.Combine(ExeDir, filename);

    public static string LoadTextFile(string filename)
    {
        var path = DataPath(filename);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
    }

    public static void SaveTextFile(string filename, string content) =>
        File.WriteAllText(DataPath(filename), content);

    public static string CacheDir
    {
        get
        {
            var dir = Path.Combine(ExeDir, AppConstants.ImageCacheSubdir);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    // ── Exclusions ───────────────────────────────────────────────────────────
    public static List<string> LoadExclusions()
    {
        var path = DataPath("excluded_games.json");
        if (!File.Exists(path)) return new();
        try
        {
            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<List<string>>(json) ?? new();
        }
        catch (Exception ex)
        {
            Logger.Log($"Error loading exclusions: {ex.Message}");
            return new();
        }
    }

    public static void SaveExclusions(IEnumerable<string> exclusions)
    {
        try
        {
            File.WriteAllText(DataPath("excluded_games.json"),
                JsonConvert.SerializeObject(exclusions.ToList()));
        }
        catch (Exception ex)
        {
            Logger.Log($"Error saving exclusions: {ex.Message}");
        }
    }

    // ── Image cache ──────────────────────────────────────────────────────────
    public static int ClearImageCache()
    {
        int count = 0;
        foreach (var f in Directory.GetFiles(CacheDir, "*.jpg")
                                    .Concat(Directory.GetFiles(CacheDir, "*.png")))
        {
            try { File.Delete(f); count++; } catch { }
        }
        return count;
    }

    /// <summary>
    /// Deletes only icon_ prefixed cache files so they are re-fetched
    /// with the correct URL priority (hash-based icon first).
    /// </summary>
    public static int ClearIconCache()
    {
        int count = 0;
        foreach (var f in Directory.GetFiles(CacheDir, "icon_*.png")
                                    .Concat(Directory.GetFiles(CacheDir, "icon_*.jpg")))
        {
            try { File.Delete(f); count++; } catch { }
        }
        if (count > 0) Logger.Log($"[Cache] Cleared {count} stale icon file(s).");
        return count;
    }
}
