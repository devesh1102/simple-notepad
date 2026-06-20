using System.Text.Json;

namespace SimpleNotepad.Services;

/// <summary>
/// Pure JSON validation/formatting helpers extracted from the UI so they can be unit tested
/// without a WPF window. Parsing and (re)serialization use System.Text.Json.
/// </summary>
public static class JsonFormatter
{
    private static readonly JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Attempts to reformat <paramref name="json"/>. On success returns the indented (when
    /// <paramref name="writeIndented"/> is true) or compact JSON; on failure returns the parse error.
    /// </summary>
    public static bool TryFormat(string json, bool writeIndented, out string formattedJson, out string errorMessage)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            formattedJson = JsonSerializer.Serialize(
                document.RootElement,
                writeIndented ? PrettyOptions : JsonSerializerOptions.Default);
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

    /// <summary>Returns true when <paramref name="text"/> parses as valid JSON.</summary>
    public static bool IsValidJson(string text) => TryFormat(text, writeIndented: false, out _, out _);
}
