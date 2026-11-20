using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EnterpriseCommerce.Identity.TestSupport;

/// <summary>
/// Throwaway self-signed RSA certificates written as password-protected PKCS#12 files to a private temporary directory,
/// deleted on dispose. Nothing is ever written into the repository.
/// </summary>
public sealed class TestKeyMaterial : IDisposable
{
    public const string Password = "Test-Pfx-Password-Never-Logged-7";

    public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("ec-identity-keys-").FullName;

    /// <summary>Creates a certificate file and returns its path and key ID (SHA-256 thumbprint).</summary>
    public (string Path, string KeyId) Create(
        string name,
        int keySize = 3072,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null,
        string? password = Password)
    {
        using var rsa = RSA.Create(keySize);
        var request = new CertificateRequest($"CN=EnterpriseCommerce test {name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(notBefore ?? DateTimeOffset.UtcNow.AddDays(-1), notAfter ?? DateTimeOffset.UtcNow.AddDays(365));
        var path = Path.Combine(Directory, name + ".pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12, password));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return (path, certificate.GetCertHashString(HashAlgorithmName.SHA256));
    }

    /// <summary>A file that is not a PKCS#12 container.</summary>
    public string CreateCorrupt(string name)
    {
        var path = Path.Combine(Directory, name + ".pfx");
        File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(512));
        return path;
    }

    public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
}
