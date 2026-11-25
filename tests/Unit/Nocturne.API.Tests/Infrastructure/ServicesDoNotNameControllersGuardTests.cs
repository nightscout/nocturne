namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// The host's services never name a controller namespace, so the dependency between them runs one
/// way: a controller calls a service, and a request or response type a service shares lives under
/// <c>Models</c>. A doc comment may still point a reader at the controller that calls a service.
/// </summary>
[Trait("Category", "Unit")]
public class ServicesDoNotNameControllersGuardTests
{
    private const string Folder = "src/API/Nocturne.API/Services";

    /// <summary>The namespace in full, or relative to <c>Nocturne.API</c> as a file inside it may write it.</summary>
    private static readonly System.Text.RegularExpressions.Regex ControllerNamespace = new(@"\bControllers\.");

    [Fact]
    public void NoServiceFileNamesAControllerNamespace()
    {
        var root = RepositoryRoot();
        var namers = Directory.EnumerateFiles(Path.Combine(root, Folder), "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadLines(path)
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Any(ControllerNamespace.IsMatch))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToList();

        namers.Should().BeEmpty("a service reaches a controller's types through Models, never through its namespace");
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
