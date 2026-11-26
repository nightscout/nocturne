using Nocturne.API.Services.Telemetry;
using Nocturne.API.Tests.Infrastructure;

namespace Nocturne.API.Tests.Services.Telemetry;

/// <summary>
/// <see cref="PageRouteIds.All"/> names exactly the pages under the web app's <c>routes</c>: a page
/// folder holds <c>+page.svelte</c> or a layout-resetting <c>+page@….svelte</c>, and its route id is
/// the folder's path under <c>routes</c>.
/// </summary>
[Trait("Category", "Unit")]
public class PageRouteIdsTests
{
    private const string Routes = "src/Web/packages/app/src/routes";

    [Fact]
    public void Every_page_of_the_web_app_is_a_known_route_id_and_nothing_else_is()
    {
        var root = Path.Combine(RepoSources.Root, Routes);
        var pages = Directory
            .EnumerateFiles(root, "+page*.svelte", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) is var name && (name == "+page.svelte" || name.StartsWith("+page@", StringComparison.Ordinal)))
            .Select(f => "/" + Path.GetRelativePath(root, Path.GetDirectoryName(f)!).Replace(Path.DirectorySeparatorChar, '/'))
            .Select(id => id == "/." ? "/" : id)
            .ToHashSet(StringComparer.Ordinal);

        pages.Should().NotBeEmpty();
        pages.Except(PageRouteIds.All).Should().BeEmpty(
            "a new page's route id has to be added to PageRouteIds.All, or its views are refused");
        PageRouteIds.All.Except(pages).Should().BeEmpty(
            "a route id whose page was moved or removed has to leave PageRouteIds.All");
    }
}
