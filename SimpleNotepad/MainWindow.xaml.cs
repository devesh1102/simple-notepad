using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
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
    private CancellationTokenSource? _autosaveCts;
    private static readonly TimeSpan AutosaveDelay = TimeSpan.FromMilliseconds(1500);

    public MainWindow()
    {
        InitializeComponent();
        _sessionsView = CollectionViewSource.GetDefaultView(_sessions);
        _sessionsView.Filter = FilterSession;
        SessionsList.ItemsSource = _sessionsView;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        Editor.TextArea.Caret.PositionChanged += (_, _) => UpdateStatus();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings = await _settingsService.LoadAsync();
            await LoadSessionsAsync();
        }
        catch (Exception exception)
        {
            ShowError("Simple Notepad could not load sessions.", exception);
            try
            {
                await CreateNewSessionAsync();
            }
            catch (Exception fallbackException)
            {
                Editor.IsReadOnly = true;
                SessionTitleBox.IsReadOnly = true;
                ShowError("Simple Notepad could not create a recovery session.", fallbackException);
            }
        }
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
        CancelPendingAutosave();
        Editor.IsReadOnly = true;
        SessionTitleBox.IsReadOnly = true;

        try
        {
            await _selectionSemaphore.WaitAsync();
            try
            {
                await SaveCurrentSessionAsync(refreshSessions: false);
                await SaveIndexIfNeededAsync();
                await _settingsService.SaveAsync(_settings);
            }
            finally
            {
                Interlocked.Increment(ref _selectionRequestId);
                _selectionSemaphore.Release();
            }

            _isClosingAfterSave = true;
            Close();
        }
        catch (Exception exception)
        {
            _isClosingSaveInProgress = false;
            Editor.IsReadOnly = false;
            SessionTitleBox.IsReadOnly = false;
            ShowError("Simple Notepad could not save your latest changes.", exception);
        }
    }

    private async Task LoadSessionsAsync()
    {
        var loaded = await _sessionStorage.LoadIndexAsync();
        var active = await _sessionStorage.PurgeExpiredSessionsAsync(loaded);

        var sessions = active
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
        var snapshot = sessions.ToList();

        _sessions.Clear();
        foreach (var session in snapshot
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
        try
        {
            if (_isClosingSaveInProgress)
            {
                return;
            }

            await _selectionSemaphore.WaitAsync();
            try
            {
                if (_isClosingSaveInProgress)
                {
                    return;
                }

                await CreateNewSessionAsync();
            }
            finally
            {
                _selectionSemaphore.Release();
            }
        }
        catch (Exception exception)
        {
            ShowError("Simple Notepad could not create a new session.", exception);
        }
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

        if (_isClosingSaveInProgress)
        {
            RestoreCurrentSelection();
            return;
        }

        CancelPendingAutosave();
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
            RestoreCurrentSelection();
            ShowError("Simple Notepad could not switch sessions.", exception);
        }
        finally
        {
            if (!_isClosingSaveInProgress)
            {
                Editor.IsReadOnly = false;
                SessionTitleBox.IsReadOnly = false;
            }

            _selectionSemaphore.Release();
        }
    }

    private async Task OpenSessionAsync(NoteSession session)
    {
        var content = await _sessionStorage.LoadContentAsync(session);

        _isLoadingSession = true;
        try
        {
            _currentSession = session;
            SessionTitleBox.Text = session.Title;
            Editor.Text = content;
            _settings.LastSessionId = session.Id;
            _hasUnsavedContent = false;
            _editVersion = 0;
            SaveStateText.Text = "Saved";
            UpdateStatus();
            try
            {
                await _settingsService.SaveAsync(_settings);
            }
            catch (Exception exception)
            {
                ShowError("Session opened, but Simple Notepad could not remember it as the last opened session.", exception);
            }

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

    private void RestoreCurrentSelection()
    {
        _isUpdatingSessions = true;
        try
        {
            SessionsList.SelectedItem = _sessions.FirstOrDefault(item => item.Id == _currentSession?.Id);
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
        ScheduleAutosave();
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
        ScheduleAutosave();
        UpdateStatus();
    }

    private void ScheduleAutosave()
    {
        CancelPendingAutosave();

        if (_isLoadingSession || _isClosingSaveInProgress || _currentSession is null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _autosaveCts = cts;
        var requestId = _selectionRequestId;
        var sessionId = _currentSession.Id;
        _ = RunAutosaveAsync(requestId, sessionId, cts.Token);
    }

    private async Task RunAutosaveAsync(int requestId, string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(AutosaveDelay, cancellationToken);
            if (_isClosingSaveInProgress ||
                _isLoadingSession ||
                _currentSession?.Id != sessionId ||
                requestId != _selectionRequestId ||
                (!_hasUnsavedContent && !_hasUnsavedIndex))
            {
                return;
            }

            await SaveCurrentSessionWithLockAsync(refreshSessions: true, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowError("Simple Notepad could not autosave the current session.", exception);
        }
    }

    private async Task SaveCurrentSessionWithLockAsync(bool refreshSessions, CancellationToken cancellationToken = default)
    {
        if (_currentSession is null || _isClosingSaveInProgress)
        {
            return;
        }

        await _selectionSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (_currentSession is null || _isClosingSaveInProgress || _isLoadingSession)
            {
                return;
            }

            await SaveCurrentSessionAsync(refreshSessions);
            UpdateStatus();
        }
        finally
        {
            _selectionSemaphore.Release();
        }
    }

    private void CancelPendingAutosave()
    {
        var autosaveCts = Interlocked.Exchange(ref _autosaveCts, null);
        if (autosaveCts is null)
        {
            return;
        }

        autosaveCts.Cancel();
        autosaveCts.Dispose();
    }

    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.S || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        e.Handled = true;
        CancelPendingAutosave();

        try
        {
            await SaveCurrentSessionWithLockAsync(refreshSessions: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowError("Simple Notepad could not save your latest changes.", exception);
        }
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
        try
        {
            if (_isClosingSaveInProgress)
            {
                return;
            }

            var item = _contextMenuTarget;
            if (item is null)
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

            await _selectionSemaphore.WaitAsync();
            try
            {
                if (_isClosingSaveInProgress)
                {
                    return;
                }

                var currentItem = _sessions.FirstOrDefault(sessionItem => sessionItem.Id == item.Id);
                if (currentItem is null)
                {
                    return;
                }

                var isDeletingCurrent = _currentSession?.Id == currentItem.Id;
                await _sessionStorage.DeleteSessionAsync(currentItem.Session);

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

                _isUpdatingSessions = true;
                try
                {
                    _sessions.Remove(currentItem);
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
            finally
            {
                _selectionSemaphore.Release();
            }
        }
        catch (Exception exception)
        {
            ShowError("Simple Notepad could not delete the session.", exception);
        }
    }

    private async void PinSession_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isClosingSaveInProgress)
            {
                return;
            }

            var item = _contextMenuTarget;
            if (item is null)
            {
                return;
            }

            await _selectionSemaphore.WaitAsync();
            try
            {
                if (_isClosingSaveInProgress)
                {
                    return;
                }

                var currentItem = _sessions.FirstOrDefault(sessionItem => sessionItem.Id == item.Id);
                if (currentItem is null)
                {
                    return;
                }

                var originalPinned = currentItem.Session.IsPinned;
                var originalExpiresAt = currentItem.Session.ExpiresAt;
                var originalUnsavedIndex = _hasUnsavedIndex;
                currentItem.Session.IsPinned = !originalPinned;
                if (originalPinned)
                {
                    currentItem.Session.ExpiresAt = DateTimeOffset.UtcNow.AddDays(7);
                }

                _hasUnsavedIndex = true;

                try
                {
                    await SaveIndexIfNeededAsync();
                }
                catch
                {
                    currentItem.Session.IsPinned = originalPinned;
                    currentItem.Session.ExpiresAt = originalExpiresAt;
                    _hasUnsavedIndex = originalUnsavedIndex;
                    throw;
                }

                UpdateSessionItems();
            }
            finally
            {
                _selectionSemaphore.Release();
            }
        }
        catch (Exception exception)
        {
            ShowError("Simple Notepad could not update the session pin.", exception);
        }
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

    private static void ShowError(string message, Exception exception)
    {
        MessageBox.Show(
            $"{message}\n\n{exception.Message}",
            "Simple Notepad",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
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