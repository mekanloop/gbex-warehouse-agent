using System.Windows;
using System.Windows.Input;
using Gbex.Warehouse.Agent.Windows.ViewModels;

namespace Gbex.Warehouse.Agent.Windows;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Func<Window> _createStationSettingsWindow;

    public MainWindow(MainViewModel viewModel, Func<Window> createStationSettingsWindow)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _createStationSettingsWindow = createStationSettingsWindow;
        DataContext = _viewModel;
        Loaded += (_, _) => ScanTextBox.Focus();
    }

    private async void ScanTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        await _viewModel.OnBarcodeScannedAsync();
        ScanTextBox.Focus();
    }

    private void StationSettings_Click(object sender, RoutedEventArgs e)
    {
        var window = _createStationSettingsWindow();
        window.Owner = this;
        window.ShowDialog();
    }
}
