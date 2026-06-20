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
    private readonly bool _hasExistingApiKey;
    private readonly bool _hasExistingConnection;

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

        // AI fields. Secrets are never echoed back into the dialog; we only note when one is saved.
        AiEndpointBox.Text = settings.AiEndpoint ?? string.Empty;
        AiDeploymentBox.Text = settings.AiDeployment ?? string.Empty;
        AiTemperatureSlider.Value = settings.AiTemperature;
        _hasExistingApiKey = !string.IsNullOrWhiteSpace(settings.AiApiKeyProtected);
        if (_hasExistingApiKey)
        {
            AiKeyConfiguredText.Text = "A key is already saved. Leave blank to keep it, or type a new key to replace it.";
            AiKeyConfiguredText.Visibility = Visibility.Visible;
        }

        // Sync fields. The connection string is a secret, so it is never displayed; we only
        // surface the derived account endpoint so the user can confirm what is configured.
        SyncContainerBox.Text = string.IsNullOrWhiteSpace(settings.SyncContainerName) ? "simplenotepad" : settings.SyncContainerName;
        SyncDeviceNameBox.Text = string.IsNullOrWhiteSpace(settings.DeviceName) ? Environment.MachineName : settings.DeviceName;
        SyncDeviceColorBox.Text = string.IsNullOrWhiteSpace(settings.DeviceColor)
            ? PickDefaultColor(settings.DeviceId ?? Environment.MachineName)
            : settings.DeviceColor;
        _hasExistingConnection = !string.IsNullOrWhiteSpace(settings.SyncConnectionStringProtected);
        if (_hasExistingConnection)
        {
            var endpoint = DeriveAccountEndpoint(SecretProtector.Unprotect(settings.SyncConnectionStringProtected));
            SyncConfiguredAccountText.Text = endpoint is null
                ? "A connection string is already saved. Leave blank to keep it, or type a new one to replace it."
                : $"Saved account: {endpoint}\nLeave blank to keep it, or type a new connection string to replace it.";
            SyncConfiguredAccountText.Visibility = Visibility.Visible;
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
        || !string.IsNullOrWhiteSpace(AiApiKeyBox.Password)
        || _hasExistingApiKey;

    private bool IsSyncActive() =>
        _hasExistingConnection || !string.IsNullOrWhiteSpace(SyncConnectionStringBox.Password);

    private static string? DeriveAccountEndpoint(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        string? accountName = null;
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = part.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = part[..separatorIndex].Trim();
            var value = part[(separatorIndex + 1)..].Trim();
            if (key.Equals("BlobEndpoint", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            if (key.Equals("AccountName", StringComparison.OrdinalIgnoreCase))
            {
                accountName = value;
            }
        }

        return string.IsNullOrWhiteSpace(accountName)
            ? null
            : $"https://{accountName}.blob.core.windows.net";
    }

    private AppSettings BuildAiSnapshot()
    {
        return new AppSettings
        {
            AiEndpoint = AiEndpointBox.Text.Trim(),
            AiDeployment = AiDeploymentBox.Text.Trim(),
            AiTemperature = AiTemperatureSlider.Value,
            AiApiKeyProtected = string.IsNullOrWhiteSpace(AiApiKeyBox.Password)
                ? _settings.AiApiKeyProtected
                : SecretProtector.Protect(AiApiKeyBox.Password),
        };
    }

    private AppSettings BuildSyncSnapshot()
    {
        return new AppSettings
        {
            SyncConnectionStringProtected = string.IsNullOrWhiteSpace(SyncConnectionStringBox.Password)
                ? _settings.SyncConnectionStringProtected
                : SecretProtector.Protect(SyncConnectionStringBox.Password.Trim()),
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

        if (string.IsNullOrWhiteSpace(AiApiKeyBox.Password) && !_hasExistingApiKey)
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
            if (!string.IsNullOrWhiteSpace(AiApiKeyBox.Password))
            {
                _settings.AiApiKeyProtected = SecretProtector.Protect(AiApiKeyBox.Password);
            }
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

            if (!string.IsNullOrWhiteSpace(SyncConnectionStringBox.Password))
            {
                _settings.SyncConnectionStringProtected = SecretProtector.Protect(SyncConnectionStringBox.Password.Trim());
            }
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
