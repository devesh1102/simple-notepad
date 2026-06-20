using System.IO;
using System.Text;
using System.Text.Json;
using SimpleNotepad.Models;

namespace SimpleNotepad.Services;

/// <summary>
/// First-run import of credentials dropped by the installer. The installer cannot encrypt
/// secrets for the end user (DPAPI is per-user and the installer runs elevated), so it writes
/// a short-lived plaintext provisioning file under %PROGRAMDATA%\SimpleNotepad. On first run the
/// app re-encrypts those secrets under the current user (DPAPI), saves them into settings.json,
/// then neutralises and deletes the provisioning file so the plaintext does not linger on disk.
/// </summary>
public sealed class ProvisioningImporter
{
    private const string AppFolderName = "SimpleNotepad";
    private const string ProvisioningFileName = "provisioning.json";

    private readonly string _provisioningPath;

    public ProvisioningImporter()
    {
        var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        _provisioningPath = Path.Combine(commonAppData, AppFolderName, ProvisioningFileName);
    }

    /// <summary>
    /// Imports installer-provisioned credentials into <paramref name="settings"/> if a
    /// provisioning file exists. Only fills fields that are currently empty so it never clobbers
    /// configuration the user has already set. Returns true when settings were modified.
    /// The provisioning file is always removed (best effort) once processed.
    /// </summary>
    public bool TryImport(AppSettings settings)
    {
        if (!File.Exists(_provisioningPath))
        {
            return false;
        }

        ProvisioningData? data = null;
        try
        {
            var json = File.ReadAllText(_provisioningPath);
            if (!string.IsNullOrWhiteSpace(json))
            {
                data = JsonSerializer.Deserialize<ProvisioningData>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        var changed = false;
        if (data is not null)
        {
            changed = Apply(settings, data);
        }

        Neutralise();
        return changed;
    }

    private static bool Apply(AppSettings settings, ProvisioningData data)
    {
        var changed = false;

        if (string.IsNullOrWhiteSpace(settings.AiEndpoint) && !string.IsNullOrWhiteSpace(data.AiEndpoint))
        {
            settings.AiEndpoint = data.AiEndpoint.Trim();
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.AiDeployment) && !string.IsNullOrWhiteSpace(data.AiDeployment))
        {
            settings.AiDeployment = data.AiDeployment.Trim();
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.AiApiKeyProtected) && !string.IsNullOrWhiteSpace(data.AiApiKey))
        {
            settings.AiApiKeyProtected = SecretProtector.Protect(data.AiApiKey.Trim());
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.SyncConnectionStringProtected) && !string.IsNullOrWhiteSpace(data.SyncConnectionString))
        {
            settings.SyncConnectionStringProtected = SecretProtector.Protect(data.SyncConnectionString.Trim());
            if (!string.IsNullOrWhiteSpace(data.SyncContainer))
            {
                settings.SyncContainerName = data.SyncContainer.Trim();
            }

            settings.DeviceId ??= Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(settings.DeviceName))
            {
                settings.DeviceName = string.IsNullOrWhiteSpace(data.DeviceName)
                    ? Environment.MachineName
                    : data.DeviceName.Trim();
            }

            changed = true;
        }

        return changed;
    }

    private void Neutralise()
    {
        // Overwrite the plaintext before deleting so the secrets are not recoverable even if the
        // delete is denied (e.g. locked file). Both steps are best effort.
        try
        {
            File.WriteAllText(_provisioningPath, "{}", new UTF8Encoding(false));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        try
        {
            File.Delete(_provisioningPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class ProvisioningData
    {
        public string? AiEndpoint { get; set; }

        public string? AiDeployment { get; set; }

        public string? AiApiKey { get; set; }

        public string? SyncConnectionString { get; set; }

        public string? SyncContainer { get; set; }

        public string? DeviceName { get; set; }
    }
}
