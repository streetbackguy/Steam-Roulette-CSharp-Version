using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SteamRoulette.Models;

namespace SteamRoulette.Services;

public static class SteamService
{
    // -------------------------------------------------------------------------
    // Steam path discovery
    // -------------------------------------------------------------------------
    public static string? GetSteamInstallPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") as string;
        }
        catch { return null; }
    }

    public static string? FindSteamPathFallback()
    {
        var candidates = new[]
        {
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steam"),
        };
        return candidates.FirstOrDefault(p => File.Exists(Path.Combine(p, "steam.exe")));
    }

    // -------------------------------------------------------------------------
    // VDF / ACF parsing  (simple regex-based parser — no external lib needed
    //                     for the flat key-value pairs we need here)
    // -------------------------------------------------------------------------

    /// <summary>Returns library paths from libraryfolders.vdf</summary>
    public static List<string> ParseLibraryFolders(string vdfPath)
    {
        var paths = new List<string>();
        if (!File.Exists(vdfPath)) return paths;

        try
        {
            var text = File.ReadAllText(vdfPath);
            // Match "path" entries inside numbered blocks
            var matches = Regex.Matches(text,
                @"""path""\s+""([^""]+)""",
                RegexOptions.IgnoreCase);
            foreach (Match m in matches)
            {
                var p = m.Groups[1].Value.Replace(@"\\", @"\");
                if (!string.IsNullOrWhiteSpace(p))
                    paths.Add(p);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Error parsing libraryfolders.vdf: {ex.Message}");
        }

        return paths;
    }

    /// <summary>Parse a single appmanifest_*.acf file and return a GameInfo.</summary>
    public static GameInfo? ParseAcfFile(string acfPath, string libraryPath)
    {
        try
        {
            var text = File.ReadAllText(acfPath);
            var appId = ExtractVdfValue(text, "appid");
            var name  = ExtractVdfValue(text, "name");

            if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(name))
                return null;

            return new GameInfo
            {
                AppId       = appId,
                Name        = name,
                LibraryPath = libraryPath,
                IsInstalled = true,
                // SteamPath is set by the caller (GetInstalledGames) which knows
                // the root Steam dir separate from the per-library path
            };
        }
        catch (Exception ex)
        {
            Logger.Log($"Error reading ACF {acfPath}: {ex.Message}");
            return null;
        }
    }

    private static string? ExtractVdfValue(string text, string key)
    {
        var m = Regex.Match(text,
            $@"""{Regex.Escape(key)}""\s+""([^""]+)""",
            RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    // -------------------------------------------------------------------------
    // Enumerate installed games
    // -------------------------------------------------------------------------
    public static List<GameInfo> GetInstalledGames(string steamPath)
    {
        var result  = new List<GameInfo>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var vdfPath  = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        var libPaths = ParseLibraryFolders(vdfPath);

        // Always include the Steam install folder itself
        if (!libPaths.Contains(steamPath, StringComparer.OrdinalIgnoreCase))
            libPaths.Insert(0, steamPath);

        foreach (var libPath in libPaths)
        {
            var steamappsDir = Path.Combine(libPath, "steamapps");
            if (!Directory.Exists(steamappsDir)) continue;

            foreach (var acf in Directory.GetFiles(steamappsDir, "appmanifest_*.acf"))
            {
                var game = ParseAcfFile(acf, libPath);
                if (game == null) continue;
                game.SteamPath = steamPath;   // root Steam dir for local asset lookup

                if (AppConstants.NonGameAppIds.Contains(game.AppId))
                {
                    Logger.Log($"Skipping non-game: {game.Name} ({game.AppId})");
                    continue;
                }

                if (seenIds.Contains(game.AppId))
                {
                    Logger.Log($"Skipping duplicate: {game.Name} ({game.AppId})");
                    continue;
                }

                seenIds.Add(game.AppId);
                result.Add(game);
            }
        }

        return result;
    }

    // -------------------------------------------------------------------------
    // Drive enumeration
    // -------------------------------------------------------------------------
    public static List<string> GetDrives() =>
        DriveInfo.GetDrives()
                 .Where(d => d.IsReady)
                 .Select(d => d.RootDirectory.FullName)
                 .ToList();
}
