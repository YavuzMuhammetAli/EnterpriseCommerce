using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace EnterpriseCommerce.Identity.Api.Protocol;

/// <summary>The YARP Gateway as the only trusted proxy (<c>Identity:Proxy</c>, ADR-017 section 10).</summary>
internal sealed class TrustedProxySettings
{
    public const string SectionName = "Identity:Proxy";

    /// <summary>IP addresses of the Gateway instances.</summary>
    public List<string> KnownProxies { get; set; } = [];

    /// <summary>CIDR networks of the Gateway instances (for example a container network).</summary>
    public List<string> KnownNetworks { get; set; } = [];

    /// <summary>The public Identity host names accepted in <c>X-Forwarded-Host</c>.</summary>
    public List<string> PublicHosts { get; set; } = [];
}

/// <summary>
/// Forwarded headers are trusted only from the Gateway, with <c>ForwardLimit = 1</c>, and only for the public Identity
/// host names. Host filtering (<c>AllowedHosts</c>) rejects any other <c>Host</c>. Together they keep a direct request
/// with a spoofed <c>Host</c> or <c>X-Forwarded-*</c> header from influencing discovery URLs (PoC-14).
/// </summary>
internal static class TrustedProxySetup
{
    public static WebApplicationBuilder AddTrustedProxy(this WebApplicationBuilder builder)
    {
        var settings = builder.Configuration.GetSection(TrustedProxySettings.SectionName).Get<TrustedProxySettings>() ?? new();
        var errors = Validate(settings, builder.Configuration["AllowedHosts"], builder.Environment.IsDevelopment()).ToList();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Invalid proxy configuration: " + string.Join(" ", errors));
        }

        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            options.ForwardLimit = 1;

            // Nothing is trusted by default, not even loopback: only the configured Gateway addresses.
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var proxy in settings.KnownProxies)
            {
                options.KnownProxies.Add(IPAddress.Parse(proxy));
            }

            foreach (var network in settings.KnownNetworks)
            {
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            }

            options.AllowedHosts = settings.PublicHosts;
        });

        return builder;
    }

    internal static IEnumerable<string> Validate(TrustedProxySettings settings, string? allowedHosts, bool isDevelopment)
    {
        foreach (var proxy in settings.KnownProxies.Where(proxy => !IPAddress.TryParse(proxy, out _)))
        {
            yield return $"'{proxy}' is not an IP address.";
        }

        foreach (var network in settings.KnownNetworks.Where(network => !System.Net.IPNetwork.TryParse(network, out _)))
        {
            yield return $"'{network}' is not a CIDR network.";
        }

        if (isDevelopment)
        {
            yield break;
        }

        if (settings.KnownProxies.Count == 0 && settings.KnownNetworks.Count == 0)
        {
            yield return "Identity:Proxy must name the Gateway (KnownProxies or KnownNetworks) outside Development.";
        }

        if (settings.PublicHosts.Count == 0)
        {
            yield return "Identity:Proxy:PublicHosts must list the public Identity host names outside Development.";
        }

        if (string.IsNullOrWhiteSpace(allowedHosts) || allowedHosts.Split(';').Any(host => host.Trim() == "*"))
        {
            yield return "AllowedHosts must list explicit host names outside Development (host filtering).";
        }
    }
}
