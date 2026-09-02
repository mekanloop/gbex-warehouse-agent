using System.IO;
using System.Windows;
using System.Windows.Input;
using Gbex.Warehouse.Agent.Windows.ViewModels;
using Microsoft.Win32;

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

    private void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        var version = _viewModel.PendingUpdate?.Version ?? "";
        var confirm = MessageBox.Show(
            this,
            $"Ajan v{version} sürümüne güncellenecek ve uygulama kapanacak. Devam edilsin mi?",
            "Güncelle",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        _viewModel.InstallUpdateNow();
    }

    private void StationSettings_Click(object sender, RoutedEventArgs e)
    {
        var window = _createStationSettingsWindow();
        window.Owner = this;
        window.ShowDialog();
    }

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Tanılama Raporunu Kaydet",
            Filter = "Metin dosyası (*.txt)|*.txt",
            FileName = $"gbex-ajan-tanilama-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            await _viewModel.ExportDiagnosticsAsync(dialog.FileName);
            MessageBox.Show(this, "Tanılama raporu kaydedildi. Bu dosyayı destek ekibine gönderebilirsiniz.", "Tanılama Raporu", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Rapor kaydedilemedi. Farklı bir konum seçip tekrar deneyin.", "Tanılama Raporu", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
