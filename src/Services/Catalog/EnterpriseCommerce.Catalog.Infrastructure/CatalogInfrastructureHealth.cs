namespace EnterpriseCommerce.Catalog.Infrastructure;

/// <summary>Conventions of the Catalog readiness checks, shared with the host that maps the health endpoints.</summary>
public static class CatalogInfrastructureHealth
{
    /// <summary>The tag of the checks run by <c>/health/ready</c>.</summary>
    public const string ReadyTag = "ready";

    /// <summary>Every dependency check gives up after this long, so a hanging dependency cannot hang the probe.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(5);
}
