using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EnterpriseCommerce.Bff.Configuration;
using Microsoft.AspNetCore.DataProtection;

namespace EnterpriseCommerce.Bff.Hosting;

/// <summary>
/// The BFF's Data Protection key ring (Phase 2.3 S6.4). One key ring per deployment, isolated by its application name and
/// shared by that deployment's instances through <see cref="BffSettings.DataProtectionKeyDirectory"/>. Outside
/// Development the keys are encrypted at rest with <see cref="BffSettings.DataProtectionCertificatePath"/>
/// (settings validation requires both). These keys protect the OAuth state, the nonce cookie and session record contents;
/// they are unrelated to Identity's token signing keys.
/// </summary>
internal static class DataProtectionSetup
{
    public static void Configure(IServiceCollection services, BffSettings settings)
    {
        var dataProtection = services.AddDataProtection().SetApplicationName($"EnterpriseCommerce.Bff.{settings.Deployment}");
        if (!string.IsNullOrWhiteSpace(settings.DataProtectionKeyDirectory))
        {
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(Environment.ExpandEnvironmentVariables(settings.DataProtectionKeyDirectory)));
        }

        if (!string.IsNullOrWhiteSpace(settings.DataProtectionCertificatePath))
        {
            dataProtection.ProtectKeysWithCertificate(LoadCertificate(settings.DataProtectionCertificatePath, settings.DataProtectionCertificatePassword));
        }
    }

    private static X509Certificate2 LoadCertificate(string path, string? password)
    {
        try
        {
            var flags = OperatingSystem.IsWindows() ? X509KeyStorageFlags.EphemeralKeySet : X509KeyStorageFlags.DefaultKeySet;
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password, flags);
            return certificate.HasPrivateKey
                ? certificate
                : throw new InvalidOperationException($"Bff:DataProtectionCertificatePath '{Path.GetFileName(path)}' has no private key.");
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // Not chained: nothing about the file content or the password reaches a log.
            throw new InvalidOperationException($"Bff:DataProtectionCertificatePath '{Path.GetFileName(path)}' cannot be read (missing file, wrong password or corrupt content).");
        }
    }
}
