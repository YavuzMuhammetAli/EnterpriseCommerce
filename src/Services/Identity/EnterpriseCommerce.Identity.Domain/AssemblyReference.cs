using System.Reflection;

namespace EnterpriseCommerce.Identity.Domain;

/// <summary>
/// Marker used to locate this assembly (e.g. by architecture tests).
/// Domain entities are intentionally not modelled yet; see docs/architecture/SERVICE_CATALOG.md.
/// </summary>
public static class AssemblyReference
{
    public static readonly Assembly Assembly = typeof(AssemblyReference).Assembly;
}
