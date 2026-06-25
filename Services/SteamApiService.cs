using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SteamRoulette.Models;

namespace SteamRoulette.Services;

public class SteamApiService
{
    private readonly HttpClient _http = new()
    {
        DefaultRequestHeaders = { { "User-Agent", "SteamRoulette/1.0" } },
        Timeout = TimeSpan.FromSeconds(20),
    };

    // Shared JSON options: case-insensitive so Steam's snake_case / camelCase
    // both map correctly onto our record properties.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Dictionary<string, bool>                  _schemaCache   = new();
    private readonly Dictionary<string, (int total, int done)> _progressCache = new();

    // -------------------------------------------------------------------------
    // Owned games
    // -------------------------------------------------------------------------
    public async Task<List<OwnedGame>> GetAllGamesAsync(string apiKey, string steamId)
    {
        // Use HTTPS — the http:// variant sometimes redirects and loses the
        // query string on certain network configs.
        var url = $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/" +
                  $"?key={apiKey}&steamid={steamId}&include_appinfo=1&include_played_free_games=1";
        try
        {
            using var resp = await _http.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Logger.Log($"[Steam API] GetOwnedGames HTTP {(int)resp.StatusCode}");
                return new();
            }
            var json  = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var body  = JsonSerializer.Deserialize<OwnedGamesResponse>(json, JsonOpts);
            var games = body?.Response?.Games ?? new();
            Logger.Log($"[Steam API] Fetched {games.Count} owned games.");
            return games;
        }
        catch (Exception ex)
        {
            Logger.Log($"[Steam API] GetOwnedGames error: {ex.Message}");
            return new();
        }
    }

    public async Task<List<GameInfo>> GetUninstalledGamesAsync(
        string apiKey, string steamId, IReadOnlyCollection<GameInfo> installedGames)
    {
        var all         = await GetAllGamesAsync(apiKey, steamId).ConfigureAwait(false);
        var installedIds = installedGames.Select(g => g.AppId).ToHashSet();
        return all
            .Where(g => !installedIds.Contains(g.AppId.ToString())
                        && !AppConstants.NonGameAppIds.Contains(g.AppId.ToString()))
            .Select(g => new GameInfo
            {
                AppId       = g.AppId.ToString(),
                Name        = string.IsNullOrWhiteSpace(g.Name) ? $"App {g.AppId}" : g.Name.Trim(),
                ImgIconUrl  = g.ImgIconUrl ?? "",
                IsInstalled = false,
            })
            .ToList();
    }

    // -------------------------------------------------------------------------
    // Icon hash enrichment
    // -------------------------------------------------------------------------
    public async Task EnrichIconHashesAsync(
        string apiKey, string steamId, IList<GameInfo> games)
    {
        try
        {
            var all    = await GetAllGamesAsync(apiKey, steamId).ConfigureAwait(false);
            var lookup = all.ToDictionary(g => g.AppId.ToString(), g => g.ImgIconUrl ?? "");
            foreach (var g in games)
                if (lookup.TryGetValue(g.AppId, out var hash) && !string.IsNullOrEmpty(hash))
                    g.ImgIconUrl = hash;
        }
        catch (Exception ex)
        {
            Logger.Log($"[Steam API] EnrichIconHashes error: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------------
    // Achievements
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns true if the game has an achievements schema defined.
    /// A 400/403/404 means the app has no stats endpoint — not an error.
    /// </summary>
    public async Task<bool> SupportsAchievementsAsync(string apiKey, string appId)
    {
        if (_schemaCache.TryGetValue(appId, out var cached)) return cached;

        var url = $"https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/" +
                  $"?key={apiKey}&appid={appId}";
        try
        {
            using var resp = await _http.GetAsync(url).ConfigureAwait(false);

            // 400 / 403 / 404 = game has no stats at all — not an error worth logging
            if ((int)resp.StatusCode is 400 or 403 or 404)
            {
                _schemaCache[appId] = false;
                return false;
            }

            if (!resp.IsSuccessStatusCode)
            {
                Logger.Log($"[Achievements] Schema HTTP {(int)resp.StatusCode} for appId {appId}");
                _schemaCache[appId] = false;
                return false;
            }

            var json   = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var schema = JsonSerializer.Deserialize<SchemaResponse>(json, JsonOpts);

            // The schema exists but may have an empty achievements list
            var count  = schema?.Game?.AvailableGameStats?.Achievements?.Count ?? 0;
            var result = count > 0;
            _schemaCache[appId] = result;
            return result;
        }
        catch (Exception ex)
        {
            Logger.Log($"[Achievements] Schema error for appId {appId}: {ex.Message}");
            _schemaCache[appId] = false;
            return false;
        }
    }

    /// <summary>
    /// Returns (total achievements, unlocked achievements) for a player + app.
    /// Returns (0, 0) for games with no achievements or inaccessible stats.
    /// </summary>
    public async Task<(int total, int unlocked)> GetAchievementProgressAsync(
        string apiKey, string steamId, string appId)
    {
        if (_progressCache.TryGetValue(appId, out var cached)) return cached;

        if (!await SupportsAchievementsAsync(apiKey, appId).ConfigureAwait(false))
        {
            _progressCache[appId] = (0, 0);
            return (0, 0);
        }

        var url = $"https://api.steampowered.com/ISteamUserStats/GetPlayerAchievements/v1/" +
                  $"?key={apiKey}&steamid={steamId}&appid={appId}";
        try
        {
            using var resp = await _http.GetAsync(url).ConfigureAwait(false);

            if ((int)resp.StatusCode is 400 or 403 or 404)
            {
                // Stats exist in schema but aren't accessible for this player
                // (private profile, never launched the game, etc.) — not an error
                _progressCache[appId] = (0, 0);
                return (0, 0);
            }

            if (!resp.IsSuccessStatusCode)
            {
                Logger.Log($"[Achievements] PlayerAchievements HTTP {(int)resp.StatusCode} for appId {appId}");
                _progressCache[appId] = (0, 0);
                return (0, 0);
            }

            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var data = JsonSerializer.Deserialize<PlayerAchievementsResponse>(json, JsonOpts);

            // Steam returns success:false with an error string when the player
            // has never launched the game or has a private profile.
            if (data?.Playerstats?.Success == false)
            {
                var msg = data.Playerstats.Error ?? "unknown reason";
                Logger.Log($"[Achievements] Skipping appId {appId}: {msg}");
                _progressCache[appId] = (0, 0);
                return (0, 0);
            }

            var achievements = data?.Playerstats?.Achievements ?? new();
            var result       = (achievements.Count, achievements.Count(a => a.Achieved == 1));
            _progressCache[appId] = result;
            return result;
        }
        catch (Exception ex)
        {
            Logger.Log($"[Achievements] Error for appId {appId}: {ex.Message}");
            _progressCache[appId] = (0, 0);
            return (0, 0);
        }
    }

    // -------------------------------------------------------------------------
    // JSON models
    // All record constructor parameters use [JsonPropertyName] explicitly so
    // they survive both case-insensitive deserialization and trimming/AOT.
    // -------------------------------------------------------------------------
    public record OwnedGamesResponse(
        [property: JsonPropertyName("response")] OwnedGamesBody? Response);

    public record OwnedGamesBody(
        [property: JsonPropertyName("games")] List<OwnedGame> Games);

    public record OwnedGame(
        [property: JsonPropertyName("appid")]        int    AppId,
        [property: JsonPropertyName("name")]         string? Name,
        [property: JsonPropertyName("img_icon_url")] string? ImgIconUrl);

    // Schema
    private record SchemaResponse(
        [property: JsonPropertyName("game")] GameSchema? Game);

    private record GameSchema(
        [property: JsonPropertyName("availableGameStats")] AvailableGameStats? AvailableGameStats);

    private record AvailableGameStats(
        [property: JsonPropertyName("achievements")] List<AchievementDefinition>? Achievements);

    // Just need the count — we don't use the achievement names
    private record AchievementDefinition(
        [property: JsonPropertyName("name")] string? Name);

    // Player achievements
    private record PlayerAchievementsResponse(
        [property: JsonPropertyName("playerstats")] PlayerStats? Playerstats);

    private record PlayerStats(
        [property: JsonPropertyName("success")]      bool                  Success,
        [property: JsonPropertyName("error")]        string?               Error,
        [property: JsonPropertyName("achievements")] List<AchievementEntry>? Achievements);

    private record AchievementEntry(
        [property: JsonPropertyName("achieved")] int Achieved);
}
