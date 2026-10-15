using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using Nocturne.API.Services.Seeding;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests;

public sealed class DemoCoverageIntegrationTests(
    ApiIntegrationTestFixture fixture,
    ITestOutputHelper output) : ApiIntegrationTestBase(fixture, output)
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Demo_seed_persists_reportable_CGM_streams_labs_and_review_states()
    {
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
            var dbContext = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();
            dbContext.TenantId = Fixture.TenantId;
            var uiSettings = await scope.ServiceProvider.GetRequiredService<IUISettingsService>().GetSettingsAsync()
                ?? new UISettingsConfiguration();
            uiSettings.DataQuality.SleepSchedule.Timezone = "Australia/Sydney";
            await scope.ServiceProvider.GetRequiredService<IUISettingsService>().SaveSettingsAsync(uiSettings);
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
                TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney")).DateTime);
            var realNight = Enumerable.Range(0, 14).Select(offset => today.AddDays(-offset))
                .First(date => date.DayOfWeek is DayOfWeek.Sunday or DayOfWeek.Tuesday or DayOfWeek.Thursday);
            var realSuggestion = new CompressionLowSuggestionEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = Fixture.TenantId,
                StartMills = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeMilliseconds(),
                EndMills = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds(),
                Confidence = 0.5,
                Status = "Pending",
                NightOf = realNight,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                LowestGlucose = 59,
                DataSource = "real-connector",
            };
            dbContext.CompressionLowSuggestions.Add(realSuggestion);
            await dbContext.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<SampleDataSeeder>()
                .SeedAsync(tenant, 14, Fixture.OwnerSubjectId, DataSources.DemoService);

            var devices = await dbContext.PatientDevices.AsNoTracking()
                .Where(d => d.DeviceCategory == "cgm")
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
            suggestions.Should().Contain(s => s.Status.Equals("Pending", StringComparison.OrdinalIgnoreCase));
            suggestions.Should().Contain(s => s.Status.Equals("Accepted", StringComparison.OrdinalIgnoreCase));
            suggestions.Should().Contain(s => s.Status.Equals("Dismissed", StringComparison.OrdinalIgnoreCase));
            suggestions.Should().Contain(s => s.Id == realSuggestion.Id && s.DataSource == "real-connector");
            suggestions.Where(s => s.Id != realSuggestion.Id)
                .Should().OnlyContain(s => s.DataSource == DataSources.DemoService);
            suggestions.Where(s => s.Id != realSuggestion.Id)
                .Should().OnlyContain(s => s.DropRate > 0 && s.LowestGlucose <= 60);
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
                Timestamp = DateTime.UtcNow.AddMinutes(-2),
                Mgdl = 123,
                DataSource = "real-connector",
            };
            dbContext.SensorGlucose.Add(realReading);
            await dbContext.SaveChangesAsync();

            await Nocturne.API.Services.Demo.DemoDataPurge.PurgeEntriesAsync(dbContext, CancellationToken.None);
            await Nocturne.API.Services.Demo.DemoDataPurge.PurgeTreatmentsAsync(dbContext, CancellationToken.None);
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
