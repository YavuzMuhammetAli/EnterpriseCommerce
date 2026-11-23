#:property PublishAot=false
// Local development only: creates the private_key_jwt keys of the three BFF deployments and the matching Identity
// client registrations (ADR-016 section 2), and the key of Tenant & Merchant's Identity client svc-tenant-merchant
// (ADR-021: invitation acceptance verifies the invitee's email at Identity). Run from the repository root:
//
//     dotnet run deploy/dev/bff-dev-keys.cs
//
// Keys are written OUTSIDE the repository, to %USERPROFILE%/.enterprisecommerce/bff-dev (or $HOME on Linux/macOS),
// and are never committed. Existing keys are kept, so the command can be repeated safely. The generated scripts set
// the Identity__Clients__* environment variables for `dotnet run` of Identity:
//
//     . $env:USERPROFILE/.enterprisecommerce/bff-dev/identity-clients.ps1     (PowerShell)
//     source ~/.enterprisecommerce/bff-dev/identity-clients.sh                 (bash)
//
// and tenant-merchant-identity.ps1 / .sh set the TenantMerchant__IdentityVerification__* variables (and the
// membership verification allow-list) for `dotnet run` of Tenant & Merchant.
//
// It also creates the key of Catalog's service client svc-catalog (ADR-025: Catalog verifies merchant memberships at
// Tenant & Merchant); catalog-tenant-merchant.ps1 / .sh set the Catalog__MembershipVerification__* variables for
// `dotnet run` of Catalog.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var directory = Path.Combine(home, ".enterprisecommerce", "bff-dev");
Directory.CreateDirectory(directory);

// Lifetimes per front end (ADR-017 section 6): access token, sliding refresh token, absolute session.
var deployments = new[]
{
    (Name: "storefront", Access: "00:10:00", Refresh: "7.00:00:00", Absolute: "30.00:00:00"),
    (Name: "merchant", Access: "00:10:00", Refresh: "02:00:00", Absolute: "12:00:00"),
    (Name: "admin", Access: "00:05:00", Refresh: "00:30:00", Absolute: "08:00:00"),
};

var variables = new List<(string Name, string Value)>();
for (var index = 0; index < deployments.Length; index++)
{
    var deployment = deployments[index];
    var keyPath = Path.Combine(directory, deployment.Name + ".pem");
    using var rsa = RSA.Create(3072);
    if (File.Exists(keyPath))
    {
        rsa.ImportFromPem(File.ReadAllText(keyPath));
    }
    else
    {
        File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    var parameters = rsa.ExportParameters(includePrivateParameters: false);
    var jwks = JsonSerializer.Serialize(new
    {
        keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = "dev-" + deployment.Name, n = Base64Url(parameters.Modulus!), e = Base64Url(parameters.Exponent!) } },
    });

    var prefix = $"Identity__Clients__{index}__";
    var origin = $"https://{deployment.Name}.localhost:7000";
    variables.Add((prefix + "ClientId", "bff-" + deployment.Name));
    variables.Add((prefix + "Kind", "Browser"));
    variables.Add((prefix + "Audience", "ec-" + deployment.Name));
    variables.Add((prefix + "RedirectUris__0", origin + "/bff/callback"));
    variables.Add((prefix + "PostLogoutRedirectUris__0", origin + "/bff/signed-out"));
    variables.Add((prefix + "JsonWebKeySet", jwks));
    variables.Add((prefix + "AccessTokenLifetime", deployment.Access));
    variables.Add((prefix + "RefreshTokenLifetime", deployment.Refresh));
    variables.Add((prefix + "AbsoluteSessionLifetime", deployment.Absolute));
}

variables.Add(("Identity__AdminConsoleClientId", "bff-admin"));

// Tenant & Merchant's service client for Identity's internal email verification API (ADR-021).
var serviceKeyPath = Path.Combine(directory, "svc-tenant-merchant.pem");
using (var rsa = RSA.Create(3072))
{
    if (File.Exists(serviceKeyPath))
    {
        rsa.ImportFromPem(File.ReadAllText(serviceKeyPath));
    }
    else
    {
        File.WriteAllText(serviceKeyPath, rsa.ExportPkcs8PrivateKeyPem());
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(serviceKeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    var parameters = rsa.ExportParameters(includePrivateParameters: false);
    var jwks = JsonSerializer.Serialize(new
    {
        keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = "dev-svc-tenant-merchant", n = Base64Url(parameters.Modulus!), e = Base64Url(parameters.Exponent!) } },
    });

    var prefix = $"Identity__Clients__{deployments.Length}__";
    variables.Add((prefix + "ClientId", "svc-tenant-merchant"));
    variables.Add((prefix + "Kind", "Service"));
    variables.Add((prefix + "JsonWebKeySet", jwks));
    variables.Add((prefix + "ServiceScopes__0__Scope", "svc.identity.account-verification"));
    variables.Add((prefix + "ServiceScopes__0__Audience", "identity-api"));
    variables.Add(("Identity__AccountVerification__AllowedClientIds__0", "svc-tenant-merchant"));
}

// Catalog's service client for Tenant & Merchant's internal membership verification API (ADR-025).
var catalogKeyPath = Path.Combine(directory, "svc-catalog.pem");
using (var rsa = RSA.Create(3072))
{
    if (File.Exists(catalogKeyPath))
    {
        rsa.ImportFromPem(File.ReadAllText(catalogKeyPath));
    }
    else
    {
        File.WriteAllText(catalogKeyPath, rsa.ExportPkcs8PrivateKeyPem());
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(catalogKeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    var parameters = rsa.ExportParameters(includePrivateParameters: false);
    var jwks = JsonSerializer.Serialize(new
    {
        keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = "dev-svc-catalog", n = Base64Url(parameters.Modulus!), e = Base64Url(parameters.Exponent!) } },
    });

    var prefix = $"Identity__Clients__{deployments.Length + 1}__";
    variables.Add((prefix + "ClientId", "svc-catalog"));
    variables.Add((prefix + "Kind", "Service"));
    variables.Add((prefix + "JsonWebKeySet", jwks));
    variables.Add((prefix + "ServiceScopes__0__Scope", "svc.tenant-merchant.membership-verification"));
    variables.Add((prefix + "ServiceScopes__0__Audience", "tenant-merchant-api"));
}

var tenantMerchant = new List<(string Name, string Value)>
{
    ("TenantMerchant__IdentityVerification__BaseAddress", "https://localhost:7101/"),
    ("TenantMerchant__IdentityVerification__PrivateKeyPath", serviceKeyPath),
    ("TenantMerchant__IdentityVerification__KeyId", "dev-svc-tenant-merchant"),
    ("TenantMerchant__MembershipVerification__AllowedClientIds__0", "svc-catalog"),
};

var catalog = new List<(string Name, string Value)>
{
    ("Catalog__MembershipVerification__BaseAddress", "https://localhost:7102/"),
    ("Catalog__MembershipVerification__TokenEndpoint", "https://localhost:7101/connect/token"),
    ("Catalog__MembershipVerification__PrivateKeyPath", catalogKeyPath),
    ("Catalog__MembershipVerification__KeyId", "dev-svc-catalog"),
};

var powershell = new StringBuilder("# Generated by deploy/dev/bff-dev-keys.cs. Local development only. Public keys only.\n");
var bash = new StringBuilder("# Generated by deploy/dev/bff-dev-keys.cs. Local development only. Public keys only.\n");
foreach (var (name, value) in variables)
{
    powershell.Append("$env:").Append(name).Append(" = '").Append(value.Replace("'", "''", StringComparison.Ordinal)).Append("'\n");
    bash.Append("export ").Append(name).Append("='").Append(value.Replace("'", "'\\''", StringComparison.Ordinal)).Append("'\n");
}

File.WriteAllText(Path.Combine(directory, "identity-clients.ps1"), powershell.ToString());
File.WriteAllText(Path.Combine(directory, "identity-clients.sh"), bash.ToString());

var tenantMerchantPowershell = new StringBuilder("# Generated by deploy/dev/bff-dev-keys.cs. Local development only. A key path, no key.\n");
var tenantMerchantBash = new StringBuilder("# Generated by deploy/dev/bff-dev-keys.cs. Local development only. A key path, no key.\n");
foreach (var (name, value) in tenantMerchant)
{
    tenantMerchantPowershell.Append("$env:").Append(name).Append(" = '").Append(value.Replace("'", "''", StringComparison.Ordinal)).Append("'\n");
    tenantMerchantBash.Append("export ").Append(name).Append("='").Append(value.Replace("'", "'\\''", StringComparison.Ordinal)).Append("'\n");
}

File.WriteAllText(Path.Combine(directory, "tenant-merchant-identity.ps1"), tenantMerchantPowershell.ToString());
File.WriteAllText(Path.Combine(directory, "tenant-merchant-identity.sh"), tenantMerchantBash.ToString());

var catalogPowershell = new StringBuilder("# Generated by deploy/dev/bff-dev-keys.cs. Local development only. A key path, no key.\n");
var catalogBash = new StringBuilder("# Generated by deploy/dev/bff-dev-keys.cs. Local development only. A key path, no key.\n");
foreach (var (name, value) in catalog)
{
    catalogPowershell.Append("$env:").Append(name).Append(" = '").Append(value.Replace("'", "''", StringComparison.Ordinal)).Append("'\n");
    catalogBash.Append("export ").Append(name).Append("='").Append(value.Replace("'", "'\\''", StringComparison.Ordinal)).Append("'\n");
}

File.WriteAllText(Path.Combine(directory, "catalog-tenant-merchant.ps1"), catalogPowershell.ToString());
File.WriteAllText(Path.Combine(directory, "catalog-tenant-merchant.sh"), catalogBash.ToString());
Console.WriteLine($"BFF, svc-tenant-merchant and svc-catalog development keys and the client settings are in {directory}");

static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
