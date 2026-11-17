namespace EnterpriseCommerce.ArchitectureTests;

/// <summary>
/// Naming conventions and structural rules for all projects under src/.
/// </summary>
public sealed class ProjectStructureTests
{
    [Fact]
    public void Discovery_FindsExpectedProjects()
    {
        var names = Repository.SourceProjects.Select(project => project.Name).ToList();

        Assert.Contains("EnterpriseCommerce.Gateway", names);
        foreach (var layer in SourceProject.Layers)
        {
            Assert.Contains($"EnterpriseCommerce.Identity.{layer}", names);
        }
    }

    [Fact]
    public void AllSourceProjects_AreInAKnownLocation()
    {
        var unknown = Repository.SourceProjects.Where(project => project.Kind == ProjectKind.Unknown);

        Assert.Empty(unknown);
    }

    [Fact]
    public void ProjectFolders_MatchProjectNames()
    {
        var mismatched = Repository.SourceProjects
            .Where(project => project.RelativePath.Split('/')[^2] != project.Name)
            .Select(project => project.RelativePath);

        Assert.Empty(mismatched);
    }

    [Fact]
    public void ServiceProjects_FollowNamingConvention()
    {
        var violations = Repository.SourceProjects
            .Where(project => project.Kind == ProjectKind.ServiceLayer)
            .Where(project =>
                !SourceProject.Layers.Contains(project.Layer)
                || project.Name != $"{SourceProject.RootNamespace}.{project.Service}.{project.Layer}")
            .Select(project => project.RelativePath);

        Assert.Empty(violations);
    }

    [Fact]
    public void BuildingBlocks_FollowNamingConvention()
    {
        var violations = Repository.SourceProjects
            .Where(project => project.Kind == ProjectKind.BuildingBlock)
            .Where(project => !project.Name.StartsWith($"{SourceProject.RootNamespace}.BuildingBlocks.", StringComparison.Ordinal))
            .Select(project => project.RelativePath);

        Assert.Empty(violations);
    }
}
