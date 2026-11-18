namespace EnterpriseCommerce.Identity.Domain.Security;

/// <summary>What to do when a refresh token is presented again after it was redeemed (ADR-017 section 5).</summary>
public enum RefreshReuseAction
{
    /// <summary>
    /// The token was presented by a client other than the one it was issued to. The request is rejected and
    /// <b>nothing is revoked</b>, so one client can never end another client's session (PoC-13e, threat T24).
    /// </summary>
    RejectPresenterMismatch,

    /// <summary>Revoke the affected session (one authorization) only.</summary>
    RevokeSession,

    /// <summary>Raise the user's security version, which invalidates every session of the user (admin console).</summary>
    RevokeAllSessions,
}

/// <summary>The refresh-token reuse policy of ADR-017 section 5.</summary>
public static class RefreshReusePolicy
{
    /// <param name="tokenClientId">The client the refresh token was issued to.</param>
    /// <param name="presentingClientId">The client that authenticated at the token endpoint.</param>
    /// <param name="adminConsoleClientId">The admin console's client ID.</param>
    public static RefreshReuseAction Decide(string? tokenClientId, string? presentingClientId, string adminConsoleClientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adminConsoleClientId);

        // The presenter check comes first. An unknown token client is treated as a mismatch: rejecting without
        // revocation is the safe choice when the victim cannot be identified.
        if (string.IsNullOrEmpty(tokenClientId)
            || string.IsNullOrEmpty(presentingClientId)
            || !string.Equals(tokenClientId, presentingClientId, StringComparison.Ordinal))
        {
            return RefreshReuseAction.RejectPresenterMismatch;
        }

        return string.Equals(tokenClientId, adminConsoleClientId, StringComparison.Ordinal)
            ? RefreshReuseAction.RevokeAllSessions
            : RefreshReuseAction.RevokeSession;
    }
}
