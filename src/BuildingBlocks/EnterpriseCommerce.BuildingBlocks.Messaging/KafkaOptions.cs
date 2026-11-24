namespace EnterpriseCommerce.BuildingBlocks.Messaging;

/// <summary>How the relay connects to Kafka.</summary>
public enum KafkaSecurityProtocol
{
    /// <summary>No encryption and no authentication. Development only (ADR-023 section 6).</summary>
    Plaintext,

    /// <summary>TLS; with a client certificate and key it is mutual TLS, which authenticates the producer.</summary>
    Ssl,
}

/// <summary>
/// The relay's Kafka connection (the service's <see cref="OutboxRelayProfile.KafkaSection"/>, for example
/// <c>TenantMerchant:Kafka</c>; ADR-023 section 6). Without
/// <see cref="BootstrapServers"/> the relay is disabled in Development and the service refuses to start elsewhere.
/// Outside Development only mutual TLS is accepted: the connection must be encrypted and authenticated. SASL is not
/// supported yet (a production gate): the service holds no credential (ServiceOwnershipTests), and a client key is a
/// mounted file, never a configuration value.
/// </summary>
public sealed class KafkaOptions
{
    /// <summary>Comma-separated <c>host:port</c> pairs.</summary>
    public string? BootstrapServers { get; set; }

    public KafkaSecurityProtocol SecurityProtocol { get; set; } = KafkaSecurityProtocol.Plaintext;

    /// <summary>The CA bundle (PEM file) that verifies the brokers; the system store when not set.</summary>
    public string? SslCaLocation { get; set; }

    /// <summary>The client certificate (PEM file) for mutual TLS.</summary>
    public string? SslCertificateLocation { get; set; }

    /// <summary>The unencrypted client private key (PEM file) for mutual TLS, mounted as a secret.</summary>
    public string? SslKeyLocation { get; set; }

    /// <summary>The bound of one delivery (<c>message.timeout.ms</c>): a delivery report arrives within it.</summary>
    public TimeSpan MessageTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Whether a broker is configured; without one the relay does not run.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BootstrapServers);

    /// <summary>The validation errors; empty when the settings are usable in this environment.</summary>
    public IEnumerable<string> Validate(bool isDevelopment)
    {
        if (!IsConfigured)
        {
            if (!isDevelopment)
            {
                yield return "BootstrapServers is required outside Development: the service publishes integration events (ADR-023 section 6).";
            }

            yield break;
        }

        if (MessageTimeout < TimeSpan.FromSeconds(1) || MessageTimeout > TimeSpan.FromMinutes(5))
        {
            yield return "MessageTimeout must be 1 second to 5 minutes.";
        }

        if (!Enum.IsDefined(SecurityProtocol))
        {
            yield return "SecurityProtocol must be Plaintext or Ssl.";
        }

        if (!isDevelopment && SecurityProtocol != KafkaSecurityProtocol.Ssl)
        {
            yield return "SecurityProtocol must be Ssl outside Development: an unencrypted, unauthenticated connection is refused (ADR-023 section 6).";
        }

        if (!isDevelopment && (string.IsNullOrWhiteSpace(SslCertificateLocation) || string.IsNullOrWhiteSpace(SslKeyLocation)))
        {
            yield return "SslCertificateLocation and SslKeyLocation are required outside Development: the producer authenticates with mutual TLS.";
        }

        foreach (var (name, path) in new[] { (nameof(SslCaLocation), SslCaLocation), (nameof(SslCertificateLocation), SslCertificateLocation), (nameof(SslKeyLocation), SslKeyLocation) })
        {
            if (!string.IsNullOrWhiteSpace(path) && !File.Exists(path))
            {
                yield return $"{name} names a file that does not exist.";
            }
        }
    }
}
