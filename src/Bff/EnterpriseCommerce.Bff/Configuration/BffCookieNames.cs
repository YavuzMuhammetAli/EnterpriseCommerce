namespace EnterpriseCommerce.Bff.Configuration;

/// <summary>
/// Cookie names of one deployment (ADR-016 section 5). Every name is distinct per front end, so the cookies of one
/// front end are never read as another's, even when two front ends share a registrable domain.
/// </summary>
internal sealed record BffCookieNames(string Session, string Correlation, string Nonce, string Antiforgery, string RequestToken)
{
    public static BffCookieNames For(BffSettings settings)
    {
        var stem = string.IsNullOrWhiteSpace(settings.Cookies.NameStem) ? $"ec-{settings.Deployment}" : settings.Cookies.NameStem;
        return new BffCookieNames(
            Session: (settings.Cookies.UseHttpOnlyPrefix ? "__Host-Http-" : "__Host-") + stem,
            Correlation: $"__Host-{stem}-oidc-c.",
            Nonce: $"__Host-{stem}-oidc-n.",
            Antiforgery: $"__Host-{stem}-af",
            RequestToken: $"__Host-{stem}-xsrf");
    }
}
