using System.Security.Cryptography;
using System.Text;
using EnterpriseCommerce.Identity.Application.Security;

namespace EnterpriseCommerce.Identity.UnitTests.Application;

public sealed class SecurityTokenCodecTests
{
    [Fact]
    public void Generate_ProducesUrlSafe256BitTokenAndItsSha256Hash()
    {
        var generated = SecurityTokenCodec.Generate();

        Assert.True(SecurityTokenCodec.HasValidFormat(generated.RawToken));
        Assert.Equal(32, DecodeBase64Url(generated.RawToken).Length);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(generated.RawToken))),
            generated.TokenHash);
    }

    [Fact]
    public void Generate_NeverRepeats()
    {
        var tokens = Enumerable.Range(0, 1000).Select(_ => SecurityTokenCodec.Generate().RawToken).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(1000, tokens.Count);
    }

    [Fact]
    public void ToString_DoesNotRenderTheRawToken()
    {
        var generated = SecurityTokenCodec.Generate();

        Assert.DoesNotContain(generated.RawToken, generated.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOP=")] // padding character
    [InlineData("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMN+/Q")] // non-URL-safe characters
    public void HasValidFormat_RejectsMalformedInput(string? candidate) =>
        Assert.False(SecurityTokenCodec.HasValidFormat(candidate));

    private static byte[] DecodeBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '='));
    }
}
