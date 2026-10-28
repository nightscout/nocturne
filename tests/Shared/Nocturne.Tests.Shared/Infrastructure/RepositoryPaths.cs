namespace Nocturne.Tests.Shared.Infrastructure;

/// <summary>Locates the checkout a test runs from, for guards that read the source tree.</summary>
public static class RepositoryPaths
{
    /// <summary>The first directory above the test binaries that holds <c>tests/Unit</c>.</summary>
    /// <exception cref="DirectoryNotFoundException">No such directory exists.</exception>
    public static string Root()
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
