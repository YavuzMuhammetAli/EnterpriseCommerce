using System.Reflection;

namespace EnterpriseCommerce.Identity.Application;

/// <summary>
/// Marker used to locate this assembly (e.g. by architecture tests).
/// </summary>
public static class AssemblyReference
{
    public static readonly Assembly Assembly = typeof(AssemblyReference).Assembly;
}
