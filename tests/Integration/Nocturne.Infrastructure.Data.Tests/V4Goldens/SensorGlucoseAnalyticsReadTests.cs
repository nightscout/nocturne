using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.Infrastructure.Data.Tests.V4Goldens;

[Trait("Category", "Integration")]
[Collection("V4 goldens")]
public class SensorGlucoseAnalyticsReadTests(V4GoldenFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialReadPreservesValuesOrderingFiltersAndCanonicalSelection(bool descending)
    {
        var tenant = Guid.NewGuid();
        using var scope = await fixture.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();
        var start = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        await repo.BulkCreateAsync(Enumerable.Range(0, 12).Select(i => new SensorGlucose
        {
            Timestamp = start.AddMinutes(i / 2 * 5),
            Mgdl = 100 + i * 10,
            Device = i % 2 == 0 ? "a" : "b",
            DataSource = "projection-test",
            LegacyId = i % 3 == 0 ? $"legacy-{i}" : null,
            UtcOffset = i % 2 == 0 ? 600 : -300,
            Direction = i % 2 == 0 ? GlucoseDirection.Flat : GlucoseDirection.SingleUp,
            AdditionalProperties = new() { ["extra"] = "must not be materialized" },
        }), WriteOrigin.Backfill);

        var full = (await repo.GetAsync(start, start.AddMinutes(25), null, null, 100, descending: descending)).ToList();
        var slim = (await repo.GetForAnalyticsAsync(start, start.AddMinutes(25), null, null, 100, descending: descending)).ToList();
        slim.Should().HaveCount(12);
        slim.Select(Values).Should().Equal(full.Select(Values));
        CanonicalGlucoseStream.Select(slim, []).Select(Values).Should()
            .Equal(CanonicalGlucoseStream.Select(full, []).Select(Values));
        slim.Should().OnlyContain(r => r.AdditionalProperties == null);

        var expected = await repo.GetAsync(start.AddMinutes(5), start.AddMinutes(20), "a", "projection-test",
            2, 1, descending, nativeOnly: true);
        var actual = await repo.GetForAnalyticsAsync(start.AddMinutes(5), start.AddMinutes(20), "a", "projection-test",
            2, 1, descending, nativeOnly: true);
        actual.Select(Values).Should().Equal(expected.Select(Values));

        var cursor = full[3];
        expected = await repo.GetAsync(start, null, null, null, 3, 99, descending,
            afterTimestamp: cursor.Timestamp, afterId: cursor.Id);
        actual = await repo.GetForAnalyticsAsync(start, null, null, null, 3, 99, descending,
            afterTimestamp: cursor.Timestamp, afterId: cursor.Id);
        actual.Select(Values).Should().Equal(expected.Select(Values));
    }

    [Fact]
    public async Task PartialReadExcludesNonPrimaryDeletedAndOtherTenantRecords()
    {
        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        using (var otherScope = await fixture.BeginTenantScopeAsync(otherTenant))
        {
            await otherScope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>().CreateAsync(
                new SensorGlucose { Timestamp = DateTime.UtcNow, Mgdl = 99 }, WriteOrigin.Backfill);
        }
        using var scope = await fixture.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();
        var start = DateTime.UtcNow;
        await repo.BulkCreateAsync(new[]
        {
            new SensorGlucose { Timestamp = start, Mgdl = 120, DataSource = "dexcom" },
            new SensorGlucose { Timestamp = start.AddSeconds(10), Mgdl = 120.5, DataSource = "libre" },
            new SensorGlucose { Timestamp = start.AddMinutes(5), Mgdl = 180, DataSource = "dexcom", LegacyId = "deleted" },
        }, WriteOrigin.Backfill);
        await repo.DeleteByLegacyIdAsync("deleted", WriteOrigin.Backfill);
        (await fixture.QueryAsync(tenant, ctx => ctx.LinkedRecords.CountAsync(r => !r.IsPrimary)))
            .Should().Be(1);
        var full = (await repo.GetAsync(null, null, null, null, 100)).ToList();
        var slim = (await repo.GetForAnalyticsAsync(null, null, null, null, 100)).ToList();
        slim.Should().ContainSingle();
        slim.Select(Values).Should().Equal(full.Select(Values));
    }

    [Fact]
    public async Task PatientDeviceFilterPreservesStreamIdentityAndCombinesWithDeviceFilter()
    {
        var tenant = Guid.NewGuid();
        using var scope = await fixture.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();
        var devices = scope.ServiceProvider.GetRequiredService<IPatientDeviceRepository>();
        var device = await devices.CreateAsync(new PatientDevice
        {
            DeviceCategory = DeviceCategory.CGM, Manufacturer = "Dexcom", Model = "G7", Rank = 1,
        }, WriteOrigin.Backfill);
        await repo.BulkCreateAsync(new[]
        {
            new SensorGlucose { Timestamp = DateTime.UtcNow, Mgdl = 100, Device = "a", PatientDeviceId = device.Id },
            new SensorGlucose { Timestamp = DateTime.UtcNow.AddMinutes(5), Mgdl = 110, Device = "b", PatientDeviceId = device.Id },
            new SensorGlucose { Timestamp = DateTime.UtcNow.AddMinutes(10), Mgdl = 120, Device = "a" },
        }, WriteOrigin.Backfill);
        var full = await repo.GetAsync(null, null, "a", null, patientDeviceId: device.Id);
        var slim = (await repo.GetForAnalyticsAsync(null, null, "a", null, patientDeviceId: device.Id)).ToList();
        slim.Should().ContainSingle().Which.PatientDeviceId.Should().Be(device.Id);
        slim.Select(Values).Should().Equal(full.Select(Values));
    }

    private static object Values(SensorGlucose r) =>
        new { r.Id, r.Timestamp, r.Mills, r.Mgdl, r.PatientDeviceId, r.DataSource, r.Device, r.UtcOffset, r.Direction };
}
