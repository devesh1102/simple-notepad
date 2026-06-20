using System.IO;
using System.Net.Sockets;
using SimpleNotepad.Models;
using SimpleNotepad.Services;
using Xunit;

namespace SimpleNotepad.Tests;

/// <summary>
/// Integration tests that exercise <see cref="CloudSyncService"/> against a real Azure Blob
/// endpoint. They target the Azurite emulator's well-known dev account and SKIP gracefully when
/// Azurite is not reachable (e.g. on a dev box without it running), so they never fail the build.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CloudSyncServiceIntegrationTests : IDisposable
{
    private const string AzuriteConnectionString =
        "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;" +
        "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;" +
        "BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;";

    private readonly string _tempDir;
    private readonly string _containerName;

    public CloudSyncServiceIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "snp-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _containerName = "snptest" + Guid.NewGuid().ToString("N")[..16];
    }

    private static bool AzuriteAvailable()
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.BeginConnect("127.0.0.1", 10000, null, null);
            var ok = connect.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(500));
            if (ok)
            {
                client.EndConnect(connect);
            }

            return ok;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private AppSettings CreateSettings(string deviceId, string deviceName)
    {
        return new AppSettings
        {
            SyncConnectionStringProtected = SecretProtector.Protect(AzuriteConnectionString),
            SyncContainerName = _containerName,
            DeviceId = deviceId,
            DeviceName = deviceName,
        };
    }

    [SkippableFact]
    public async Task Test_CreatesContainer()
    {
        Skip.IfNot(AzuriteAvailable(), "Azurite emulator is not running on 127.0.0.1:10000.");

        var service = new CloudSyncService();
        var settings = CreateSettings("device-a", "Device A");

        await service.TestAsync(settings, CancellationToken.None);

        Assert.True(service.IsConfigured(settings));
    }

    [SkippableFact]
    public async Task Sync_PushesOwnSession_AndOtherDevicePullsItReadOnly()
    {
        Skip.IfNot(AzuriteAvailable(), "Azurite emulator is not running on 127.0.0.1:10000.");

        var service = new CloudSyncService();

        // Device A owns and pushes one session.
        var storageA = new SessionStorageService(Path.Combine(_tempDir, "a"));
        var settingsA = CreateSettings("device-a", "Device A");
        var sessionA = storageA.CreateSession("Shared note");
        await storageA.SaveContentAsync(sessionA, "content from A");

        var resultA = await service.SyncAsync(settingsA, new[] { sessionA }, storageA, CancellationToken.None);
        Assert.Contains(resultA.Sessions, s => s.Id == sessionA.Id && !s.IsRemote);

        // Device B (no local sessions) pulls A's session as a read-only mirror.
        var storageB = new SessionStorageService(Path.Combine(_tempDir, "b"));
        var settingsB = CreateSettings("device-b", "Device B");

        var resultB = await service.SyncAsync(settingsB, Array.Empty<NoteSession>(), storageB, CancellationToken.None);

        var mirror = Assert.Single(resultB.Sessions, s => s.Id == sessionA.Id);
        Assert.True(mirror.IsRemote);
        Assert.Equal("device-a", mirror.OriginDeviceId);

        var mirroredContent = await storageB.LoadContentAsync(mirror);
        Assert.Equal("content from A", mirroredContent);
    }

    public void Dispose()
    {
        try
        {
            if (AzuriteAvailable())
            {
                var settings = CreateSettings("cleanup", "cleanup");
                var connectionString = SecretProtector.Unprotect(settings.SyncConnectionStringProtected);
                var container = new Azure.Storage.Blobs.BlobContainerClient(connectionString, _containerName);
                container.DeleteIfExists();
            }
        }
        catch (Exception)
        {
            // Best-effort cleanup.
        }

        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
