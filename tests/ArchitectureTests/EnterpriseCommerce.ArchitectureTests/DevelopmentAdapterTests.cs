using System.Reflection;
using System.Text.Json;

namespace EnterpriseCommerce.ArchitectureTests;

/// <summary>
/// Isolation of development-only adapters, such as the Identity development mail sink (ADR-018 section 4, layer 5).
/// They may reference only their own service's Application and Domain, may be referenced only by their own service's
/// Api and only in Debug builds, and no committed configuration may enable them.
/// </summary>
public sealed class DevelopmentAdapterTests
{
    private const string MailSink = "EnterpriseCommerce.Identity.DevMailSink";

    private static readonly string[] ForbiddenPackagePrefixes =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.Data.SqlClient",
        "OpenIddict",
        "Microsoft.AspNetCore",
        "Serilog",
    ];

    private static IReadOnlyList<SourceProject> Adapters =>
        [.. Repository.SourceProjects.Where(project => project.Kind == ProjectKind.DevelopmentAdapter)];

    [Fact]
    public void TheIdentityMailSink_IsRecognizedAsADevelopmentAdapter() =>
        Assert.Contains(Adapters, adapter => adapter.Name == MailSink);

    [Fact]
    public void Adapters_ReferenceOnlyTheirOwnApplicationOrDomain()
    {
        var violations = Adapters.SelectMany(adapter => adapter.ProjectReferences
            .Where(reference => reference != $"{SourceProject.RootNamespace}.{adapter.Service}.Application"
                && reference != $"{SourceProject.RootNamespace}.{adapter.Service}.Domain")
            .Select(reference => $"{adapter.Name} -> {reference}"));

        Assert.Empty(violations);
    }

    [Fact]
    public void Adapters_UseNoPersistenceProtocolOrHostingPackages()
    {
        var violations = Adapters.SelectMany(adapter => adapter.PackageReferences
            .Where(package => ForbiddenPackagePrefixes.Any(prefix => package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .Select(package => $"{adapter.Name}: {package}"));

        Assert.Empty(violations);
    }

    [Fact]
    public void Adapters_AreReferencedOnlyByTheirOwnApi_AndOnlyInDebugBuilds()
    {
        var violations = new List<string>();
        foreach (var adapter in Adapters)
        {
            foreach (var project in Repository.SourceProjects)
            {
                var reference = project.ProjectReferenceItems.SingleOrDefault(item => item.Name == adapter.Name);
                if (reference is null)
                {
                    continue;
                }

                if (project.Name != $"{SourceProject.RootNamespace}.{adapter.Service}.Api")
                {
                    violations.Add($"{project.Name} must not reference {adapter.Name}.");
                }
                else if (!reference.IsDebugOnly)
                {
                    violations.Add($"{project.Name} references {adapter.Name} without the condition '$(Configuration)' == 'Debug'.");
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void ReleaseBuild_IdentityApi_DoesNotReferenceOrShipTheMailSink()
    {
#if DEBUG
        Assert.Skip("Verified in Release builds (CI). Debug builds include the development mail sink by design.");
#else
        var referenced = Assembly.Load("EnterpriseCommerce.Identity.Api").GetReferencedAssemblies().Select(name => name.Name);

        Assert.DoesNotContain(MailSink, referenced);
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, MailSink + ".dll")), "The mail sink assembly must not be in a Release output.");
#endif
    }

    [Fact]
    public void NoCommittedConfiguration_EnablesTheDevelopmentMailSink()
    {
        var violations = new List<string>();
        var settingsFiles = Directory.EnumerateFiles(Path.Combine(Repository.Root, "src"), "appsettings*.json", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(segment => segment is "bin" or "obj"));

        foreach (var file in settingsFiles)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            if (json.RootElement.TryGetProperty("Identity", out var identity) && identity.TryGetProperty("DevelopmentMailSink", out var sink))
            {
                var isBaseSettings = Path.GetFileName(file) == "appsettings.json";
                var enabled = sink.TryGetProperty("Enabled", out var flag) && flag.ValueKind == JsonValueKind.True;
                if (isBaseSettings || enabled)
                {
                    violations.Add(Path.GetRelativePath(Repository.Root, file));
                }
            }
        }

        Assert.Empty(violations);
    }
}
