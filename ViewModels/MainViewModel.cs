using System.Threading;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Imaging;
using SteamRoulette.Models;
using SteamRoulette.Services;

namespace SteamRoulette.ViewModels;

public partial class MainViewModel : INotifyPropertyChanged
{
    // -------------------------------------------------------------------------
    // Fields
    // -------------------------------------------------------------------------
    private readonly SteamApiService _api = new();
    private readonly string _cacheDir = SettingsService.CacheDir;
    private string _steamPath = "";  // set in Initialise()

    // Two-level cache: disk paths (unlimited) + decoded bitmaps (LRU, capped)
    private readonly ImageCache _imageCache = new(maxDecoded: 30);
    private SteamGridDbService? _sgdb;

    private GameInfo? _selectedGame;
    private BitmapSource? _headerImage;
    private string _statusText    = "Welcome to Steam Roulette!";
    private string _gameNameText  = "";
    private bool _isSpinning;
    private bool _canLaunch;
    private bool _isDarkMode;
    private string _excludedCount = "Excluded Games:\n0";
    private string _gameCountText = "";
    private string _numGamesLabel = "Spinning:\nAll Games";
    private string _pleaseWaitText = "";
    private bool _includeUninstalled;
    private bool _filterAchievements;
    private int? _selectedNumGames;

    // -------------------------------------------------------------------------
    // Observable lists
    // -------------------------------------------------------------------------
    public List<GameInfo> AllGames    { get; } = new();
    public List<GameInfo> UninstalledGames { get; } = new();
    public List<string>   ExcludedGames { get; private set; } = new();

    // -------------------------------------------------------------------------
    // Properties
    // -------------------------------------------------------------------------
    public BitmapSource? HeaderImage
    {
        get => _headerImage;
        private set { _headerImage = value; OnPropertyChanged(); }
    }

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    public string GameNameText
    {
        get => _gameNameText;
        set { _gameNameText = value; OnPropertyChanged(); }
    }

    public bool IsSpinning
    {
        get => _isSpinning;
        private set { _isSpinning = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanSpin)); }
    }

    public bool CanSpin => !IsSpinning;

    public bool CanLaunch
    {
        get => _canLaunch;
        private set { _canLaunch = value; OnPropertyChanged(); }
    }

    public bool IsDarkMode
    {
        get => _isDarkMode;
        set { _isDarkMode = value; OnPropertyChanged(); }
    }

    public string ExcludedCount
    {
        get => _excludedCount;
        private set { _excludedCount = value; OnPropertyChanged(); }
    }

    public string GameCountText
    {
        get => _gameCountText;
        private set { _gameCountText = value; OnPropertyChanged(); }
    }

    public string NumGamesLabel
    {
        get => _numGamesLabel;
        private set { _numGamesLabel = value; OnPropertyChanged(); }
    }

    public string PleaseWaitText
    {
        get => _pleaseWaitText;
        set { _pleaseWaitText = value; OnPropertyChanged(); }
    }

    public bool IncludeUninstalled
    {
        get => _includeUninstalled;
        set { _includeUninstalled = value; OnPropertyChanged(); }
    }

    public bool FilterAchievements
    {
        get => _filterAchievements;
        set { _filterAchievements = value; OnPropertyChanged(); }
    }

    // -------------------------------------------------------------------------
    // Spin animation state (used by MainWindow code-behind)
    // -------------------------------------------------------------------------
    public List<(BitmapSource Image, GameInfo Game)> SpinFrames { get; } = new();
    public int WinnerFrameIndex { get; private set; }
    public event EventHandler? SpinReady;
    public event EventHandler? SpinFinished;

    // Fired during background pre-load so the UI can show a progress bar.
    // Args: (done, total) — total is 0 when pre-load completes.
    public event Action<int, int>? PreloadProgress;

    // -------------------------------------------------------------------------
    // Initialisation
    // -------------------------------------------------------------------------
    public void Initialise(IEnumerable<GameInfo> games, string steamPath = "")
    {
        _steamPath = steamPath;
        AllGames.AddRange(games);
        ExcludedGames = SettingsService.LoadExclusions();
        RefreshExcludedCount();
        GameCountText = GamesFoundText();
        NumGamesLabel = $"Spinning:\nAll {AllGames.Count} Games";

        // Initialise SteamGridDB fallback if a key is available
        _sgdb = SteamGridDbService.TryCreate();
        if (_sgdb != null) Logger.Log("[SGDB] API key found — SteamGridDB fallback enabled.");

        // Pre-load images in background — fires PreloadProgress events as each image lands
        Task.Run(PreloadInstalledImagesAsync);

        // Fetch icon hashes if API key is set
        var apiKey = SettingsService.LoadTextFile("apikey.txt");
        var userId = SettingsService.LoadTextFile("steamuserid.txt");
        if (!string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(userId))
            Task.Run(() => _api.EnrichIconHashesAsync(apiKey, userId, AllGames));

        // Show a random header image
        _ = ShowRandomHeaderImageAsync();
    }

    // -------------------------------------------------------------------------
    // Random header display
    // -------------------------------------------------------------------------
    public async Task ShowRandomHeaderImageAsync()
    {
        if (AllGames.Count == 0) return;
        var game = AllGames[Random.Shared.Next(AllGames.Count)];
        var img  = await GetDecodedImageAsync(game).ConfigureAwait(false);
        Application.Current.Dispatcher.Invoke(() => HeaderImage = img);
    }

    // -------------------------------------------------------------------------
    // Spin
    // -------------------------------------------------------------------------
    public async Task SpinAsync()
    {
        var valid = AllGames
            .Where(g => g.AppId != null && !ExcludedGames.Contains(g.AppId))
            .ToList();

        if (valid.Count == 0)
        {
            MessageBox.Show("No valid games available to spin.", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        IsSpinning = true;
        CanLaunch  = false;
        StatusText = "Loading images…";
        GameNameText = "";

        int n = _selectedNumGames ?? valid.Count;
        var sample = valid.OrderBy(_ => Random.Shared.Next())
                          .Take(Math.Min(n, valid.Count))
                          .ToList();

        _selectedGame = valid[Random.Shared.Next(valid.Count)];
        Logger.Log($"Winner: {_selectedGame.Name} ({_selectedGame.AppId})");

        var gamesToDraw = new List<GameInfo>(sample);
        if (!gamesToDraw.Any(g => g.AppId == _selectedGame.AppId))
            gamesToDraw.Add(_selectedGame);

        // Fetch missing images
        var missing = gamesToDraw.Where(g => !_imageCache.HasPath(g.AppId)).ToList();
        if (missing.Count > 0)
            Logger.Log($"[Spin] Fetching {missing.Count} missing image(s)…");

        await Task.WhenAll(missing.Select(EnsureImageOnDiskAsync)).ConfigureAwait(false);

        // Build a long looping strip so the easing deceleration is visible
        // across several frames rather than snapping to the very last image.
        //
        // Layout:  [~20 shuffled filler frames] [winner] [3 trailer frames]
        //
        // The animation scrolls until the winner sits at x=0, with the three
        // trailer frames drifting slowly past — this makes the slowdown look
        // like it "landed" rather than cutting abruptly to the static image.
        SpinFrames.Clear();

        var pool = gamesToDraw
            .Where(g => g.AppId != _selectedGame!.AppId)
            .ToList();

        const int fillerCount  = 20;
        const int trailerCount = 3;

        // Pad the pool by repeating if there aren't enough unique games
        var filler = new List<GameInfo>();
        while (filler.Count < fillerCount + trailerCount)
            filler.AddRange(pool.OrderBy(_ => Random.Shared.Next()));

        var preWinner  = filler.Take(fillerCount).ToList();
        var postWinner = filler.Skip(fillerCount).Take(trailerCount).ToList();

        foreach (var g in preWinner)
        {
            var bi = _imageCache.GetDecoded(g.AppId)
                  ?? ImageService.CreatePlaceholderBitmap();
            SpinFrames.Add((bi, g));
        }

        // Record which index the winner lands on so the view knows where to stop
        WinnerFrameIndex = SpinFrames.Count;
        var winImg = _imageCache.GetDecoded(_selectedGame!.AppId)
                  ?? ImageService.CreatePlaceholderBitmap();
        SpinFrames.Add((winImg, _selectedGame));

        foreach (var g in postWinner)
        {
            var ti = _imageCache.GetDecoded(g.AppId)
                  ?? ImageService.CreatePlaceholderBitmap();
            SpinFrames.Add((ti, g));
        }

        Application.Current.Dispatcher.Invoke(() =>
        {
            StatusText = "Rolling…";
            SpinReady?.Invoke(this, EventArgs.Empty);
        });
    }

    public void OnSpinComplete()
    {
        if (_selectedGame == null) return;
        HeaderImage = _imageCache.GetDecoded(_selectedGame.AppId)
                   ?? ImageService.CreatePlaceholderBitmap();

        StatusText   = "Done!";
        GameNameText = _selectedGame.Name;
        IsSpinning   = false;
        CanLaunch    = true;
        SpinFinished?.Invoke(this, EventArgs.Empty);
    }

    // -------------------------------------------------------------------------
    // Game actions
    // -------------------------------------------------------------------------
    public void LaunchGame()
    {
        if (_selectedGame != null)
            OpenUrl($"steam://run/{_selectedGame.AppId}");
    }

    public void OpenStore()
    {
        if (_selectedGame != null)
            OpenUrl($"https://store.steampowered.com/app/{_selectedGame.AppId}");
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Log($"Error opening URL: {ex.Message}"); }
    }

    // -------------------------------------------------------------------------
    // Number of games
    // -------------------------------------------------------------------------
    public bool TrySetNumGames(string input, out string error)
    {
        error = "";
        if (!int.TryParse(input, out int n) || n < 1 || n > AllGames.Count)
        {
            error = $"Enter a number 1–{AllGames.Count}";
            return false;
        }
        _selectedNumGames = n;
        NumGamesLabel = $"Spinning:\n{n} of {AllGames.Count}";
        return true;
    }

    // -------------------------------------------------------------------------
    // Exclusions
    // -------------------------------------------------------------------------
    public void ApplyExclusions(IEnumerable<string> excludedIds)
    {
        ExcludedGames = excludedIds.ToList();
        SettingsService.SaveExclusions(ExcludedGames);
        RefreshExcludedCount();
    }

    public void ClearExclusions()
    {
        ExcludedGames.Clear();
        SettingsService.SaveExclusions(ExcludedGames);
        RefreshExcludedCount();
    }

    private void RefreshExcludedCount() =>
        ExcludedCount = $"Excluded Games:\n{ExcludedGames.Count}";

    // -------------------------------------------------------------------------
    // Uninstalled games
    // -------------------------------------------------------------------------
    public async Task<(bool success, string message)> LoadUninstalledGamesAsync(
        IProgress<string>? progress = null)
    {
        var apiKey = SettingsService.LoadTextFile("apikey.txt");
        var userId = SettingsService.LoadTextFile("steamuserid.txt");

        if (string.IsNullOrEmpty(apiKey))
            return (false, "Please set your Steam API key first.");
        if (string.IsNullOrEmpty(userId))
            return (false, "Please set your Steam User ID first.");

        progress?.Report("Fetching your Steam library…");
        var newGames = await _api.GetUninstalledGamesAsync(apiKey, userId, AllGames)
                                  .ConfigureAwait(false);

        if (newGames.Count == 0)
            return (false, "No uninstalled games found to include.");

        UninstalledGames.Clear();
        UninstalledGames.AddRange(newGames);
        AllGames.AddRange(newGames);
        GameCountText = GamesFoundText();
        if (_selectedNumGames == null) NumGamesLabel = $"Spinning:\nAll {AllGames.Count} Games";

        progress?.Report($"Downloading images… 0 of {newGames.Count}");
        int done = 0;
        await Task.WhenAll(newGames.Select(async g =>
        {
            await EnsureImageOnDiskAsync(g).ConfigureAwait(false);
            var n = Interlocked.Increment(ref done);
            progress?.Report($"Downloading images… {n} of {newGames.Count}");
        })).ConfigureAwait(false);

        return (true, $"Added {newGames.Count} uninstalled games.");
    }

    public void RemoveUninstalledGames()
    {
        var ids = UninstalledGames.Select(g => g.AppId).ToHashSet();
        AllGames.RemoveAll(g => ids.Contains(g.AppId));
        UninstalledGames.Clear();
        GameCountText = GamesFoundText();
        if (_selectedNumGames == null) NumGamesLabel = $"Spinning:\nAll {AllGames.Count} Games";
    }

    // -------------------------------------------------------------------------
    // Achievement filter
    // -------------------------------------------------------------------------
    public async Task ExcludeAchievementGamesAsync(IProgress<string>? progress = null)
    {
        var apiKey = SettingsService.LoadTextFile("apikey.txt");
        var userId = SettingsService.LoadTextFile("steamuserid.txt");
        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(userId)) return;

        int done = 0;
        int total = AllGames.Count;

        await Parallel.ForEachAsync(AllGames,
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            async (game, _) =>
            {
                var (tot, unlocked) = await _api.GetAchievementProgressAsync(apiKey, userId, game.AppId)
                                                 .ConfigureAwait(false);
                if (tot > 0 && unlocked == tot)
                {
                    lock (ExcludedGames)
                    {
                        if (!ExcludedGames.Contains(game.AppId))
                            ExcludedGames.Add(game.AppId);
                    }
                }
                int n = Interlocked.Increment(ref done);
                progress?.Report($"Checking achievements… {n}/{total}");
            }).ConfigureAwait(false);

        RefreshExcludedCount();
        SettingsService.SaveExclusions(ExcludedGames);
    }

    public async Task IncludeAchievementGamesAsync()
    {
        var apiKey = SettingsService.LoadTextFile("apikey.txt");
        var userId = SettingsService.LoadTextFile("steamuserid.txt");
        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(userId)) return;

        var toRemove = new List<string>();
        foreach (var appId in ExcludedGames.ToList())
        {
            var (tot, done) = await _api.GetAchievementProgressAsync(apiKey, userId, appId)
                                         .ConfigureAwait(false);
            if (tot > 0 && done == tot)
                toRemove.Add(appId);
        }
        foreach (var id in toRemove) ExcludedGames.Remove(id);
        RefreshExcludedCount();
        SettingsService.SaveExclusions(ExcludedGames);
    }

    // -------------------------------------------------------------------------
    // Cache
    // -------------------------------------------------------------------------
    public string ClearImageCache()
    {
        _imageCache.ClearDecoded();
        int count = SettingsService.ClearImageCache();
        return $"Deleted {count} cached image(s). They will be re-downloaded as needed.";
    }

    // -------------------------------------------------------------------------
    // Icon fetching (used by ExcludeGamesWindow)
    // -------------------------------------------------------------------------
    public async Task<BitmapSource?> GetIconAsync(GameInfo game)
    {
        var path = await ImageService.EnsureIconAsync(
            game.AppId, game.ImgIconUrl, _cacheDir,
            gameName: game.Name, steamPath: _steamPath, sgdb: _sgdb).ConfigureAwait(false);
        if (path == null) return null;
        try { return ImageService.LoadBitmapFromPath(path); }
        catch { return null; }
    }

    /// <summary>
    /// Downloads icons for a list of games, reporting (done, total) progress.
    /// Each icon is yielded via <paramref name="onIcon"/> as it arrives so the
    /// UI can update the row immediately rather than waiting for the whole batch.
    /// </summary>
    public async Task FetchIconsAsync(
        IList<GameInfo> games,
        Action<GameInfo, BitmapSource> onIcon,
        IProgress<(int done, int total)>? progress = null)
    {
        int total = games.Count;
        int done  = 0;
        progress?.Report((0, total));

        // Sequential — icon fetches are cheap cache-hits after the first run;
        // parallel here would hammer the CDN and thrash disk on first open.
        foreach (var game in games)
        {
            var icon = await GetIconAsync(game).ConfigureAwait(false);
            if (icon != null) onIcon(game, icon);
            progress?.Report((Interlocked.Increment(ref done), total));
        }
    }

    // -------------------------------------------------------------------------
    // SteamGridDB
    // -------------------------------------------------------------------------
    /// <summary>Re-reads sgdb_apikey.txt and re-creates the SGDB client.
    /// Call this after the user saves a new key.</summary>
    public void RefreshSgdb()
    {
        _sgdb = SteamGridDbService.TryCreate();
        Logger.Log(_sgdb != null
            ? "[SGDB] API key updated — SteamGridDB fallback enabled."
            : "[SGDB] API key cleared — SteamGridDB fallback disabled.");
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------
    // Ensures the image is on disk and registered in the path cache.
    // Does NOT decode — decoding happens lazily in GetDecodedImageAsync.
    private async Task EnsureImageOnDiskAsync(GameInfo game)
    {
        if (_imageCache.HasPath(game.AppId)) return;
        var path = await ImageService.EnsureHeaderImageAsync(
            game.AppId, _cacheDir, game.Name, _steamPath, _sgdb).ConfigureAwait(false);
        if (path != null) _imageCache.RegisterPath(game.AppId, path);
    }

    // Returns a decoded BitmapSource, loading from the LRU decoded cache
    // or decoding from disk on demand. Returns a placeholder if unavailable.
    private async Task<BitmapSource> GetDecodedImageAsync(GameInfo game)
    {
        await EnsureImageOnDiskAsync(game).ConfigureAwait(false);
        var decoded = _imageCache.GetDecoded(game.AppId);
        if (decoded != null) return decoded;
        return ImageService.CreatePlaceholderBitmap();
    }

    private async Task PreloadInstalledImagesAsync()
    {
        // Only ensure files are on disk — don't decode into memory.
        // Reports progress via PreloadProgress so the UI can show a bar.
        int total = AllGames.Count;
        int done  = 0;
        PreloadProgress?.Invoke(0, total);

        await Parallel.ForEachAsync(AllGames,
            new ParallelOptions { MaxDegreeOfParallelism = AppConstants.PreloadWorkers },
            async (game, _) =>
            {
                await EnsureImageOnDiskAsync(game).ConfigureAwait(false);
                int n = Interlocked.Increment(ref done);
                PreloadProgress?.Invoke(n, total);
            });

        Logger.Log($"Pre-load complete. {_imageCache.PathCount} images on disk.");
        PreloadProgress?.Invoke(total, 0);   // 0 total = signal that we're done
    }

    private string GamesFoundText()
    {
        var installed   = AllGames.Count(g => g.IsInstalled);
        var uninstalled = AllGames.Count(g => !g.IsInstalled);
        return uninstalled > 0
            ? $"Games found: {installed} installed, {uninstalled} uninstalled"
            : $"Games found: {installed}";
    }

    // -------------------------------------------------------------------------
    // INotifyPropertyChanged
    // -------------------------------------------------------------------------
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        var handler = PropertyChanged;
        if (handler == null) return;
        var args = new PropertyChangedEventArgs(name);
        // Always raise on the UI thread so WPF bindings update correctly
        // even when called from background tasks (image downloads, API calls).
        if (Application.Current?.Dispatcher.CheckAccess() != false)
            handler(this, args);
        else
            Application.Current.Dispatcher.Invoke(() => handler(this, args));
    }
}
