// The Inventory service host (ADR-026). The persistence, the APIs and the background services are added by the
// following Phase 5 milestones; this host answers liveness only.
var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();
app.MapGet("/health/live", () => Results.Text("Healthy"));

app.Run();

/// <summary>The entry point, visible to the integration tests' host factory.</summary>
public partial class Program;
