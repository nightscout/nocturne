namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// A connector reaches the data tables through the host's repository contracts, so no project under
/// <c>src/Connectors</c> references <c>Nocturne.Infrastructure.Data</c>: a reference would rebuild
/// every connector on a data change and let one open a database context. The file-level form is
/// <see cref="ConnectorServicesPersistenceGuardTests"/>.
/// </summary>
[Trait("Category", "Unit")]
public class ConnectorProjectReferenceGuardTests
{
    private const string Folder = "src/Connectors";
    private const string DataProject = "Nocturne.Infrastructure.Data";

    [Fact]
    public void NoConnectorProjectReferencesTheDataProject()
    {
        var root = RepositoryRoot();
        var referencing = Directory.EnumerateFiles(Path.Combine(root, Folder), "*.csproj", SearchOption.AllDirectories)
            .Where(path => System.Xml.Linq.XDocument.Load(path)
                .Descendants("ProjectReference")
                .Select(reference => (string?)reference.Attribute("Include") ?? string.Empty)
                .Any(include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/')) == DataProject))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToList();

        referencing.Should().BeEmpty("a connector reaches the data tables through a repository contract");
    }

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
