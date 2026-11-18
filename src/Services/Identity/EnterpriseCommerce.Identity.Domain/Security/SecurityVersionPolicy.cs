namespace EnterpriseCommerce.Identity.Domain.Security;

/// <summary>Outcome of comparing a session's recorded security version with the user's current one.</summary>
public enum SecurityVersionVerdict
{
    /// <summary>The session was established at the user's current security version.</summary>
    Current,

    /// <summary>The user's security version was raised after the session was established. The session is revoked.</summary>
    Stale,

    /// <summary>The user has no security-version record. Fails closed.</summary>
    MissingRecord,

    /// <summary>The session carries no usable security version. Fails closed.</summary>
    MissingSessionVersion,
}

/// <summary>
/// The per-user security version is the authoritative revocation state (ADR-017 sections 6 and 7).
/// It is a counter that is distinct from the ASP.NET Core Identity security stamp.
/// </summary>
public static class SecurityVersionPolicy
{
    /// <summary>The version a new account starts with.</summary>
    public const long InitialVersion = 1;

    /// <summary>
    /// Evaluates a session against the user's current security version.
    /// Every outcome other than <see cref="SecurityVersionVerdict.Current"/> must deny the request.
    /// </summary>
    public static SecurityVersionVerdict Evaluate(long? currentVersion, long? sessionVersion)
    {
        if (currentVersion is null)
        {
            return SecurityVersionVerdict.MissingRecord;
        }

        if (sessionVersion is null or < InitialVersion)
        {
            return SecurityVersionVerdict.MissingSessionVersion;
        }

        // A session version above the current one can only come from tampering or data loss: fail closed.
        return sessionVersion == currentVersion ? SecurityVersionVerdict.Current : SecurityVersionVerdict.Stale;
    }
}
