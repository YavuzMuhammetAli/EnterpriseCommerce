namespace EnterpriseCommerce.Identity.Application.Security;

/// <summary>
/// Runs authoritative security changes in one short database transaction (ADR-017 section 8).
/// </summary>
/// <remarks>
/// <para>
/// A SQL Server deadlock (error 1205) causes a <b>complete</b> retry of the work in a fresh unit of work,
/// with a bounded number of attempts and backoff. Individual statements are never retried. The work may
/// therefore run more than once and must have no side effects outside the unit.
/// </para>
/// <para>
/// When retries are exhausted or the database is unavailable, a <see cref="SecurityStateUnavailableException"/>
/// is thrown and nothing is committed.
/// </para>
/// </remarks>
public interface IAuthoritativeSecurityTransaction
{
    Task<TResult> ExecuteAsync<TResult>(
        string operation,
        Func<IAuthoritativeSecurityUnit, CancellationToken, Task<TransactionDecision<TResult>>> work,
        CancellationToken cancellationToken);
}
