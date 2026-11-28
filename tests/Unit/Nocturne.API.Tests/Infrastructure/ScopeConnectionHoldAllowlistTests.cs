using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// <see cref="ScopeConnection.HoldConnectionAsync"/> runs a scope's contexts on one session written once, so it is
/// limited to the members listed here, each with the reason it meets the preconditions in the
/// <see cref="ScopeConnection"/> remarks. An entry names the file and the member the call sits in, so a new call in a
/// listed file is not covered by its neighbour. A new caller is added here deliberately or not at all.
/// </summary>
[Trait("Category", "Unit")]
public class ScopeConnectionHoldAllowlistTests
{
    private static readonly AllowlistFence Holders = new(
        $"{nameof(ScopeConnection.HoldConnectionAsync)} shares one session across a scope's contexts",
        new Dictionary<string, string>
        {
            ["src/API/Nocturne.API/Services/Identity/TenantOverviewService.cs: TenantOverviewService.ReadPersonAsync"] =
                "one person's reads, for the overview build and for GetPersonAsync, each in a scope from "
                + "TenantJobScope.OpenFor; they run one at a time, write nothing and release the hold before the person "
                + "is judged",
        },
        source => SourceMentions.Calling(source, [nameof(ScopeConnection.HoldConnectionAsync)]));

    [Fact]
    public void OnlyTheAllowlistHoldsAScopeConnection()
    {
        Holders.Unlisted(RepoSources.Src).Should().BeEmpty($"{Holders.Rule}; allowlist a new caller here deliberately");
    }

    [Fact]
    public void EveryAllowlistEntryStillHoldsIt()
    {
        Holders.Stale(RepoSources.Src).Should().BeEmpty("a stale entry would silently cover a future call in that member");
    }

    [Fact]
    public void AnUnlistedHolderIsReported()
    {
        var elsewhere = new SourceFile(
            "src/API/Nocturne.API/Services/Elsewhere.cs",
            "class Elsewhere { async Task M(ScopeConnection c) { await using var held = await c.HoldConnectionAsync(ct); } }");

        Holders.Unlisted([elsewhere]).Should().Equal("src/API/Nocturne.API/Services/Elsewhere.cs: Elsewhere.M");
    }
}
