using System.ClientModel;
using Azure.AI.OpenAI;
using OpenAI.Chat;
using SimpleNotepad.Models;

namespace SimpleNotepad.Services;

/// <summary>
/// Thin wrapper over Azure OpenAI used by the Rewrite feature. The client is created per call
/// so that referencing the SDK has no cost until the user actually invokes a rewrite.
/// </summary>
public sealed class AiRewriteService
{
    private const string SystemPrompt =
        "You are a writing assistant embedded in a notepad. Rewrite the user's text according to " +
        "their instruction. Preserve the original meaning and language. Return ONLY the rewritten " +
        "text with no preamble, explanation, quotes, or markdown fences.";

    private const string DefaultInstruction =
        "Rewrite this text to improve clarity, grammar, and flow while keeping the same meaning and tone.";

    public bool IsConfigured(AppSettings settings)
    {
        return !string.IsNullOrWhiteSpace(settings.AiEndpoint)
            && !string.IsNullOrWhiteSpace(settings.AiDeployment)
            && !string.IsNullOrWhiteSpace(settings.AiApiKeyProtected);
    }

    public async Task<string> RewriteAsync(
        AppSettings settings,
        string text,
        string? instruction,
        CancellationToken cancellationToken)
    {
        var chatClient = CreateChatClient(settings);

        var effectiveInstruction = string.IsNullOrWhiteSpace(instruction)
            ? DefaultInstruction
            : instruction.Trim();

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(SystemPrompt),
            new UserChatMessage($"Instruction: {effectiveInstruction}\n\nText:\n{text}"),
        };

        var options = new ChatCompletionOptions
        {
            Temperature = (float)Math.Clamp(settings.AiTemperature, 0d, 2d),
        };

        ChatCompletion completion = await chatClient.CompleteChatAsync(messages, options, cancellationToken);

        if (completion.Content.Count == 0)
        {
            throw new InvalidOperationException("The model returned an empty response.");
        }

        return completion.Content[0].Text.Trim();
    }

    public async Task<string> GenerateTitleAsync(
        AppSettings settings,
        string text,
        CancellationToken cancellationToken)
    {
        var chatClient = CreateChatClient(settings);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(
                "You generate a short, descriptive title for a note. Reply with ONLY the title: " +
                "at most 6 words, no quotes, no trailing punctuation, no markdown."),
            new UserChatMessage($"Note content:\n{text}"),
        };

        var options = new ChatCompletionOptions
        {
            Temperature = 0.3f,
            MaxOutputTokenCount = 24,
        };

        ChatCompletion completion = await chatClient.CompleteChatAsync(messages, options, cancellationToken);

        if (completion.Content.Count == 0)
        {
            throw new InvalidOperationException("The model returned an empty response.");
        }

        return CleanTitle(completion.Content[0].Text);
    }

    private static string CleanTitle(string raw)
    {
        var title = raw.Trim().Trim('"', '\'', '.', ' ');
        var newline = title.IndexOfAny(new[] { '\r', '\n' });
        if (newline >= 0)
        {
            title = title[..newline].Trim();
        }

        return title.Length > 80 ? title[..80].Trim() : title;
    }

    public async Task TestAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var chatClient = CreateChatClient(settings);

        var messages = new List<ChatMessage>
        {
            new UserChatMessage("Reply with the single word: ok"),
        };

        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = 5,
        };

        _ = await chatClient.CompleteChatAsync(messages, options, cancellationToken);
    }

    private static ChatClient CreateChatClient(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.AiEndpoint))
        {
            throw new InvalidOperationException("Azure OpenAI endpoint is not configured.");
        }

        if (string.IsNullOrWhiteSpace(settings.AiDeployment))
        {
            throw new InvalidOperationException("Azure OpenAI deployment name is not configured.");
        }

        var apiKey = SecretProtector.Unprotect(settings.AiApiKeyProtected);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Azure OpenAI API key is not configured or could not be read.");
        }

        if (!Uri.TryCreate(settings.AiEndpoint, UriKind.Absolute, out var endpointUri) ||
            endpointUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Azure OpenAI endpoint must be a valid HTTPS URL.");
        }

        var azureClient = new AzureOpenAIClient(endpointUri, new ApiKeyCredential(apiKey));
        return azureClient.GetChatClient(settings.AiDeployment);
    }
}
