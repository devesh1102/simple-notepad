using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Media;
using SimpleNotepad.Models;
using SimpleNotepad.Services;
using SimpleNotepad.ViewModels;

namespace SimpleNotepad;

public partial class MainWindow : Window
{
    private readonly SessionStorageService _sessionStorage = new();
    private readonly AppSettingsService _settingsService = new();
    private readonly ObservableCollection<SessionListItem> _sessions = [];
    private readonly ICollectionView _sessionsView;

    private AppSettings _settings = new();
    private NoteSession? _currentSession;
    private bool _isLoadingSession;
    private bool _isClosingAfterSave;
    private bool _hasUnsavedContent;
    private bool _hasUnsavedIndex;

    public MainWindow()
    {
        InitializeComponent();
        _sessionsView = CollectionViewSource.GetDefaultView(_sessions);
        _sessionsView.Filter = FilterSession;
        SessionsList.ItemsSource = _sessionsView;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Editor.TextArea.Caret.PositionChanged += (_, _) => UpdateStatus();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = await _settingsService.LoadAsync();
        await LoadSessionsAsync();
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isClosingAfterSave)
        {
            return;
        }

        e.Cancel = true;

        try
        {
            await SaveCurrentSessionAsync();
            await SaveIndexIfNeededAsync();
            await _settingsService.SaveAsync(_settings);
            _isClosingAfterSave = true;
            Close();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"Simple Notepad could not save your latest changes:\n\n{exception.Message}",
                "Save failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task LoadSessionsAsync()
    {
        var sessions = (await _sessionStorage.LoadIndexAsync())
            .OrderByDescending(session => session.IsPinned)
            .ThenByDescending(session => session.UpdatedAt)
            .ToList();

        if (sessions.Count == 0)
        {
            var session = _sessionStorage.CreateSession();
            sessions.Add(session);
            await _sessionStorage.SaveContentAsync(session, string.Empty);
            await _sessionStorage.SaveIndexAsync(sessions);
        }

        ReplaceSessionItems(sessions);

        var selectedItem = _sessions.FirstOrDefault(item => item.Id == _settings.LastSessionId) ?? _sessions.FirstOrDefault();
        if (selectedItem is not null)
        {
            SessionsList.SelectedItem = selectedItem;
        }
    }

    private void ReplaceSessionItems(IEnumerable<NoteSession> sessions)
    {
        _sessions.Clear();
        foreach (var session in sessions
                     .OrderByDescending(session => session.IsPinned)
                     .ThenByDescending(session => session.UpdatedAt))
        {
            _sessions.Add(new SessionListItem(session));
        }

        _sessionsView.Refresh();
    }

    private bool FilterSession(object item)
    {
        if (item is not SessionListItem session)
        {
            return false;
        }

        var query = SessionSearchBox.Text;
        return string.IsNullOrWhiteSpace(query) ||
               session.Title.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private async void NewSessionButton_Click(object sender, RoutedEventArgs e)
    {
        await CreateNewSessionAsync();
    }

    private async Task CreateNewSessionAsync()
    {
        await SaveCurrentSessionAsync();

        var session = _sessionStorage.CreateSession();
        await _sessionStorage.SaveContentAsync(session, string.Empty);

        _sessions.Insert(0, new SessionListItem(session));
        await SaveIndexAsync();
        SessionsList.SelectedIndex = 0;
    }

    private async void SessionsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isLoadingSession || SessionsList.SelectedItem is not SessionListItem item)
        {
            return;
        }

        await SaveCurrentSessionAsync();
        await OpenSessionAsync(item.Session);
    }

    private async Task OpenSessionAsync(NoteSession session)
    {
        _isLoadingSession = true;
        try
        {
            _currentSession = session;
            SessionTitleBox.Text = session.Title;
            Editor.Text = await _sessionStorage.LoadContentAsync(session);
            _settings.LastSessionId = session.Id;
            _hasUnsavedContent = false;
            SaveStateText.Text = "Saved";
            UpdateStatus();
            await _settingsService.SaveAsync(_settings);
        }
        finally
        {
            _isLoadingSession = false;
        }
    }

    private async Task SaveCurrentSessionAsync()
    {
        if (_currentSession is null)
        {
            return;
        }

        var content = Editor.Text;
        var preview = CreatePreview(content);
        var title = string.IsNullOrWhiteSpace(SessionTitleBox.Text) ? "Untitled" : SessionTitleBox.Text.Trim();

        if (_hasUnsavedContent || _currentSession.Title != title || _currentSession.Preview != preview)
        {
            _currentSession.Title = title;
            _currentSession.Preview = preview;
            _currentSession.UpdatedAt = DateTimeOffset.UtcNow;
            _currentSession.ExpiresAt = _currentSession.UpdatedAt.AddDays(7);

            await _sessionStorage.SaveContentAsync(_currentSession, content);
            _hasUnsavedContent = false;
            _hasUnsavedIndex = true;
        }

        await SaveIndexIfNeededAsync();
        SaveStateText.Text = "Saved";
        UpdateSessionItems();
    }

    private async Task SaveIndexIfNeededAsync()
    {
        if (_hasUnsavedIndex)
        {
            await SaveIndexAsync();
        }
    }

    private async Task SaveIndexAsync()
    {
        await _sessionStorage.SaveIndexAsync(_sessions.Select(item => item.Session));
        _hasUnsavedIndex = false;
    }

    private void UpdateSessionItems()
    {
        var selectedId = _currentSession?.Id;
        ReplaceSessionItems(_sessions.Select(item => item.Session));
        SessionsList.SelectedItem = _sessions.FirstOrDefault(item => item.Id == selectedId);
    }

    private void Editor_TextChanged(object? sender, EventArgs e)
    {
        if (_isLoadingSession)
        {
            return;
        }

        _hasUnsavedContent = true;
        SaveStateText.Text = "Unsaved";
        UpdateStatus();
    }

    private void SessionTitleBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_isLoadingSession || _currentSession is null)
        {
            return;
        }

        _hasUnsavedIndex = true;
        SaveStateText.Text = "Unsaved";
    }

    private void SessionSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _sessionsView.Refresh();
    }

    private void RenameSession_Click(object sender, RoutedEventArgs e)
    {
        SessionTitleBox.Focus();
        SessionTitleBox.SelectAll();
    }

    private async void DeleteSession_Click(object sender, RoutedEventArgs e)
    {
        if (SessionsList.SelectedItem is not SessionListItem item)
        {
            return;
        }

        var result = MessageBox.Show(
            $"Delete \"{item.Title}\"?",
            "Delete session",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        await _sessionStorage.DeleteSessionAsync(item.Session);
        _sessions.Remove(item);
        _hasUnsavedIndex = true;

        if (_currentSession?.Id == item.Id)
        {
            _currentSession = null;
            _hasUnsavedContent = false;
            _isLoadingSession = true;
            try
            {
                SessionTitleBox.Text = string.Empty;
                Editor.Text = string.Empty;
            }
            finally
            {
                _isLoadingSession = false;
            }
        }

        await SaveIndexIfNeededAsync();

        if (_sessions.Count == 0)
        {
            await CreateNewSessionAsync();
            return;
        }

        SessionsList.SelectedIndex = 0;
    }

    private async void PinSession_Click(object sender, RoutedEventArgs e)
    {
        if (SessionsList.SelectedItem is not SessionListItem item)
        {
            return;
        }

        item.Session.IsPinned = !item.Session.IsPinned;
        _hasUnsavedIndex = true;
        await SaveIndexIfNeededAsync();
        UpdateSessionItems();
    }

    private void SessionsList_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource);
        if (item is not null)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void UpdateStatus()
    {
        var location = Editor.TextArea.Caret.Location;
        var state = _hasUnsavedContent || _hasUnsavedIndex ? "Unsaved" : "Saved";
        StatusText.Text = $"Ln {location.Line}, Col {location.Column} | {Editor.Text.Length} chars | {state}";
    }

    private static string CreatePreview(string content)
    {
        var firstLine = content
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.None)
            .Select(line => line.Trim())
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));

        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return string.Empty;
        }

        return firstLine.Length <= 80 ? firstLine : $"{firstLine[..80]}...";
    }

    private static T? FindAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}