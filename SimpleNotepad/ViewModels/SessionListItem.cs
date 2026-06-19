using SimpleNotepad.Models;

namespace SimpleNotepad.ViewModels;

public sealed class SessionListItem
{
    public SessionListItem(NoteSession session)
    {
        Session = session;
    }

    public NoteSession Session { get; }

    public string Id => Session.Id;

    public string Title => Session.Title;

    public string Preview => string.IsNullOrWhiteSpace(Session.Preview) ? "No content yet" : Session.Preview;

    public string LastModifiedText => Session.UpdatedAt.LocalDateTime.ToString("g");

    public string ExpiryText
    {
        get
        {
            if (Session.IsPinned)
            {
                return "pinned";
            }

            var remaining = Session.ExpiresAt - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return "expires today";
            }

            var days = Math.Max(1, (int)Math.Ceiling(remaining.TotalDays));
            return days == 1 ? "expires in 1 day" : $"expires in {days} days";
        }
    }
}
