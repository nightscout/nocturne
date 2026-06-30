using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.Infrastructure.Data.Tests.V4Goldens;

/// <summary>
/// Regression guard for the <c>legacy_id</c> column width. Some uploaders mint treatment <c>_id</c>
/// values well beyond the original 64-char column (e.g. a temp basal keyed
/// <c>"tempBasal &lt;full-precision-rate&gt; &lt;ISO-timestamp&gt;"</c>, up to ~104 chars), which used to
/// fail the insert with Postgres <c>22001: value too long for type character varying(64)</c> and
/// silently drop the record. The column is now <c>varchar(255)</c>; this test pins that an over-64
/// legacy id round-trips intact through a real insert + lookup.
/// </summary>
[Trait("Category", "Integration")]
[Collection("V4 goldens")]
public class LegacyIdLengthGoldenTests
{
    private readonly V4GoldenFixture _fx;

    public LegacyIdLengthGoldenTests(V4GoldenFixture fx) => _fx = fx;

    private static readonly DateTime T0 = new(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc);

    // 104 chars — the longest legacy id observed in production (a full-precision temp-basal key).
    private const string LongLegacyId =
        "tempBasal 0.005633353027734491164 2026-06-22T13:45:38Z padding-to-104-chars-aaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task TempBasal_LegacyIdOver64Chars_PersistsAndRoundTrips()
    {
        LongLegacyId.Length.Should().BeGreaterThan(64, "the scenario must exceed the old column width");

        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ITempBasalRepository>();

        await repo.CreateAsync(new TempBasal
        {
            StartTimestamp = T0,
            EndTimestamp = T0.AddMinutes(30),
            Rate = 0.005633353027734491164,
            Origin = TempBasalOrigin.Manual,
            DataSource = "loop",
            LegacyId = LongLegacyId,
        }, CancellationToken.None);

        var fetched = await repo.GetByLegacyIdAsync(LongLegacyId, CancellationToken.None);
        fetched.Should().NotBeNull("an over-64-char legacy id must persist, not overflow the column");
        fetched!.LegacyId.Should().Be(LongLegacyId, "the full legacy id must be stored untruncated");
    }
}
