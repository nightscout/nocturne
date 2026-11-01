namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// The connector services and publishers reach the data tables through repository contracts: no file
/// under <see cref="Folders"/> names a context, the context factory, an entity namespace or the
/// Infrastructure.Data query extensions. This is the per-folder form of #3103 that
/// <see cref="HostPersistenceGuardTests"/>, which guards by concept, cannot express.
/// </summary>
[Trait("Category", "Unit")]
public class ConnectorServicesPersistenceGuardTests
{
    private static readonly string[] Folders =
    [
        "src/API/Nocturne.API/Services/Connectors",
        "src/API/Nocturne.API/Services/ConnectorPublishing",
    ];

    private static readonly string[] Forbidden =
    [
        "NocturneDbContext",
        "INocturneDbContextFactory",
        "Infrastructure.Data.Entities",
        "Infrastructure.Data.Extensions",
    ];

    [Fact]
    public void NoConnectorServiceFileNamesTheDataLayer()
    {
        var root = RepositoryRoot();
        var namers = Files(root)
            .SelectMany(path => Forbidden
                .Where(File.ReadAllText(Path.Combine(root, path)).Contains)
                .Select(term => $"{path}({term})"))
            .ToList();

        namers.Should().BeEmpty("a connector service reaches the data tables through a repository contract");
    }

    private static IEnumerable<string> Files(string root) =>
        Folders.SelectMany(folder =>
                Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "tests", "Unit")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"No tests/Unit directory above {AppContext.BaseDirectory}.");
    }
}
