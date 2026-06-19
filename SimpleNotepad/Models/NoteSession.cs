namespace SimpleNotepad.Models;

public sealed class NoteSession
{
    public required string Id { get; set; }

    public required string Title { get; set; }

    public required string FilePath { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public bool IsPinned { get; set; }
}
