using System.Threading;
using System.Windows;
using SimpleNotepad.Models;
using SimpleNotepad.Services;

namespace SimpleNotepad;

public partial class RewritePreviewWindow : Window
{
    private readonly AppSettings _settings;
    private readonly AiRewriteService _rewriteService;
    private readonly string _originalText;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// The text the user accepted (edited suggestion). Valid only when DialogResult is true.
    /// </summary>
    public string AcceptedText { get; private set; } = string.Empty;

    public RewritePreviewWindow(AppSettings settings, AiRewriteService rewriteService, string originalText)
    {
        InitializeComponent();

        _settings = settings;
        _rewriteService = rewriteService;
        _originalText = originalText;

        OriginalBox.Text = originalText;

        Loaded += async (_, _) => await GenerateAsync();
    }

    private async Task GenerateAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        SetBusy(true);
        StatusText.Text = "Generating...";

        try
        {
            var suggestion = await _rewriteService.RewriteAsync(
                _settings, _originalText, InstructionBox.Text, token);

            if (token.IsCancellationRequested)
            {
                return;
            }

            SuggestionBox.Text = suggestion;
            ReplaceButton.IsEnabled = !string.IsNullOrWhiteSpace(suggestion);
            StatusText.Text = "Review the suggestion, edit if needed, then Replace.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Generation stopped.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Rewrite failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        RegenerateButton.IsEnabled = !busy;
        InstructionBox.IsEnabled = !busy;
        CancelCallButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            ReplaceButton.IsEnabled = false;
        }
    }

    private async void RegenerateButton_Click(object sender, RoutedEventArgs e)
    {
        await GenerateAsync();
    }

    private void CancelCallButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }

    private void ReplaceButton_Click(object sender, RoutedEventArgs e)
    {
        AcceptedText = SuggestionBox.Text;
        DialogResult = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        base.OnClosed(e);
    }
}
