using Nocturne.API.Services.Realtime;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Tests.Unit.Services.Realtime;

/// <summary>
/// Unit tests for <see cref="RealtimeCategories"/> — the SignalR group allowlist. Pins the invariant
/// that the native V4 categories never collide with the legacy v1 collection names, so a v4 broadcast
/// can never land on a v1 group (and vice versa).
/// </summary>
public class RealtimeCategoriesTests
{
    [Fact]
    public void V1AndV4Categories_AreDisjoint()
    {
        RealtimeCategories.V1.Intersect(RealtimeCategories.V4).Should().BeEmpty();
    }

    [Fact]
    public void All_IsUnionOfV1AndV4()
    {
        RealtimeCategories.All.Should().HaveCount(RealtimeCategories.V1.Length + RealtimeCategories.V4.Length);
        RealtimeCategories.All.Should().BeEquivalentTo([.. RealtimeCategories.V1, .. RealtimeCategories.V4]);
    }

    [Fact]
    public void EverySubscribableCategory_HasAGoverningScope()
    {
        // DataHub.Subscribe joins a category group only when the credential satisfies the scope this
        // map lists, so a category in All but absent from the map is unsubscribable — the fail-closed
        // direction, but a silent one. This makes adding a category without classifying it fail here.
        RealtimeCategories.All.Should().BeSubsetOf(RealtimeCategories.GoverningScopes.Keys);
    }

    [Fact]
    public void EveryGoverningScope_IsARealReadScope()
    {
        RealtimeCategories.GoverningScopes.Values.Distinct()
            .Should().BeSubsetOf(Scope.AllScopes);
    }
}
