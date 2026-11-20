using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace EnterpriseCommerce.Bff.Hosting;

/// <summary>The YARP Gateway as the only trusted proxy (<c>Bff:Proxy</c>), as for Identity (ADR-017 section 10).</summary>
internal sealed class TrustedProxySettings
{
    public const string SectionName = "Bff:Proxy";

    public List<string> KnownProxies { get; set; } = [];

    public List<string> KnownNetworks { get; set; } = [];

    /// <summary>The public host names of this front end accepted in <c>X-Forwarded-Host</c>.</summary>
    public List<string> PublicHosts { get; set; } = [];
}

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
            yield return "Bff:Proxy must name the Gateway (KnownProxies or KnownNetworks) outside Development.";
        }

        if (settings.PublicHosts.Count == 0)
        {
            yield return "Bff:Proxy:PublicHosts must list the public host names of this front end outside Development.";
        }

        if (string.IsNullOrWhiteSpace(allowedHosts) || allowedHosts.Split(';').Any(host => host.Trim() == "*"))
        {
            yield return "AllowedHosts must list explicit host names outside Development (host filtering).";
        }
    }
}
