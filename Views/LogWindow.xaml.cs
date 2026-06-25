using System.Windows;
using System.Windows.Media;
using SteamRoulette.Services;

namespace SteamRoulette.Views;

public partial class LogWindow : Window
{
    public LogWindow()
    {
        InitializeComponent();
        Logger.MessageLogged += msg =>
            Dispatcher.InvokeAsync(() =>
            {
                LogText.AppendText(msg);
                LogText.ScrollToEnd();
            });
    }

    public void ApplyTheme(bool darkMode)
    {
        if (darkMode)
        {
            var bg = new SolidColorBrush(Color.FromRgb(0x2e, 0x2e, 0x2e));
            Background = bg;
            LogText.Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e));
            LogText.Foreground = new SolidColorBrush(Color.FromRgb(0xd4, 0xd4, 0xd4));
            BtnClear.Background = bg;
            BtnClear.Foreground = Brushes.White;
            BtnCopyAll.Background = bg;
            BtnCopyAll.Foreground = Brushes.White;
        }
        else
        {
            Background = Brushes.White;
            LogText.Background = new SolidColorBrush(Color.FromRgb(0xf5, 0xf5, 0xf5));
            LogText.Foreground = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e));
            BtnClear.Background = Brushes.White;
            BtnClear.Foreground = Brushes.Black;
            BtnCopyAll.Background = Brushes.White;
            BtnCopyAll.Foreground = Brushes.Black;
        }
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)   => LogText.Clear();
    private void BtnCopyAll_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(LogText.Text);
    private void LogWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
