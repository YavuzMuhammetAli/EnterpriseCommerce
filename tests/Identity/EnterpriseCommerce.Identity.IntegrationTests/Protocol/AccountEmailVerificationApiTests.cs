using System.Net;
using System.Net.Http.Headers;
using System.Text;
using EnterpriseCommerce.Identity.Application.Accounts;
using Microsoft.AspNetCore.Hosting;

namespace EnterpriseCommerce.Identity.IntegrationTests.Protocol;

/// <summary>
/// The internal account email verification API (IDENTITY_API_CONTRACT.md section 2.13, ADR-021): the service-token
/// authorization boundary, the four answers, input validation, and that the account's own address is never returned.
/// </summary>
public sealed class AccountEmailVerificationApiTests(IdentityServerFixture identity) : IClassFixture<IdentityServerFixture>
{
    [Fact]
    public async Task OnlyAnAllowListedServiceClientWithTheVerificationScope_MayVerify()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        var userId = await identity.GetUserIdAsync(email);
        var body = new { email };

        var userToken = (await identity.SignInAsync(identity.Merchant, email)).AccessToken!;
        var otherServiceToken = await identity.ServiceTokenAsync(identity.Service, "svc.inventory.reservations");
        var lifecycleToken = await identity.ServiceTokenAsync(identity.AccountAdmin);
        var unlistedToken = await identity.ServiceTokenAsync(identity.UnlistedVerifier, IdentityServerFixture.AccountVerificationScope);
        var verifierToken = await identity.ServiceTokenAsync(identity.EmailVerifier, IdentityServerFixture.AccountVerificationScope);

        Assert.Equal(HttpStatusCode.Unauthorized, (await identity.CallEmailVerificationAsync(userId, bearerToken: null, body)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await identity.CallEmailVerificationAsync(userId, "not-a-token", body)).Status);

        // A token for another service's audience is not a token for Identity at all.
        Assert.Equal(HttpStatusCode.Unauthorized, (await identity.CallEmailVerificationAsync(userId, otherServiceToken, body)).Status);

        // A user token of the merchant portal: valid for Identity, never a verification caller.
        Assert.Equal(HttpStatusCode.Forbidden, (await identity.CallEmailVerificationAsync(userId, userToken, body)).Status);

        // The account-lifecycle scope does not grant verification, and an unlisted client with the right scope is refused.
        Assert.Equal(HttpStatusCode.Forbidden, (await identity.CallEmailVerificationAsync(userId, lifecycleToken, body)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await identity.CallEmailVerificationAsync(userId, unlistedToken, body)).Status);

        // The verification token cannot change account status either.
        Assert.Equal(HttpStatusCode.Forbidden, (await identity.CallAccountLifecycleAsync("disable", userId, verifierToken)).Status);

        var (status, answer) = await identity.CallEmailVerificationAsync(userId, verifierToken, body);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(userId, answer.GetProperty("accountId").GetString());
        Assert.Equal("Match", answer.GetProperty("result").GetString());
    }

    [Fact]
    public async Task TheAnswer_ReflectsStatusConfirmationAndAddress_WithoutReturningTheAddress()
    {
        identity.RequireAvailable();
        var token = await identity.ServiceTokenAsync(identity.EmailVerifier, IdentityServerFixture.AccountVerificationScope);
        var email = await identity.CreateUserAsync();
        var userId = await identity.GetUserIdAsync(email);

        // Case and surrounding whitespace do not matter: both sides use Identity's normalization.
        var (_, match) = await identity.CallEmailVerificationAsync(userId, token, new { email = "  " + email.ToUpperInvariant() + " " });
        Assert.Equal("Match", match.GetProperty("result").GetString());
        Assert.DoesNotContain(email, match.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var (_, mismatch) = await identity.CallEmailVerificationAsync(userId, token, new { email = "someone-else@example.test" });
        Assert.Equal("Mismatch", mismatch.GetProperty("result").GetString());
        Assert.DoesNotContain(email, mismatch.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var unconfirmedEmail = await identity.CreateUserAsync(emailConfirmed: false);
        var (_, unverified) = await identity.CallEmailVerificationAsync(await identity.GetUserIdAsync(unconfirmedEmail), token, new { email = unconfirmedEmail });
        Assert.Equal("Unverified", unverified.GetProperty("result").GetString());

        await identity.DisableAsync(userId);
        var (_, inactive) = await identity.CallEmailVerificationAsync(userId, token, new { email });
        Assert.Equal("Inactive", inactive.GetProperty("result").GetString());

        var (notFound, _) = await identity.CallEmailVerificationAsync(Guid.NewGuid().ToString(), token, new { email });
        Assert.Equal(HttpStatusCode.NotFound, notFound);
    }

    [Fact]
    public async Task InvalidRequests_AreRejectedAsProblems()
    {
        identity.RequireAvailable();
        var token = await identity.ServiceTokenAsync(identity.EmailVerifier, IdentityServerFixture.AccountVerificationScope);
        var userId = await identity.GetUserIdAsync(await identity.CreateUserAsync());

        foreach (var body in new object[]
        {
            new { },
            new { email = "" },
            new { email = "not-an-address" },
            new { email = "Alice <alice@example.test>" },
            new { email = "alice@localhost" },
            new { email = new string('a', AccountEmail.MaxLength) + "@example.test" },
        })
        {
            var (status, problem) = await identity.CallEmailVerificationAsync(userId, token, body);
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.True(problem.TryGetProperty("errors", out _));
        }

        // Unknown members are rejected, so that no caller can believe an extra field (a subject, a tenant) is honored.
        var unknownMember = new StringContent("""{"email":"a@example.test","subject":"x"}""", Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, (await identity.CallEmailVerificationAsync(userId, token, unknownMember)).Status);
    }
}

/// <summary>A database failure answers 503 and never an answer that could be read as a verdict.</summary>
public sealed class AccountEmailVerificationAvailabilityTests(ControlledIdentityServerFixture identity) : IClassFixture<ControlledIdentityServerFixture>
{
    [Fact]
    public async Task AnUnavailableDatabase_Answers503()
    {
        identity.RequireAvailable();
        var token = await identity.ServiceTokenAsync(identity.EmailVerifier, IdentityServerFixture.AccountVerificationScope);
        var email = await identity.CreateUserAsync();
        var userId = await identity.GetUserIdAsync(email);

        using (identity.Transactions.FailOperation(VerifyAccountEmailHandler.Operation))
        {
            var (status, body) = await identity.CallEmailVerificationAsync(userId, token, new { email });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
            Assert.False(body.TryGetProperty("result", out _));
        }

        Assert.Equal(HttpStatusCode.OK, (await identity.CallEmailVerificationAsync(userId, token, new { email })).Status);
    }
}

/// <summary>The verification request carries an email address; it never reaches the logs.</summary>
public sealed class AccountEmailVerificationLogTests(LogCapturingIdentityServerFixture identity) : IClassFixture<LogCapturingIdentityServerFixture>
{
    [Fact]
    public async Task Verification_NeverLogsTheEmailAddress()
    {
        identity.RequireAvailable();
        var token = await identity.ServiceTokenAsync(identity.EmailVerifier, IdentityServerFixture.AccountVerificationScope);
        var email = await identity.CreateUserAsync();
        var userId = await identity.GetUserIdAsync(email);
        const string probe = "never-log-this-probe@example.test";

        Assert.Equal(HttpStatusCode.OK, (await identity.CallEmailVerificationAsync(userId, token, new { email })).Status);
        Assert.Equal(HttpStatusCode.OK, (await identity.CallEmailVerificationAsync(userId, token, new { email = probe })).Status);

        Assert.Contains(identity.Sink.Events, e => e.Contains("Account email verification", StringComparison.Ordinal));
        Assert.DoesNotContain(identity.Sink.Events, e => e.Contains(probe, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(identity.Sink.Events, e => e.Contains(email, StringComparison.OrdinalIgnoreCase)
            && e.Contains("verification", StringComparison.OrdinalIgnoreCase));
    }
}
