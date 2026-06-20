using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SimpleNotepad.Models;

namespace SimpleNotepad.Services;

public sealed class AppSettingsService
{
    private const string AppFolderName = "SimpleNotepad";
    private const string SettingsFileName = "settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // Defensive: never let a stray non-finite double (e.g. NaN/Infinity from window
        // metrics) make a settings save throw and surface as a "could not save" error.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly string _settingsPath;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public AppSettingsService()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _settingsPath = Path.Combine(localAppData, AppFolderName, SettingsFileName);
    }

    /// <summary>Test/diagnostic overload that targets an explicit settings file path.</summary>
    public AppSettingsService(string settingsPath)
    {
        if (string.IsNullOrWhiteSpace(settingsPath))
        {
            throw new ArgumentException("Settings path is required.", nameof(settingsPath));
        }

        _settingsPath = settingsPath;
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsPath))
        {
            return await TryLoadBackupAsync(cancellationToken) ?? new AppSettings();
        }

        try
        {
            await using var stream = File.OpenRead(_settingsPath);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken) ?? new AppSettings();
        }
        catch (JsonException)
        {
            // Settings are corrupt: preserve the bad file for diagnostics and try the last-good backup
            // rather than silently discarding all settings (and the protected secrets within).
            QuarantineFile(_settingsPath);
            return await TryLoadBackupAsync(cancellationToken) ?? new AppSettings();
        }
        catch (DecoderFallbackException)
        {
            QuarantineFile(_settingsPath);
            return await TryLoadBackupAsync(cancellationToken) ?? new AppSettings();
        }
        catch (IOException)
        {
            return await TryLoadBackupAsync(cancellationToken) ?? new AppSettings();
        }
        catch (UnauthorizedAccessException)
        {
            return await TryLoadBackupAsync(cancellationToken) ?? new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_settingsPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Cannot determine settings directory.");
        }

        await _saveLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var tempPath = Path.Combine(directory, $"{SettingsFileName}.{Guid.NewGuid():N}.tmp");

            try
            {
                await File.WriteAllTextAsync(tempPath, json, StrictUtf8, cancellationToken);

                if (File.Exists(_settingsPath))
                {
                    // Atomically swap in the new file while keeping the previous good copy as a backup.
                    File.Replace(tempPath, _settingsPath, BackupPath, ignoreMetadataErrors: true);
                    return;
                }

                File.Move(tempPath, _settingsPath);
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
        finally
        {
            _saveLock.Release();
        }
    }

    private string BackupPath => _settingsPath + ".bak";

    private async Task<AppSettings?> TryLoadBackupAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(BackupPath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(BackupPath);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            QuarantineFile(BackupPath);
            return null;
        }
        catch (DecoderFallbackException)
        {
            QuarantineFile(BackupPath);
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void QuarantineFile(string path)
    {
        try
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
            File.Move(path, quarantinePath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
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
}
