using EnterpriseCommerce.Identity.Api.Protocol.Keys;
using EnterpriseCommerce.Identity.TestSupport;
using Microsoft.IdentityModel.Tokens;

namespace EnterpriseCommerce.Identity.UnitTests.Api;

/// <summary>The key ring fails closed on every invalid configuration and never reveals key material (Phase 2.3 S6.3).</summary>
public sealed class KeyRingLoaderTests : IDisposable
{
    private static readonly KeyRetention Retention = new(Signing: TimeSpan.FromDays(2), Encryption: TimeSpan.FromDays(1));
    private readonly TestKeyMaterial _keys = new();

    [Fact]
    public void AValidRing_LoadsEveryStateWithCertificateThumbprintsAsKeyIds()
    {
        var active = _keys.Create("sign-active");
        var pending = _keys.Create("sign-pending");
        var retired = _keys.Create("sign-retired");
        var encryption = _keys.Create("enc-active");

        var ring = Load(new KeyRingSettings
        {
            Signing = [Entry(active.Path, KeyStatus.Active), Entry(pending.Path, KeyStatus.Pending), Entry(retired.Path, KeyStatus.Retired, DateTimeOffset.UtcNow.AddHours(-1))],
            Encryption = [Entry(encryption.Path, KeyStatus.Active)],
        });

        Assert.False(ring.IsEphemeral);
        Assert.Equal(active.KeyId, ring.ActiveSigning.KeyId);
        Assert.Equal(encryption.KeyId, ring.ActiveEncryption.KeyId);
        Assert.Equal([active.KeyId, pending.KeyId, retired.KeyId], ring.Of(KeyUse.Signing).Select(k => k.KeyId));
        Assert.All(ring.Keys, key => Assert.Equal(key.KeyId, key.SecurityKey.KeyId));
        Assert.Empty(ring.Dropped);
    }

    [Fact]
    public void ARetiredKeyPastItsWindow_IsNotLoaded()
    {
        var retired = _keys.Create("sign-old");
        var ring = Load(Valid(extraSigning: Entry(retired.Path, KeyStatus.Retired, DateTimeOffset.UtcNow - Retention.Signing - TimeSpan.FromMinutes(1))));

        Assert.DoesNotContain(ring.Keys, key => key.KeyId == retired.KeyId);
        Assert.Equal(retired.KeyId, Assert.Single(ring.Dropped).KeyId);
    }

    [Fact]
    public void WithoutKeys_DevelopmentUsesEphemeralKeys_AndOtherEnvironmentsRefuseToStart()
    {
        var ring = Load(new KeyRingSettings(), isDevelopment: true);
        Assert.True(ring.IsEphemeral);
        Assert.IsType<RsaSecurityKey>(ring.ActiveSigning.SecurityKey);

        Assert.Contains("must be configured outside Development", Fails(new KeyRingSettings(), isDevelopment: false), StringComparison.Ordinal);
    }

    public static TheoryData<string> InvalidRings =>
    [
        "missing file", "corrupt file", "wrong password", "no active signing key", "two active signing keys",
        "no encryption key", "weak key", "expired active key", "not yet valid active key", "retired without date",
        "retired in the future", "active with retirement date", "unknown status", "duplicate key", "unknown provider",
    ];

    [Theory]
    [MemberData(nameof(InvalidRings))]
    public void InvalidRings_FailClosed_WithoutRevealingThePassword(string problem)
    {
        var settings = problem switch
        {
            "missing file" => Valid(activeSigningPath: Path.Combine(_keys.Directory, "absent.pfx")),
            "corrupt file" => Valid(activeSigningPath: _keys.CreateCorrupt("corrupt")),
            "wrong password" => Valid(activeSigningPath: _keys.Create("other-password", password: "A-Different-Password-1").Path),
            "no active signing key" => Valid(activeSigningStatus: KeyStatus.Pending),
            "two active signing keys" => Valid(extraSigning: Entry(_keys.Create("second").Path, KeyStatus.Active)),
            "no encryption key" => WithoutEncryption(),
            "weak key" => Valid(activeSigningPath: _keys.Create("weak", keySize: 2048).Path),
            "expired active key" => Valid(activeSigningPath: _keys.Create("expired", notBefore: DateTimeOffset.UtcNow.AddDays(-30), notAfter: DateTimeOffset.UtcNow.AddDays(-1)).Path),
            "not yet valid active key" => Valid(activeSigningPath: _keys.Create("future", notBefore: DateTimeOffset.UtcNow.AddDays(1), notAfter: DateTimeOffset.UtcNow.AddDays(30)).Path),
            "retired without date" => Valid(extraSigning: Entry(_keys.Create("retired-undated").Path, KeyStatus.Retired)),
            "retired in the future" => Valid(extraSigning: Entry(_keys.Create("retired-future").Path, KeyStatus.Retired, DateTimeOffset.UtcNow.AddDays(1))),
            "active with retirement date" => Valid(extraSigning: Entry(_keys.Create("pending-dated").Path, KeyStatus.Pending, DateTimeOffset.UtcNow)),
            "unknown status" => Valid(extraSigning: new KeyEntrySettings { CertificatePath = _keys.Create("no-status").Path, CertificatePassword = TestKeyMaterial.Password }),
            "duplicate key" => Duplicate(),
            "unknown provider" => WithProvider("AzureKeyVault"),
            _ => throw new ArgumentOutOfRangeException(nameof(problem)),
        };

        var message = Fails(settings, isDevelopment: true);

        Assert.StartsWith("Invalid Identity key ring:", message, StringComparison.Ordinal);
        Assert.DoesNotContain(TestKeyMaterial.Password, message, StringComparison.Ordinal);
        Assert.DoesNotContain("A-Different-Password-1", message, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", message, StringComparison.Ordinal);
    }

    [Fact]
    public void OutsideDevelopment_APasswordlessCertificate_IsRefused()
    {
        var unprotected = _keys.Create("no-password", password: null);
        var settings = Valid(activeSigningPath: unprotected.Path);
        settings.Signing[0].CertificatePassword = null;

        Assert.Contains("has no password configured", Fails(settings, isDevelopment: false), StringComparison.Ordinal);
    }

    public void Dispose() => _keys.Dispose();

    private KeyRingSettings Valid(
        string? activeSigningPath = null,
        KeyStatus activeSigningStatus = KeyStatus.Active,
        KeyEntrySettings? extraSigning = null)
    {
        var settings = new KeyRingSettings
        {
            Signing = [Entry(activeSigningPath ?? _keys.Create("sign-" + Guid.NewGuid().ToString("N")).Path, activeSigningStatus)],
            Encryption = [Entry(_keys.Create("enc-" + Guid.NewGuid().ToString("N")).Path, KeyStatus.Active)],
        };
        if (extraSigning is not null)
        {
            settings.Signing.Add(extraSigning);
        }

        return settings;
    }

    private KeyRingSettings Valid(string activeSigningPath) => Valid(activeSigningPath, KeyStatus.Active);

    private KeyRingSettings WithoutEncryption()
    {
        var settings = Valid();
        settings.Encryption.Clear();
        return settings;
    }

    private KeyRingSettings Duplicate()
    {
        var settings = Valid();
        settings.Signing.Add(Entry(settings.Signing[0].CertificatePath!, KeyStatus.Pending));
        return settings;
    }

    private KeyRingSettings WithProvider(string provider)
    {
        var settings = Valid();
        settings.Provider = provider;
        return settings;
    }

    private static KeyEntrySettings Entry(string path, KeyStatus status, DateTimeOffset? retiredAt = null) =>
        new() { CertificatePath = path, CertificatePassword = TestKeyMaterial.Password, Status = status, RetiredAt = retiredAt };

    private static KeyRing Load(KeyRingSettings settings, bool isDevelopment = true) =>
        KeyRingLoader.Load(settings, new CertificateFileKeySource(), Retention, DateTimeOffset.UtcNow, isDevelopment);

    private static string Fails(KeyRingSettings settings, bool isDevelopment) =>
        Assert.Throws<InvalidOperationException>(() => Load(settings, isDevelopment)).Message;
}
