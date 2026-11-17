namespace EnterpriseCommerce.ArchitectureTests;

/// <summary>
/// Locates the repository root and discovers every project under src/.
/// </summary>
public static class Repository
{
    private const string SolutionFileName = "EnterpriseCommerce.sln";

    private static readonly Lazy<string> LazyRoot = new(FindRoot);

    private static readonly Lazy<IReadOnlyList<SourceProject>> LazyProjects = new(() =>
        Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .Select(path => SourceProject.Load(Root, path))
            .OrderBy(project => project.Name, StringComparer.Ordinal)
            .ToList());

    public static string Root => LazyRoot.Value;

    public static IReadOnlyList<SourceProject> SourceProjects => LazyProjects.Value;

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"Could not locate {SolutionFileName} above {AppContext.BaseDirectory}.");
    }

    private static bool IsBuildOutput(string path)
    {
        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var segments = path.Split(separators);
        return segments.Contains("bin", StringComparer.Ordinal) || segments.Contains("obj", StringComparer.Ordinal);
    }
}
