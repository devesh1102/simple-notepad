using System.IO;
using System.Text;
using System.Text.Json;
using SimpleNotepad.Models;

namespace SimpleNotepad.Services;

public sealed class SessionStorageService
{
    private const string AppFolderName = "SimpleNotepad";
    private const string SessionsFolderName = "sessions";
    private const string IndexFileName = "sessions.index.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly string _rootPath;
    private readonly string _sessionsPath;
    private readonly string _indexPath;

    public SessionStorageService()
        : this(GetDefaultRootPath())
    {
    }

    public SessionStorageService(string rootPath)
    {
        _rootPath = rootPath;
        _sessionsPath = Path.Combine(_rootPath, SessionsFolderName);
        _indexPath = Path.Combine(_rootPath, IndexFileName);
    }

    public string RootPath => _rootPath;

    public string SessionsPath => _sessionsPath;

    public async Task<IReadOnlyList<NoteSession>> LoadIndexAsync(CancellationToken cancellationToken = default)
    {
        EnsureStorageFolders();

        if (!File.Exists(_indexPath))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(_indexPath);
            var sessions = await JsonSerializer.DeserializeAsync<List<NoteSession>>(stream, JsonOptions, cancellationToken);
            return sessions ?? [];
        }
        catch (JsonException)
        {
            await QuarantineFileAsync(_indexPath, cancellationToken);
            return await TryLoadBackupIndexAsync(cancellationToken);
        }
        catch (DecoderFallbackException)
        {
            await QuarantineFileAsync(_indexPath, cancellationToken);
            return await TryLoadBackupIndexAsync(cancellationToken);
        }
    }

    public Task SaveIndexAsync(IEnumerable<NoteSession> sessions, CancellationToken cancellationToken = default)
    {
        EnsureStorageFolders();
        var json = JsonSerializer.Serialize(sessions, JsonOptions);
        return AtomicWriteTextAsync(_indexPath, json, cancellationToken);
    }

    public async Task<string> LoadContentAsync(NoteSession session, CancellationToken cancellationToken = default)
    {
        EnsureStorageFolders();
        var path = GetSessionContentPath(session);

        if (!File.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            return await File.ReadAllTextAsync(path, StrictUtf8, cancellationToken);
        }
        catch (DecoderFallbackException exception)
        {
            await QuarantineFileAsync(path, cancellationToken);
            throw new InvalidOperationException($"Session content was not valid UTF-8 and was moved to quarantine: {path}", exception);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException($"Session content could not be read: {path}", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidOperationException($"Session content could not be accessed: {path}", exception);
        }
    }

    public Task SaveContentAsync(NoteSession session, string content, CancellationToken cancellationToken = default)
    {
        EnsureStorageFolders();
        return AtomicWriteTextAsync(GetSessionContentPath(session), content, cancellationToken);
    }

    public NoteSession CreateSession(string? title = null)
    {
        EnsureStorageFolders();

        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N");

        return new NoteSession
        {
            Id = id,
            Title = string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim(),
            FilePath = Path.Combine(SessionsFolderName, $"{id}.txt"),
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now.AddDays(7),
            IsPinned = false
        };
    }

    private static string GetDefaultRootPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, AppFolderName);
    }

    private string GetSessionContentPath(NoteSession session)
    {
        if (Path.IsPathRooted(session.FilePath))
        {
            return session.FilePath;
        }

        return Path.Combine(_rootPath, session.FilePath);
    }

    private void EnsureStorageFolders()
    {
        Directory.CreateDirectory(_rootPath);
        Directory.CreateDirectory(_sessionsPath);
    }

    private async Task<IReadOnlyList<NoteSession>> TryLoadBackupIndexAsync(CancellationToken cancellationToken)
    {
        var backupPath = GetBackupPath(_indexPath);

        if (!File.Exists(backupPath))
        {
            return [];
        }

        await using var stream = File.OpenRead(backupPath);
        var sessions = await JsonSerializer.DeserializeAsync<List<NoteSession>>(stream, JsonOptions, cancellationToken);
        return sessions ?? [];
    }

    private static async Task AtomicWriteTextAsync(string path, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException($"Cannot determine directory for storage path: {path}");
        }

        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var backupPath = GetBackupPath(path);

        await File.WriteAllTextAsync(tempPath, content, StrictUtf8, cancellationToken);

        if (File.Exists(path))
        {
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }

            File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);
            return;
        }

        File.Move(tempPath, path);
    }

    private static async Task QuarantineFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var quarantinePath = Path.Combine(
            directory,
            $"{Path.GetFileName(path)}.corrupt.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}");

        await Task.Run(() => File.Move(path, quarantinePath), cancellationToken);
    }

    private static string GetBackupPath(string path) => $"{path}.bak";
}
