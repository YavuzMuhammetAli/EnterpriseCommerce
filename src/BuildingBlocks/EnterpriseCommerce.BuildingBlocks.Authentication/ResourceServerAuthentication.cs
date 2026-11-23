using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace EnterpriseCommerce.BuildingBlocks.Authentication;

/// <summary>
/// The validated values a resource server needs to accept Identity access tokens. Each service binds and validates its
/// own configuration section and passes the result here; this block reads no configuration.
/// </summary>
/// <param name="Issuer">Identity's public issuer URI, matched exactly against <c>iss</c>.</param>
/// <param name="MetadataAddress">Optional internal address of Identity's OpenID Connect metadata.</param>
/// <param name="Audiences">Every audience the service accepts: its service audience and its allowed front-end audiences.</param>
/// <param name="RequireHttpsMetadata">Whether the metadata must be fetched over HTTPS (true outside Development).</param>
public sealed record ResourceServerTokenSettings(
    string Issuer,
    string? MetadataAddress,
    IReadOnlyList<string> Audiences,
    bool RequireHttpsMetadata);

/// <summary>
/// Local access token validation for resource servers (ADR-017 section 4; ADR-025 section 3).
/// </summary>
/// <remarks>
/// <para>
/// One JWT bearer scheme validates every access token locally: the signature against Identity's published keys, the
/// exact issuer, one of the accepted audiences, RS256 only, <c>typ: at+jwt</c>, and 30 seconds of clock skew. A token
/// that fails any of these answers <c>401</c>.
/// </para>
/// <para>
/// Claims keep their JWT names (no inbound mapping). The role claim type is one that no token carries, so a forged
/// <c>role</c> claim never becomes a role: merchant and platform roles come from the owning services only
/// (ADR-017 section 2).
/// </para>
/// </remarks>
public static class ResourceServerAuthentication
{
    /// <summary>The accepted signing algorithm.</summary>
    public const string Algorithm = SecurityAlgorithms.RsaSha256;

    /// <summary>The accepted JWT <c>typ</c> header of access tokens.</summary>
    public const string AccessTokenType = "at+jwt";

    /// <summary>The accepted clock skew.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    /// <summary>Adds the JWT bearer scheme as the default authentication scheme.</summary>
    public static AuthenticationBuilder AddResourceServerJwtBearer(this IServiceCollection services, ResourceServerTokenSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Audiences.Count == 0)
        {
            throw new ArgumentException("At least one audience is required.", nameof(settings));
        }

        return services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.Authority = settings.Issuer;
            if (settings.MetadataAddress is { Length: > 0 } metadataAddress)
            {
                options.MetadataAddress = metadataAddress;
            }

            options.RequireHttpsMetadata = settings.RequireHttpsMetadata;
            options.MapInboundClaims = false;

            var parameters = options.TokenValidationParameters;
            parameters.ValidIssuer = settings.Issuer;
            parameters.ValidAudiences = [.. settings.Audiences];
            parameters.ValidAlgorithms = [Algorithm];
            parameters.ValidTypes = [AccessTokenType];
            parameters.ClockSkew = ClockSkew;
            parameters.NameClaimType = CallerClaims.Subject;
            parameters.RoleClaimType = CallerClaims.NoRoleClaimType;
        });
    }
}
