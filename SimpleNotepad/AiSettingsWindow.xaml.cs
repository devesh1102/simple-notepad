using System.Threading;
using System.Windows;
using SimpleNotepad.Models;
using SimpleNotepad.Services;

namespace SimpleNotepad;

public partial class AiSettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly AiRewriteService _rewriteService;

    public AiSettingsWindow(AppSettings settings, AiRewriteService rewriteService)
    {
        InitializeComponent();

        _settings = settings;
        _rewriteService = rewriteService;

        EndpointBox.Text = settings.AiEndpoint ?? string.Empty;
        DeploymentBox.Text = settings.AiDeployment ?? string.Empty;
        TemperatureSlider.Value = settings.AiTemperature;

        var existingKey = SecretProtector.Unprotect(settings.AiApiKeyProtected);
        if (!string.IsNullOrEmpty(existingKey))
        {
            ApiKeyBox.Password = existingKey;
        }
    }

    private AppSettings BuildSettingsSnapshot()
    {
        return new AppSettings
        {
            AiEndpoint = EndpointBox.Text.Trim(),
            AiDeployment = DeploymentBox.Text.Trim(),
            AiTemperature = TemperatureSlider.Value,
            AiApiKeyProtected = SecretProtector.Protect(ApiKeyBox.Password),
        };
    }

    private bool ValidateInputs(out string error)
    {
        if (string.IsNullOrWhiteSpace(EndpointBox.Text))
        {
            error = "Endpoint is required.";
            return false;
        }

        if (!Uri.TryCreate(EndpointBox.Text.Trim(), UriKind.Absolute, out var endpointUri) ||
            endpointUri.Scheme != Uri.UriSchemeHttps)
        {
            error = "Endpoint must be a valid HTTPS URL (e.g. https://my-resource.openai.azure.com/).";
            return false;
        }

        if (string.IsNullOrWhiteSpace(DeploymentBox.Text))
        {
            error = "Deployment name is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(ApiKeyBox.Password))
        {
            error = "API key is required.";
            return false;
        }

        error = string.Empty;
        return true;
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
            await _rewriteService.TestAsync(BuildSettingsSnapshot(), cts.Token);
            StatusText.Text = "Connection succeeded.";
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

        _settings.AiEndpoint = EndpointBox.Text.Trim();
        _settings.AiDeployment = DeploymentBox.Text.Trim();
        _settings.AiTemperature = TemperatureSlider.Value;
        _settings.AiApiKeyProtected = SecretProtector.Protect(ApiKeyBox.Password);

        DialogResult = true;
    }
}
