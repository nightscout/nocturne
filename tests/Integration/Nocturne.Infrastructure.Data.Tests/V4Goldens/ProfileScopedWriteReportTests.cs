using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.Infrastructure.Data.Tests.V4Goldens;

/// <summary>The soft delete by legacy id prefix raises no broadcast, so it reports its write itself.</summary>
[Trait("Category", "Integration")]
[Collection("V4 goldens")]
public class ProfileScopedWriteReportTests
{
    private readonly V4GoldenFixture _fx;

    public ProfileScopedWriteReportTests(V4GoldenFixture fx) => _fx = fx;

    private static readonly DateTime T0 = new(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ASoftDeleteByLegacyIdPrefix_ReportsTheModel()
    {
        using var scope = await _fx.BeginTenantScopeAsync(Guid.NewGuid());
        var repo = scope.ServiceProvider.GetRequiredService<IBasalScheduleRepository>();
        await repo.CreateAsync(
            new BasalSchedule { Timestamp = T0, ProfileName = "Day", LegacyId = "doc:day" }, WriteOrigin.Live);
        _fx.CacheInvalidations.Clear();

        var deleted = await repo.DeleteByLegacyIdPrefixAsync("doc", WriteOrigin.Live);

        deleted.Should().Be(1);
        _fx.CacheInvalidations.Snapshot().Should().Equal(typeof(BasalSchedule));
    }
}
