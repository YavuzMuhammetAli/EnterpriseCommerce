using System.Xml.Linq;

namespace EnterpriseCommerce.ArchitectureTests;

public enum ProjectKind
{
    ServiceLayer,
    Gateway,
    BuildingBlock,
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
    IReadOnlyList<string> FrameworkReferences)
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
            Includes(root, "FrameworkReference").ToList());
    }

    private static IEnumerable<string> Includes(XElement root, string itemName) =>
        root.Descendants(itemName)
            .Select(element => element.Attribute("Include")?.Value)
            .OfType<string>()
            .Select(value => value.Replace('\\', '/').Split('/').Last());
}
