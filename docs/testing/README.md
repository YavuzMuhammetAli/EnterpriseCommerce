# Testing

## Test projects

| Project | Kind | What it covers |
|---|---|---|
| `EnterpriseCommerce.{Service}.UnitTests` | Unit | Domain rules, application handlers and isolated components, with no I/O |
| `EnterpriseCommerce.{Service}.IntegrationTests` | Integration | The real HTTP pipeline in memory (`WebApplicationFactory`), and later real infrastructure |
| `EnterpriseCommerce.ArchitectureTests` | Architecture | Layer dependency direction, forbidden technologies in inner layers, no cross-service references, naming conventions |

## Tooling

- **xUnit v3** on **Microsoft.Testing.Platform**. `global.json` sets `"test": { "runner": "Microsoft.Testing.Platform" }`, so `dotnet test` uses MTP mode.
- `Microsoft.AspNetCore.Mvc.Testing` for in-memory API hosting.
- Architecture tests need no extra package: they parse `.csproj` files and inspect compiled assembly references.

## Running

```bash
dotnet test --solution EnterpriseCommerce.sln -c Release
dotnet test --project tests/ArchitectureTests/EnterpriseCommerce.ArchitectureTests
```

## Planned

Integration tests against real SQL Server, MongoDB, Redis and Kafka arrive with the first persistence and messaging features. The approach (Compose-provided infrastructure or per-test containers) is decided then. Code coverage reporting is also planned.
