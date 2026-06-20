using System.IO;
using System.Text.Json;
using SimpleNotepad.Models;
using SimpleNotepad.Services;
using Xunit;

namespace SimpleNotepad.Tests;

public sealed class ProvisioningImporterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _provisioningPath;

    public ProvisioningImporterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "snp-prov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _provisioningPath = Path.Combine(_tempDir, "provisioning.json");
    }

    private void WriteProvisioning(object data)
    {
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        File.WriteAllText(_provisioningPath, json);
    }

    [Fact]
    public void TryImport_NoFile_ReturnsFalse()
    {
        var importer = new ProvisioningImporter(_provisioningPath);

        Assert.False(importer.TryImport(new AppSettings()));
    }

    [Fact]
    public void TryImport_FillsEmptyFields_AndEncryptsSecrets_AndDeletesFile()
    {
        WriteProvisioning(new
        {
            aiEndpoint = "https://example.openai.azure.com/",
            aiDeployment = "gpt-4o",
            aiApiKey = "secret-key",
            syncConnectionString = "UseDevelopmentStorage=true",
            syncContainer = "mycontainer",
            deviceName = "installer-box",
        });

        var settings = new AppSettings();
        var importer = new ProvisioningImporter(_provisioningPath);

        var changed = importer.TryImport(settings);

        Assert.True(changed);
        Assert.Equal("https://example.openai.azure.com/", settings.AiEndpoint);
        Assert.Equal("gpt-4o", settings.AiDeployment);

        // Secrets stored encrypted, never in plaintext, and decryptable back to original.
        Assert.NotNull(settings.AiApiKeyProtected);
        Assert.NotEqual("secret-key", settings.AiApiKeyProtected);
        Assert.Equal("secret-key", SecretProtector.Unprotect(settings.AiApiKeyProtected));

        Assert.NotNull(settings.SyncConnectionStringProtected);
        Assert.Equal("UseDevelopmentStorage=true", SecretProtector.Unprotect(settings.SyncConnectionStringProtected));
        Assert.Equal("mycontainer", settings.SyncContainerName);
        Assert.False(string.IsNullOrWhiteSpace(settings.DeviceId));
        Assert.Equal("installer-box", settings.DeviceName);

        Assert.False(File.Exists(_provisioningPath));
    }

    [Fact]
    public void TryImport_DoesNotClobberExistingValues()
    {
        WriteProvisioning(new
        {
            aiEndpoint = "https://new.openai.azure.com/",
            aiApiKey = "new-key",
        });

        var settings = new AppSettings
        {
            AiEndpoint = "https://existing.openai.azure.com/",
            AiApiKeyProtected = SecretProtector.Protect("existing-key"),
        };
        var importer = new ProvisioningImporter(_provisioningPath);

        importer.TryImport(settings);

        Assert.Equal("https://existing.openai.azure.com/", settings.AiEndpoint);
        Assert.Equal("existing-key", SecretProtector.Unprotect(settings.AiApiKeyProtected));
    }

    [Fact]
    public void TryImport_MalformedFile_ReturnsFalse_AndDeletesFile()
    {
        File.WriteAllText(_provisioningPath, "{ not valid json");
        var importer = new ProvisioningImporter(_provisioningPath);

        var changed = importer.TryImport(new AppSettings());

        Assert.False(changed);
        Assert.False(File.Exists(_provisioningPath));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
