using Nocturne.API.Services.Realtime;

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
}
