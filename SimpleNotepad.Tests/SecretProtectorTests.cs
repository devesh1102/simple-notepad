using SimpleNotepad.Services;
using Xunit;

namespace SimpleNotepad.Tests;

public class SecretProtectorTests
{
    [Fact]
    public void Protect_ThenUnprotect_RoundTrips()
    {
        const string secret = "DefaultEndpointsProtocol=https;AccountName=acct;AccountKey=abc123==;";

        var protectedValue = SecretProtector.Protect(secret);

        Assert.NotNull(protectedValue);
        Assert.NotEqual(secret, protectedValue);
        Assert.Equal(secret, SecretProtector.Unprotect(protectedValue));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Protect_EmptyOrNull_ReturnsNull(string? input)
    {
        Assert.Null(SecretProtector.Protect(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Unprotect_EmptyOrNull_ReturnsNull(string? input)
    {
        Assert.Null(SecretProtector.Unprotect(input));
    }

    [Fact]
    public void Unprotect_GarbageInput_ReturnsNull()
    {
        Assert.Null(SecretProtector.Unprotect("not-base64-or-cipher"));
        Assert.Null(SecretProtector.Unprotect("YWJjZGVm"));
    }

    [Fact]
    public void Protect_ProducesDifferentCiphertextEachTime()
    {
        const string secret = "repeatable-secret";

        var first = SecretProtector.Protect(secret);
        var second = SecretProtector.Protect(secret);

        // DPAPI adds randomness, so ciphertext differs but both decrypt back to the same value.
        Assert.NotEqual(first, second);
        Assert.Equal(secret, SecretProtector.Unprotect(first));
        Assert.Equal(secret, SecretProtector.Unprotect(second));
    }
}
