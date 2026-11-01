using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Sqlite.Query.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.API.Services.Caching;

namespace Nocturne.API.Tests.Services.Analytics;

[Trait("Category", "Unit")]
public class DataOverviewServiceDuplicateFlagSqlTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime Nov10_2025_Noon = new(2025, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    private readonly StatementRecorder _statements = new();
    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly DataOverviewService _service;

    public DataOverviewServiceDuplicateFlagSqlTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(
            TenantId,
            o => o.ReplaceService<IMethodCallTranslatorProvider, IsNaNTranslatingProvider>(),
            _statements);
        _context = _db.CreateContext();

        var therapySettings = new Mock<ITherapySettingsResolver>();
        therapySettings
            .Setup(p => p.GetTimezoneAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("UTC");

        _service = new DataOverviewService(
            new TestNocturneDbContextFactory(_context),
            new TenantClockResolver(
                therapySettings.Object,
                new CategoryReadContext(),
                NullLogger<TenantClockResolver>.Instance),
            new Mock<IStatisticsService>().WithRealTimeInRange().Object,
            TestTenantCache.Create(),
            TimeProvider.System, NullLogger<DataOverviewService>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetDailySummaryAsync_ExcludesDuplicatesByTheirFlag()
    {
        await SeedPrimaryAndDuplicateAsync();

        var result = await _service.GetDailySummaryAsync(2025);

        var day = result.Days.Should().ContainSingle().Subject;
        day.TotalBolusUnits.Should().Be(4.0);
        day.TotalBasalUnits.Should().Be(1.0);
        day.TotalCarbs.Should().Be(30.0);
        day.AverageGlucoseMgdl.Should().Be(100.0);
        day.Counts["Glucose"].Should().Be(1);
        AssertExclusionReadsTheFlag();
    }

    [Fact]
    public async Task GetGriTimelineAsync_ExcludesDuplicatesByTheirFlag()
    {
        await SeedPrimaryAndDuplicateAsync(primaryGlucoseReadings: 72);

        var result = await _service.GetGriTimelineAsync(2025);

        var month = result.Periods.Should().ContainSingle().Subject;
        month.ReadingCount.Should().Be(72);
        month.AverageGlucoseMgdl.Should().Be(100.0);
        // (4 U bolus + 1 U basal) and 30 g carbs over November's 30 days.
        month.TotalDailyDose.Should().Be(0.17);
        month.AverageDailyCarbs.Should().Be(1.0);
        AssertExclusionReadsTheFlag();
    }

    [Fact]
    public async Task GetEHbA1cTimelineAsync_ExcludesDuplicatesByTheirFlag()
    {
        await SeedPrimaryAndDuplicateAsync();

        await _service.GetEHbA1cTimelineAsync(2025);

        AssertExclusionReadsTheFlag();
    }

    private void AssertExclusionReadsTheFlag()
    {
        _statements.All.Should().NotContain(s => s.Contains("linked_records", StringComparison.Ordinal));
        _statements.All.Should().Contain(s =>
            s.Contains("\"mgdl\"", StringComparison.Ordinal) && s.Contains("\"is_duplicate\"", StringComparison.Ordinal));
    }

    private async Task SeedPrimaryAndDuplicateAsync(int primaryGlucoseReadings = 1)
    {
        var sensorDuplicate = Guid.NewGuid();
        var bolusDuplicate = Guid.NewGuid();
        var tempBasalDuplicate = Guid.NewGuid();
        var carbDuplicate = Guid.NewGuid();

        _context.SensorGlucose.AddRange(Enumerable.Range(0, primaryGlucoseReadings)
            .Select(i => new SensorGlucoseEntity
            {
                Id = Guid.NewGuid(), TenantId = TenantId, Timestamp = Nov10_2025_Noon.AddMinutes(5 * i),
                Mgdl = 100.0, DataSource = "dexcom",
            }));
        _context.SensorGlucose.Add(
            new SensorGlucoseEntity
            {
                Id = sensorDuplicate, TenantId = TenantId, Timestamp = Nov10_2025_Noon,
                Mgdl = 300.0, DataSource = "glooko",
            });
        _context.Boluses.AddRange(
            new BolusEntity
            {
                Id = Guid.NewGuid(), TenantId = TenantId, Timestamp = Nov10_2025_Noon,
                Insulin = 4.0, DataSource = "dexcom",
            },
            new BolusEntity
            {
                Id = bolusDuplicate, TenantId = TenantId, Timestamp = Nov10_2025_Noon,
                Insulin = 6.0, DataSource = "glooko",
            });
        _context.TempBasals.AddRange(
            new TempBasalEntity
            {
                Id = Guid.NewGuid(), TenantId = TenantId, Timestamp = Nov10_2025_Noon,
                EndTimestamp = Nov10_2025_Noon.AddHours(1), Rate = 1.0, Origin = "Scheduled",
                DataSource = "dexcom",
            },
            new TempBasalEntity
            {
                Id = tempBasalDuplicate, TenantId = TenantId, Timestamp = Nov10_2025_Noon,
                EndTimestamp = Nov10_2025_Noon.AddHours(1), Rate = 2.0, Origin = "Scheduled",
                DataSource = "glooko",
            });
        _context.CarbIntakes.AddRange(
            new CarbIntakeEntity
            {
                Id = Guid.NewGuid(), TenantId = TenantId, Timestamp = Nov10_2025_Noon,
                Carbs = 30.0, DataSource = "dexcom",
            },
            new CarbIntakeEntity
            {
                Id = carbDuplicate, TenantId = TenantId, Timestamp = Nov10_2025_Noon,
                Carbs = 45.0, DataSource = "glooko",
            });

        await DuplicateGroups.LinkAsync(_context, RecordType.SensorGlucose, primary: null, sensorDuplicate);
        await DuplicateGroups.LinkAsync(_context, RecordType.Bolus, primary: null, bolusDuplicate);
        await DuplicateGroups.LinkAsync(_context, RecordType.TempBasal, primary: null, tempBasalDuplicate);
        await DuplicateGroups.LinkAsync(_context, RecordType.CarbIntake, primary: null, carbDuplicate);
        _statements.Clear();
    }

    /// <summary>
    /// SQLite has no translation for <see cref="double.IsNaN(double)"/>, so without this the
    /// glucose value reads throw and are skipped. SQLite stores NaN as NULL, hence IS NULL.
    /// </summary>
#pragma warning disable EF1001
    private sealed class IsNaNTranslatingProvider : SqliteMethodCallTranslatorProvider
    {
        public IsNaNTranslatingProvider(RelationalMethodCallTranslatorProviderDependencies dependencies)
            : base(dependencies) =>
            AddTranslators([new IsNaNTranslator(dependencies.SqlExpressionFactory)]);
    }
#pragma warning restore EF1001

    private sealed class IsNaNTranslator(ISqlExpressionFactory sql) : IMethodCallTranslator
    {
        private static readonly MethodInfo IsNaN = typeof(double).GetMethod(nameof(double.IsNaN))!;

        public SqlExpression? Translate(
            SqlExpression? instance, MethodInfo method, IReadOnlyList<SqlExpression> arguments,
            IDiagnosticsLogger<DbLoggerCategory.Query> logger) =>
            method == IsNaN ? sql.IsNull(arguments[0]) : null;
    }
}
