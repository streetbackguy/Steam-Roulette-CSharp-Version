using System.Windows;
using SteamRoulette.Services;
using SteamRoulette.ViewModels;

namespace SteamRoulette;

public static class Startup
{
    [STAThread]
    public static void Main()
    {
        var app = new App();

        // ── Locate Steam ──────────────────────────────────────────────────────
        var steamPath = SteamService.GetSteamInstallPath()
                     ?? SteamService.FindSteamPathFallback();

        if (steamPath == null || !System.IO.Directory.Exists(steamPath))
        {
            MessageBox.Show("Could not locate your Steam installation.",
                "Steam Roulette", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // ── Load ACF manifests ────────────────────────────────────────────────
        var games = SteamService.GetInstalledGames(steamPath);
        if (games.Count == 0)
        {
            MessageBox.Show("No installed Steam games found.",
                "Steam Roulette", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Wipe any icons that were cached with the wrong URL in an older build
        SettingsService.ClearIconCache();

        Logger.Log($"Found {games.Count} installed games.");

        // ── Build the VM and main window (not yet shown) ──────────────────────
        var vm  = new MainViewModel();
        var win = new Views.MainWindow(vm);

        // ── Show splash, subscribe to preload progress ────────────────────────
        var splash = new Views.SplashScreen();

        vm.PreloadProgress += (done, total) =>
            splash.Dispatcher.InvokeAsync(() => splash.ReportProgress(done, total));

        // When the splash finishes its fade-out, show the main window
        splash.Closed += (_, _) =>
        {
            win.Show();
            // Hand off the message pump — app.Run() returns when win is closed
        };

        // Kick off Initialise (starts the background preload) then show splash.
        // Initialise is synchronous for game-list setup; the image pre-load runs
        // on the thread pool and fires PreloadProgress events back to the splash.
        vm.Initialise(games, steamPath);

        // Show the splash — this returns immediately (non-blocking Show, not ShowDialog)
        splash.Show();

        // Run the message pump.  The splash will close itself when preload
        // finishes, its Closed handler will Show() the main window, and the
        // app keeps running until the main window is closed.
        app.Run();
    }
}
