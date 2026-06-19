namespace SimpleNotepad.Models;

public sealed class AppSettings
{
    public string? LastSessionId { get; set; }

    public string Theme { get; set; } = "Dark";

    public bool WordWrap { get; set; } = true;

    public double FontSize { get; set; } = 14;

    public double SidebarWidth { get; set; } = 280;

    public double? WindowWidth { get; set; }

    public double? WindowHeight { get; set; }

    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    public bool WindowMaximized { get; set; }
}
