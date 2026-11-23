using Microsoft.AspNetCore.Mvc;

namespace EnterpriseCommerce.Catalog.Api.Hosting;

/// <summary>Request size limits of the Catalog API (ADR-024 section 14).</summary>
internal static class RequestLimits
{
    /// <summary>The largest accepted request body: 256 KiB, well above the largest valid product.</summary>
    public const long MaxRequestBodyBytes = 256 * 1024;

    /// <summary>Applies the limit to every endpoint of a group; Kestrel also enforces it for the whole host.</summary>
    public static TBuilder WithRequestSizeLimit<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes));
}
