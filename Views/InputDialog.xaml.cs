using System.Windows;
using System.Windows.Media;

namespace SteamRoulette.Views;

public partial class InputDialog : Window
{
    public string Value { get; private set; } = "";

    public InputDialog(string title, string prompt, bool darkMode)
    {
        InitializeComponent();
        Title = title;
        PromptLabel.Text = prompt;

        if (darkMode)
        {
            var bg = new SolidColorBrush(Color.FromRgb(0x2e, 0x2e, 0x2e));
            var fg = Brushes.White;
            Background = bg;
            PromptLabel.Foreground = fg;
            InputBox.Background = bg;
            InputBox.Foreground = fg;
            SubmitBtn.Background = bg;
            SubmitBtn.Foreground = fg;
        }
    }

    public void SetInitialValue(string value) => InputBox.Text = value;

    private void SubmitBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(InputBox.Text))
        {
            MessageBox.Show("Field cannot be empty.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Value = InputBox.Text.Trim();
        DialogResult = true;
    }
}
