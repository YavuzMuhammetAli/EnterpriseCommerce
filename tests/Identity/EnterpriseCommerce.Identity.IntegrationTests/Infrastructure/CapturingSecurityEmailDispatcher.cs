using System.Collections.Concurrent;
using EnterpriseCommerce.Identity.Application.SecurityEmails;
using EnterpriseCommerce.Identity.Domain.SecurityEmails;

namespace EnterpriseCommerce.Identity.IntegrationTests.Infrastructure;

/// <summary>
/// Test replacement for the Notification integration: records every handover and lets a test choose the outcome
/// per recipient and attempt.
/// </summary>
public sealed class CapturingSecurityEmailDispatcher : ISecurityEmailDispatcher
{
    private readonly ConcurrentQueue<(SecurityEmailMessage Message, int Attempt)> _handovers = new();
    private readonly ConcurrentDictionary<Guid, int> _attempts = new();
    private readonly ConcurrentDictionary<string, Func<int, SecurityEmailDispatchOutcome>> _plans = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<(SecurityEmailMessage Message, int Attempt)> Handovers => _handovers;

    /// <summary>Sets the outcome for every attempt to <paramref name="recipient"/>. Throwing simulates a misbehaving adapter.</summary>
    public void Plan(string recipient, Func<int, SecurityEmailDispatchOutcome> outcomeForAttempt) => _plans[recipient] = outcomeForAttempt;

    public IReadOnlyList<SecurityEmailMessage> MessagesTo(string recipient) =>
        [.. _handovers.Where(h => string.Equals(h.Message.RecipientEmail, recipient, StringComparison.OrdinalIgnoreCase)).Select(h => h.Message)];

    public Task<SecurityEmailDispatchOutcome> DispatchAsync(SecurityEmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var attempt = _attempts.AddOrUpdate(message.DeliveryId, 1, (_, previous) => previous + 1);
        _handovers.Enqueue((message, attempt));
        var outcome = _plans.TryGetValue(message.RecipientEmail, out var plan) ? plan(attempt) : SecurityEmailDispatchOutcome.Delivered;
        return Task.FromResult(outcome);
    }

    /// <summary>Waits until <paramref name="count"/> handovers to the recipient exist.</summary>
    public async Task<IReadOnlyList<SecurityEmailMessage>> WaitForAsync(string recipient, int count = 1)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var messages = MessagesTo(recipient);
            if (messages.Count >= count)
            {
                return messages;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Expected {count} security email(s) to {recipient}.");
    }

    /// <summary>The raw token of a captured link. Tests use it like a user who opened the email.</summary>
    public static string TokenOf(SecurityEmailMessage message) =>
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(message.Link.Query)["token"].ToString();
}
