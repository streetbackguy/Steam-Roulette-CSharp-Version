using System.Windows;

namespace SteamRoulette.Views;

public partial class ProgressDialog : Window
{
    public ProgressDialog(string title, string message)
    {
        InitializeComponent();
        Title = title;
        StatusLabel.Text = message;
    }

    public void SetMessage(string msg) =>
        Dispatcher.Invoke(() => StatusLabel.Text = msg);

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e) => e.Cancel = true;
    public void ForceClose() { Dispatcher.Invoke(() => { base.OnClosing(new System.ComponentModel.CancelEventArgs()); Close(); }); }
}
