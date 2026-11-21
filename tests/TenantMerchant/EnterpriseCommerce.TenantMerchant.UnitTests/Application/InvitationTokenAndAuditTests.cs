using System.Security.Cryptography;
using System.Text;
using EnterpriseCommerce.TenantMerchant.Application.Invitations;
using EnterpriseCommerce.TenantMerchant.Domain.Audit;
using EnterpriseCommerce.TenantMerchant.Domain.Invitations;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;

namespace EnterpriseCommerce.TenantMerchant.UnitTests.Application;

/// <summary>The invitation token format and hash, the audit record's contents, and the invitation settings (ADR-021).</summary>
public sealed class InvitationTokenAndAuditTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Generate_MakesPrefixedUrlSafe256BitTokens_ThatNeverRepeat()
    {
        var tokens = Enumerable.Range(0, 1000).Select(_ => InvitationToken.Generate()).ToList();

        Assert.All(tokens, t =>
        {
            Assert.StartsWith(InvitationToken.Prefix, t.Token, StringComparison.Ordinal);
            Assert.Equal(InvitationToken.Length, t.Token.Length);
            Assert.Equal(32, Convert.FromBase64String(Base64(t.Token[InvitationToken.Prefix.Length..])).Length);
            Assert.All(t.Token[InvitationToken.Prefix.Length..], c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_', c.ToString()));
        });
        Assert.Equal(tokens.Count, tokens.Select(t => t.Token).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TheHash_IsTheSha256OfTheToken_AndTryHashAgrees()
    {
        var (token, hash) = InvitationToken.Generate();

        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(token)), hash.ToArray());
        Assert.True(InvitationToken.TryHash(token, out var again));
        Assert.Equal(hash, again);
        Assert.DoesNotContain(token, hash.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TryHash_RefusesNull() => Assert.False(InvitationToken.TryHash(null, out _));

    public static TheoryData<string> NotTokens()
    {
        var body = new string('A', InvitationToken.Length - InvitationToken.Prefix.Length);
        return new TheoryData<string>
        {
            string.Empty,
            InvitationToken.Prefix,
            InvitationToken.Prefix + "AAAA",
            "xxxx_" + body,
            "ECMI_" + body,
            InvitationToken.Prefix + body[..^1] + "+",
            InvitationToken.Prefix + body[..^1] + "=",
            InvitationToken.Prefix + body[..^1] + " ",
            InvitationToken.Prefix + body + "A",
            InvitationToken.Prefix + body[..^1],
        };
    }

    [Theory]
    [MemberData(nameof(NotTokens))]
    public void TryHash_RefusesEverythingThatIsNotATokenOfTheFormat(string candidate) =>
        Assert.False(InvitationToken.TryHash(candidate, out _));

    [Fact]
    public void AuditRecords_NeverCarryTheAddressOrToken()
    {
        var properties = typeof(MembershipAuditRecord).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain(properties, name => name.Contains("Email", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, name => name.Contains("Token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, name => name.Contains("Hash", StringComparison.OrdinalIgnoreCase));

        Assert.True(InviteeEmail.TryCreate("private.person@example.test", out var email));
        Assert.True(SubjectId.TryCreate("owner-1", out var owner));
        var invitation = MerchantInvitation.Create(Guid.NewGuid(), email, MerchantRole.MerchantAdmin, InvitationToken.Generate().Hash, owner, Now, TimeSpan.FromDays(7));
        var record = MembershipAuditRecord.InvitationCreated(invitation, AuditActor.User(owner), "corr-1", Now);

        var values = typeof(MembershipAuditRecord).GetProperties().Select(p => p.GetValue(record)?.ToString() ?? string.Empty);
        Assert.DoesNotContain(values, value => value.Contains("private.person", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(MembershipAuditAction.InvitationCreated, record.Action);
        Assert.Equal(invitation.Id, record.InvitationId);
        Assert.Equal("MerchantAdmin", record.NewRole);
        Assert.Equal("corr-1", record.CorrelationId);
        Assert.Equal(AuditActorKind.User, record.ActorKind);
        Assert.Equal("owner-1", record.ActorId);
    }

    [Fact]
    public void AuditRecords_DescribeStatusAndRoleChanges()
    {
        Assert.True(SubjectId.TryCreate("staff-1", out var staffSubject));
        Assert.True(SubjectId.TryCreate("owner-1", out var ownerSubject));
        var staff = Membership.CreateMember(Guid.NewGuid(), staffSubject, MerchantRole.MerchantStaff, Now);
        staff.Suspend(activeOwnerCount: 1, Now);

        var suspended = MembershipAuditRecord.MembershipStatusChanged(staff, MembershipStatus.Active, AuditActor.User(ownerSubject), correlationId: null, Now);
        Assert.Equal(MembershipAuditAction.MembershipSuspended, suspended.Action);
        Assert.Equal(("Active", "Suspended"), (suspended.PreviousStatus, suspended.NewStatus));
        Assert.Equal(staff.Id, suspended.MembershipId);
        Assert.Equal("staff-1", suspended.SubjectId);

        // A correlation ID longer than the header allows is not recorded rather than truncated.
        var tooLong = MembershipAuditRecord.MembershipStatusChanged(staff, MembershipStatus.Active, AuditActor.System, new string('c', 65), Now);
        Assert.Null(tooLong.CorrelationId);
        Assert.Equal(AuditActorKind.System, tooLong.ActorKind);

        Assert.True(InviteeEmail.TryCreate("someone@example.test", out var email));
        var invitation = MerchantInvitation.Create(staff.TenantId, email, MerchantRole.MerchantStaff, InvitationToken.Generate().Hash, ownerSubject, Now, TimeSpan.FromDays(1));
        Assert.Throws<ArgumentException>(() => MembershipAuditRecord.InvitationClosed(
            MembershipAuditAction.MembershipRemoved, invitation, InvitationStatus.Pending, AuditActor.System, null, Now));
    }

    [Fact]
    public void InvitationSettings_AreBounded()
    {
        Assert.Empty(new InvitationSettings().Validate());
        Assert.NotEmpty(new InvitationSettings { Lifetime = TimeSpan.FromMinutes(30) }.Validate());
        Assert.NotEmpty(new InvitationSettings { Lifetime = TimeSpan.FromDays(31) }.Validate());
        Assert.NotEmpty(new InvitationSettings { MaxOpenPerTenant = 0 }.Validate());
        Assert.NotEmpty(new InvitationSettings { MaxOpenPerTenant = 1001 }.Validate());
    }

    private static string Base64(string base64Url)
    {
        var value = base64Url.Replace('-', '+').Replace('_', '/');
        return value + new string('=', (4 - (value.Length % 4)) % 4);
    }
}
