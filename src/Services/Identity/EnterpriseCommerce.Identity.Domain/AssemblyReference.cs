using System.Reflection;

namespace EnterpriseCommerce.Identity.Domain;

/// <summary>
/// Marker used to locate this assembly (e.g. by architecture tests).
/// The Identity domain stays minimal: account status, account security tokens and the security-version rule.
/// Framework types (ASP.NET Core Identity, OpenIddict) never appear here (ADR-015 A6).
/// </summary>
public static class AssemblyReference
{
    public static readonly Assembly Assembly = typeof(AssemblyReference).Assembly;
}
