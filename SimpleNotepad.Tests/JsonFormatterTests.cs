using SimpleNotepad.Services;
using Xunit;

namespace SimpleNotepad.Tests;

public class JsonFormatterTests
{
    [Fact]
    public void TryFormat_Indented_ProducesMultiLineJson()
    {
        var ok = JsonFormatter.TryFormat("{\"a\":1,\"b\":[2,3]}", writeIndented: true, out var formatted, out var error);

        Assert.True(ok);
        Assert.Equal(string.Empty, error);
        Assert.Contains("\n", formatted);
        Assert.Contains("\"a\": 1", formatted);
    }

    [Fact]
    public void TryFormat_Compact_ProducesSingleLineJson()
    {
        var ok = JsonFormatter.TryFormat("{\n  \"a\": 1\n}", writeIndented: false, out var formatted, out var error);

        Assert.True(ok);
        Assert.Equal(string.Empty, error);
        Assert.Equal("{\"a\":1}", formatted);
    }

    [Fact]
    public void TryFormat_InvalidJson_ReturnsFalseWithError()
    {
        var ok = JsonFormatter.TryFormat("{ not json }", writeIndented: true, out var formatted, out var error);

        Assert.False(ok);
        Assert.Equal(string.Empty, formatted);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("just a sentence")]
    [InlineData("")]
    [InlineData("[1, 2,]")]
    public void IsValidJson_RejectsNonJson(string text)
    {
        Assert.False(JsonFormatter.IsValidJson(text));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[1,2,3]")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    public void IsValidJson_AcceptsValidJson(string text)
    {
        Assert.True(JsonFormatter.IsValidJson(text));
    }
}
