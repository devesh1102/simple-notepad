namespace SimpleNotepad.Models;

public sealed class NoteSession
{
    public required string Id { get; set; }

    public required string Title { get; set; }

    public required string FilePath { get; set; }

    public string Preview { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public bool IsPinned { get; set; }

    /// <summary>
    /// Stable id of the device that owns this session. Null/empty for legacy local sessions,
    /// which are treated as owned by the current device.
    /// </summary>
    public string? OriginDeviceId { get; set; }

    /// <summary>
    /// True when this is a read-only mirror of a session owned by another device.
    /// </summary>
    public bool IsRemote { get; set; }

    /// <summary>
    /// True when locally-owned content has changed since the last successful push.
    /// </summary>
    public bool Dirty { get; set; }

    /// <summary>
    /// SHA-256 (hex) of the content the last time it was synced; used for change detection.
    /// </summary>
    public string? LastSyncedContentHash { get; set; }

    /// <summary>
    /// Display color (hex) of the owning device, used to tint remote mirrors in the list.
    /// </summary>
    public string? OriginDeviceColor { get; set; }

    /// <summary>
    /// Display name of the owning device, shown on remote mirrors.
    /// </summary>
    public string? OriginDeviceName { get; set; }
}
