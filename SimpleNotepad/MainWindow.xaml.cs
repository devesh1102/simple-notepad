using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Rendering;
using Microsoft.Win32;
using SimpleNotepad.Models;
using SimpleNotepad.Services;
using SimpleNotepad.ViewModels;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace SimpleNotepad;

public partial class MainWindow : Window
{
    private readonly SessionStorageService _sessionStorage = new();
    private readonly AppSettingsService _settingsService = new();
    private readonly LinkedPowerShellService _linkedPowerShell = new();
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
    private string _saveState = "Saved";
    private JsonSyntaxColorizer? _jsonColorizer;
    private FoldingManager? _jsonFoldingManager;
    private readonly JsonFoldingStrategy _jsonFoldingStrategy = new();
    private int _editVersion;
    private int _selectionRequestId;
    private readonly SemaphoreSlim _selectionSemaphore = new(1, 1);
    private CancellationTokenSource? _autosaveCts;
    private static readonly TimeSpan AutosaveDelay = TimeSpan.FromMilliseconds(1500);
    private const double MinimumEditorFontSize = 8;
    private const double MaximumEditorFontSize = 48;
    private const double EditorFontSizeStep = 1;
    private const int JsonHighlightMaxLength = 200_000;
    private const int JsonAutoValidationMaxLength = 200_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true
    };

    public MainWindow()
    {
        InitializeComponent();
        _sessionsView = CollectionViewSource.GetDefaultView(_sessions);
        _sessionsView.Filter = FilterSession;
        SessionsList.ItemsSource = _sessionsView;

        ApplyEditorDarkTheme();
        UpdateJsonState();

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += (_, _) => _linkedPowerShell.Dispose();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        Editor.PreviewMouseWheel += Editor_PreviewMouseWheel;
        Editor.TextArea.Caret.PositionChanged += (_, _) => UpdateStatus();
        Editor.TextArea.SelectionChanged += (_, _) =>
        {
            UpdateJsonState();
            UpdateLinkedPowerShellState();
        };
    }

    private void ApplyEditorDarkTheme()
    {
        var bgColor = Color.FromRgb(0x1E, 0x1E, 0x1E);
        var fgColor = Color.FromRgb(0xD4, 0xD4, 0xD4);

        Editor.Background = new SolidColorBrush(bgColor);
        Editor.Foreground = new SolidColorBrush(fgColor);
        Editor.TextArea.Background = new SolidColorBrush(bgColor);
        Editor.TextArea.Foreground = new SolidColorBrush(fgColor);
        Editor.TextArea.TextView.LinkTextForegroundBrush = new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6));
        Editor.LineNumbersForeground = new SolidColorBrush(Color.FromRgb(0x85, 0x85, 0x85));
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
        await CreateNewSessionWithLockAsync();
    }

    private async Task CreateNewSessionWithLockAsync()
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
            SetSaveState("Saved", Color.FromRgb(0x4E, 0xC9, 0xB0));
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
            UpdateFindMatchCount();
            UpdateJsonState();
        }
    }

    private async Task SaveCurrentSessionAsync(bool refreshSessions = true)
    {
        if (_currentSession is null)
        {
            return;
        }

        SetSaveState("Saving...", Color.FromRgb(0xD7, 0xBA, 0x7D));

        try
        {
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
            SetSaveState(
                _hasUnsavedContent || _hasUnsavedIndex ? "Unsaved" : "Saved",
                _hasUnsavedContent || _hasUnsavedIndex ? Color.FromRgb(0xD7, 0xBA, 0x7D) : Color.FromRgb(0x4E, 0xC9, 0xB0));

            if (shouldRefreshSessions && refreshSessions)
            {
                UpdateSessionItems();
            }
        }
        catch
        {
            MarkSaveError();
            throw;
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
        SetSaveState("Unsaved", Color.FromRgb(0xD7, 0xBA, 0x7D));
        ScheduleAutosave();
        UpdateStatus();
        UpdateFindMatchCount();
        UpdateJsonState();
    }

    private void SessionTitleBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_isLoadingSession || _currentSession is null)
        {
            return;
        }

        _hasUnsavedIndex = true;
        SetSaveState("Unsaved", Color.FromRgb(0xD7, 0xBA, 0x7D));
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
        var hasControl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var hasShift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            FindReplaceBar.Visibility = Visibility.Collapsed;
            Editor.Focus();
            return;
        }

        if (!hasControl)
        {
            return;
        }

        e.Handled = true;

        switch (e.Key)
        {
            case Key.F when hasShift:
                FormatJsonSelectionOrDocument();
                return;

            case Key.F:
                OpenFindReplaceBar();
                return;

            case Key.N:
                await CreateNewSessionWithLockAsync();
                return;

            case Key.O:
                await OpenFileAsSessionAsync();
                return;

            case Key.S when hasShift:
                await SaveCurrentSessionAsAsync();
                return;

            case Key.S:
                await SaveCurrentSessionFromShortcutAsync();
                return;

            case Key.Add:
            case Key.OemPlus:
                ChangeEditorFontSize(EditorFontSizeStep);
                return;

            case Key.Subtract:
            case Key.OemMinus:
                ChangeEditorFontSize(-EditorFontSizeStep);
                return;

            case Key.D0 when hasControl:
                ResetEditorFontSize();
                return;

            default:
                e.Handled = false;
                return;
        }
    }

    private async Task SaveCurrentSessionFromShortcutAsync()
    {
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
            MarkSaveError();
            ShowError("Simple Notepad could not save your latest changes.", exception);
        }
    }

    private async Task OpenFileAsSessionAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open file",
            Filter = "Text and JSON files (*.txt;*.json)|*.txt;*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var content = await File.ReadAllTextAsync(dialog.FileName, StrictUtf8);
            await _selectionSemaphore.WaitAsync();
            try
            {
                if (_isClosingSaveInProgress)
                {
                    return;
                }

                await SaveCurrentSessionAsync();

                var session = _sessionStorage.CreateSession();
                session.Title = Path.GetFileName(dialog.FileName);
                session.Preview = CreatePreview(content);
                session.UpdatedAt = DateTimeOffset.UtcNow;
                session.ExpiresAt = session.UpdatedAt.AddDays(7);

                await _sessionStorage.SaveContentAsync(session, content);
                ReplaceSessionItems(_sessions.Select(item => item.Session).Append(session));
                await SaveIndexAsync();
                SessionsList.SelectedItem = _sessions.FirstOrDefault(item => item.Id == session.Id);
            }
            finally
            {
                _selectionSemaphore.Release();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            MarkSaveError();
            ShowError("Simple Notepad could not open the selected file.", exception);
        }
    }

    private async Task SaveCurrentSessionAsAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save as",
            Filter = "Text files (*.txt)|*.txt|JSON files (*.json)|*.json|All files (*.*)|*.*",
            FileName = string.IsNullOrWhiteSpace(SessionTitleBox.Text) ? "Untitled.txt" : $"{SessionTitleBox.Text.Trim()}.txt",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(dialog.FileName, Editor.Text, StrictUtf8);
            await SaveCurrentSessionFromShortcutAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EncoderFallbackException)
        {
            MarkSaveError();
            ShowError("Simple Notepad could not save the file.", exception);
        }
    }

    private void FormatJsonButton_Click(object sender, RoutedEventArgs e)
    {
        FormatJsonSelectionOrDocument();
    }

    private void MinifyJsonButton_Click(object sender, RoutedEventArgs e)
    {
        MinifyJsonSelectionOrDocument();
    }

    private async void OpenJsonInVsCodeButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenJsonInVsCodeAsync();
    }

    private void Editor_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        UpdateJsonState();
        UpdateLinkedPowerShellState();
    }

    private void FormatJsonSelectionOrDocument()
    {
        ReplaceJsonSelectionOrDocument(writeIndented: true);
    }

    private void MinifyJsonSelectionOrDocument()
    {
        ReplaceJsonSelectionOrDocument(writeIndented: false);
    }

    private void ReplaceJsonSelectionOrDocument(bool writeIndented)
    {
        var target = GetJsonTarget();
        if (string.IsNullOrWhiteSpace(target.Text))
        {
            ShowJsonMessage("There is no JSON content to process.");
            return;
        }

        if (!TryFormatJson(target.Text, writeIndented, out var formattedJson, out var errorMessage))
        {
            ShowJsonMessage($"The selected content is not valid JSON.\n\n{errorMessage}");
            return;
        }

        Editor.Document.Replace(target.Start, target.Length, formattedJson);
        Editor.Select(target.Start, formattedJson.Length);
        UpdateJsonState();
    }

    private async Task OpenJsonInVsCodeAsync()
    {
        var target = GetJsonTarget();
        if (string.IsNullOrWhiteSpace(target.Text))
        {
            ShowJsonMessage("There is no JSON content to open in VS Code.");
            return;
        }

        if (!TryFormatJson(target.Text, writeIndented: true, out var formattedJson, out var errorMessage))
        {
            ShowJsonMessage($"The selected content is not valid JSON.\n\n{errorMessage}");
            return;
        }

        try
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), "SimpleNotepad");
            Directory.CreateDirectory(tempDirectory);
            var tempPath = Path.Combine(tempDirectory, $"{CreateSafeFileName(SessionTitleBox.Text)}-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(tempPath, formattedJson, StrictUtf8);

            var startInfo = new ProcessStartInfo
            {
                FileName = "code",
                Arguments = $"\"{tempPath}\"",
                UseShellExecute = true
            };

            if (Process.Start(startInfo) is null)
            {
                throw new InvalidOperationException("Visual Studio Code did not start.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            ShowJsonMessage($"Simple Notepad could not open JSON in VS Code. Make sure the 'code' command is installed and available in PATH.\n\n{exception.Message}");
        }
    }

    private (string Text, int Start, int Length) GetJsonTarget()
    {
        if (!string.IsNullOrWhiteSpace(Editor.SelectedText))
        {
            if (TryFormatJson(Editor.SelectedText, writeIndented: false, out _, out _))
            {
                return (Editor.SelectedText, Editor.SelectionStart, Editor.SelectionLength);
            }

            var selectionStart = Editor.SelectionStart;
            var selectionEnd = selectionStart + Editor.SelectionLength;
            if (JsonFoldingStrategy.TryFindJsonTargetInRange(Editor.Text, Editor.TextArea.Caret.Offset, selectionStart, selectionEnd, out var selectedTarget))
            {
                return selectedTarget;
            }

            return (Editor.SelectedText, selectionStart, Editor.SelectionLength);
        }

        if (JsonFoldingStrategy.TryFindJsonTarget(Editor.Text, Editor.TextArea.Caret.Offset, out var target))
        {
            return target;
        }

        if (JsonFoldingStrategy.TryFindFirstJsonTarget(Editor.Text, out target))
        {
            return target;
        }

        return (Editor.Text, 0, Editor.Text.Length);
    }

    private static bool TryFormatJson(string json, bool writeIndented, out string formattedJson, out string errorMessage)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            formattedJson = JsonSerializer.Serialize(document.RootElement, writeIndented ? PrettyJsonOptions : JsonSerializerOptions.Default);
            errorMessage = string.Empty;
            return true;
        }
        catch (JsonException exception)
        {
            formattedJson = string.Empty;
            errorMessage = exception.Message;
            return false;
        }
    }

    private void UpdateJsonState()
    {
        var target = GetJsonTarget();
        var hasContent = !string.IsNullOrWhiteSpace(target.Text);
        var canAutoValidate = hasContent && target.Text.Length <= JsonAutoValidationMaxLength;
        var isValidJson = canAutoValidate && TryFormatJson(target.Text, writeIndented: false, out _, out _);
        var shouldEnableActions = isValidJson || (hasContent && target.Text.Length > JsonAutoValidationMaxLength);

        FormatJsonButton.IsEnabled = shouldEnableActions;
        MinifyJsonButton.IsEnabled = shouldEnableActions;
        OpenJsonInVsCodeButton.IsEnabled = shouldEnableActions;
        FormatJsonMenuItem.IsEnabled = shouldEnableActions;
        MinifyJsonMenuItem.IsEnabled = shouldEnableActions;
        OpenJsonInVsCodeMenuItem.IsEnabled = shouldEnableActions;

        var jsonBlocks = Editor.Text.Length <= JsonHighlightMaxLength
            ? JsonFoldingStrategy.GetJsonBlocks(Editor.Text)
            : (IReadOnlyList<(int Start, int End)>)System.Array.Empty<(int Start, int End)>();

        if (jsonBlocks.Count > 0)
        {
            EnableJsonHighlighting();
            _jsonColorizer!.SetRanges(jsonBlocks);
            Editor.TextArea.TextView.Redraw();
            EnableJsonFolding();
            return;
        }

        DisableJsonHighlighting();
        DisableJsonFolding();
    }

    private void EnableJsonHighlighting()
    {
        if (_jsonColorizer is not null)
        {
            return;
        }

        _jsonColorizer = new JsonSyntaxColorizer();
        Editor.TextArea.TextView.LineTransformers.Add(_jsonColorizer);
        Editor.TextArea.TextView.Redraw();
    }

    private void DisableJsonHighlighting()
    {
        if (_jsonColorizer is null)
        {
            return;
        }

        Editor.TextArea.TextView.LineTransformers.Remove(_jsonColorizer);
        _jsonColorizer = null;
        Editor.TextArea.TextView.Redraw();
    }

    private bool EnableJsonFolding()
    {
        _jsonFoldingManager ??= FoldingManager.Install(Editor.TextArea);
        return _jsonFoldingStrategy.UpdateFoldings(_jsonFoldingManager, Editor.Document);
    }

    private void DisableJsonFolding()
    {
        if (_jsonFoldingManager is null)
        {
            return;
        }

        _jsonFoldingManager.Clear();
    }

    private static string CreateSafeFileName(string title)
    {
        var fallback = string.IsNullOrWhiteSpace(title) ? "json" : title.Trim();
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var safeName = new string(fallback.Select(character => invalidCharacters.Contains(character) ? '-' : character).ToArray());
        return string.IsNullOrWhiteSpace(safeName) ? "json" : safeName;
    }

    private static void ShowJsonMessage(string message)
    {
        MessageBox.Show(message, "Simple Notepad JSON", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void SendToPowerShellMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await SendSelectedCommandToPowerShellAsync(LinkedPowerShellTarget.Normal);
    }

    private async void SendToAdminPowerShellMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await SendSelectedCommandToPowerShellAsync(LinkedPowerShellTarget.Admin);
    }

    private void RestartNormalPowerShellMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RestartLinkedPowerShell(LinkedPowerShellTarget.Normal);
    }

    private void RestartAdminPowerShellMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RestartLinkedPowerShell(LinkedPowerShellTarget.Admin);
    }

    private void StopNormalPowerShellMenuItem_Click(object sender, RoutedEventArgs e)
    {
        StopLinkedPowerShell(LinkedPowerShellTarget.Normal);
    }

    private void StopAdminPowerShellMenuItem_Click(object sender, RoutedEventArgs e)
    {
        StopLinkedPowerShell(LinkedPowerShellTarget.Admin);
    }

    private async Task SendSelectedCommandToPowerShellAsync(LinkedPowerShellTarget target)
    {
        var command = Editor.SelectedText;
        if (string.IsNullOrWhiteSpace(command))
        {
            ShowLinkedPowerShellMessage("Select command text before sending it to linked PowerShell.");
            return;
        }

        try
        {
            await _linkedPowerShell.SendCommandAsync(target, command);
            UpdateLinkedPowerShellState();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            ShowLinkedPowerShellMessage($"Simple Notepad could not send the command to linked PowerShell.\n\n{exception.Message}");
            UpdateLinkedPowerShellState();
        }
    }

    private void RestartLinkedPowerShell(LinkedPowerShellTarget target)
    {
        var result = MessageBox.Show(
            $"Restart {_linkedPowerShell.GetSessionDescription(target)}?",
            "Restart linked PowerShell",
            MessageBoxButton.YesNo,
            target == LinkedPowerShellTarget.Admin ? MessageBoxImage.Warning : MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _linkedPowerShell.Restart(target);
            UpdateLinkedPowerShellState();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            ShowLinkedPowerShellMessage($"Simple Notepad could not restart linked PowerShell.\n\n{exception.Message}");
            UpdateLinkedPowerShellState();
        }
    }

    private void StopLinkedPowerShell(LinkedPowerShellTarget target)
    {
        try
        {
            _linkedPowerShell.Stop(target);
            UpdateLinkedPowerShellState();
        }
        catch (InvalidOperationException exception)
        {
            ShowLinkedPowerShellMessage(exception.Message);
            UpdateLinkedPowerShellState();
        }
    }

    private void UpdateLinkedPowerShellState()
    {
        var hasSelection = !string.IsNullOrWhiteSpace(Editor.SelectedText);
        SendToPowerShellMenuItem.IsEnabled = hasSelection;
        SendToAdminPowerShellMenuItem.IsEnabled = hasSelection;
        RestartNormalPowerShellMenuItem.IsEnabled = true;
        RestartAdminPowerShellMenuItem.IsEnabled = !_linkedPowerShell.IsRunning(LinkedPowerShellTarget.Admin);
        StopNormalPowerShellMenuItem.IsEnabled = _linkedPowerShell.IsRunning(LinkedPowerShellTarget.Normal);
        StopAdminPowerShellMenuItem.IsEnabled = _linkedPowerShell.IsRunning(LinkedPowerShellTarget.Admin);
        LinkedPowerShellStatusText.Text = _linkedPowerShell.GetStatus();
    }

    private static void ShowLinkedPowerShellMessage(string message)
    {
        MessageBox.Show(message, "Simple Notepad Linked PowerShell", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void WordWrapButton_Click(object sender, RoutedEventArgs e)
    {
        Editor.WordWrap = !Editor.WordWrap;
        WordWrapButton.Content = Editor.WordWrap ? "Wrap: On" : "Wrap: Off";
    }

    private void Editor_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        e.Handled = true;
        ChangeEditorFontSize(e.Delta > 0 ? EditorFontSizeStep : -EditorFontSizeStep);
    }

    private void ChangeEditorFontSize(double delta)
    {
        Editor.FontSize = Math.Clamp(Editor.FontSize + delta, MinimumEditorFontSize, MaximumEditorFontSize);
        UpdateStatus();
    }

    private void ResetEditorFontSize()
    {
        Editor.FontSize = 14;
        UpdateStatus();
    }

    private void MarkSaveError()
    {
        SetSaveState("Error", Color.FromRgb(0xF4, 0x47, 0x47));
        UpdateStatus();
    }

    private void SetSaveState(string state, Color color)
    {
        _saveState = state;
        SaveStateText.Text = state;
        SaveStateText.Foreground = new SolidColorBrush(color);
        UpdateStatus();
    }

    private void OpenFindReplaceBar()
    {
        FindReplaceBar.Visibility = Visibility.Visible;

        if (!string.IsNullOrEmpty(Editor.SelectedText) && !Editor.SelectedText.Contains('\r') && !Editor.SelectedText.Contains('\n'))
        {
            FindTextBox.Text = Editor.SelectedText;
        }

        FindTextBox.Focus();
        FindTextBox.SelectAll();
        UpdateFindMatchCount();
    }

    private void CloseFindReplaceButton_Click(object sender, RoutedEventArgs e)
    {
        FindReplaceBar.Visibility = Visibility.Collapsed;
        Editor.Focus();
    }

    private void FindNextButton_Click(object sender, RoutedEventArgs e)
    {
        FindNext();
    }

    private void FindPreviousButton_Click(object sender, RoutedEventArgs e)
    {
        FindPrevious();
    }

    private void ReplaceButton_Click(object sender, RoutedEventArgs e)
    {
        ReplaceCurrent();
    }

    private void ReplaceAllButton_Click(object sender, RoutedEventArgs e)
    {
        ReplaceAll();
    }

    private void FindTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        UpdateFindMatchCount();
    }

    private void FindOptionsChanged(object sender, RoutedEventArgs e)
    {
        UpdateFindMatchCount();
    }

    private void FindReplaceTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        var restoreFocusTarget = sender as WpfTextBox;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            FindPrevious();
            restoreFocusTarget?.Focus();
            return;
        }

        FindNext();
        restoreFocusTarget?.Focus();
    }

    private void FindNext()
    {
        var matches = GetFindMatches();
        if (matches.Count == 0)
        {
            UpdateFindMatchCount(0);
            return;
        }

        var startOffset = Editor.SelectionLength > 0
            ? Editor.SelectionStart + Editor.SelectionLength
            : Editor.TextArea.Caret.Offset;
        var matchIndex = matches.FindIndex(candidate => candidate.Start >= startOffset);
        var match = matchIndex >= 0 ? matches[matchIndex] : matches[0];
        SelectFindMatch(match);
        UpdateFindMatchCount(matches.Count);
    }

    private void FindPrevious()
    {
        var matches = GetFindMatches();
        if (matches.Count == 0)
        {
            UpdateFindMatchCount(0);
            return;
        }

        var startOffset = Editor.SelectionLength > 0
            ? Editor.SelectionStart - 1
            : Editor.TextArea.Caret.Offset - 1;
        var matchIndex = matches.FindLastIndex(candidate => candidate.Start <= startOffset);
        var match = matchIndex >= 0 ? matches[matchIndex] : matches[^1];
        SelectFindMatch(match);
        UpdateFindMatchCount(matches.Count);
    }

    private void ReplaceCurrent()
    {
        var matches = GetFindMatches();
        if (matches.Count == 0)
        {
            UpdateFindMatchCount(0);
            return;
        }

        var currentMatch = matches.FirstOrDefault(match => match.Start == Editor.SelectionStart && match.Length == Editor.SelectionLength);
        if (currentMatch.Length == 0)
        {
            FindNext();
            return;
        }

        var replacement = ReplaceTextBox.Text;
        Editor.Document.Replace(currentMatch.Start, currentMatch.Length, replacement);
        Editor.Select(currentMatch.Start, replacement.Length);
        FindNext();
    }

    private void ReplaceAll()
    {
        var matches = GetFindMatches();
        if (matches.Count == 0)
        {
            UpdateFindMatchCount(0);
            return;
        }

        var replacement = ReplaceTextBox.Text;
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            var match = matches[i];
            Editor.Document.Replace(match.Start, match.Length, replacement);
        }

        Editor.TextArea.Caret.Offset = 0;
        Editor.Select(0, 0);
        UpdateFindMatchCount();
    }

    private List<(int Start, int Length)> GetFindMatches()
    {
        var query = FindTextBox.Text;
        var text = Editor.Text;
        var matches = new List<(int Start, int Length)>();

        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(text))
        {
            return matches;
        }

        var comparison = MatchCaseCheckBox.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var offset = 0;
        while (offset <= text.Length - query.Length)
        {
            var index = text.IndexOf(query, offset, comparison);
            if (index < 0)
            {
                break;
            }

            if (WholeWordCheckBox.IsChecked != true || IsWholeWordMatch(text, index, query.Length))
            {
                matches.Add((index, query.Length));
            }

            offset = index + Math.Max(query.Length, 1);
        }

        return matches;
    }

    private void SelectFindMatch((int Start, int Length) match)
    {
        Editor.Focus();
        Editor.Select(match.Start, match.Length);
        Editor.TextArea.Caret.Offset = match.Start + match.Length;
        Editor.ScrollToLine(Editor.Document.GetLineByOffset(match.Start).LineNumber);
    }

    private void UpdateFindMatchCount()
    {
        if (FindReplaceBar.Visibility != Visibility.Visible)
        {
            return;
        }

        UpdateFindMatchCount(GetFindMatches().Count);
    }

    private void UpdateFindMatchCount(int count)
    {
        MatchCountText.Text = count == 1 ? "1 match" : $"{count} matches";
    }

    private static bool IsWholeWordMatch(string text, int start, int length)
    {
        var beforeIndex = start - 1;
        var afterIndex = start + length;
        return (beforeIndex < 0 || !IsWordCharacter(text[beforeIndex])) &&
               (afterIndex >= text.Length || !IsWordCharacter(text[afterIndex]));
    }

    private static bool IsWordCharacter(char character)
    {
        return char.IsLetterOrDigit(character) || character == '_';
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
        StatusText.Text = $"Ln {location.Line}, Col {location.Column} | {Editor.Text.Length} chars | {_saveState}";
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

    private sealed class JsonSyntaxColorizer : DocumentColorizingTransformer
    {
        private static readonly SolidColorBrush PropertyBrush = CreateFrozenBrush(0x9C, 0xDC, 0xFE);
        private static readonly SolidColorBrush StringBrush = CreateFrozenBrush(0xCE, 0x91, 0x78);
        private static readonly SolidColorBrush NumberBrush = CreateFrozenBrush(0xB5, 0xCE, 0xA8);
        private static readonly SolidColorBrush KeywordBrush = CreateFrozenBrush(0x56, 0x9C, 0xD6);

        private IReadOnlyList<(int Start, int End)> _ranges = System.Array.Empty<(int Start, int End)>();

        public void SetRanges(IReadOnlyList<(int Start, int End)> ranges)
        {
            _ranges = ranges ?? System.Array.Empty<(int Start, int End)>();
        }

        private bool IsInJsonRange(int absoluteOffset)
        {
            foreach (var (start, end) in _ranges)
            {
                if (absoluteOffset >= start && absoluteOffset < end)
                {
                    return true;
                }
            }

            return false;
        }

        private bool LineOverlapsJson(DocumentLine line)
        {
            var lineStart = line.Offset;
            var lineEnd = line.EndOffset;
            foreach (var (start, end) in _ranges)
            {
                if (start < lineEnd && end > lineStart)
                {
                    return true;
                }
            }

            return false;
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            if (_ranges.Count == 0 || !LineOverlapsJson(line))
            {
                return;
            }

            var lineText = CurrentContext.Document.GetText(line);
            var index = 0;

            while (index < lineText.Length)
            {
                if (!IsInJsonRange(line.Offset + index))
                {
                    index++;
                    continue;
                }

                var character = lineText[index];
                if (character == '"')
                {
                    var endIndex = FindStringEnd(lineText, index);
                    var brush = IsPropertyName(lineText, endIndex) ? PropertyBrush : StringBrush;
                    ApplyBrush(line.Offset + index, line.Offset + endIndex + 1, brush);
                    index = endIndex + 1;
                    continue;
                }

                if (character == '-' || char.IsDigit(character))
                {
                    var endIndex = FindNumberEnd(lineText, index);
                    ApplyBrush(line.Offset + index, line.Offset + endIndex, NumberBrush);
                    index = endIndex;
                    continue;
                }

                if (StartsWithKeyword(lineText, index, "true", out var trueEnd) ||
                    StartsWithKeyword(lineText, index, "false", out trueEnd) ||
                    StartsWithKeyword(lineText, index, "null", out trueEnd))
                {
                    ApplyBrush(line.Offset + index, line.Offset + trueEnd, KeywordBrush);
                    index = trueEnd;
                    continue;
                }

                index++;
            }
        }

        private static int FindStringEnd(string text, int start)
        {
            var escaped = false;
            for (var index = start + 1; index < text.Length; index++)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (text[index] == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (text[index] == '"')
                {
                    return index;
                }
            }

            return text.Length - 1;
        }

        private static int FindNumberEnd(string text, int start)
        {
            var index = start;
            while (index < text.Length && (char.IsDigit(text[index]) || text[index] is '-' or '+' or '.' or 'e' or 'E'))
            {
                index++;
            }

            return index;
        }

        private static bool IsPropertyName(string text, int stringEnd)
        {
            for (var index = stringEnd + 1; index < text.Length; index++)
            {
                if (char.IsWhiteSpace(text[index]))
                {
                    continue;
                }

                return text[index] == ':';
            }

            return false;
        }

        private static bool StartsWithKeyword(string text, int start, string keyword, out int end)
        {
            end = start + keyword.Length;
            if (end > text.Length || !text.AsSpan(start, keyword.Length).SequenceEqual(keyword))
            {
                return false;
            }

            if ((start > 0 && char.IsLetterOrDigit(text[start - 1])) ||
                (end < text.Length && char.IsLetterOrDigit(text[end])))
            {
                return false;
            }

            return true;
        }

        private void ApplyBrush(int startOffset, int endOffset, Brush brush)
        {
            ChangeLinePart(startOffset, endOffset, element => element.TextRunProperties.SetForegroundBrush(brush));
        }

        private static SolidColorBrush CreateFrozenBrush(byte red, byte green, byte blue)
        {
            var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }
    }

    private sealed class JsonFoldingStrategy
    {
        private const int MaxCandidateLength = JsonAutoValidationMaxLength;

        public static bool TryFindJsonTarget(string text, int offset, out (string Text, int Start, int Length) target)
        {
            target = default;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            offset = Math.Clamp(offset, 0, text.Length);
            (int Start, int End)? bestMatch = null;

            foreach (var (start, end) in FindJsonCandidates(text))
            {
                if (start > offset || end < offset)
                {
                    continue;
                }

                if (bestMatch is null || end - start > bestMatch.Value.End - bestMatch.Value.Start)
                {
                    bestMatch = (start, end);
                }
            }

            if (bestMatch is null)
            {
                return false;
            }

            var match = bestMatch.Value;
            target = (text[match.Start..match.End], match.Start, match.End - match.Start);
            return true;
        }

        public static bool TryFindJsonTargetInRange(
            string text,
            int offset,
            int rangeStart,
            int rangeEnd,
            out (string Text, int Start, int Length) target)
        {
            target = default;
            if (string.IsNullOrWhiteSpace(text) || rangeStart < 0 || rangeEnd > text.Length || rangeStart >= rangeEnd)
            {
                return false;
            }

            offset = Math.Clamp(offset, rangeStart, rangeEnd);
            (int Start, int End)? bestContainingMatch = null;
            (int Start, int End)? firstMatch = null;

            foreach (var (start, end) in FindJsonCandidates(text))
            {
                if (start < rangeStart || end > rangeEnd)
                {
                    continue;
                }

                firstMatch ??= (start, end);
                if (start <= offset && end >= offset &&
                    (bestContainingMatch is null || end - start > bestContainingMatch.Value.End - bestContainingMatch.Value.Start))
                {
                    bestContainingMatch = (start, end);
                }
            }

            var match = bestContainingMatch ?? firstMatch;
            if (match is null)
            {
                return false;
            }

            target = (text[match.Value.Start..match.Value.End], match.Value.Start, match.Value.End - match.Value.Start);
            return true;
        }

        public static bool TryFindFirstJsonTarget(string text, out (string Text, int Start, int Length) target)
        {
            target = default;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var firstMatch = FindJsonCandidates(text).FirstOrDefault();
            if (firstMatch == default)
            {
                return false;
            }

            target = (text[firstMatch.Start..firstMatch.End], firstMatch.Start, firstMatch.End - firstMatch.Start);
            return true;
        }

        public bool UpdateFoldings(FoldingManager manager, TextDocument document)
        {
            var foldings = CreateNewFoldings(document).ToList();
            manager.UpdateFoldings(foldings.OrderBy(folding => folding.StartOffset), firstErrorOffset: -1);
            return foldings.Count > 0;
        }

        private static IEnumerable<NewFolding> CreateNewFoldings(TextDocument document)
        {
            var text = document.Text;
            foreach (var (index, endOffset) in FindJsonCandidates(text))
            {
                var startLine = document.GetLineByOffset(index).LineNumber;
                var endLine = document.GetLineByOffset(endOffset - 1).LineNumber;
                if (startLine == endLine)
                {
                    continue;
                }

                yield return new NewFolding(index, endOffset)
                {
                    Name = text[index] == '{' ? "{...}" : "[...]"
                };
            }
        }

        public static IReadOnlyList<(int Start, int End)> GetJsonBlocks(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return System.Array.Empty<(int Start, int End)>();
            }

            var merged = new List<(int Start, int End)>();
            foreach (var (start, end) in FindJsonCandidates(text).OrderBy(candidate => candidate.Start))
            {
                if (merged.Count > 0 && start <= merged[^1].End)
                {
                    if (end > merged[^1].End)
                    {
                        merged[^1] = (merged[^1].Start, end);
                    }
                }
                else
                {
                    merged.Add((start, end));
                }
            }

            return merged;
        }

        private static IEnumerable<(int Start, int End)> FindJsonCandidates(string text)
        {
            for (var index = 0; index < text.Length; index++)
            {
                if (text[index] is not ('{' or '['))
                {
                    continue;
                }

                if (!TryFindJsonCandidateEnd(text, index, out var endOffset))
                {
                    continue;
                }

                if (endOffset - index > MaxCandidateLength)
                {
                    continue;
                }

                var candidateJson = text[index..endOffset];
                if (!TryFormatJson(candidateJson, writeIndented: false, out _, out _))
                {
                    continue;
                }

                yield return (index, endOffset);
            }
        }

        private static bool TryFindJsonCandidateEnd(string text, int start, out int endOffset)
        {
            endOffset = -1;
            var stack = new Stack<char>();
            var inString = false;
            var escaped = false;

            for (var index = start; index < text.Length && index - start <= MaxCandidateLength; index++)
            {
                var character = text[index];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }

                    if (character == '\\')
                    {
                        escaped = true;
                        continue;
                    }

                    if (character == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (character == '"')
                {
                    inString = true;
                    continue;
                }

                if (character is '{' or '[')
                {
                    stack.Push(character);
                    continue;
                }

                if (character is not ('}' or ']') || stack.Count == 0)
                {
                    continue;
                }

                var opening = stack.Pop();
                if ((opening == '{' && character != '}') || (opening == '[' && character != ']'))
                {
                    return false;
                }

                if (stack.Count == 0)
                {
                    endOffset = index + 1;
                    return true;
                }
            }

            return false;
        }
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