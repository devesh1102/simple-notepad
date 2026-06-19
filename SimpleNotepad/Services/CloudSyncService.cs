using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using SimpleNotepad.Models;

namespace SimpleNotepad.Services;

/// <summary>
/// Mirrors note sessions to a private Azure Blob container using a PER-DEVICE OWNERSHIP model:
/// each device only writes its own sessions, so no content merge is ever required. Other
/// devices' sessions are pulled as read-only mirrors. The Blob SDK is only touched when the
/// user actually syncs, so non-sync users pay no startup cost.
/// </summary>
public sealed class CloudSyncService
{
    private const int SchemaVersion = 1;
    private const string ManifestPrefix = "manifests/";
    private const string ContentPrefix = "sessions/";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public bool IsConfigured(AppSettings settings)
    {
        return !string.IsNullOrWhiteSpace(settings.SyncConnectionStringProtected)
            && !string.IsNullOrWhiteSpace(settings.SyncContainerName)
            && !string.IsNullOrWhiteSpace(settings.DeviceId);
    }

    public async Task TestAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var container = CreateContainerClient(settings);
        await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);

        var metaBlob = container.GetBlobClient("meta.json");
        var meta = BinaryData.FromString(JsonSerializer.Serialize(new { schemaVersion = SchemaVersion }, JsonOptions));
        await metaBlob.UploadAsync(meta, overwrite: true, cancellationToken);
    }

    public async Task<SyncResult> SyncAsync(
        AppSettings settings,
        IReadOnlyList<NoteSession> localSessions,
        SessionStorageService storage,
        CancellationToken cancellationToken)
    {
        var myDeviceId = settings.DeviceId
            ?? throw new InvalidOperationException("This device has no sync identity. Configure sync first.");

        var container = CreateContainerClient(settings);
        await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);

        var owned = localSessions
            .Where(s => !s.IsRemote && (string.IsNullOrEmpty(s.OriginDeviceId) || s.OriginDeviceId == myDeviceId))
            .ToList();

        var pushed = await PushAsync(container, settings, myDeviceId, owned, storage, cancellationToken);

        var (mirrors, deviceCount, pulled) = await PullAsync(
            container, myDeviceId, localSessions, storage, cancellationToken);

        var result = new List<NoteSession>(owned.Count + mirrors.Count);
        result.AddRange(owned);
        result.AddRange(mirrors);

        var summary = $"Pushed {pushed}, pulled {pulled} from {deviceCount} other device(s).";
        return new SyncResult(result, summary);
    }

    private async Task<int> PushAsync(
        BlobContainerClient container,
        AppSettings settings,
        string myDeviceId,
        IReadOnlyList<NoteSession> owned,
        SessionStorageService storage,
        CancellationToken cancellationToken)
    {
        var previous = await TryDownloadManifestAsync(container, myDeviceId, cancellationToken);
        var previousLive = previous?.Sessions
            .Where(e => !e.Deleted)
            .ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);

        // Only sessions the user EXPLICITLY deleted should be tombstoned (and their content
        // removed everywhere). A session that merely expired locally (7-day inactivity purge)
        // must NOT be deleted from the cloud or from other devices' mirrors.
        var explicitDeletions = (settings.PendingSyncDeletions ?? new List<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var currentIds = owned.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pushedCount = 0;
        var entries = new List<ManifestEntry>(owned.Count);

        foreach (var session in owned)
        {
            cancellationToken.ThrowIfCancellationRequested();

            session.OriginDeviceId = myDeviceId;

            var content = await storage.LoadContentAsync(session, cancellationToken);
            var hash = ComputeHash(content);
            var contentBlob = container.GetBlobClient($"{ContentPrefix}{session.Id}.txt");

            var needsUpload = session.Dirty
                || !string.Equals(session.LastSyncedContentHash, hash, StringComparison.Ordinal)
                || !await contentBlob.ExistsAsync(cancellationToken);

            if (needsUpload)
            {
                await contentBlob.UploadAsync(BinaryData.FromString(content), overwrite: true, cancellationToken);
                session.LastSyncedContentHash = hash;
                pushedCount++;
            }

            session.Dirty = false;

            entries.Add(new ManifestEntry
            {
                Id = session.Id,
                Title = session.Title,
                Preview = session.Preview,
                Pinned = session.IsPinned,
                CreatedAt = session.CreatedAt,
                UpdatedAt = session.UpdatedAt,
                ExpiresAt = session.ExpiresAt,
                ContentHash = hash,
                Deleted = false,
            });
        }

        foreach (var previousId in previousLive.Keys.Where(id => !currentIds.Contains(id)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (explicitDeletions.Contains(previousId))
            {
                // User deleted this owned session: tombstone it and remove its content blob.
                await container.GetBlobClient($"{ContentPrefix}{previousId}.txt")
                    .DeleteIfExistsAsync(cancellationToken: cancellationToken);

                entries.Add(new ManifestEntry
                {
                    Id = previousId,
                    Title = string.Empty,
                    Deleted = true,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            }
            else
            {
                // Gone locally but not explicitly deleted (e.g. local 7-day expiry purge):
                // preserve the cloud copy so other devices keep their mirror.
                entries.Add(previousLive[previousId]);
            }
        }

        var manifest = new DeviceManifest
        {
            SchemaVersion = SchemaVersion,
            DeviceId = myDeviceId,
            DeviceName = settings.DeviceName ?? myDeviceId,
            Color = settings.DeviceColor,
            Sessions = entries,
        };

        var manifestBlob = container.GetBlobClient($"{ManifestPrefix}{myDeviceId}.json");
        var payload = BinaryData.FromString(JsonSerializer.Serialize(manifest, JsonOptions));
        await manifestBlob.UploadAsync(payload, overwrite: true, cancellationToken);

        // Explicit deletions are now durable tombstones in the cloud manifest; clear the ledger.
        settings.PendingSyncDeletions?.Clear();

        return pushedCount;
    }

    private async Task<(List<NoteSession> Mirrors, int DeviceCount, int Pulled)> PullAsync(
        BlobContainerClient container,
        string myDeviceId,
        IReadOnlyList<NoteSession> localSessions,
        SessionStorageService storage,
        CancellationToken cancellationToken)
    {
        var mirrors = new List<NoteSession>();
        var deviceCount = 0;
        var pulled = 0;

        await foreach (var blobItem in container.GetBlobsAsync(
            BlobTraits.None, BlobStates.None, ManifestPrefix, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = blobItem.Name[ManifestPrefix.Length..];
            var deviceId = fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^5]
                : fileName;

            if (string.Equals(deviceId, myDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var manifest = await TryDownloadManifestAsync(container, deviceId, cancellationToken);
            if (manifest is null)
            {
                continue;
            }

            deviceCount++;

            foreach (var entry in manifest.Sessions)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Never let another device's manifest entry clobber a session we own locally.
                // Owned sessions share the same on-disk path (sessions/<id>.txt); writing a mirror
                // for a colliding id would overwrite our own content. Skip such entries.
                var ownedLocal = localSessions.FirstOrDefault(
                    s => string.Equals(s.Id, entry.Id, StringComparison.OrdinalIgnoreCase) && !s.IsRemote);
                if (ownedLocal is not null)
                {
                    continue;
                }

                var existing = localSessions.FirstOrDefault(
                    s => string.Equals(s.Id, entry.Id, StringComparison.OrdinalIgnoreCase) && s.IsRemote);

                if (entry.Deleted)
                {
                    if (existing is not null)
                    {
                        await storage.DeleteSessionAsync(existing, cancellationToken);
                    }

                    continue;
                }

                var mirror = existing ?? new NoteSession
                {
                    Id = entry.Id,
                    Title = entry.Title,
                    FilePath = System.IO.Path.Combine("sessions", $"{entry.Id}.txt"),
                    CreatedAt = entry.CreatedAt,
                };

                mirror.Title = entry.Title;
                mirror.Preview = entry.Preview;
                mirror.UpdatedAt = entry.UpdatedAt;
                mirror.IsPinned = entry.Pinned;
                mirror.IsRemote = true;
                mirror.OriginDeviceId = deviceId;
                mirror.OriginDeviceColor = manifest.Color;
                mirror.OriginDeviceName = manifest.DeviceName;
                mirror.ExpiresAt = DateTimeOffset.MaxValue;

                if (!string.Equals(mirror.LastSyncedContentHash, entry.ContentHash, StringComparison.Ordinal))
                {
                    var contentBlob = container.GetBlobClient($"{ContentPrefix}{entry.Id}.txt");
                    if (await contentBlob.ExistsAsync(cancellationToken))
                    {
                        var download = await contentBlob.DownloadContentAsync(cancellationToken);
                        var content = download.Value.Content.ToString();
                        await storage.SaveContentAsync(mirror, content, cancellationToken);
                        mirror.LastSyncedContentHash = entry.ContentHash;
                        pulled++;
                    }
                }

                mirrors.Add(mirror);
            }
        }

        return (mirrors, deviceCount, pulled);
    }

    private static async Task<DeviceManifest?> TryDownloadManifestAsync(
        BlobContainerClient container,
        string deviceId,
        CancellationToken cancellationToken)
    {
        var blob = container.GetBlobClient($"{ManifestPrefix}{deviceId}.json");
        try
        {
            var download = await blob.DownloadContentAsync(cancellationToken);
            return JsonSerializer.Deserialize<DeviceManifest>(download.Value.Content.ToString(), JsonOptions);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static BlobContainerClient CreateContainerClient(AppSettings settings)
    {
        var connectionString = SecretProtector.Unprotect(settings.SyncConnectionStringProtected);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("The sync connection string is not configured or could not be read.");
        }

        var containerName = string.IsNullOrWhiteSpace(settings.SyncContainerName)
            ? "simplenotepad"
            : settings.SyncContainerName.Trim();

        var serviceClient = new BlobServiceClient(connectionString);
        return serviceClient.GetBlobContainerClient(containerName);
    }

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }

    private sealed class DeviceManifest
    {
        public int SchemaVersion { get; set; }

        public string DeviceId { get; set; } = string.Empty;

        public string DeviceName { get; set; } = string.Empty;

        public string? Color { get; set; }

        public List<ManifestEntry> Sessions { get; set; } = [];
    }

    private sealed class ManifestEntry
    {
        public string Id { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string Preview { get; set; } = string.Empty;

        public bool Pinned { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        public DateTimeOffset ExpiresAt { get; set; }

        public string ContentHash { get; set; } = string.Empty;

        public bool Deleted { get; set; }
    }
}

public sealed record SyncResult(IReadOnlyList<NoteSession> Sessions, string Summary);
