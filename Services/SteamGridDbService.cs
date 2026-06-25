using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SteamRoulette.Services;

/// <summary>
/// Wraps the SteamGridDB v2 REST API.
/// API key is stored in sgdb_apikey.txt next to the exe.
/// Docs: https://www.steamgriddb.com/api/v2
/// </summary>
public class SteamGridDbService
{
    private readonly HttpClient _http;

    // Plain HttpClient with no auth header — used to download the actual image
    // bytes from SGDB's CDN. Sending the Bearer token to the CDN causes 401s.
    private static readonly HttpClient CdnHttp = new()
    {
        DefaultRequestHeaders = { { "User-Agent", "SteamRoulette/1.0" } },
        Timeout = TimeSpan.FromSeconds(20),
    };

    public SteamGridDbService(string apiKey)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://www.steamgriddb.com/api/v2/"),
            Timeout     = TimeSpan.FromSeconds(15),
        };
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", apiKey);
        _http.DefaultRequestHeaders.Add("User-Agent", "SteamRoulette/1.0");
    }

    // -------------------------------------------------------------------------
    // Official Steam assets via SGDB (/assets/ endpoint)
    // These are the images Valve themselves uploaded — identical to what Steam
    // shows in your library. Separate from community grids/icons.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fetches the official Valve-uploaded header image for the game via SGDB's
    /// asset proxy. Returns null if SGDB has no record or the download fails.
    /// </summary>
    public async Task<byte[]?> FetchOfficialHeaderBytesAsync(string steamAppId, string gameName = "")
    {
        string label = gameName.Length > 0 ? $"{gameName} ({steamAppId})" : steamAppId;
        try
        {
            int? sgdbId = await GetSgdbIdAsync(steamAppId).ConfigureAwait(false);
            if (sgdbId == null) return null;

            // /assets/game/{id} returns the official Steam store/library images.
            // type filter: header = landscape banner; capsule = portrait box art
            var assets = await GetAsync<SgdbListResponse>(
                $"assets/game/{sgdbId}?type=header&limit=3")
                .ConfigureAwait(false);

            var url = assets?.Data?.FirstOrDefault(x => x.Url != null)?.Url;
            if (url == null)
            {
                // Try without type filter — some older entries only have one asset type
                assets = await GetAsync<SgdbListResponse>(
                    $"assets/game/{sgdbId}?limit=5")
                    .ConfigureAwait(false);
                url = assets?.Data?.FirstOrDefault(x => x.Url != null)?.Url;
            }

            if (url == null)
            {
                Logger.Log($"[SGDB-Official] No header assets for {label}");
                return null;
            }

            return await DownloadBytesAsync(url, label, "[SGDB-Official-Header]").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Log($"[SGDB-Official] Header error for {label}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Fetches the official Valve-uploaded icon for the game via SGDB's asset proxy.
    /// </summary>
    public async Task<byte[]?> FetchOfficialIconBytesAsync(string steamAppId, string gameName = "")
    {
        string label = gameName.Length > 0 ? $"{gameName} ({steamAppId})" : steamAppId;
        try
        {
            int? sgdbId = await GetSgdbIdAsync(steamAppId).ConfigureAwait(false);
            if (sgdbId == null) return null;

            var assets = await GetAsync<SgdbListResponse>(
                $"assets/game/{sgdbId}?type=icon&limit=3")
                .ConfigureAwait(false);

            var url = assets?.Data?.FirstOrDefault(x => x.Url != null)?.Url;
            if (url == null)
            {
                Logger.Log($"[SGDB-Official] No icon assets for {label}");
                return null;
            }

            return await DownloadBytesAsync(url, label, "[SGDB-Official-Icon]").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Log($"[SGDB-Official] Icon error for {label}: {ex.Message}");
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Header / grid image (community)
    // -------------------------------------------------------------------------
    public async Task<byte[]?> FetchHeaderImageBytesAsync(string steamAppId, string gameName = "")
    {
        string label = gameName.Length > 0 ? $"{gameName} ({steamAppId})" : steamAppId;
        try
        {
            int? sgdbId = await GetSgdbIdAsync(steamAppId).ConfigureAwait(false);
            if (sgdbId == null)
            {
                Logger.Log($"[SGDB] No game found for {label}");
                return null;
            }

            // Prefer landscape grid (920x430 or 460x215), fall back to portrait (600x900)
            foreach (var dims in new[] { "920x430,460x215", "600x900" })
            {
                var grids = await GetAsync<SgdbListResponse>(
                    $"grids/game/{sgdbId}?dimensions={dims}&limit=5")
                    .ConfigureAwait(false);

                var url = grids?.Data?.FirstOrDefault(x => x.Url != null)?.Url;
                if (url != null)
                    return await DownloadBytesAsync(url, label, "[SGDB-Grid]").ConfigureAwait(false);
            }

            Logger.Log($"[SGDB] No grids found for {label}");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Log($"[SGDB] Header error for {label}: {ex.Message}");
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Icon image
    // -------------------------------------------------------------------------
    public async Task<byte[]?> FetchIconBytesAsync(string steamAppId, string gameName = "")
    {
        string label = gameName.Length > 0 ? $"{gameName} ({steamAppId})" : steamAppId;
        try
        {
            int? sgdbId = await GetSgdbIdAsync(steamAppId).ConfigureAwait(false);
            if (sgdbId == null) return null;

            var icons = await GetAsync<SgdbListResponse>(
                $"icons/game/{sgdbId}?limit=3")
                .ConfigureAwait(false);

            var url = icons?.Data?.FirstOrDefault(x => x.Url != null)?.Url;
            if (url == null)
            {
                Logger.Log($"[SGDB] No icons found for {label}");
                return null;
            }

            return await DownloadBytesAsync(url, label, "[SGDB-Icon]").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Log($"[SGDB] Icon error for {label}: {ex.Message}");
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------
    private async Task<int?> GetSgdbIdAsync(string steamAppId)
    {
        var result = await GetAsync<SgdbGameResponse>(
            $"games/steam/{steamAppId}")
            .ConfigureAwait(false);
        return result?.Data?.Id;
    }

    private async Task<T?> GetAsync<T>(string relativeUrl)
    {
        using var resp = await _http.GetAsync(relativeUrl).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            Logger.Log($"[SGDB] HTTP {(int)resp.StatusCode} for /{relativeUrl}");
            return default;
        }
        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(json, _jsonOpts);
    }

    private async Task<byte[]?> DownloadBytesAsync(string url, string label, string tag)
    {
        // Use CdnHttp (no auth, no BaseAddress) — the image URLs point to a
        // separate CDN host and sending the Bearer token there causes 401s.
        using var resp = await CdnHttp.GetAsync(url).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            Logger.Log($"{tag} HTTP {(int)resp.StatusCode} for {label}");
            return null;
        }
        var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        if (bytes.Length < 512)
        {
            Logger.Log($"{tag} Response too small ({bytes.Length}b) for {label}");
            return null;
        }
        Logger.Log($"{tag} Downloaded {label} ({bytes.Length / 1024} KB)");
        return bytes;
    }

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // -------------------------------------------------------------------------
    // JSON models
    // -------------------------------------------------------------------------
    private record SgdbGameResponse(bool Success, SgdbGame? Data);
    private record SgdbGame(int Id, string? Name);
    private record SgdbListResponse(bool Success, List<SgdbImage>? Data);
    private record SgdbImage(int Id, string? Url);

    // -------------------------------------------------------------------------
    // Factory
    // -------------------------------------------------------------------------
    public static SteamGridDbService? TryCreate()
    {
        var key = SettingsService.LoadTextFile("sgdb_apikey.txt");
        return string.IsNullOrWhiteSpace(key) ? null : new SteamGridDbService(key);
    }
}
