using FluentAssertions;
using Xunit;

namespace Nocturne.API.Tests.Middleware;

/// <summary>
/// Pins where the API is allowed to learn a caller's address from.
/// </summary>
/// <remarks>
/// <see cref="Nocturne.API.Extensions.NocturneForwardedHeadersExtensions"/> resolves it from the right of
/// <c>X-Forwarded-For</c>, where only a hop can write. Reading the raw header somewhere else takes
/// the leftmost entry instead — the one a caller supplies — and no amount of care at the resolving
/// site helps if a controller quietly prefers its own reading.
/// </remarks>
public class ClientAddressSourceCoverageTests
{
    [Fact]
    public void NothingReadsTheForwardedForHeaderItself()
    {
        var readers = SourceFiles()
            .Where(file => ReadsTheHeader(file.Text))
            .Select(file => file.Path)
            .ToList();

        readers.Should().BeEmpty(
            "an address read straight from the header is the entry a caller chose, which then "
            + "reaches audit rows and rate-limit partitions");
    }

    [Fact]
    public void TheScan_ReachesTheApiSources()
    {
        var files = SourceFiles();

        files.Should().HaveCountGreaterThan(200,
            "a scan that found nothing would pass while guarding nothing");
        files.Should().Contain(file => file.Path.EndsWith("/Program.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDetector_RecognisesTheReadItForbids()
    {
        ReadsTheHeader("""var forwarded = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();""")
            .Should().BeTrue();
        ReadsTheHeader("""Request.Headers["x-forwarded-for"].FirstOrDefault()?.Split(',')[0]""")
            .Should().BeTrue();
        ReadsTheHeader("""/// resolved from the right of <c>X-Forwarded-For</c>.""")
            .Should().BeFalse("naming the header in prose is not reading it");
    }

    private static bool ReadsTheHeader(string text) =>
        text.Contains("""Headers["X-Forwarded-For"]""", StringComparison.OrdinalIgnoreCase);

    private sealed record SourceFile(string Path, string Text);

    private static List<SourceFile> SourceFiles()
    {
        var root = RepositoryRoot();
        var api = Path.Combine(root, "src", "API", "Nocturne.API");

        return Directory.EnumerateFiles(api, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(path => new SourceFile(
                Path.GetRelativePath(root, path).Replace('\\', '/'),
                File.ReadAllText(path)))
            .ToList();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "tests", "Unit")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"No tests/Unit directory above {AppContext.BaseDirectory}.");
    }
}
