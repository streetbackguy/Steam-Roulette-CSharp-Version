using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SteamRoulette.Services;
using SteamRoulette.ViewModels;

namespace SteamRoulette.Views;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    // =========================================================================
    // INotifyPropertyChanged — MainWindow is its own DataContext, so it must
    // implement INPC itself. We DON'T try to override Window's DependencyObject
    // helpers; we simply raise our own event and let WPF bindings react.
    // =========================================================================
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? name = null) =>
        Dispatcher.InvokeAsync(() =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));

    // =========================================================================
    // Fields
    // =========================================================================
    private readonly MainViewModel _vm;
    private LogWindow? _logWindow;

    // ── Theme ─────────────────────────────────────────────────────────────────
    private SolidColorBrush _windowBackground = Brushes.White;
    private SolidColorBrush _windowForeground = Brushes.Black;

    public SolidColorBrush WindowBackground
    {
        get => _windowBackground;
        private set { _windowBackground = value; Notify(); }
    }

    public SolidColorBrush WindowForeground
    {
        get => _windowForeground;
        private set { _windowForeground = value; Notify(); }
    }

    // ── Progress bar backing fields ───────────────────────────────────────────
    private bool   _progressVisible;
    private bool   _progressIndeterminate = true;
    private double _progressMax   = 100;
    private double _progressValue = 0;

    public Visibility ProgressVisibility
    {
        get => _progressVisible ? Visibility.Visible : Visibility.Collapsed;
        private set { _progressVisible = value == Visibility.Visible; Notify(); }
    }

    public bool ProgressIndeterminate
    {
        get => _progressIndeterminate;
        private set { _progressIndeterminate = value; Notify(); }
    }

    public double ProgressMax
    {
        get => _progressMax;
        private set { _progressMax = value; Notify(); }
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set { _progressValue = value; Notify(); }
    }

    // ── Proxied VM properties — bindings read these ───────────────────────────
    public string        StatusText         => _vm.StatusText;
    public string        GameNameText       => _vm.GameNameText;
    public bool          CanSpin            => _vm.CanSpin;
    public bool          CanLaunch          => _vm.CanLaunch;
    public bool          IsDarkMode         => _vm.IsDarkMode;
    public string        ExcludedCount      => _vm.ExcludedCount;
    public string        GameCountText      => _vm.GameCountText;
    public string        NumGamesLabel      => _vm.NumGamesLabel;
    public string        PleaseWaitText     => _vm.PleaseWaitText;
    public BitmapSource? HeaderImage        => _vm.HeaderImage;

    public bool IncludeUninstalled
    {
        get => _vm.IncludeUninstalled;
        set => _vm.IncludeUninstalled = value;
    }

    public bool FilterAchievements
    {
        get => _vm.FilterAchievements;
        set => _vm.FilterAchievements = value;
    }

    // =========================================================================
    // Spin animation — driven by CompositionTarget.Rendering (every vsync)
    // =========================================================================
    private readonly List<Image> _spinImages = new();
    private bool   _spinning;
    private double _canvasWidth;
    private double _totalPixels;   // distance to scroll so the winner lands at x=0
    private double _scrolled;      // pixels moved so far
    private DateTime _spinStart;
    private const double SpinDurationSec = 3.8;   // total animation time in seconds

    // =========================================================================
    // Constructor
    // =========================================================================
    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = this;

        // Relay every VM property change as our own so WPF bindings update.
        // The property names on the window match the VM property names, so
        // INPC works transparently for all the proxied props.
        _vm.PropertyChanged += (_, e) => Notify(e.PropertyName);

        _vm.SpinReady += (_, _) => Dispatcher.Invoke(StartSpinAnimation);

        CompositionTarget.Rendering += OnRendering;
    }

    // =========================================================================
    // Spin animation — CompositionTarget.Rendering fires every vsync (~16 ms)
    // =========================================================================
    private void StartSpinAnimation()
    {
        // Remove all spin images from a previous spin
        foreach (var img in _spinImages) SpinCanvas.Children.Remove(img);
        _spinImages.Clear();

        // Hide the static banner — the strip itself will show the winner at the end,
        // so we never need to do a jarring visibility swap mid-animation.
        StaticHeaderImage.Visibility = Visibility.Collapsed;

        _canvasWidth = SpinCanvas.ActualWidth;
        double x = 0;
        foreach (var (bitmap, _) in _vm.SpinFrames)
        {
            var img = new Image
            {
                Width   = _canvasWidth,
                Height  = SpinCanvas.ActualHeight,
                Stretch = Stretch.Fill,
                Source  = bitmap,
            };
            Canvas.SetLeft(img, x);
            Canvas.SetTop(img, 0);
            SpinCanvas.Children.Add(img);
            _spinImages.Add(img);
            x += _canvasWidth;
        }

        // Scroll exactly to the winner frame (not the last frame).
        // The VM places the winner mid-strip with a few trailer frames after it,
        // so the deceleration curve visibly slows across those trailers before stopping.
        _totalPixels = _vm.WinnerFrameIndex * _canvasWidth;
        _scrolled    = 0;
        _spinStart   = DateTime.UtcNow;
        _spinning    = true;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_spinning || _spinImages.Count == 0) return;

        double elapsed = (DateTime.UtcNow - _spinStart).TotalSeconds;
        double t       = Math.Min(elapsed / SpinDurationSec, 1.0);

        // Ease-out quint: f(t) = 1 - (1-t)^5
        // Spends most of its duration near full speed then decelerates sharply,
        // so the viewer watches many frames fly past before it settles on the winner.
        double eased  = 1.0 - Math.Pow(1.0 - t, 5);
        double target = eased * _totalPixels;
        double delta  = target - _scrolled;
        _scrolled     = target;

        foreach (var img in _spinImages)
            Canvas.SetLeft(img, Canvas.GetLeft(img) - delta);

        if (t >= 1.0)
        {
            _spinning = false;

            // Snap the winner image exactly to x = 0 to fix any floating-point drift
            var winnerImg = _spinImages[_vm.WinnerFrameIndex];
            double drift  = Canvas.GetLeft(winnerImg);
            if (drift != 0)
                foreach (var img in _spinImages)
                    Canvas.SetLeft(img, Canvas.GetLeft(img) - drift);

            // Remove everything except the winner frame — leave it visible so
            // there is no flash when the static image takes over.
            for (int i = 0; i < _spinImages.Count; i++)
            {
                if (i != _vm.WinnerFrameIndex)
                    SpinCanvas.Children.Remove(_spinImages[i]);
            }
            var keepImg = _spinImages[_vm.WinnerFrameIndex];
            _spinImages.Clear();
            _spinImages.Add(keepImg);   // track it so next spin clears it

            // Notify the VM — it will update HeaderImage, but we keep the canvas
            // frame visible until the next spin starts (StartSpinAnimation removes it).
            _vm.OnSpinComplete();

            // Now that the VM has updated HeaderImage, swap to the static control
            // and remove the canvas frame — done in the same dispatcher beat so
            // the user never sees a blank gap.
            StaticHeaderImage.Visibility = Visibility.Visible;
            SpinCanvas.Children.Remove(keepImg);
            _spinImages.Clear();
        }
    }

    // =========================================================================
    // Progress bar helpers
    // =========================================================================

    /// <summary>
    /// Parses "N of M" (image download) or "N/M" (achievement check) out of
    /// a progress string and updates the bound progress-bar properties.
    /// </summary>
    private void UpdateProgress(string msg)
    {
        _vm.PleaseWaitText = msg;

        // "… 42 of 300 …"
        var m = Regex.Match(msg, @"(\d+)\s+of\s+(\d+)");
        if (m.Success
            && int.TryParse(m.Groups[1].Value, out int done)
            && int.TryParse(m.Groups[2].Value, out int total)
            && total > 0)
        {
            ProgressIndeterminate = false;
            ProgressMax   = total;
            ProgressValue = done;
            return;
        }

        // "… 42/300 …"  (achievement checker)
        var m2 = Regex.Match(msg, @"(\d+)/(\d+)");
        if (m2.Success
            && int.TryParse(m2.Groups[1].Value, out int d2)
            && int.TryParse(m2.Groups[2].Value, out int t2)
            && t2 > 0)
        {
            ProgressIndeterminate = false;
            ProgressMax   = t2;
            ProgressValue = d2;
        }
    }

    private void BeginOperation()
    {
        ChkUninstalled.IsEnabled  = false;
        ChkAchievements.IsEnabled = false;
        BtnSpin.IsEnabled         = false;
        ProgressIndeterminate = true;
        ProgressValue         = 0;
        ProgressVisibility    = Visibility.Visible;
    }

    private void EndOperation()
    {
        ChkUninstalled.IsEnabled  = true;
        ChkAchievements.IsEnabled = true;
        BtnSpin.IsEnabled         = true;
        ProgressVisibility    = Visibility.Collapsed;
        _vm.PleaseWaitText    = "";
    }

    // =========================================================================
    // Button handlers
    // =========================================================================
    private async void BtnSpin_Click(object sender, RoutedEventArgs e)
    {
        BtnSpin.Content = "Spinning…";
        await _vm.SpinAsync();
        BtnSpin.Content = "Re-Roll";
    }

    private void BtnLaunch_Click(object sender, RoutedEventArgs e) => _vm.LaunchGame();
    private void BtnStore_Click(object sender, RoutedEventArgs e)  => _vm.OpenStore();

    private void BtnSetNumGames_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new InputDialog(
            "Select Number of Games",
            $"Enter number of games to spin (1–{_vm.AllGames.Count}):",
            IsDarkMode)
        { Owner = this };

        if (dlg.ShowDialog() == true)
            if (!_vm.TrySetNumGames(dlg.Value, out var err))
                MessageBox.Show(err, "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void BtnExclude_Click(object sender, RoutedEventArgs e)
    {
        var win = new ExcludeGamesWindow(_vm, IsDarkMode) { Owner = this };
        win.ShowDialog();
    }

    private void BtnApiKey_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new InputDialog("Set API Key", "Enter your Steam API Key:", IsDarkMode)
        { Owner = this };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Value))
        {
            SettingsService.SaveTextFile("apikey.txt", dlg.Value);
            MessageBox.Show("API Key saved.", "Steam Roulette",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void BtnUserId_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new InputDialog("Set Steam User ID", "Enter your Steam User ID:", IsDarkMode)
        { Owner = this };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Value))
        {
            SettingsService.SaveTextFile("steamuserid.txt", dlg.Value);
            MessageBox.Show("Steam User ID saved.", "Steam Roulette",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void BtnSgdbKey_Click(object sender, RoutedEventArgs e)
    {
        var current = SettingsService.LoadTextFile("sgdb_apikey.txt");
        var dlg = new InputDialog(
            "Set SteamGridDB API Key",
            "Enter your SteamGridDB API key.\n" +
            "Get one free at https://www.steamgriddb.com/profile/preferences/api\n\n" +
            "Used as a fallback when Steam has no image for a game.",
            IsDarkMode)
        { Owner = this };

        // Pre-fill with the existing key if set
        if (!string.IsNullOrEmpty(current))
            dlg.SetInitialValue(current);

        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Value))
        {
            SettingsService.SaveTextFile("sgdb_apikey.txt", dlg.Value.Trim());
            _vm.RefreshSgdb();
            MessageBox.Show(
                "SteamGridDB API key saved.\n" +
                "Images will now fall back to SteamGridDB when Steam has nothing.",
                "Steam Roulette", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void BtnDarkMode_Click(object sender, RoutedEventArgs e)
    {
        _vm.IsDarkMode = !_vm.IsDarkMode;
        if (_vm.IsDarkMode)
        {
            var dark = new SolidColorBrush(Color.FromRgb(0x2e, 0x2e, 0x2e));
            WindowBackground = dark;
            WindowForeground = Brushes.White;
            Background       = dark;
        }
        else
        {
            WindowBackground = Brushes.White;
            WindowForeground = Brushes.Black;
            Background       = Brushes.White;
        }
        _logWindow?.ApplyTheme(_vm.IsDarkMode);
    }

    private void BtnClearCache_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(_vm.ClearImageCache(), "Cache Cleared",
            MessageBoxButton.OK, MessageBoxImage.Information);

    private void BtnLog_Click(object sender, RoutedEventArgs e)
    {
        if (_logWindow == null || !_logWindow.IsLoaded)
            _logWindow = new LogWindow { Owner = this };

        _logWindow.Show();
        _logWindow.Activate();
        _logWindow.ApplyTheme(_vm.IsDarkMode);
    }

    // =========================================================================
    // Checkbox handlers
    // =========================================================================
    private async void ChkUninstalled_Changed(object sender, RoutedEventArgs e)
    {
        if (_vm.IncludeUninstalled)
        {
            BeginOperation();
            _vm.PleaseWaitText = "Please Wait…";

            var progress = new Progress<string>(msg =>
                Dispatcher.Invoke(() => UpdateProgress(msg)));

            var (ok, message) = await _vm.LoadUninstalledGamesAsync(progress);
            EndOperation();

            MessageBox.Show(message, "Steam Roulette", MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        else
        {
            _vm.RemoveUninstalledGames();
            MessageBox.Show("Uninstalled games removed from the spin pool.",
                "Steam Roulette", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void ChkAchievements_Changed(object sender, RoutedEventArgs e)
    {
        BeginOperation();
        _vm.PleaseWaitText = "Please Wait…";

        var progress = new Progress<string>(msg =>
            Dispatcher.Invoke(() => UpdateProgress(msg)));

        if (_vm.FilterAchievements)
            await _vm.ExcludeAchievementGamesAsync(progress);
        else
            await _vm.IncludeAchievementGamesAsync();

        EndOperation();
    }
}
