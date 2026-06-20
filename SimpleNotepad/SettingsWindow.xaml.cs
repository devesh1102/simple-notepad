using System.Threading;
using System.Windows;
using SimpleNotepad.Models;
using SimpleNotepad.Services;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace SimpleNotepad;

public enum SettingsSection
{
    Ai,
    Sync,
}

public partial class SettingsWindow : Window
{
    private static readonly string[] DevicePalette =
    [
        "#4EC9B0", "#569CD6", "#C586C0", "#D7BA7D", "#CE9178", "#6A9955", "#D16969", "#9CDCFE",
    ];

    private readonly AppSettings _settings;
    private readonly AiRewriteService _rewriteService;
    private readonly CloudSyncService _syncService;
    private readonly bool _isLightTheme;

    public SettingsWindow(
        AppSettings settings,
        AiRewriteService rewriteService,
        CloudSyncService syncService,
        SettingsSection focus = SettingsSection.Ai,
        bool isLightTheme = false)
    {
        InitializeComponent();

        _settings = settings;
        _rewriteService = rewriteService;
        _syncService = syncService;
        _isLightTheme = isLightTheme;

        // AI fields.
        AiEndpointBox.Text = settings.AiEndpoint ?? string.Empty;
        AiDeploymentBox.Text = settings.AiDeployment ?? string.Empty;
        AiTemperatureSlider.Value = settings.AiTemperature;
        var existingKey = SecretProtector.Unprotect(settings.AiApiKeyProtected);
        if (!string.IsNullOrEmpty(existingKey))
        {
            AiApiKeyBox.Password = existingKey;
        }

        // Sync fields.
        SyncContainerBox.Text = string.IsNullOrWhiteSpace(settings.SyncContainerName) ? "simplenotepad" : settings.SyncContainerName;
        SyncDeviceNameBox.Text = string.IsNullOrWhiteSpace(settings.DeviceName) ? Environment.MachineName : settings.DeviceName;
        SyncDeviceColorBox.Text = string.IsNullOrWhiteSpace(settings.DeviceColor)
            ? PickDefaultColor(settings.DeviceId ?? Environment.MachineName)
            : settings.DeviceColor;
        var existingConnection = SecretProtector.Unprotect(settings.SyncConnectionStringProtected);
        if (!string.IsNullOrEmpty(existingConnection))
        {
            SyncConnectionStringBox.Text = existingConnection;
        }

        if (focus == SettingsSection.Sync)
        {
            Loaded += (_, _) => SyncConnectionStringBox.BringIntoView();
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TitleBarThemer.Apply(this, _isLightTheme);
    }

    private static string PickDefaultColor(string seed)
    {
        var index = Math.Abs(seed.GetHashCode()) % DevicePalette.Length;
        return DevicePalette[index];
    }

    private bool IsAiActive() =>
        !string.IsNullOrWhiteSpace(AiEndpointBox.Text)
        || !string.IsNullOrWhiteSpace(AiDeploymentBox.Text)
        || !string.IsNullOrWhiteSpace(AiApiKeyBox.Password);

    private bool IsSyncActive() => !string.IsNullOrWhiteSpace(SyncConnectionStringBox.Text);

    private AppSettings BuildAiSnapshot()
    {
        return new AppSettings
        {
            AiEndpoint = AiEndpointBox.Text.Trim(),
            AiDeployment = AiDeploymentBox.Text.Trim(),
            AiTemperature = AiTemperatureSlider.Value,
            AiApiKeyProtected = SecretProtector.Protect(AiApiKeyBox.Password),
        };
    }

    private AppSettings BuildSyncSnapshot()
    {
        return new AppSettings
        {
            SyncConnectionStringProtected = SecretProtector.Protect(SyncConnectionStringBox.Text.Trim()),
            SyncContainerName = SyncContainerBox.Text.Trim(),
            DeviceId = _settings.DeviceId ?? Guid.NewGuid().ToString("N"),
            DeviceName = SyncDeviceNameBox.Text.Trim(),
            DeviceColor = SyncDeviceColorBox.Text.Trim(),
        };
    }

    private bool ValidateAi(out string error)
    {
        if (!Uri.TryCreate(AiEndpointBox.Text.Trim(), UriKind.Absolute, out var endpointUri) ||
            endpointUri.Scheme != Uri.UriSchemeHttps)
        {
            error = "AI endpoint must be a valid HTTPS URL (e.g. https://my-resource.openai.azure.com/).";
            return false;
        }

        if (string.IsNullOrWhiteSpace(AiDeploymentBox.Text))
        {
            error = "AI deployment name is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(AiApiKeyBox.Password))
        {
            error = "AI API key is required.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private bool ValidateSync(out string error)
    {
        if (string.IsNullOrWhiteSpace(SyncContainerBox.Text))
        {
            error = "A sync container name is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(SyncDeviceNameBox.Text))
        {
            error = "A device name is required.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private void SyncDeviceColorBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (ColorSwatch is null)
        {
            return;
        }

        try
        {
            var color = (Color)ColorConverter.ConvertFromString(SyncDeviceColorBox.Text.Trim());
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

    private async void AiTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (!IsAiActive())
        {
            AiStatusText.Text = "Enter the AI endpoint, deployment and key first.";
            return;
        }

        if (!ValidateAi(out var error))
        {
            AiStatusText.Text = error;
            return;
        }

        AiTestButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        AiStatusText.Text = "Testing connection...";

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _rewriteService.TestAsync(BuildAiSnapshot(), cts.Token);
            AiStatusText.Text = "Connection succeeded.";
        }
        catch (OperationCanceledException)
        {
            AiStatusText.Text = "Connection test timed out.";
        }
        catch (Exception ex)
        {
            AiStatusText.Text = $"Connection failed: {ex.Message}";
        }
        finally
        {
            AiTestButton.IsEnabled = true;
            SaveButton.IsEnabled = true;
        }
    }

    private async void SyncTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (!IsSyncActive())
        {
            SyncStatusText.Text = "Enter a storage connection string first.";
            return;
        }

        if (!ValidateSync(out var error))
        {
            SyncStatusText.Text = error;
            return;
        }

        SyncTestButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        SyncStatusText.Text = "Testing connection...";

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _syncService.TestAsync(BuildSyncSnapshot(), cts.Token);
            SyncStatusText.Text = "Connection succeeded. Container is ready.";
        }
        catch (OperationCanceledException)
        {
            SyncStatusText.Text = "Connection test timed out.";
        }
        catch (Exception ex)
        {
            SyncStatusText.Text = $"Connection failed: {ex.Message}";
        }
        finally
        {
            SyncTestButton.IsEnabled = true;
            SaveButton.IsEnabled = true;
        }
    }

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppLogger.LogFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppLogger.LogFolder,
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            AppLogger.Error("Could not open logs folder.", exception);
            System.Windows.MessageBox.Show(
                $"Could not open the logs folder.\n\n{exception.Message}",
                "Simple Notepad",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // AI section.
        if (IsAiActive())
        {
            if (!ValidateAi(out var aiError))
            {
                AiStatusText.Text = aiError;
                AiEndpointBox.BringIntoView();
                return;
            }

            _settings.AiEndpoint = AiEndpointBox.Text.Trim();
            _settings.AiDeployment = AiDeploymentBox.Text.Trim();
            _settings.AiTemperature = AiTemperatureSlider.Value;
            _settings.AiApiKeyProtected = SecretProtector.Protect(AiApiKeyBox.Password);
        }
        else
        {
            _settings.AiEndpoint = null;
            _settings.AiDeployment = null;
            _settings.AiApiKeyProtected = null;
            _settings.AiTemperature = AiTemperatureSlider.Value;
        }

        // Sync section.
        if (IsSyncActive())
        {
            if (!ValidateSync(out var syncError))
            {
                SyncStatusText.Text = syncError;
                SyncConnectionStringBox.BringIntoView();
                return;
            }

            _settings.SyncConnectionStringProtected = SecretProtector.Protect(SyncConnectionStringBox.Text.Trim());
            _settings.SyncContainerName = SyncContainerBox.Text.Trim();
            _settings.DeviceId ??= Guid.NewGuid().ToString("N");
            _settings.DeviceName = SyncDeviceNameBox.Text.Trim();
            _settings.DeviceColor = string.IsNullOrWhiteSpace(SyncDeviceColorBox.Text)
                ? PickDefaultColor(_settings.DeviceId)
                : SyncDeviceColorBox.Text.Trim();
        }
        else
        {
            _settings.SyncConnectionStringProtected = null;
        }

        DialogResult = true;
    }
}
