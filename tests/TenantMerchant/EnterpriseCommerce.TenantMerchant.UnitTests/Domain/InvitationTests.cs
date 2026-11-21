using EnterpriseCommerce.TenantMerchant.Application.Invitations;
using EnterpriseCommerce.TenantMerchant.Domain.Invitations;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;

namespace EnterpriseCommerce.TenantMerchant.UnitTests.Domain;

/// <summary>The invitation lifecycle (ADR-021): pending is the only open state, and expiry follows the authoritative clock.</summary>
public sealed class InvitationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    [Fact]
    public void Create_IsPending_ForTheTenant_WithTheNormalizedAddress()
    {
        var invitation = NewInvitation("Alice@Example.Test");

        Assert.Equal(InvitationStatus.Pending, invitation.Status);
        Assert.Equal("Alice@Example.Test", invitation.Email);
        Assert.Equal("ALICE@EXAMPLE.TEST", invitation.NormalizedEmail);
        Assert.Equal(Now + Lifetime, invitation.ExpiresAt);
        Assert.Equal(InvitationTokenHash.Length, invitation.TokenHash.Length);
        Assert.True(invitation.IsOpen(Now));
    }

    [Fact]
    public void Create_RefusesTheOwnerRole_AnEmptyTenant_AndLifetimesOutOfRange()
    {
        Assert.True(InviteeEmail.TryCreate("a@example.test", out var email));
        Assert.True(SubjectId.TryCreate("inviter", out var inviter));
        var hash = InvitationToken.Generate().Hash;

        Assert.Throws<ArgumentException>(() => MerchantInvitation.Create(Guid.NewGuid(), email, MerchantRole.MerchantOwner, hash, inviter, Now, Lifetime));
        Assert.Throws<ArgumentException>(() => MerchantInvitation.Create(Guid.NewGuid(), email, (MerchantRole)42, hash, inviter, Now, Lifetime));
        Assert.Throws<ArgumentException>(() => MerchantInvitation.Create(Guid.Empty, email, MerchantRole.MerchantStaff, hash, inviter, Now, Lifetime));
        Assert.Throws<ArgumentException>(() => MerchantInvitation.Create(Guid.NewGuid(), email, MerchantRole.MerchantStaff, hash, inviter, Now, TimeSpan.FromMinutes(59)));
        Assert.Throws<ArgumentException>(() => MerchantInvitation.Create(Guid.NewGuid(), email, MerchantRole.MerchantStaff, hash, inviter, Now, TimeSpan.FromDays(31)));
    }

    [Fact]
    public void Accept_RecordsTheSubjectAndMembership_Once()
    {
        var invitation = NewInvitation();
        var membershipId = Guid.NewGuid();

        Assert.Equal(InvitationChangeOutcome.Changed, invitation.Accept(Subject("bob"), membershipId, Now.AddHours(1)));
        Assert.Equal(InvitationStatus.Accepted, invitation.Status);
        Assert.Equal("bob", invitation.AcceptedBySubjectId);
        Assert.Equal(membershipId, invitation.AcceptedMembershipId);
        Assert.Equal(Now.AddHours(1), invitation.AcceptedAt);

        // Accepted is terminal: a second acceptance, by anyone, is not possible, and nothing changes.
        Assert.Equal(InvitationChangeOutcome.InvalidTransition, invitation.Accept(Subject("bob"), Guid.NewGuid(), Now.AddHours(2)));
        Assert.Equal(InvitationChangeOutcome.InvalidTransition, invitation.Accept(Subject("eve"), Guid.NewGuid(), Now.AddHours(2)));
        Assert.Equal(InvitationChangeOutcome.InvalidTransition, invitation.Cancel(Subject("owner"), Now.AddHours(2)));
        Assert.Equal(InvitationChangeOutcome.InvalidTransition, invitation.MarkExpired(Now + Lifetime));
        Assert.Equal(membershipId, invitation.AcceptedMembershipId);
    }

    [Fact]
    public void ExpiryBoundary_TheExpiryInstantItselfIsExpired()
    {
        var invitation = NewInvitation();
        var expiresAt = Now + Lifetime;

        Assert.True(invitation.IsOpen(expiresAt.AddTicks(-1)));
        Assert.False(invitation.IsOpen(expiresAt));
        Assert.Equal(InvitationStatus.Pending, invitation.EffectiveStatus(expiresAt.AddTicks(-1)));
        Assert.Equal(InvitationStatus.Expired, invitation.EffectiveStatus(expiresAt));

        // A lapsed invitation is not accepted or cancelled: the caller records its expiry instead.
        Assert.Equal(InvitationChangeOutcome.Expired, invitation.Accept(Subject("bob"), Guid.NewGuid(), expiresAt));
        Assert.Equal(InvitationChangeOutcome.Expired, invitation.Cancel(Subject("owner"), expiresAt));
        Assert.Equal(InvitationStatus.Pending, invitation.Status);
    }

    [Fact]
    public void MarkExpired_OnlyAfterTheExpiry_SoNoClockCutsAnInvitationShort()
    {
        var invitation = NewInvitation();

        Assert.Equal(InvitationChangeOutcome.InvalidTransition, invitation.MarkExpired(Now + Lifetime - TimeSpan.FromSeconds(1)));
        Assert.Equal(InvitationStatus.Pending, invitation.Status);

        Assert.Equal(InvitationChangeOutcome.Changed, invitation.MarkExpired(Now + Lifetime));
        Assert.Equal(InvitationStatus.Expired, invitation.Status);
        Assert.Equal(Now + Lifetime, invitation.ExpiredAt);
        Assert.Equal(InvitationChangeOutcome.Unchanged, invitation.MarkExpired(Now + Lifetime + Lifetime));
        Assert.Equal(InvitationChangeOutcome.InvalidTransition, invitation.Accept(Subject("bob"), Guid.NewGuid(), Now));
    }

    [Fact]
    public void Cancel_IsTerminal_AndRepeatingItChangesNothing()
    {
        var invitation = NewInvitation();

        Assert.Equal(InvitationChangeOutcome.Changed, invitation.Cancel(Subject("owner"), Now.AddMinutes(5)));
        Assert.Equal(InvitationChangeOutcome.Unchanged, invitation.Cancel(Subject("someone-else"), Now.AddMinutes(6)));
        Assert.Equal("owner", invitation.CancelledBySubjectId);
        Assert.Equal(InvitationChangeOutcome.InvalidTransition, invitation.Accept(Subject("bob"), Guid.NewGuid(), Now.AddMinutes(7)));
        Assert.Equal(InvitationStatus.Cancelled, invitation.EffectiveStatus(Now + Lifetime + Lifetime));
    }

    [Fact]
    public void TheTokenHash_IsACopy_SoNoCallerCanChangeTheStoredValue()
    {
        var invitation = NewInvitation();
        var copy = invitation.TokenHash;
        copy[0] ^= 0xFF;

        Assert.NotEqual(copy, invitation.TokenHash);
    }

    [Theory]
    [InlineData("alice@example.test", "ALICE@EXAMPLE.TEST")]
    [InlineData("  Alice@Example.Test  ", "ALICE@EXAMPLE.TEST")]
    [InlineData("first.last+tag@sub.example.test", "FIRST.LAST+TAG@SUB.EXAMPLE.TEST")]
    public void InviteeEmail_NormalizesLikeIdentity(string input, string normalized)
    {
        Assert.True(InviteeEmail.TryCreate(input, out var email));
        Assert.Equal(normalized, email.Normalized);
        Assert.Equal(input.Trim(), email.Value);
        Assert.DoesNotContain("@", email.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-at-sign")]
    [InlineData("alice@localhost")]
    [InlineData("Alice <alice@example.test>")]
    [InlineData("alice@example.test\u0000")]
    [InlineData("a b@example.test")]
    public void InviteeEmail_RejectsWhatIdentityWouldReject(string? input) =>
        Assert.False(InviteeEmail.TryCreate(input, out _));

    [Fact]
    public void InviteeEmail_RejectsAddressesLongerThanIdentityAllows() =>
        Assert.False(InviteeEmail.TryCreate(new string('a', InviteeEmail.MaxLength - "@example.test".Length + 1) + "@example.test", out _));

    private static MerchantInvitation NewInvitation(string address = "alice@example.test")
    {
        Assert.True(InviteeEmail.TryCreate(address, out var email));
        return MerchantInvitation.Create(Guid.NewGuid(), email, MerchantRole.MerchantStaff, InvitationToken.Generate().Hash, Subject("owner"), Now, Lifetime);
    }

    private static SubjectId Subject(string value)
    {
        Assert.True(SubjectId.TryCreate(value, out var subject));
        return subject;
    }
}
