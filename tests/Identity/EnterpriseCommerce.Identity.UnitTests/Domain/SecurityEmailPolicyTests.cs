using EnterpriseCommerce.Identity.Domain.SecurityEmails;

namespace EnterpriseCommerce.Identity.UnitTests.Domain;

public sealed class SecurityEmailPolicyTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RateLimit_FirstRequest_IsAllowed() =>
        Assert.True(SecurityEmailRateLimit.Allows([], Now));

    [Theory]
    [InlineData(59, false)]
    [InlineData(60, true)]
    public void RateLimit_RequiresSixtySecondsBetweenRequests(int secondsAgo, bool allowed) =>
        Assert.Equal(allowed, SecurityEmailRateLimit.Allows([Now.AddSeconds(-secondsAgo)], Now));

    [Fact]
    public void RateLimit_AllowsAtMostThreePerHour()
    {
        DateTime[] two = [Now.AddMinutes(-50), Now.AddMinutes(-30)];
        DateTime[] three = [.. two, Now.AddMinutes(-10)];
        DateTime[] threeButOneOlderThanAnHour = [Now.AddMinutes(-61), Now.AddMinutes(-30), Now.AddMinutes(-10)];

        Assert.True(SecurityEmailRateLimit.Allows(two, Now));
        Assert.False(SecurityEmailRateLimit.Allows(three, Now));
        Assert.True(SecurityEmailRateLimit.Allows(threeButOneOlderThanAnHour, Now));
    }

    [Fact]
    public void RateLimit_AllowsAtMostTenPerDay()
    {
        var nine = Enumerable.Range(1, 9).Select(i => Now.AddHours(-2 * i)).ToList();
        var ten = nine.Append(Now.AddHours(-1.5)).ToList();

        Assert.True(SecurityEmailRateLimit.Allows(nine, Now));
        Assert.False(SecurityEmailRateLimit.Allows(ten, Now));
        Assert.True(SecurityEmailRateLimit.Allows(ten.Select(at => at.AddDays(-1)).ToList(), Now));
    }

    [Theory]
    [InlineData(SecurityEmailDispatchOutcome.Delivered, 1, false, SecurityEmailDeliveryStatus.Sent, null)]
    [InlineData(SecurityEmailDispatchOutcome.PermanentFailure, 1, false, SecurityEmailDeliveryStatus.Failed, "rejected")]
    [InlineData(SecurityEmailDispatchOutcome.TransientFailure, 1, true, SecurityEmailDeliveryStatus.Dispatching, null)]
    [InlineData(SecurityEmailDispatchOutcome.TransientFailure, 3, false, SecurityEmailDeliveryStatus.Failed, "retries-exhausted")]
    [InlineData(SecurityEmailDispatchOutcome.Unknown, 2, true, SecurityEmailDeliveryStatus.Dispatching, null)]
    [InlineData(SecurityEmailDispatchOutcome.Unknown, 3, false, SecurityEmailDeliveryStatus.Indeterminate, "retries-exhausted-unknown")]
    public void RetryPolicy_IsBounded_AndNeverReportsAnUnknownOutcomeAsFailed(
        SecurityEmailDispatchOutcome outcome,
        int attempt,
        bool retry,
        SecurityEmailDeliveryStatus status,
        string? category)
    {
        var decision = SecurityEmailRetryPolicy.Decide(outcome, attempt, maxAttempts: 3);

        Assert.Equal(retry, decision.Retry);
        if (!retry)
        {
            Assert.Equal(status, decision.FinalStatus);
            Assert.Equal(category, decision.ErrorCategory);
        }
    }

    [Theory]
    [InlineData(SecurityEmailDeliveryStatus.Queued, SecurityEmailDeliveryStatus.Dispatching, true)]
    [InlineData(SecurityEmailDeliveryStatus.Queued, SecurityEmailDeliveryStatus.Dropped, true)]
    [InlineData(SecurityEmailDeliveryStatus.Dispatching, SecurityEmailDeliveryStatus.Sent, true)]
    [InlineData(SecurityEmailDeliveryStatus.Dispatching, SecurityEmailDeliveryStatus.Indeterminate, true)]
    [InlineData(SecurityEmailDeliveryStatus.Dispatching, SecurityEmailDeliveryStatus.Dropped, false)] // a claimed delivery is never dropped
    [InlineData(SecurityEmailDeliveryStatus.Queued, SecurityEmailDeliveryStatus.Sent, false)]
    [InlineData(SecurityEmailDeliveryStatus.Dropped, SecurityEmailDeliveryStatus.Dispatching, false)]
    [InlineData(SecurityEmailDeliveryStatus.Sent, SecurityEmailDeliveryStatus.Failed, false)]
    public void Transitions_FollowTheDeliveryStateMachine(SecurityEmailDeliveryStatus from, SecurityEmailDeliveryStatus to, bool allowed) =>
        Assert.Equal(allowed, SecurityEmailDeliveryTransitions.IsAllowed(from, to));
}
