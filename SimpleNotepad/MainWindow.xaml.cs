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
    private SessionListItem? _contextMenuTarget;
    private string? _pendingRenameSessionId;
    private bool _isLoadingSession;
    private bool _isUpdatingSessions;
    private bool _isClosingAfterSave;
    private bool _isClosingSaveInProgress;
    private bool _hasFreshContextMenuTarget;
    private bool _hasUnsavedContent;
    private bool _hasUnsavedIndex;
    private int _editVersion;
    private int _selectionRequestId;
    private readonly SemaphoreSlim _selectionSemaphore = new(1, 1);

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
        if (_isClosingSaveInProgress)
        {
            return;
        }

        _isClosingSaveInProgress = true;
        Editor.IsReadOnly = true;
        SessionTitleBox.IsReadOnly = true;

        try
        {
            await SaveCurrentSessionAsync(refreshSessions: false);
            await SaveIndexIfNeededAsync();
            await _settingsService.SaveAsync(_settings);
            _isClosingAfterSave = true;
            Close();
        }
        catch (Exception exception)
        {
            _isClosingSaveInProgress = false;
            Editor.IsReadOnly = false;
            SessionTitleBox.IsReadOnly = false;
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

        ReplaceSessionItems(_sessions.Select(item => item.Session).Append(session));
        await SaveIndexAsync();
        SessionsList.SelectedItem = _sessions.FirstOrDefault(item => item.Id == session.Id);
    }

    private async void SessionsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isLoadingSession || _isUpdatingSessions || SessionsList.SelectedItem is not SessionListItem item)
        {
            return;
        }

        var requestId = Interlocked.Increment(ref _selectionRequestId);
        await _selectionSemaphore.WaitAsync();
        try
        {
            if (requestId != _selectionRequestId)
            {
                return;
            }

            Editor.IsReadOnly = true;
            SessionTitleBox.IsReadOnly = true;
            await SaveCurrentSessionAsync(refreshSessions: false);

            if (requestId != _selectionRequestId)
            {
                return;
            }

            await OpenSessionAsync(item.Session);
            UpdateSessionItems();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"Simple Notepad could not switch sessions:\n\n{exception.Message}",
                "Session switch failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            Editor.IsReadOnly = false;
            SessionTitleBox.IsReadOnly = false;
            _selectionSemaphore.Release();
        }
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
            _editVersion = 0;
            SaveStateText.Text = "Saved";
            UpdateStatus();
            await _settingsService.SaveAsync(_settings);

            if (_pendingRenameSessionId == session.Id)
            {
                _pendingRenameSessionId = null;
                SessionTitleBox.Focus();
                SessionTitleBox.SelectAll();
            }
        }
        finally
        {
            _isLoadingSession = false;
        }
    }

    private async Task SaveCurrentSessionAsync(bool refreshSessions = true)
    {
        if (_currentSession is null)
        {
            return;
        }

        var content = Editor.Text;
        var versionToSave = _editVersion;
        var preview = CreatePreview(content);
        var title = string.IsNullOrWhiteSpace(SessionTitleBox.Text) ? "Untitled" : SessionTitleBox.Text.Trim();

        var shouldRefreshSessions = false;

        if (_hasUnsavedContent || _currentSession.Title != title || _currentSession.Preview != preview)
        {
            _currentSession.Title = title;
            _currentSession.Preview = preview;
            _currentSession.UpdatedAt = DateTimeOffset.UtcNow;
            _currentSession.ExpiresAt = _currentSession.UpdatedAt.AddDays(7);

            await _sessionStorage.SaveContentAsync(_currentSession, content);
            _hasUnsavedContent = _editVersion != versionToSave;
            _hasUnsavedIndex = true;
            shouldRefreshSessions = true;
        }

        await SaveIndexIfNeededAsync();
        SaveStateText.Text = _hasUnsavedContent || _hasUnsavedIndex ? "Unsaved" : "Saved";

        if (shouldRefreshSessions && refreshSessions)
        {
            UpdateSessionItems();
        }
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

        _isUpdatingSessions = true;
        try
        {
            ReplaceSessionItems(_sessions.Select(item => item.Session));
            SessionsList.SelectedItem = _sessions.FirstOrDefault(item => item.Id == selectedId);
        }
        finally
        {
            _isUpdatingSessions = false;
        }
    }

    private void Editor_TextChanged(object? sender, EventArgs e)
    {
        if (_isLoadingSession)
        {
            return;
        }

        _hasUnsavedContent = true;
        _editVersion++;
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
        if (_contextMenuTarget is not null)
        {
            _pendingRenameSessionId = _contextMenuTarget.Id;
            SessionsList.SelectedItem = _contextMenuTarget;
            if (_currentSession?.Id != _contextMenuTarget.Id)
            {
                return;
            }
        }

        _pendingRenameSessionId = null;
        SessionTitleBox.Focus();
        SessionTitleBox.SelectAll();
    }

    private async void DeleteSession_Click(object sender, RoutedEventArgs e)
    {
        var item = _contextMenuTarget;
        if (item is null)
        {
            return;
        }

        var isDeletingCurrent = _currentSession?.Id == item.Id;
        var result = MessageBox.Show(
            $"Delete \"{item.Title}\"?",
            "Delete session",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        if (isDeletingCurrent)
        {
            _currentSession = null;
            _hasUnsavedContent = false;
            _hasUnsavedIndex = false;
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

        await _sessionStorage.DeleteSessionAsync(item.Session);

        _isUpdatingSessions = true;
        try
        {
            _sessions.Remove(item);
        }
        finally
        {
            _isUpdatingSessions = false;
        }

        _hasUnsavedIndex = true;

        await SaveIndexIfNeededAsync();

        if (_sessions.Count == 0)
        {
            await CreateNewSessionAsync();
            return;
        }

        if (isDeletingCurrent)
        {
            SessionsList.SelectedIndex = 0;
        }
        else
        {
            UpdateSessionItems();
        }
    }

    private async void PinSession_Click(object sender, RoutedEventArgs e)
    {
        var item = _contextMenuTarget;
        if (item is null)
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
        _contextMenuTarget = item?.DataContext as SessionListItem;
        _hasFreshContextMenuTarget = true;
    }

    private void SessionsList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (!_hasFreshContextMenuTarget)
        {
            _contextMenuTarget = SessionsList.SelectedItem as SessionListItem;
        }

        _hasFreshContextMenuTarget = false;

        if (_contextMenuTarget is null)
        {
            e.Handled = true;
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