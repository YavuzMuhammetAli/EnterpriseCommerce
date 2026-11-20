using System.Security.Cryptography;
using EnterpriseCommerce.Identity.Api.Hosting;
using EnterpriseCommerce.Identity.TestSupport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseCommerce.Identity.UnitTests.Api;

/// <summary>
/// Identity's Data Protection key ring (Phase 2.3 S6.4): persistent, shared by every instance, encrypted at rest,
/// isolated by application name, and never silently ephemeral outside Development.
/// </summary>
public sealed class DataProtectionSetupTests : IDisposable
{
    private readonly TestKeyMaterial _keys = new();

    [Fact]
    public void OutsideDevelopment_TheKeyDirectoryAndTheEncryptionCertificate_AreRequired()
    {
        var errors = DataProtectionSetup.Validate(new IdentityDataProtectionSettings(), isDevelopment: false).ToList();

        Assert.Contains(errors, e => e.Contains("KeyDirectory is required", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("KeyEncryptionCertificatePath is required", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => DataProtectionSetup.Configure(new ServiceCollection(), new IdentityDataProtectionSettings(), isDevelopment: false));
    }

    [Fact]
    public void AnUnimplementedProvider_IsRefused() =>
        Assert.Contains(
            DataProtectionSetup.Validate(new IdentityDataProtectionSettings { Provider = "AzureBlob" }, isDevelopment: true),
            e => e.Contains("is not implemented", StringComparison.Ordinal));

    [Fact]
    public void AnUnreadableCertificate_FailsWithoutRevealingThePassword()
    {
        var settings = Settings(_keys.CreateCorrupt("corrupt"));

        var error = Assert.Throws<InvalidOperationException>(() => DataProtectionSetup.Configure(new ServiceCollection(), settings, isDevelopment: false));

        Assert.DoesNotContain(TestKeyMaterial.Password, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoInstances_ShareTheKeyRing_WhichIsEncryptedAtRest()
    {
        var certificate = _keys.Create("dp").Path;
        var first = Protector(Settings(certificate));
        var second = Protector(Settings(certificate));

        var payload = first.Protect("sso-session");

        Assert.Equal("sso-session", second.Unprotect(payload));
        var keyFile = Assert.Single(Directory.GetFiles(KeyDirectory, "key-*.xml"));
        var xml = File.ReadAllText(keyFile);
        Assert.Contains("encryptedSecret", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("<masterKey", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void AnotherApplicationOrAnotherCertificate_CannotReadIdentityPayloads()
    {
        var payload = Protector(Settings(_keys.Create("dp-owner").Path)).Protect("sso-session");

        // Same key directory, another application name (for example a BFF): isolated by the discriminator.
        var services = new ServiceCollection();
        services.AddDataProtection().SetApplicationName("EnterpriseCommerce.Bff.storefront").PersistKeysToFileSystem(new DirectoryInfo(KeyDirectory));
        var bff = services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);
        Assert.ThrowsAny<CryptographicException>(() => bff.Unprotect(payload));

        // Same application, but without the key-encryption certificate: the stored keys cannot be decrypted.
        var stranger = Protector(Settings(_keys.Create("dp-stranger").Path));
        Assert.ThrowsAny<CryptographicException>(() => stranger.Unprotect(payload));
    }

    public void Dispose() => _keys.Dispose();

    private const string Purpose = "test";

    private string KeyDirectory => Path.Combine(_keys.Directory, "data-protection");

    private IdentityDataProtectionSettings Settings(string certificatePath) => new()
    {
        KeyDirectory = KeyDirectory,
        KeyEncryptionCertificatePath = certificatePath,
        KeyEncryptionCertificatePassword = TestKeyMaterial.Password,
    };

    private static IDataProtector Protector(IdentityDataProtectionSettings settings)
    {
        var services = new ServiceCollection();
        DataProtectionSetup.Configure(services, settings, isDevelopment: false);
        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);
    }
}
