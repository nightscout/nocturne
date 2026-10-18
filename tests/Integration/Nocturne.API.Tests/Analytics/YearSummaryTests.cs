using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Analytics;

[Trait("Category", "Integration")]
public class YearSummaryTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    [Theory]
    [InlineData("UTC", null, null, false)]
    [InlineData("America/New_York", "vendor-a", null, false)]
    [InlineData("Australia/Sydney", null, null, false)]
    [InlineData("Asia/Kathmandu", "absent", null, false)]
    [InlineData("UTC", null, "glucose.read", false)]
    [InlineData("UTC", null, "treatments.read", false)]
    [InlineData("UTC", null, "", false)]
    [InlineData("UTC", null, null, true)]
    [InlineData("UTC", null, "glucose.read", true)]
    public async Task Combined_metrics_match_standalone_reports_and_read_each_projection_once(
        string timezone, string? source, string? categories, bool clamped)
    {
        using var scope = Fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantAccessor>().SetTenant(
            new TenantContext(Fixture.TenantId, "year-summary", "Year summary", true, false));
        await using (var context = await scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>().CreateAsync())
        {
            for (var i = 0; i < 90; i++)
            {
                var timestamp = new DateTime(2024, 3, 10, 6, 0, 0, DateTimeKind.Utc).AddMinutes(i * 2);
                context.SensorGlucose.Add(new SensorGlucoseEntity { Id = Guid.NewGuid(), Timestamp = timestamp, Mgdl = 81 + i, DataSource = "vendor-a" });
                context.MeterGlucose.Add(new MeterGlucoseEntity { Id = Guid.NewGuid(), Timestamp = timestamp, Mgdl = 201 + i, DataSource = "vendor-a" });
                context.Boluses.AddRange(
                    new BolusEntity { Id = Guid.NewGuid(), Timestamp = timestamp, Insulin = 2.345, BolusKind = "Manual", DataSource = "vendor-a" },
                    new BolusEntity { Id = Guid.NewGuid(), Timestamp = timestamp, Insulin = 0.117, BolusKind = "Algorithm", DataSource = "vendor-a" });
                context.TempBasals.Add(new TempBasalEntity { Id = Guid.NewGuid(), Timestamp = timestamp, Rate = 1.13, Origin = "Pump", DataSource = "vendor-a" });
                context.CarbIntakes.Add(new CarbIntakeEntity { Id = Guid.NewGuid(), Timestamp = timestamp, Carbs = 30.257, DataSource = "vendor-a" });
            }
            await context.SaveChangesAsync();
        }
        var readContext = scope.ServiceProvider.GetRequiredService<ICategoryReadContext>();
        if (categories is not null)
        {
            readContext.MarkShare();
            readContext.SetVisibleCategories(categories);
            readContext.SetFullHistory(!clamped);
        }
        else if (clamped)
            readContext.ClampMemberHistory();
        var settings = new Mock<ITherapySettingsResolver>();
        settings.Setup(resolver => resolver.GetTimezoneAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(timezone);
        var service = ActivatorUtilities.CreateInstance<DataOverviewService>(scope.ServiceProvider, settings.Object);
        string[]? sources = source is null ? null : [source];
        using var commands = new MetricCommands(Fixture.TenantId);
        var daily = await service.GetDailySummaryAsync(2024, sources);
        var gri = await service.GetGriTimelineAsync(2024, sources);
        commands.Count.Should().Be(12);
        commands.Reset();
        var combined = await service.GetYearSummaryAsync(2024, sources);
        commands.Count.Should().Be(6);
        JsonSerializer.Serialize(combined.DailySummary).Should().Be(JsonSerializer.Serialize(daily));
        JsonSerializer.Serialize(combined.GriTimeline).Should().Be(JsonSerializer.Serialize(gri));
        if (clamped || categories == "")
        {
            combined.DailySummary!.Days.Should().BeEmpty();
            combined.GriTimeline!.Periods.Should().BeEmpty();
        }
        else if (categories == "glucose.read")
        {
            combined.DailySummary!.Days.Should().OnlyContain(day => day.TotalCarbs == null && day.TotalDailyDose == null);
            var month = combined.GriTimeline!.Periods.Should().ContainSingle().Which;
            month.AverageDailyCarbs.Should().BeNull();
            month.TotalDailyDose.Should().BeNull();
        }
        else if (categories == "treatments.read")
        {
            combined.DailySummary!.Days.Should().OnlyContain(day => day.AverageGlucoseMgdl == null && day.TotalCarbs > 0);
            combined.GriTimeline!.Periods.Should().BeEmpty();
        }
    }

    private sealed class MetricCommands : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly Guid _tenantId;
        private readonly List<IDisposable> _subscriptions = [];
        public int Count { get; private set; }

        public MetricCommands(Guid tenantId)
        {
            _tenantId = tenantId;
            _subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));
        }

        public void Reset() => Count = 0;
        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "Microsoft.EntityFrameworkCore")
                _subscriptions.Add(listener.Subscribe(this));
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key != RelationalEventId.CommandExecuted.Name || value.Value is not CommandExecutedEventData data
                || data.Context is not Nocturne.Infrastructure.Data.NocturneDbContext context || context.TenantId != _tenantId)
                return;
            var sql = data.Command.CommandText;
            if (sql.StartsWith("SELECT", StringComparison.Ordinal)
                && new[] { ".mgdl", ".insulin", ".rate", ".carbs" }.Any(sql.Contains))
                Count++;
        }

        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void Dispose()
        {
            foreach (var subscription in _subscriptions)
                subscription.Dispose();
        }
    }
}
