using System.IO;
using SimpleNotepad.Models;
using SimpleNotepad.Services;
using Xunit;

namespace SimpleNotepad.Tests;

public sealed class AppSettingsServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public AppSettingsServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "snp-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsValues()
    {
        var service = new AppSettingsService(_settingsPath);
        var settings = new AppSettings
        {
            Theme = "Light",
            FontSize = 18,
            SidebarWidth = 333,
            DeviceName = "test-box",
            AiDeployment = "gpt-4o",
        };

        await service.SaveAsync(settings);
        var loaded = await service.LoadAsync();

        Assert.Equal("Light", loaded.Theme);
        Assert.Equal(18, loaded.FontSize);
        Assert.Equal(333, loaded.SidebarWidth);
        Assert.Equal("test-box", loaded.DeviceName);
        Assert.Equal("gpt-4o", loaded.AiDeployment);
    }

    [Fact]
    public async Task Save_NonFiniteDoubles_DoesNotThrowAndLoadsBack()
    {
        // Regression: window metrics could become NaN/Infinity and previously crashed the save.
        var service = new AppSettingsService(_settingsPath);
        var settings = new AppSettings
        {
            WindowWidth = double.NaN,
            WindowHeight = double.PositiveInfinity,
            WindowLeft = double.NegativeInfinity,
        };

        await service.SaveAsync(settings);
        var loaded = await service.LoadAsync();

        Assert.True(double.IsNaN(loaded.WindowWidth!.Value));
        Assert.True(double.IsPositiveInfinity(loaded.WindowHeight!.Value));
        Assert.True(double.IsNegativeInfinity(loaded.WindowLeft!.Value));
    }

    [Fact]
    public async Task Load_MissingFile_ReturnsDefaults()
    {
        var service = new AppSettingsService(_settingsPath);

        var loaded = await service.LoadAsync();

        Assert.Equal("Dark", loaded.Theme);
        Assert.True(loaded.WordWrap);
    }

    [Fact]
    public async Task Load_CorruptFile_ReturnsDefaults()
    {
        await File.WriteAllTextAsync(_settingsPath, "{ this is not valid json");
        var service = new AppSettingsService(_settingsPath);

        var loaded = await service.LoadAsync();

        Assert.Equal("Dark", loaded.Theme);
    }

    [Fact]
    public async Task Load_CorruptFile_RecoversFromBackup()
    {
        var service = new AppSettingsService(_settingsPath);

        // First save creates the file; second save rotates the previous good copy into the .bak.
        await service.SaveAsync(new AppSettings { Theme = "Light", DeviceName = "good-backup" });
        await service.SaveAsync(new AppSettings { Theme = "Light", DeviceName = "newer-good" });

        // Corrupt the primary settings file; the last-good backup should still be recoverable.
        await File.WriteAllTextAsync(_settingsPath, "{ not valid json at all");

        var loaded = await service.LoadAsync();

        Assert.Equal("Light", loaded.Theme);
        Assert.Equal("good-backup", loaded.DeviceName);
        Assert.True(File.Exists(_settingsPath + ".bak"));
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
