using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SteamRoulette.Models;
using SteamRoulette.ViewModels;

namespace SteamRoulette.Views;

public partial class ExcludeGamesWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ObservableCollection<GameRow> _rows = new();
    private List<GameRow> _allRows = new();

    public ExcludeGamesWindow(MainViewModel vm, bool darkMode)
    {
        _vm = vm;
        InitializeComponent();
        GameList.ItemsSource = _rows;

        if (darkMode) ApplyDark();

        Loaded += async (_, _) => await LoadRowsAsync();
    }

    private async Task LoadRowsAsync()
    {
        StatusBar.Text = "Loading…";
        var games = _vm.AllGames
            .Where(g => !string.IsNullOrWhiteSpace(g.Name))
            .OrderBy(g => g.Name)
            .ToList();

        _allRows = games.Select(g => new GameRow(g, _vm.ExcludedGames.Contains(g.AppId))).ToList();
        RefreshList(SearchBox.Text);
        StatusBar.Text = $"Loaded {_allRows.Count} games.";

        // Show the icon progress bar
        int total = _allRows.Count;
        int done  = 0;
        IconProgressBar.Maximum    = total;
        IconProgressBar.Value      = 0;
        IconProgressBar.IsIndeterminate = false;
        IconProgressBar.Visibility = Visibility.Visible;
        IconProgressLabel.Text       = $"Loading icons… 0 of {total}";
        IconProgressLabel.Visibility = Visibility.Visible;

        // Load icons in background, updating the bar as each one arrives
        await Task.Run(async () =>
        {
            foreach (var row in _allRows)
            {
                var icon = await _vm.GetIconAsync(row.Game).ConfigureAwait(false);
                int n    = Interlocked.Increment(ref done);

                Dispatcher.Invoke(() =>
                {
                    if (icon != null) row.Icon = icon;
                    IconProgressBar.Value    = n;
                    IconProgressLabel.Text   = n < total
                        ? $"Loading icons… {n} of {total}"
                        : $"Icons loaded ({total})";
                });
            }
        });

        // Hide bar when done
        IconProgressBar.Visibility   = Visibility.Collapsed;
        IconProgressLabel.Visibility = Visibility.Collapsed;
        StatusBar.Text = $"Showing {_rows.Count} of {_allRows.Count} games.";
    }

    private void RefreshList(string filter)
    {
        _rows.Clear();
        var filtered = string.IsNullOrWhiteSpace(filter)
            ? _allRows
            : _allRows.Where(r => r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var r in filtered) _rows.Add(r);
        StatusBar.Text = $"Showing {_rows.Count} of {_allRows.Count} games.";
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        RefreshList(SearchBox.Text);

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        var excluded = _allRows.Where(r => r.IsExcluded).Select(r => r.Game.AppId);
        _vm.ApplyExclusions(excluded);
        MessageBox.Show($"Excluded {_vm.ExcludedGames.Count} games.", "Exclusions Applied",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await LoadRowsAsync();

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
    {
        // If everything visible is already ticked, untick all — acts as a toggle
        bool allTicked = _rows.All(r => r.IsExcluded);
        bool newState  = !allTicked;

        foreach (var r in _rows) r.IsExcluded = newState;

        // Keep label in sync with current state
        BtnSelectAll.Content = newState ? "Deselect All" : "Select All";
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        _vm.ClearExclusions();
        foreach (var r in _allRows) r.IsExcluded = false;
        RefreshList(SearchBox.Text);
        MessageBox.Show("All exclusions cleared.", "Cleared", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void GameList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (GameList.SelectedItem is GameRow row)
            row.IsExcluded = !row.IsExcluded;
    }

    private void RowCheckBox_Changed(object sender, RoutedEventArgs e) { /* binding handles it */ }

    private void ApplyDark()
    {
        var bg = new SolidColorBrush(Color.FromRgb(0x2e, 0x2e, 0x2e));
        Background = bg;
        GameList.Background = bg;
        GameList.Foreground = Brushes.White;
        SearchBox.Background = bg;
        SearchBox.Foreground = Brushes.White;
        SearchLabel.Foreground = Brushes.White;
        StatusBar.Foreground = Brushes.LightGray;
        IconProgressLabel.Foreground = Brushes.LightGray;
        foreach (var btn in new[] { BtnApply, BtnRefresh, BtnSelectAll, BtnClear })
        {
            btn.Background = bg;
            btn.Foreground = Brushes.White;
        }
    }
}

// ─── Row view-model ──────────────────────────────────────────────────────────
public class GameRow : INotifyPropertyChanged
{
    private bool _isExcluded;
    private BitmapSource? _icon;

    public GameInfo Game { get; }
    public string   Name => Game.Name;

    public bool IsExcluded
    {
        get => _isExcluded;
        set { _isExcluded = value; OnPropertyChanged(); }
    }

    public BitmapSource? Icon
    {
        get => _icon;
        set { _icon = value; OnPropertyChanged(); }
    }

    public GameRow(GameInfo game, bool excluded)
    {
        Game       = game;
        _isExcluded = excluded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
