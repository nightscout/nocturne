using Nocturne.Core.Models;

namespace Nocturne.API.Tests.Infrastructure;

[Trait("Category", "Unit")]
public class TrackerRestoreGuardTests
{
    private const string Mapper = "src/Infrastructure/Nocturne.Infrastructure.Data/Mappers/TrackerMapper.cs";
    private const string Declaration = "src/Core/Nocturne.Core.Models/Trackers/TrackerDefinition.cs";

    /// <summary>
    /// Stored rows are restored unchecked, so only the mapper may restore one: any other caller
    /// could build a definition that breaks the rules <see cref="TrackerDefinition.Create"/> holds.
    /// </summary>
    [Fact]
    public void OnlyTheMapperRestoresATrackerDefinition()
    {
        var root = RepositoryRoot();
        var separator = Path.DirectorySeparatorChar;

        var callers = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{separator}obj{separator}") && !path.Contains($"{separator}bin{separator}"))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Where(path => path != Mapper && path != Declaration)
            .Where(path => File.ReadAllText(Path.Combine(root, path)).Contains("TrackerDefinition.Restore(", StringComparison.Ordinal))
            .ToList();

        callers.Should().BeEmpty("a stored definition is restored unchecked, and only the mapper may do it");
    }

    [Fact]
    public void TheMapperStillRestoresDefinitions()
    {
        File.ReadAllText(Path.Combine(RepositoryRoot(), Mapper)).Should().Contain("TrackerDefinition.Restore(");
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
