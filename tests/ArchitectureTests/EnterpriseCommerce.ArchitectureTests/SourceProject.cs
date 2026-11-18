using System.Xml.Linq;

namespace EnterpriseCommerce.ArchitectureTests;

public enum ProjectKind
{
    ServiceLayer,
    Gateway,
    BuildingBlock,

    /// <summary>
    /// A development-only adapter of a service (<c>EnterpriseCommerce.{Service}.Dev*</c>), for example the Identity
    /// development mail sink (ADR-018 section 4). Stricter rules apply: see DevelopmentAdapterTests.
    /// </summary>
    DevelopmentAdapter,
    Unknown,
}

/// <summary>
/// A project under src/, with the dependencies declared in its .csproj file.
/// </summary>
public sealed record SourceProject(
    string Name,
    string RelativePath,
    ProjectKind Kind,
    string? Service,
    string? Layer,
    string Sdk,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> PackageReferences,
    IReadOnlyList<string> FrameworkReferences,
    IReadOnlyList<ProjectReferenceItem> ProjectReferenceItems)
{
    public const string RootNamespace = "EnterpriseCommerce";

    public static readonly IReadOnlyList<string> Layers = ["Domain", "Application", "Infrastructure", "Api"];

    public override string ToString() => Name;

    public static SourceProject Load(string repositoryRoot, string csprojPath)
    {
        var relativePath = Path.GetRelativePath(repositoryRoot, csprojPath).Replace('\\', '/');
        var name = Path.GetFileNameWithoutExtension(csprojPath);
        var document = XDocument.Load(csprojPath);
        var root = document.Root ?? throw new InvalidOperationException($"Empty project file: {relativePath}");

        var segments = relativePath.Split('/');
        var (kind, service, layer) = segments switch
        {
            ["src", "Services", var svc, _, _] when name.StartsWith($"{RootNamespace}.{svc}.Dev", StringComparison.Ordinal)
                => (ProjectKind.DevelopmentAdapter, svc, (string?)null),
            ["src", "Services", var svc, _, _] => (ProjectKind.ServiceLayer, svc, name.Split('.').Last()),
            ["src", "Gateway", _, _] => (ProjectKind.Gateway, (string?)null, (string?)null),
            ["src", "BuildingBlocks", _, _] => (ProjectKind.BuildingBlock, null, null),
            _ => (ProjectKind.Unknown, null, null),
        };

        return new SourceProject(
            name,
            relativePath,
            kind,
            service,
            layer,
            root.Attribute("Sdk")?.Value ?? string.Empty,
            Includes(root, "ProjectReference").Select(Path.GetFileNameWithoutExtension).OfType<string>().ToList(),
            Includes(root, "PackageReference").ToList(),
            Includes(root, "FrameworkReference").ToList(),
            root.Descendants("ProjectReference")
                .Where(element => element.Attribute("Include") is not null)
                .Select(element => new ProjectReferenceItem(
                    Path.GetFileNameWithoutExtension(element.Attribute("Include")!.Value.Replace('\\', '/').Split('/').Last()),
                    element.Attribute("Condition")?.Value ?? element.Parent?.Attribute("Condition")?.Value))
                .ToList());
    }

    private static IEnumerable<string> Includes(XElement root, string itemName) =>
        root.Descendants(itemName)
            .Select(element => element.Attribute("Include")?.Value)
            .OfType<string>()
            .Select(value => value.Replace('\\', '/').Split('/').Last());
}

/// <summary>A project reference and the MSBuild condition it is subject to (on the item or its ItemGroup).</summary>
public sealed record ProjectReferenceItem(string Name, string? Condition)
{
    /// <summary>Whether the reference exists only in Debug builds.</summary>
    public bool IsDebugOnly =>
        Condition is not null
        && string.Equals(Condition.Replace(" ", string.Empty, StringComparison.Ordinal), "'$(Configuration)'=='Debug'", StringComparison.Ordinal);
}
