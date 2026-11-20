using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;

namespace EnterpriseCommerce.Identity.Api.Protocol.Keys;

/// <summary>The rollover state of one configured key (docs/security/IDENTITY_OPERATIONS.md, rollover procedure).</summary>
internal enum KeyStatus
{
    /// <summary>Published (JWKS) and accepted for validation, but never used to sign or encrypt. Staged for activation.</summary>
    Pending,

    /// <summary>Signs (signing keys) or encrypts (encryption keys) every new token. Exactly one per use.</summary>
    Active,

    /// <summary>No longer used for new tokens; still published and accepted until its retention window has passed.</summary>
    Retired,
}

/// <summary>What a key is used for. Signing and encryption keys are separate certificates (ADR-017 section 12).</summary>
internal enum KeyUse
{
    Signing,
    Encryption,
}

/// <summary>One configured key: a PKCS#12 file and its rollover state (<c>Identity:Keys:Signing:n</c> or <c>:Encryption:n</c>).</summary>
internal sealed class KeyEntrySettings
{
    /// <summary>The PKCS#12 (.pfx) file, a mounted secret in deployed environments. Never committed.</summary>
    public string? CertificatePath { get; set; }

    /// <summary>The PKCS#12 password, from a protected store or the environment. Required outside Development.</summary>
    public string? CertificatePassword { get; set; }

    public KeyStatus? Status { get; set; }

    /// <summary>When the key was retired (UTC). Required for <see cref="KeyStatus.Retired"/>; it starts the retention window.</summary>
    public DateTimeOffset? RetiredAt { get; set; }
}

/// <summary>
/// The signing and encryption key ring (<c>Identity:Keys</c>, ADR-017 section 12). Activation, publication and retirement
/// are explicit configuration states, never certificate dates or code constants (finding I34).
/// </summary>
internal sealed class KeyRingSettings
{
    /// <summary>The only implemented provider: PKCS#12 files from mounted secrets.</summary>
    public const string CertificateFilesProvider = "CertificateFiles";

    /// <summary>
    /// The key material provider. Only <see cref="CertificateFilesProvider"/> exists. A KMS or HSM provider is a
    /// deployment-phase decision (D19) and is refused until it is implemented.
    /// </summary>
    public string Provider { get; set; } = CertificateFilesProvider;

    public List<KeyEntrySettings> Signing { get; set; } = [];

    public List<KeyEntrySettings> Encryption { get; set; } = [];

    /// <summary>
    /// The longest time a validator (a BFF or a service) may keep using a cached JWKS. It is added to a retired signing
    /// key's retention, and a pending key must be published at least this long before it is activated.
    /// </summary>
    public TimeSpan ValidatorCacheAllowance { get; set; } = TimeSpan.FromHours(24);
}

/// <summary>A loaded key of the ring. <see cref="SecurityKey"/> holds the private key and is never logged or published.</summary>
internal sealed record RingKey(
    string KeyId,
    KeyUse Use,
    KeyStatus Status,
    SecurityKey SecurityKey,
    DateTimeOffset? NotBefore,
    DateTimeOffset? NotAfter,
    DateTimeOffset? RetiredAt);

/// <summary>A retired key that was not loaded because its retention window has passed.</summary>
internal sealed record DroppedKey(string KeyId, KeyUse Use, DateTimeOffset RetiredAt);

/// <summary>The keys Identity runs with. Built once at startup; the process restarts to change it.</summary>
internal sealed class KeyRing(IReadOnlyList<RingKey> keys, IReadOnlyList<DroppedKey> dropped, bool isEphemeral)
{
    public IReadOnlyList<RingKey> Keys { get; } = keys;

    public IReadOnlyList<DroppedKey> Dropped { get; } = dropped;

    /// <summary>Development without configured keys: generated in memory, lost on restart.</summary>
    public bool IsEphemeral { get; } = isEphemeral;

    public RingKey ActiveSigning => Keys.Single(key => key.Use == KeyUse.Signing && key.Status == KeyStatus.Active);

    public RingKey ActiveEncryption => Keys.Single(key => key.Use == KeyUse.Encryption && key.Status == KeyStatus.Active);

    public IEnumerable<RingKey> Of(KeyUse use) => Keys.Where(key => key.Use == use);
}

/// <summary>How long a retired key stays loaded after <see cref="KeyEntrySettings.RetiredAt"/>.</summary>
/// <param name="Signing">Replay protection window + clock skew + validator cache allowance.</param>
/// <param name="Encryption">Replay protection window + clock skew: no token encrypted with it can still be accepted after that.</param>
internal sealed record KeyRetention(TimeSpan Signing, TimeSpan Encryption);

/// <summary>
/// The key material provider boundary. Implementations return a certificate with its private key, or throw. A KMS or HSM
/// implementation would replace <see cref="CertificateFileKeySource"/> here; none exists yet (deployment gate).
/// </summary>
internal interface IKeyMaterialSource
{
    X509Certificate2 Load(KeyEntrySettings entry, bool isDevelopment);
}

/// <summary>Loads PKCS#12 files. Outside Development the file must have a password and, on Unix, must not be readable by others.</summary>
internal sealed class CertificateFileKeySource : IKeyMaterialSource
{
    public X509Certificate2 Load(KeyEntrySettings entry, bool isDevelopment)
    {
        var path = entry.CertificatePath!;
        if (!File.Exists(path))
        {
            throw new KeyRingException($"the certificate file '{Path.GetFileName(path)}' does not exist");
        }

        if (!isDevelopment)
        {
            if (string.IsNullOrEmpty(entry.CertificatePassword))
            {
                throw new KeyRingException($"the certificate file '{Path.GetFileName(path)}' has no password configured");
            }

            if (!OperatingSystem.IsWindows()
                && (File.GetUnixFileMode(path) & (UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.GroupWrite)) != 0)
            {
                throw new KeyRingException($"the certificate file '{Path.GetFileName(path)}' is readable or writable by other users");
            }
        }

        try
        {
            // EphemeralKeySet: the private key stays in process memory and is never written to a machine key store.
            var flags = OperatingSystem.IsWindows() ? X509KeyStorageFlags.EphemeralKeySet : X509KeyStorageFlags.DefaultKeySet;
            return X509CertificateLoader.LoadPkcs12FromFile(path, entry.CertificatePassword, flags);
        }
        catch (CryptographicException)
        {
            // The exception is not chained: nothing about the file content or the password reaches a log.
            throw new KeyRingException($"the certificate file '{Path.GetFileName(path)}' cannot be read (wrong password or corrupt content)");
        }
    }
}

/// <summary>A key ring problem. Its message names files and positions only, never key material or passwords.</summary>
internal sealed class KeyRingException(string message) : Exception(message);

/// <summary>Builds and validates the key ring at startup. Every problem stops the host (fail closed).</summary>
internal static class KeyRingLoader
{
    public const int MinimumRsaKeySize = 3072;

    public static KeyRing Load(
        KeyRingSettings settings,
        IKeyMaterialSource source,
        KeyRetention retention,
        DateTimeOffset now,
        bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(retention);

        if (!string.Equals(settings.Provider, KeyRingSettings.CertificateFilesProvider, StringComparison.Ordinal))
        {
            throw Invalid([$"Identity:Keys:Provider '{settings.Provider}' is not implemented; only '{KeyRingSettings.CertificateFilesProvider}' is available."]);
        }

        if (settings.Signing.Count == 0 && settings.Encryption.Count == 0)
        {
            if (!isDevelopment)
            {
                throw Invalid(["Signing and encryption certificates must be configured outside Development (ADR-017 section 12)."]);
            }

            return Ephemeral();
        }

        if (settings.ValidatorCacheAllowance < TimeSpan.Zero || settings.ValidatorCacheAllowance > TimeSpan.FromDays(30))
        {
            throw Invalid(["Identity:Keys:ValidatorCacheAllowance must be between 0 and 30 days."]);
        }

        var errors = new List<string>();
        var keys = new List<RingKey>();
        var dropped = new List<DroppedKey>();
        LoadUse(KeyUse.Signing, settings.Signing, retention.Signing);
        LoadUse(KeyUse.Encryption, settings.Encryption, retention.Encryption);

        foreach (var use in new[] { KeyUse.Signing, KeyUse.Encryption })
        {
            var active = keys.Count(key => key.Use == use && key.Status == KeyStatus.Active);
            if (active != 1)
            {
                errors.Add($"Identity:Keys:{use} needs exactly one Active key ({active} configured).");
            }
        }

        foreach (var duplicate in keys.GroupBy(key => key.KeyId, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            errors.Add($"The certificate with key ID {duplicate.Key} is configured more than once.");
        }

        if (errors.Count > 0)
        {
            foreach (var key in keys)
            {
                ((X509SecurityKey)key.SecurityKey).Certificate.Dispose();
            }

            throw Invalid(errors);
        }

        return new KeyRing(keys, dropped, isEphemeral: false);

        void LoadUse(KeyUse use, List<KeyEntrySettings> entries, TimeSpan retiredRetention)
        {
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                var position = $"Identity:Keys:{use}:{index}";

                if (string.IsNullOrWhiteSpace(entry.CertificatePath) || entry.Status is not { } status || !Enum.IsDefined(status))
                {
                    errors.Add($"{position} needs CertificatePath and Status (Pending, Active or Retired).");
                    continue;
                }

                if (status == KeyStatus.Retired)
                {
                    if (entry.RetiredAt is not { } retiredAt || retiredAt > now)
                    {
                        errors.Add($"{position} is Retired and needs a RetiredAt that is not in the future.");
                        continue;
                    }

                    if (now - retiredAt > retiredRetention)
                    {
                        // Past its window: nothing it signed or encrypted can still be accepted. Not loaded at all.
                        dropped.Add(new DroppedKey(KeyIdOfFile(entry, source, isDevelopment) ?? "unreadable", use, retiredAt));
                        continue;
                    }
                }
                else if (entry.RetiredAt is not null)
                {
                    errors.Add($"{position} has a RetiredAt but is not Retired.");
                    continue;
                }

                X509Certificate2 certificate;
                try
                {
                    certificate = source.Load(entry, isDevelopment);
                }
                catch (KeyRingException exception)
                {
                    errors.Add($"{position}: {exception.Message}.");
                    continue;
                }

                var problem = Check(certificate, status, now);
                if (problem is not null)
                {
                    errors.Add($"{position}: {problem}.");
                    certificate.Dispose();
                    continue;
                }

                var keyId = KeyIdOf(certificate);
                keys.Add(new RingKey(keyId, use, status, new X509SecurityKey(certificate, keyId), certificate.NotBefore.ToUniversalTime(), certificate.NotAfter.ToUniversalTime(), entry.RetiredAt));
            }
        }
    }

    /// <summary>The key ID: the certificate's SHA-256 thumbprint. Unique per certificate, public, stable across restarts.</summary>
    public static string KeyIdOf(X509Certificate2 certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256);

    private static string? Check(X509Certificate2 certificate, KeyStatus status, DateTimeOffset now)
    {
        if (!certificate.HasPrivateKey)
        {
            return "the certificate has no private key";
        }

        if (certificate.GetRSAPublicKey() is not { KeySize: >= MinimumRsaKeySize } rsa)
        {
            return $"the certificate must use an RSA key of at least {MinimumRsaKeySize} bits (ADR-017 section 2)";
        }

        rsa.Dispose();
        var notBefore = certificate.NotBefore.ToUniversalTime();
        var notAfter = certificate.NotAfter.ToUniversalTime();
        return status switch
        {
            KeyStatus.Active when now < notBefore || now >= notAfter => "the Active certificate is outside its validity period",
            KeyStatus.Pending when now >= notAfter => "the Pending certificate has already expired",
            _ => null,
        };
    }

    private static string? KeyIdOfFile(KeyEntrySettings entry, IKeyMaterialSource source, bool isDevelopment)
    {
        try
        {
            using var certificate = source.Load(entry, isDevelopment);
            return KeyIdOf(certificate);
        }
        catch (KeyRingException)
        {
            // A retired file that is already gone is fine: it would not be loaded anyway.
            return null;
        }
    }

    private static KeyRing Ephemeral()
    {
        // Development and tests only: generated in memory at startup, never stored or committed. Tokens become invalid
        // when the process restarts.
        RingKey Generate(KeyUse use)
        {
            var keyId = Guid.NewGuid().ToString("N");
            return new RingKey(keyId, use, KeyStatus.Active, new RsaSecurityKey(RSA.Create(MinimumRsaKeySize)) { KeyId = keyId }, null, null, null);
        }

        return new KeyRing([Generate(KeyUse.Signing), Generate(KeyUse.Encryption)], [], isEphemeral: true);
    }

    private static InvalidOperationException Invalid(IEnumerable<string> errors) =>
        new("Invalid Identity key ring: " + string.Join(" ", errors));
}

/// <summary>
/// Makes the Active keys the ones OpenIddict uses. OpenIddict sorts its credentials by certificate dates (V24) and signs
/// with the first one (V44), so a pending certificate with a later expiry would otherwise start signing early. This
/// post-configuration is registered after OpenIddict's own and moves the Active signing and encryption credentials to
/// the front. A test asserts that issued tokens carry the Active key ID.
/// </summary>
internal sealed class ActiveKeysFirst(KeyRing ring) : IPostConfigureOptions<OpenIddictServerOptions>
{
    public void PostConfigure(string? name, OpenIddictServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        MoveFirst(options.SigningCredentials, ring.ActiveSigning.KeyId, credential => credential.Key.KeyId);
        MoveFirst(options.EncryptionCredentials, ring.ActiveEncryption.KeyId, credential => credential.Key.KeyId);
    }

    private static void MoveFirst<T>(List<T> credentials, string keyId, Func<T, string?> keyIdOf)
    {
        var index = credentials.FindIndex(credential => string.Equals(keyIdOf(credential), keyId, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new InvalidOperationException("The Active key is not registered with the authorization server.");
        }

        var active = credentials[index];
        credentials.RemoveAt(index);
        credentials.Insert(0, active);
    }
}

/// <summary>Logs the key ring once at startup: key IDs, states and validity only (public information).</summary>
internal sealed partial class KeyRingReporter(KeyRing ring, ILogger<KeyRingReporter> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (ring.IsEphemeral)
        {
            LogEphemeral(logger);
        }

        foreach (var key in ring.Keys.Where(_ => !ring.IsEphemeral))
        {
            LogKey(logger, key.Use, key.KeyId, key.Status, key.NotAfter);
        }

        foreach (var key in ring.Dropped)
        {
            LogDropped(logger, key.Use, key.KeyId, key.RetiredAt);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 2300, Level = LogLevel.Warning, Message = "Identity runs with ephemeral development keys; tokens become invalid on restart")]
    private static partial void LogEphemeral(ILogger logger);

    [LoggerMessage(EventId = 2301, Level = LogLevel.Information, Message = "Key ring: {Use} key {KeyId} is {Status}, valid until {NotAfter}")]
    private static partial void LogKey(ILogger logger, KeyUse use, string keyId, KeyStatus status, DateTimeOffset? notAfter);

    [LoggerMessage(EventId = 2302, Level = LogLevel.Warning, Message = "Key ring: retired {Use} key {KeyId} (retired at {RetiredAt}) is past its retention window and was not loaded; remove it from the configuration")]
    private static partial void LogDropped(ILogger logger, KeyUse use, string keyId, DateTimeOffset retiredAt);
}
