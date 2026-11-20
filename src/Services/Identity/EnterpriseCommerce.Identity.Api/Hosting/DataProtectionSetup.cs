using EnterpriseCommerce.Identity.Api.Protocol.Keys;
using Microsoft.AspNetCore.DataProtection;

namespace EnterpriseCommerce.Identity.Api.Hosting;

/// <summary>
/// Identity's ASP.NET Core Data Protection key ring (section <c>Identity:DataProtection</c>, Phase 2.3 S6.4). It protects
/// the Identity SSO cookie and the antiforgery tokens. It is unrelated to the OpenIddict signing and encryption keys
/// (<c>Identity:Keys</c>), which sign and encrypt tokens.
/// </summary>
internal sealed class IdentityDataProtectionSettings
{
    public const string SectionName = "Identity:DataProtection";

    /// <summary>Isolates Identity's key ring and payloads from every other application, including the BFFs.</summary>
    public const string ApplicationName = "EnterpriseCommerce.Identity";

    /// <summary>The only implemented provider: a directory shared by every Identity instance, keys encrypted with a certificate.</summary>
    public const string FileSystemProvider = "FileSystem";

    public string Provider { get; set; } = FileSystemProvider;

    /// <summary>The key directory, shared by every Identity instance of one environment (a protected volume). Required outside Development.</summary>
    public string? KeyDirectory { get; set; }

    /// <summary>A PKCS#12 file whose certificate encrypts the keys at rest. Required outside Development. Never committed.</summary>
    public string? KeyEncryptionCertificatePath { get; set; }

    public string? KeyEncryptionCertificatePassword { get; set; }
}

internal static class DataProtectionSetup
{
    public static WebApplicationBuilder AddIdentityDataProtection(this WebApplicationBuilder builder)
    {
        var settings = builder.Configuration.GetSection(IdentityDataProtectionSettings.SectionName).Get<IdentityDataProtectionSettings>() ?? new();
        Configure(builder.Services, settings, builder.Environment.IsDevelopment());
        return builder;
    }

    /// <summary>Configures Data Protection or throws. Outside Development there is no silent fallback to ephemeral keys.</summary>
    public static void Configure(IServiceCollection services, IdentityDataProtectionSettings settings, bool isDevelopment)
    {
        var errors = Validate(settings, isDevelopment).ToList();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Invalid Identity Data Protection configuration: " + string.Join(" ", errors));
        }

        var dataProtection = services.AddDataProtection().SetApplicationName(IdentityDataProtectionSettings.ApplicationName);
        if (!string.IsNullOrWhiteSpace(settings.KeyDirectory))
        {
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(Environment.ExpandEnvironmentVariables(settings.KeyDirectory)));
        }

        if (!string.IsNullOrWhiteSpace(settings.KeyEncryptionCertificatePath))
        {
            System.Security.Cryptography.X509Certificates.X509Certificate2 certificate;
            try
            {
                certificate = new CertificateFileKeySource().Load(
                    new KeyEntrySettings { CertificatePath = settings.KeyEncryptionCertificatePath, CertificatePassword = settings.KeyEncryptionCertificatePassword },
                    isDevelopment);
            }
            catch (KeyRingException exception)
            {
                throw new InvalidOperationException($"Invalid Identity Data Protection configuration: {IdentityDataProtectionSettings.SectionName}:KeyEncryptionCertificatePath: {exception.Message}.");
            }

            // The same certificate also decrypts: ASP.NET Core registers it for decryption, no certificate store needed.
            dataProtection.ProtectKeysWithCertificate(certificate);
        }
    }

    public static IEnumerable<string> Validate(IdentityDataProtectionSettings settings, bool isDevelopment)
    {
        if (!string.Equals(settings.Provider, IdentityDataProtectionSettings.FileSystemProvider, StringComparison.Ordinal))
        {
            yield return $"{IdentityDataProtectionSettings.SectionName}:Provider '{settings.Provider}' is not implemented; only '{IdentityDataProtectionSettings.FileSystemProvider}' is available.";
        }

        if (!isDevelopment)
        {
            if (string.IsNullOrWhiteSpace(settings.KeyDirectory))
            {
                yield return $"{IdentityDataProtectionSettings.SectionName}:KeyDirectory is required outside Development (shared by every Identity instance).";
            }

            if (string.IsNullOrWhiteSpace(settings.KeyEncryptionCertificatePath))
            {
                yield return $"{IdentityDataProtectionSettings.SectionName}:KeyEncryptionCertificatePath is required outside Development (keys are encrypted at rest).";
            }
        }
        else if (!string.IsNullOrWhiteSpace(settings.KeyEncryptionCertificatePath) && string.IsNullOrWhiteSpace(settings.KeyDirectory))
        {
            yield return $"{IdentityDataProtectionSettings.SectionName}:KeyEncryptionCertificatePath needs a KeyDirectory.";
        }
    }
}
