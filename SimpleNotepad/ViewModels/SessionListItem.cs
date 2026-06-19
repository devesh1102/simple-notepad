using SimpleNotepad.Models;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

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

    public bool IsRemote => Session.IsRemote;

    public string Preview => string.IsNullOrWhiteSpace(Session.Preview) ? "No content yet" : Session.Preview;

    public string LastModifiedText => Session.UpdatedAt.LocalDateTime.ToString("g");

    public System.Windows.Visibility OriginVisibility =>
        Session.IsRemote ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public string OriginLabel =>
        Session.IsRemote
            ? $"🔒 {(string.IsNullOrWhiteSpace(Session.OriginDeviceName) ? "other device" : Session.OriginDeviceName)}"
            : string.Empty;

    public Brush AccentBrush
    {
        get
        {
            var hex = Session.IsRemote ? Session.OriginDeviceColor : null;
            if (string.IsNullOrWhiteSpace(hex))
            {
                return Brushes.Transparent;
            }

            try
            {
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            }
            catch (FormatException)
            {
                return Brushes.Transparent;
            }
            catch (NotSupportedException)
            {
                return Brushes.Transparent;
            }
        }
    }

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
