using System.Windows;
using System.Windows.Media.Animation;

namespace SteamRoulette.Views;

public partial class SplashScreen : Window
{
    // Set to true by Startup once the main window is ready so the closing
    // handler knows it's an intentional dismiss rather than a user close.
    private bool _readyToClose;
    private bool _minimumElapsed;
    private bool _preloadDone;

    public SplashScreen()
    {
        InitializeComponent();

        // Guarantee the splash is visible for at least 800 ms even if all images
        // are already on disk and the preload finishes instantly.
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(800)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _minimumElapsed = true;
            if (_preloadDone) FinishAndClose();
        };
        timer.Start();
    }

    // -------------------------------------------------------------------------
    // Called by Startup on the UI thread for every PreloadProgress tick.
    // done==0, total>0  → starting   (switch bar from indeterminate to determinate)
    // done>0,  total>0  → progress
    // done==total        → finishing  (fade out and close)
    // -------------------------------------------------------------------------
    public void ReportProgress(int done, int total)
    {
        // total==0 is the completion sentinel from PreloadInstalledImagesAsync
        if (total == 0 || (Bar.Maximum > 0 && done >= Bar.Maximum))
        {
            _preloadDone = true;
            if (_minimumElapsed) FinishAndClose();
            return;
        }

        if (done == 0)
        {
            // First tick — switch from indeterminate spinner to a real bar
            Bar.IsIndeterminate = false;
            Bar.Maximum         = total;
            Bar.Value           = 0;
            StatusText.Text     = "Preparing images…";
            CounterText.Text    = $"0 of {total}";
            return;
        }

        Bar.Value        = done;
        CounterText.Text = $"{done} of {total}";

        // Update status text at sensible milestones
        double pct = done / (double)total;
        StatusText.Text = pct switch
        {
            < 0.33 => "Preparing images…",
            < 0.66 => "Almost there…",
            _      => "Finishing up…",
        };
    }

    // -------------------------------------------------------------------------
    // Fade out then close — runs on the UI thread
    // -------------------------------------------------------------------------
    public void FinishAndClose()
    {
        if (_readyToClose) return;
        _readyToClose = true;

        StatusText.Text  = "Ready!";
        CounterText.Text = "";
        Bar.Value        = Bar.Maximum > 0 ? Bar.Maximum : 100;

        var fade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(350))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    // Prevent the user accidentally closing the splash manually
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_readyToClose)
            e.Cancel = true;
        else
            base.OnClosing(e);
    }
}
