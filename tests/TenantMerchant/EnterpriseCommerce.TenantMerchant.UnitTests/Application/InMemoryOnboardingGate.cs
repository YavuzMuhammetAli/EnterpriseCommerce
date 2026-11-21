using EnterpriseCommerce.TenantMerchant.Application.Common;
using EnterpriseCommerce.TenantMerchant.Application.Onboarding;
using EnterpriseCommerce.TenantMerchant.Application.Tenants;
using EnterpriseCommerce.TenantMerchant.Domain.Audit;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using EnterpriseCommerce.TenantMerchant.Domain.Tenants;

namespace EnterpriseCommerce.TenantMerchant.UnitTests.Application;

/// <summary>
/// An in-memory stand-in for <see cref="ITenantOnboardingGate"/>, for use-case logic only: it applies the unique slug and
/// the per-subject key at commit, as the database does. The lock, atomicity and concurrency are proven against SQL
/// Server in the integration tests, never here.
/// </summary>
internal sealed class InMemoryOnboardingGate : ITenantOnboardingGate
{
    public List<Tenant> Tenants { get; } = [];

    public List<Membership> Memberships { get; } = [];

    public List<MembershipAuditRecord> AuditRecords { get; } = [];

    /// <summary>Runs before the staged tenant is committed, once, to simulate a concurrent winner.</summary>
    public Action? BeforeCommit { get; set; }

    /// <summary>Makes every commit fail as a unique violation that no read in the session can see.</summary>
    public bool ConflictOnEveryCommit { get; set; }

    public int Attempts { get; private set; }

    public async Task<TResult> ExecuteAsync<TResult>(
        SubjectId subject,
        Func<ITenantOnboardingSession, CancellationToken, Task<WriteDecision<TResult>>> work,
        CancellationToken cancellationToken)
    {
        Attempts++;
        var session = new Session(this, subject.Value);
        var decision = await work(session, cancellationToken);
        if (!decision.ShouldCommit || session.Staged is not { } staged)
        {
            return decision.Result;
        }

        var beforeCommit = BeforeCommit;
        BeforeCommit = null;
        beforeCommit?.Invoke();

        if (ConflictOnEveryCommit || Tenants.Any(t => t.Slug == staged.Tenant.Slug
            || (t.Origin == TenantOrigin.SelfService && t.SelfServiceSubjectId == staged.Tenant.SelfServiceSubjectId && t.ProvisioningKey == staged.Tenant.ProvisioningKey)))
        {
            throw new TenantWriteConflictException(TenantWriteConflict.UniqueConstraint, "unique", new InvalidOperationException("unique"));
        }

        Tenants.Add(staged.Tenant);
        Memberships.Add(staged.Owner);
        AuditRecords.Add(staged.Audit);
        return decision.Result;
    }

    private sealed class Session(InMemoryOnboardingGate gate, string subjectId) : ITenantOnboardingSession
    {
        public (Tenant Tenant, Membership Owner, MembershipAuditRecord Audit)? Staged { get; private set; }

        public Task<SelfServiceTenant?> FindByKeyAsync(string idempotencyKey, CancellationToken cancellationToken)
        {
            var tenant = gate.Tenants.SingleOrDefault(t =>
                t.Origin == TenantOrigin.SelfService && t.SelfServiceSubjectId == subjectId && t.ProvisioningKey == idempotencyKey);
            if (tenant is null)
            {
                return Task.FromResult<SelfServiceTenant?>(null);
            }

            var membership = gate.Memberships.SingleOrDefault(m => m.TenantId == tenant.Id && m.SubjectId == subjectId && m.Status != MembershipStatus.Removed);
            return Task.FromResult<SelfServiceTenant?>(new SelfServiceTenant(
                new TenantView(tenant.Id, tenant.Slug, tenant.Status, tenant.CreatedAt, tenant.MerchantProfile.DisplayName),
                tenant.ProvisioningRequestHash,
                membership is null ? null : MembershipView.Of(membership)));
        }

        public Task<int> CountTenantsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(gate.Tenants.Count(t => t.Origin == TenantOrigin.SelfService && t.SelfServiceSubjectId == subjectId));

        public Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken) =>
            Task.FromResult(gate.Tenants.Any(t => t.Slug == slug));

        public void Add(Tenant tenant, Membership owner, MembershipAuditRecord audit) => Staged = (tenant, owner, audit);
    }
}
