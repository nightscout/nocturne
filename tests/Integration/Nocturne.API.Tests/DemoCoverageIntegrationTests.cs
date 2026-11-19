using System.Globalization;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using FluentAssertions;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Seeding;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Configuration;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Services.Demo.Services;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests;

[Collection(ApiIntegrationShards.One)]
public sealed class DemoCoverageIntegrationTests(
    ApiIntegrationTestFixture fixture,
    ITestOutputHelper output) : ApiIntegrationTestBase(fixture, output)
{
    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("2026-10-30T12:00:00Z")]
    // Sunday's planted low is still ahead, and the trace leaves insulin on board at Tuesday's.
    [InlineData("2026-11-01T00:05:00Z")]
    public async Task Demo_seed_persists_reportable_CGM_streams_labs_and_review_states(string now)
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse(now, CultureInfo.InvariantCulture));
        await using var restoreDb = Fixture.CreateDbContext(Fixture.TenantId);
        var originalIsDemo = await restoreDb.Tenants.AsNoTracking()
            .Where(t => t.Id == Fixture.TenantId)
            .Select(t => t.IsDemo)
            .SingleAsync();
        var originalDemoMode = Fixture.DemoModeService.IsEnabled;
        using var scope = Fixture.Services.CreateScope();
        var http = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var originalHttpContext = http.HttpContext;
        try
        {
            Fixture.DemoModeService.IsEnabled = true;
            await restoreDb.Database.ExecuteSqlInterpolatedAsync($"UPDATE tenants SET is_demo = true WHERE id = {Fixture.TenantId}");
            var tenant = new TenantContext(Fixture.TenantId, ApiIntegrationTestFixture.TenantSlug, "Integration", true, true);
            scope.ServiceProvider.GetRequiredService<ITenantAccessor>().SetTenant(tenant);
            http.HttpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            http.HttpContext.RequestServices.GetRequiredService<ITenantAccessor>().SetTenant(tenant);
            await using var dbContext = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>()
                .CreateForTenantAsync(Fixture.TenantId);
            var patients = scope.ServiceProvider.GetRequiredService<IPatientRecordRepository>();
            var patient = await patients.GetOrCreateAsync();
            patient.Timezone = "UTC";
            await patients.UpdateAsync(patient, WriteOrigin.Live);
            var uiSettings = await scope.ServiceProvider.GetRequiredService<IUISettingsService>().GetSettingsAsync()
                ?? new UISettingsConfiguration();
            // The seeder lays its compression artifacts at wall-clock times in the zone it resolves.
            var clock = SeedingClock.Resolve(
                await dbContext.PatientRecords.Select(r => r.Timezone).FirstOrDefaultAsync(),
                time);
            uiSettings.DataQuality.SleepSchedule.Timezone = clock.ZoneId;
            await scope.ServiceProvider.GetRequiredService<IUISettingsService>().SaveSettingsAsync(uiSettings);
            var today = DateOnly.FromDateTime(clock.LocalToday);
            var realNight = Enumerable.Range(0, 14).Select(offset => today.AddDays(-offset))
                .First(night => DemoCoverageSeeds.CompressionArtifactEndedBy(night, clock.Demo, clock.UtcNow));
            var realSuggestion = new CompressionLowSuggestionEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = Fixture.TenantId,
                StartMills = time.GetUtcNow().AddHours(-2).ToUnixTimeMilliseconds(),
                EndMills = time.GetUtcNow().AddHours(-1).ToUnixTimeMilliseconds(),
                Confidence = 0.5,
                Status = "Pending",
                NightOf = realNight,
                CreatedAt = time.GetUtcNow().ToUnixTimeMilliseconds(),
                LowestGlucose = 59,
                DataSource = "real-connector",
            };
            dbContext.CompressionLowSuggestions.Add(realSuggestion);
            await dbContext.SaveChangesAsync();
            await ActivatorUtilities.CreateInstance<SampleDataSeeder>(scope.ServiceProvider, time)
                .SeedAsync(tenant, 14, Fixture.OwnerSubjectId, DataSources.DemoService);

            var devices = await dbContext.PatientDevices.AsNoTracking()
                .Where(d => d.DeviceCategory == nameof(DeviceCategory.CGM))
                .ToListAsync();
            devices.Should().HaveCountGreaterThanOrEqualTo(2);
            var deviceIds = devices.Select(d => d.Id).ToArray();
            var readings = await dbContext.SensorGlucose.AsNoTracking()
                .Where(r => r.PatientDeviceId != null && deviceIds.Contains(r.PatientDeviceId.Value))
                .ToListAsync();
            readings.Select(r => r.PatientDeviceId).Distinct().Should().HaveCount(2);

            var range = readings.OrderBy(r => r.Timestamp).ToArray();
            var compareUrl = $"/api/v4/cgm-comparison?deviceAId={deviceIds[0]}&deviceBId={deviceIds[1]}&startDate={Uri.EscapeDataString(range[0].Timestamp.ToString("O"))}&endDate={Uri.EscapeDataString(range[^1].Timestamp.ToString("O"))}";
            using var comparison = await AuthenticatedClient.GetAsync(compareUrl);
            comparison.EnsureSuccessStatusCode();
            var comparisonJson = await comparison.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            comparisonJson.GetProperty("pairs").GetArrayLength().Should().BeGreaterThan(0);

            using var stats = await AuthenticatedClient.GetAsync(
                $"/api/v4/statistics/range-analytics?startDate={Uri.EscapeDataString(range[0].Timestamp.ToString("O"))}&endDate={Uri.EscapeDataString(range[^1].Timestamp.ToString("O"))}");
            stats.EnsureSuccessStatusCode();
            var statsJson = await stats.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            statsJson.GetProperty("analysis").EnumerateObject().Should().NotBeEmpty();

            using var labs = await AuthenticatedClient.GetAsync("/api/v4/lab-results/hba1c");
            labs.EnsureSuccessStatusCode();
            (await labs.Content.ReadFromJsonAsync<System.Text.Json.JsonElement[]>()).Should().HaveCount(3);

            var suggestions = await dbContext.CompressionLowSuggestions.AsNoTracking().ToListAsync();
            suggestions.Should().Contain(s => s.Id == realSuggestion.Id && s.DataSource == "real-connector"
                && s.Status.Equals("Pending", StringComparison.OrdinalIgnoreCase));
            suggestions.Should().OnlyContain(s => s.DataSource == DataSources.DemoService || s.DataSource == "real-connector");
            var demo = suggestions.Where(s => s.DataSource == DataSources.DemoService).ToList();
            demo.Should().NotContain(s => s.NightOf == realNight);
            // The detector also flags natural dips in the random demo trace below its nadir threshold.
            demo.Should().OnlyContain(s => s.DropRate > 0
                && s.LowestGlucose < CompressionLowDetectionService.NadirThresholdMgDl);
            var planted = demo.Where(s => s.LowestGlucose <= DemoCoverageSeeds.CompressionNadirMgdl
                    && s.StartMills <= DemoCoverageSeeds.CompressionNadirMills(s.NightOf, clock.Demo)
                    && s.EndMills >= DemoCoverageSeeds.CompressionNadirMills(s.NightOf, clock.Demo))
                .ToList();
            planted.Should().Contain(s => s.Status.Equals("Pending", StringComparison.OrdinalIgnoreCase));
            planted.Should().Contain(s => s.Status.Equals("Accepted", StringComparison.OrdinalIgnoreCase));
            planted.Should().Contain(s => s.Status.Equals("Dismissed", StringComparison.OrdinalIgnoreCase));
            demo.Except(planted).Should().OnlyContain(s => s.Status.Equals("Pending", StringComparison.OrdinalIgnoreCase));
            var accepted = suggestions.Single(s => s.Status.Equals("Accepted", StringComparison.OrdinalIgnoreCase));
            accepted.StateSpanId.Should().NotBeNull();
            var acceptedSpan = await dbContext.StateSpans.IgnoreQueryFilters()
                .SingleAsync(s => s.Id == accepted.StateSpanId);
            acceptedSpan.Source.Should().Be("compression-low-detection");
            acceptedSpan.MetadataJson.Should().Contain("\"DemoSeed\": true");

            var realReading = new SensorGlucoseEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = Fixture.TenantId,
                Timestamp = time.GetUtcNow().UtcDateTime.AddMinutes(-2),
                Mgdl = 123,
                DataSource = "real-connector",
            };
            dbContext.SensorGlucose.Add(realReading);
            await dbContext.SaveChangesAsync();

            await scope.ServiceProvider.GetRequiredService<IDemoDataPurge>().PurgeAsync(CancellationToken.None);
            (await dbContext.SensorGlucose.IgnoreQueryFilters().AnyAsync(r => r.Id == realReading.Id)).Should().BeTrue();
            (await dbContext.CompressionLowSuggestions.IgnoreQueryFilters()
                .AnyAsync(s => s.Id == realSuggestion.Id)).Should().BeTrue();
            (await dbContext.CompressionLowSuggestions.IgnoreQueryFilters()
                .AnyAsync(s => s.DataSource == DataSources.DemoService)).Should().BeFalse();
            (await dbContext.StateSpans.IgnoreQueryFilters().AnyAsync(s => s.Id == acceptedSpan.Id)).Should().BeFalse();
        }
        finally
        {
            Fixture.DemoModeService.IsEnabled = originalDemoMode;
            http.HttpContext = originalHttpContext;
            await restoreDb.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE tenants SET is_demo = {originalIsDemo} WHERE id = {Fixture.TenantId}");
        }
        (await restoreDb.Tenants.AsNoTracking()
            .Where(t => t.Id == Fixture.TenantId)
            .Select(t => t.IsDemo)
            .SingleAsync()).Should().Be(originalIsDemo);
        http.HttpContext.Should().BeSameAs(originalHttpContext);
    }
}
