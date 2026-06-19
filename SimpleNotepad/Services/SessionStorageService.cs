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
            return await LoadBackupContentAsync(path, exception, cancellationToken);
        }
        catch (IOException exception)
        {
            return await LoadBackupContentAsync(path, exception, cancellationToken);
        }
        catch (UnauthorizedAccessException exception)
        {
            return await LoadBackupContentAsync(path, exception, cancellationToken);
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
            throw new InvalidOperationException("Session file paths must be relative to the app storage folder.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(_rootPath, session.FilePath));
        var allowedRoot = Path.GetFullPath(_sessionsPath);

        if (!fullPath.StartsWith(allowedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Session file path resolves outside the app sessions folder.");
        }

        return fullPath;
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

        try
        {
            await using var stream = File.OpenRead(backupPath);
            var sessions = await JsonSerializer.DeserializeAsync<List<NoteSession>>(stream, JsonOptions, cancellationToken);
            return sessions ?? [];
        }
        catch (JsonException)
        {
            await QuarantineFileAsync(backupPath, cancellationToken);
            return [];
        }
        catch (DecoderFallbackException)
        {
            await QuarantineFileAsync(backupPath, cancellationToken);
            return [];
        }
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

        try
        {
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
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }
    }

    private static async Task<string> LoadBackupContentAsync(
        string path,
        Exception originalException,
        CancellationToken cancellationToken)
    {
        var backupPath = GetBackupPath(path);

        if (!File.Exists(backupPath))
        {
            throw new InvalidOperationException($"Session content could not be read and no backup exists: {path}", originalException);
        }

        try
        {
            return await File.ReadAllTextAsync(backupPath, StrictUtf8, cancellationToken);
        }
        catch (DecoderFallbackException backupException)
        {
            await QuarantineFileAsync(backupPath, cancellationToken);
            throw new InvalidOperationException($"Session content and backup were unreadable: {path}", backupException);
        }
        catch (IOException backupException)
        {
            throw new InvalidOperationException($"Session content and backup could not be read: {path}", backupException);
        }
        catch (UnauthorizedAccessException backupException)
        {
            throw new InvalidOperationException($"Session content backup could not be accessed: {backupPath}", backupException);
        }
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

        var quarantinePath = Path.Combine(directory, $"{Path.GetFileName(path)}.corrupt.{Guid.NewGuid():N}");

        await Task.Run(() => File.Move(path, quarantinePath), cancellationToken);
    }

    private static string GetBackupPath(string path) => $"{path}.bak";
}
