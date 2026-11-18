using System.Net;
using EnterpriseCommerce.Identity.Infrastructure.SecurityEmails;

namespace EnterpriseCommerce.Identity.Api.SecurityEmails;

/// <summary>The development mail sink opt-in (<c>Identity:DevelopmentMailSink</c>). Never committed as enabled.</summary>
internal sealed class DevelopmentMailSinkSettings
{
    public const string SectionName = "Identity:DevelopmentMailSink";

    /// <summary>The assembly that must never be present outside Development (ADR-018 section 4, layer 4).</summary>
    public const string AssemblyFileName = "EnterpriseCommerce.Identity.DevMailSink.dll";

    /// <summary>Compose-internal mail sink host names that are accepted besides loopback.</summary>
    public static readonly IReadOnlySet<string> ApprovedComposeHosts = new HashSet<string>(["mailpit"], StringComparer.OrdinalIgnoreCase);

    public bool Enabled { get; set; }

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 1025;
}

/// <summary>The outcome of the development mail sink guard.</summary>
internal sealed record DevelopmentMailSinkDecision(bool Register, string? Error);

/// <summary>
/// Composition of security email delivery in the host (ADR-018 sections 3 and 4). Production always uses the
/// Notification integration registered by Infrastructure; the development mail sink can replace it only in a Debug
/// build, in Development, with an explicit opt-in and a local host.
/// </summary>
internal static class SecurityEmailSetup
{
    public static WebApplicationBuilder AddSecurityEmailDelivery(this WebApplicationBuilder builder)
    {
        var isDevelopment = builder.Environment.IsDevelopment();
        var sinkSection = builder.Configuration.GetSection(DevelopmentMailSinkSettings.SectionName);
        var sink = sinkSection.Get<DevelopmentMailSinkSettings>() ?? new();
        var decision = Evaluate(
            isDevelopment,
            sinkSection.Exists(),
            sink,
            File.Exists(Path.Combine(AppContext.BaseDirectory, DevelopmentMailSinkSettings.AssemblyFileName)));

        if (decision.Error is not null)
        {
            throw new InvalidOperationException(decision.Error);
        }

        if (!isDevelopment)
        {
            // ADR-018: outside Development, Identity runs only with the production delivery adapter configured.
            var notification = builder.Configuration.GetSection(NotificationDeliveryOptions.SectionName).Get<NotificationDeliveryOptions>() ?? new();
            if (!notification.IsConfigured)
            {
                throw new InvalidOperationException(
                    $"{NotificationDeliveryOptions.SectionName} must be configured outside Development (BaseAddress, TokenEndpoint, Issuer, svc- ClientId, PrivateKeyPath).");
            }
        }

        if (decision.Register)
        {
#if DEBUG
            // Layer 2: this call does not exist in Release builds.
            EnterpriseCommerce.Identity.DevMailSink.DevelopmentMailSinkRegistration.AddDevelopmentMailSink(builder.Services, sink.Host, sink.Port);
#else
            throw new InvalidOperationException("The development mail sink is available in Debug builds only.");
#endif
        }

        return builder;
    }

    /// <summary>Layers 3 and 4 of ADR-018 section 4, as a pure decision.</summary>
    internal static DevelopmentMailSinkDecision Evaluate(bool isDevelopment, bool sectionPresent, DevelopmentMailSinkSettings settings, bool assemblyPresent)
    {
        if (!isDevelopment)
        {
            if (sectionPresent)
            {
                return new(false, $"{DevelopmentMailSinkSettings.SectionName} must not be configured outside Development.");
            }

            return assemblyPresent
                ? new(false, $"{DevelopmentMailSinkSettings.AssemblyFileName} must not be deployed outside Development.")
                : new(false, null);
        }

        if (!settings.Enabled)
        {
            return new(false, null);
        }

        if (settings.Port is < 1 or > 65535)
        {
            return new(false, $"{DevelopmentMailSinkSettings.SectionName}:Port is invalid.");
        }

        var isLocal = string.Equals(settings.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(settings.Host, out var address) && IPAddress.IsLoopback(address))
            || DevelopmentMailSinkSettings.ApprovedComposeHosts.Contains(settings.Host);

        return isLocal
            ? new(true, null)
            : new(false, $"{DevelopmentMailSinkSettings.SectionName}:Host must be loopback or an approved Compose host.");
    }
}
