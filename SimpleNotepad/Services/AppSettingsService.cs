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

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        try
        {
            await using var stream = File.OpenRead(_settingsPath);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
        catch (IOException)
        {
            return new AppSettings();
        }
        catch (UnauthorizedAccessException)
        {
            return new AppSettings();
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
                    File.Replace(tempPath, _settingsPath, null, ignoreMetadataErrors: true);
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
