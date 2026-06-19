using System.Threading;
using System.Windows;
using SimpleNotepad.Models;
using SimpleNotepad.Services;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace SimpleNotepad;

public partial class SyncSettingsWindow : Window
{
    private static readonly string[] DevicePalette =
    [
        "#4EC9B0", "#569CD6", "#C586C0", "#D7BA7D", "#CE9178", "#6A9955", "#D16969", "#9CDCFE",
    ];

    private readonly AppSettings _settings;
    private readonly CloudSyncService _syncService;

    public SyncSettingsWindow(AppSettings settings, CloudSyncService syncService)
    {
        InitializeComponent();

        _settings = settings;
        _syncService = syncService;

        ContainerBox.Text = string.IsNullOrWhiteSpace(settings.SyncContainerName) ? "simplenotepad" : settings.SyncContainerName;
        DeviceNameBox.Text = string.IsNullOrWhiteSpace(settings.DeviceName) ? Environment.MachineName : settings.DeviceName;
        DeviceColorBox.Text = string.IsNullOrWhiteSpace(settings.DeviceColor)
            ? PickDefaultColor(settings.DeviceId ?? Environment.MachineName)
            : settings.DeviceColor;

        var existing = SecretProtector.Unprotect(settings.SyncConnectionStringProtected);
        if (!string.IsNullOrEmpty(existing))
        {
            ConnectionStringBox.Text = existing;
        }
    }

    private static string PickDefaultColor(string seed)
    {
        var index = Math.Abs(seed.GetHashCode()) % DevicePalette.Length;
        return DevicePalette[index];
    }

    private AppSettings BuildSnapshot()
    {
        return new AppSettings
        {
            SyncConnectionStringProtected = SecretProtector.Protect(ConnectionStringBox.Text.Trim()),
            SyncContainerName = ContainerBox.Text.Trim(),
            DeviceId = _settings.DeviceId ?? Guid.NewGuid().ToString("N"),
            DeviceName = DeviceNameBox.Text.Trim(),
            DeviceColor = DeviceColorBox.Text.Trim(),
        };
    }

    private bool ValidateInputs(out string error)
    {
        if (string.IsNullOrWhiteSpace(ConnectionStringBox.Text))
        {
            error = "A storage connection string is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(ContainerBox.Text))
        {
            error = "A container name is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(DeviceNameBox.Text))
        {
            error = "A device name is required.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private void DeviceColorBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (ColorSwatch is null)
        {
            return;
        }

        try
        {
            var color = (Color)ColorConverter.ConvertFromString(DeviceColorBox.Text.Trim());
            ColorSwatch.Background = new SolidColorBrush(color);
        }
        catch (FormatException)
        {
            ColorSwatch.Background = Brushes.Transparent;
        }
        catch (NotSupportedException)
        {
            ColorSwatch.Background = Brushes.Transparent;
        }
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInputs(out var error))
        {
            StatusText.Text = error;
            return;
        }

        TestButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        StatusText.Text = "Testing connection...";

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _syncService.TestAsync(BuildSnapshot(), cts.Token);
            StatusText.Text = "Connection succeeded. Container is ready.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Connection test timed out.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Connection failed: {ex.Message}";
        }
        finally
        {
            TestButton.IsEnabled = true;
            SaveButton.IsEnabled = true;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInputs(out var error))
        {
            StatusText.Text = error;
            return;
        }

        _settings.SyncConnectionStringProtected = SecretProtector.Protect(ConnectionStringBox.Text.Trim());
        _settings.SyncContainerName = ContainerBox.Text.Trim();
        _settings.DeviceId ??= Guid.NewGuid().ToString("N");
        _settings.DeviceName = DeviceNameBox.Text.Trim();
        _settings.DeviceColor = string.IsNullOrWhiteSpace(DeviceColorBox.Text)
            ? PickDefaultColor(_settings.DeviceId)
            : DeviceColorBox.Text.Trim();

        DialogResult = true;
    }
}
