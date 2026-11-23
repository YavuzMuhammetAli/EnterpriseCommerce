namespace EnterpriseCommerce.TenantMerchant.Application.Common;

/// <summary>
/// The correlation and causation identifiers of the messages a write produces (ADR-023 section 5). They are not domain
/// data, so application code passes them explicitly to the write store or session. Saving an aggregate whose events
/// are published without them fails closed.
/// </summary>
/// <param name="CorrelationId">The flow's correlation ID (<c>X-Correlation-ID</c> for HTTP-initiated work).</param>
/// <param name="CausationId">The <c>eventId</c> of the causing message, or for a root message the correlation ID.</param>
public sealed record MessageMetadata(string CorrelationId, string CausationId)
{
    /// <summary>
    /// The metadata of a root message caused directly by an HTTP request: its causation ID is the request's correlation
    /// ID. <c>null</c> when the request has none, which makes a save with published events fail.
    /// </summary>
    public static MessageMetadata? ForRequest(string? correlationId) =>
        string.IsNullOrEmpty(correlationId) ? null : new MessageMetadata(correlationId, correlationId);
}
