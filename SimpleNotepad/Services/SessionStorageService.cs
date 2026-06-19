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
            return ValidateSessions(sessions ?? []);
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
        catch (IOException)
        {
            return await TryLoadBackupIndexAsync(cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
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

    public async Task<IReadOnlyList<NoteSession>> PurgeExpiredSessionsAsync(
        IReadOnlyList<NoteSession> sessions,
        CancellationToken cancellationToken = default)
    {
        var normalizedSessions = sessions.ToList();
        var now = DateTimeOffset.UtcNow;
        var changed = false;

        foreach (var session in normalizedSessions.Where(session => session.ExpiresAt == default))
        {
            session.ExpiresAt = now.AddDays(7);
            changed = true;
        }

        var expired = normalizedSessions.Where(s => !s.IsPinned && s.ExpiresAt <= now).ToList();
        var remaining = normalizedSessions.Where(s => s.IsPinned || s.ExpiresAt > now).ToList();

        if (expired.Count == 0)
        {
            if (changed)
            {
                try
                {
                    await SaveIndexAsync(normalizedSessions, cancellationToken);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return normalizedSessions;
        }

        try
        {
            await SaveIndexAsync(remaining, cancellationToken);
        }
        catch (IOException)
        {
            return normalizedSessions;
        }
        catch (UnauthorizedAccessException)
        {
            return normalizedSessions;
        }

        foreach (var session in expired)
        {
            await DeleteSessionAsync(session, cancellationToken);
        }

        return remaining;
    }

    public Task DeleteSessionAsync(NoteSession session, CancellationToken cancellationToken = default)
    {
        EnsureStorageFolders();
        var path = GetSessionContentPath(session);
        var backupPath = GetBackupPath(path);

        TryDeleteFile(path);
        TryDeleteFile(backupPath);
        PruneReplacementBackups(backupPath);

        return Task.CompletedTask;
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
            Preview = string.Empty,
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
        if (session is null)
        {
            throw new InvalidOperationException("Session cannot be null.");
        }

        return ResolveSessionContentPath(session.FilePath);
    }

    private IReadOnlyList<NoteSession> ValidateSessions(IEnumerable<NoteSession?> sessions)
    {
        return sessions.Where(IsValidSession).Cast<NoteSession>().ToList();
    }

    private bool IsValidSession(NoteSession? session)
    {
        if (session is null ||
            string.IsNullOrWhiteSpace(session.Id) ||
            string.IsNullOrWhiteSpace(session.Title) ||
            string.IsNullOrWhiteSpace(session.FilePath))
        {
            return false;
        }

        try
        {
            _ = ResolveSessionContentPath(session.FilePath);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private string ResolveSessionContentPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new InvalidOperationException("Session file path cannot be empty.");
        }

        if (Path.IsPathRooted(filePath))
        {
            throw new InvalidOperationException("Session file paths must be relative to the app storage folder.");
        }

        string fullPath;
        string allowedRoot;

        try
        {
            fullPath = Path.GetFullPath(Path.Combine(_rootPath, filePath));
            allowedRoot = Path.GetFullPath(_sessionsPath);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("Session file path is malformed.", exception);
        }
        catch (NotSupportedException exception)
        {
            throw new InvalidOperationException("Session file path is not supported.", exception);
        }
        catch (PathTooLongException exception)
        {
            throw new InvalidOperationException("Session file path is too long.", exception);
        }

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
        foreach (var backupPath in GetBackupCandidates(_indexPath))
        {
            try
            {
                await using var stream = File.OpenRead(backupPath);
                var sessions = await JsonSerializer.DeserializeAsync<List<NoteSession>>(stream, JsonOptions, cancellationToken);
                return ValidateSessions(sessions ?? []);
            }
            catch (JsonException)
            {
                await QuarantineFileAsync(backupPath, cancellationToken);
            }
            catch (DecoderFallbackException)
            {
                await QuarantineFileAsync(backupPath, cancellationToken);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

        }

        return [];
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

        try
        {
            await File.WriteAllTextAsync(tempPath, content, StrictUtf8, cancellationToken);

            if (File.Exists(path))
            {
                var replacementBackupPath = $"{backupPath}.{Guid.NewGuid():N}.replace";
                File.Replace(tempPath, path, replacementBackupPath, ignoreMetadataErrors: true);
                var promoted = PromoteReplacementBackup(replacementBackupPath, backupPath);
                PruneReplacementBackups(backupPath, promoted ? null : replacementBackupPath);
                return;
            }

            File.Move(tempPath, path);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                TryDeleteFile(tempPath);
            }

            throw;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool PromoteReplacementBackup(string replacementBackupPath, string backupPath)
    {
        try
        {
            if (File.Exists(backupPath))
            {
                File.Replace(replacementBackupPath, backupPath, null, ignoreMetadataErrors: true);
                return true;
            }

            File.Move(replacementBackupPath, backupPath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<string> LoadBackupContentAsync(
        string path,
        Exception originalException,
        CancellationToken cancellationToken)
    {
        foreach (var backupPath in GetBackupCandidates(path))
        {
            try
            {
                return await File.ReadAllTextAsync(backupPath, StrictUtf8, cancellationToken);
            }
            catch (DecoderFallbackException)
            {
                await QuarantineFileAsync(backupPath, cancellationToken);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        throw new InvalidOperationException($"Session content and backups could not be read: {path}", originalException);
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

        try
        {
            await Task.Run(() => File.Move(path, quarantinePath), CancellationToken.None);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static IReadOnlyList<string> GetBackupCandidates(string path)
    {
        var backupPath = GetBackupPath(path);
        var candidates = new List<string>();

        if (File.Exists(backupPath))
        {
            candidates.Add(backupPath);
        }

        candidates.AddRange(GetReplacementBackups(backupPath));

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(GetLastWriteTimeOrMinValue)
            .ToList();
    }

    private static IReadOnlyList<string> GetReplacementBackups(string backupPath)
    {
        var directory = Path.GetDirectoryName(backupPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            return Directory.GetFiles(directory, $"{Path.GetFileName(backupPath)}.*.replace");
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void PruneReplacementBackups(string backupPath, string? keepPath = null)
    {
        foreach (var replacementBackup in GetReplacementBackups(backupPath))
        {
            if (keepPath is not null && string.Equals(replacementBackup, keepPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            TryDeleteFile(replacementBackup);
        }
    }

    private static DateTime GetLastWriteTimeOrMinValue(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (IOException)
        {
            return DateTime.MinValue;
        }
        catch (UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    private static string GetBackupPath(string path) => $"{path}.bak";
}
