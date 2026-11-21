using EnterpriseCommerce.TenantMerchant.Domain.Invitations;

namespace EnterpriseCommerce.TenantMerchant.Application.Invitations;

/// <summary>Invitation limits (<c>TenantMerchant:Invitations</c> configuration section).</summary>
public sealed class InvitationSettings
{
    public const string SectionName = "TenantMerchant:Invitations";

    /// <summary>How long an invitation can be accepted. 1 hour to 30 days; 7 days by default.</summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>The most open (pending, unexpired) invitations one tenant may have at a time. 1 to 1000; 50 by default.</summary>
    public int MaxOpenPerTenant { get; set; } = 50;

    public IEnumerable<string> Validate()
    {
        if (Lifetime < MerchantInvitation.MinimumLifetime || Lifetime > MerchantInvitation.MaximumLifetime)
        {
            yield return $"{SectionName}:Lifetime must be between {MerchantInvitation.MinimumLifetime} and {MerchantInvitation.MaximumLifetime}.";
        }

        if (MaxOpenPerTenant is < 1 or > 1000)
        {
            yield return $"{SectionName}:MaxOpenPerTenant must be between 1 and 1000.";
        }
    }
}
