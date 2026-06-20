using System.IO;
using SimpleNotepad.Models;
using SimpleNotepad.Services;
using Xunit;

namespace SimpleNotepad.Tests;

public sealed class SessionStorageServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SessionStorageService _storage;

    public SessionStorageServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "snp-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _storage = new SessionStorageService(_tempDir);
    }

    [Fact]
    public async Task SaveContent_ThenLoad_RoundTrips()
    {
        var session = _storage.CreateSession("My note");

        await _storage.SaveContentAsync(session, "hello \u4e16\u754c");
        var loaded = await _storage.LoadContentAsync(session);

        Assert.Equal("hello \u4e16\u754c", loaded);
    }

    [Fact]
    public async Task SaveIndex_ThenLoad_RoundTrips()
    {
        var a = _storage.CreateSession("alpha");
        var b = _storage.CreateSession("beta");

        await _storage.SaveIndexAsync(new[] { a, b });
        var loaded = await _storage.LoadIndexAsync();

        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, s => s.Title == "alpha");
        Assert.Contains(loaded, s => s.Title == "beta");
    }

    [Fact]
    public async Task Purge_RemovesExpiredUnpinned_KeepsPinnedAndActive()
    {
        var now = DateTimeOffset.UtcNow;

        var expired = _storage.CreateSession("expired");
        expired.ExpiresAt = now.AddDays(-1);

        var pinnedExpired = _storage.CreateSession("pinned");
        pinnedExpired.ExpiresAt = now.AddDays(-1);
        pinnedExpired.IsPinned = true;

        var active = _storage.CreateSession("active");
        active.ExpiresAt = now.AddDays(3);

        await _storage.SaveContentAsync(expired, "gone");
        await _storage.SaveIndexAsync(new[] { expired, pinnedExpired, active });

        var remaining = await _storage.PurgeExpiredSessionsAsync(new[] { expired, pinnedExpired, active });

        Assert.DoesNotContain(remaining, s => s.Title == "expired");
        Assert.Contains(remaining, s => s.Title == "pinned");
        Assert.Contains(remaining, s => s.Title == "active");

        // Content of the purged session is deleted from disk.
        var expiredPath = Path.Combine(_tempDir, expired.FilePath);
        Assert.False(File.Exists(expiredPath));
    }

    [Theory]
    [InlineData("..\\escape.txt")]
    [InlineData("..\\..\\Windows\\evil.txt")]
    public async Task SaveContent_PathTraversal_Throws(string maliciousRelativePath)
    {
        var session = _storage.CreateSession("evil");
        session.FilePath = maliciousRelativePath;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _storage.SaveContentAsync(session, "payload"));
    }

    [Fact]
    public async Task SaveContent_RootedPath_Throws()
    {
        var session = _storage.CreateSession("rooted");
        session.FilePath = Path.Combine(Path.GetTempPath(), "absolute.txt");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _storage.SaveContentAsync(session, "payload"));
    }

    [Fact]
    public async Task LoadContent_MissingFile_ReturnsEmpty()
    {
        var session = _storage.CreateSession("none");

        var content = await _storage.LoadContentAsync(session);

        Assert.Equal(string.Empty, content);
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
