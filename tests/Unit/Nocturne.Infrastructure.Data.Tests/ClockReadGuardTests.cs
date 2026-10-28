using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// The folders of Infrastructure.Data that read their clock through an injected
/// <see cref="TimeProvider"/> never read a static clock. The entities still stamp their initialisers
/// from the static clock, so the project cannot ban it outright.
/// </summary>
[Trait("Category", "Unit")]
public class ClockReadGuardTests
{
    private static readonly string[] Folders =
    [
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Services",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Mappers",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Interceptors",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Extensions",
    ];

    /// <summary>
    /// Reads that stay, by file and expression. Each must still be present, so a removed one leaves
    /// no stale entry behind.
    /// </summary>
    private static readonly (string File, string Read, string Reason)[] Allowed =
    [
        ("src/Infrastructure/Nocturne.Infrastructure.Data/Extensions/ServiceCollectionExtensions.cs",
            "TimeProvider.System",
            "registers the default clock every injected TimeProvider resolves to, for a host that has not"),
    ];

    [Fact]
    public void A_migrated_folder_reads_no_static_clock()
    {
        var reads = StaticClockReads.InFolders(Folders);

        reads.Where(r => !Allowed.Any(a => a.File == r.File && a.Read == r.Read))
            .Select(r => r.ToString())
            .Should().BeEmpty("these read a static clock or the system clock; inject TimeProvider instead");

        Allowed.Where(a => reads.Count(r => r.File == a.File && r.Read == a.Read) != 1)
            .Select(a => $"{a.File}: {a.Read}")
            .Should().BeEmpty("each allowed read is pinned to exactly one occurrence");
    }
}
