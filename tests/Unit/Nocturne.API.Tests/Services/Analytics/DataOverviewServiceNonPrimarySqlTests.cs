using System.Data.Common;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Sqlite.Query.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Analytics;
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

namespace Nocturne.API.Tests.Services.Analytics;

[Trait("Category", "Unit")]
public class DataOverviewServiceNonPrimarySqlTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime Nov10_2025_Noon = new(2025, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    private static readonly Regex InSubqueryOverLinkedRecords = new(
        @"\bIN\s*\(\s*SELECT\b[^()]*\bFROM\s+""linked_records""",
        RegexOptions.IgnoreCase);

    private readonly StatementRecorder _statements = new();
    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly DataOverviewService _service;

    public DataOverviewServiceNonPrimarySqlTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(
            TenantId,
            o => o.ReplaceService<IMethodCallTranslatorProvider, IsNaNTranslatingProvider>(),
            _statements);
        _context = _db.CreateContext();

        var therapySettings = new Mock<ITherapySettingsResolver>();
        therapySettings
            .Setup(p => p.GetTimezoneAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.SetupGet(a => a.Context)
            .Returns(new TenantContext(TenantId, "test", "Test", true, false));

        _service = new DataOverviewService(
            new TestTenantDbContextFactory(_context),
            therapySettings.Object,
            new Mock<IStatisticsService>().Object,
            new Mock<ICacheService>().Object,
            tenantAccessor.Object,
            new CategoryReadContext(),
            NullLogger<DataOverviewService>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetDailySummaryAsync_ExcludesNonPrimaryThroughNotExists()
    {
        SeedPrimaryAndDuplicate();

        var result = await _service.GetDailySummaryAsync(2025);

        var day = result.Days.Should().ContainSingle().Subject;
        day.TotalBolusUnits.Should().Be(4.0);
        day.TotalBasalUnits.Should().Be(1.0);
        day.TotalCarbs.Should().Be(30.0);
        day.AverageGlucoseMgdl.Should().Be(100.0);
        day.Counts["Glucose"].Should().Be(1);
        AssertExclusionIsCorrelated();
    }

    [Fact]
    public async Task GetGriTimelineAsync_ExcludesNonPrimaryThroughNotExists()
    {
        SeedPrimaryAndDuplicate(primaryGlucoseReadings: 72);

        var result = await _service.GetGriTimelineAsync(2025);

        var month = result.Periods.Should().ContainSingle().Subject;
        month.ReadingCount.Should().Be(72);
        month.AverageGlucoseMgdl.Should().Be(100.0);
        // (4 U bolus + 1 U basal) and 30 g carbs over November's 30 days.
        month.TotalDailyDose.Should().Be(0.17);
        month.AverageDailyCarbs.Should().Be(1.0);
        AssertExclusionIsCorrelated();
    }

    [Fact]
    public async Task GetEHbA1cTimelineAsync_ExcludesNonPrimaryThroughNotExists()
    {
        SeedPrimaryAndDuplicate();

        await _service.GetEHbA1cTimelineAsync(2025);

        AssertExclusionIsCorrelated();
    }

    private void AssertExclusionIsCorrelated()
    {
        var linked = _statements.All
            .Where(s => s.Contains("\"linked_records\"", StringComparison.Ordinal))
            .ToList();

        linked.Should().Contain(s => s.Contains("\"mgdl\"", StringComparison.Ordinal));
        linked.Should().OnlyContain(s => s.Contains("NOT EXISTS", StringComparison.OrdinalIgnoreCase));
        linked.Should().NotContain(s => InSubqueryOverLinkedRecords.IsMatch(s));
    }

    private void SeedPrimaryAndDuplicate(int primaryGlucoseReadings = 1)
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
                Id = Guid.NewGuid(), TenantId = TenantId, StartTimestamp = Nov10_2025_Noon,
                EndTimestamp = Nov10_2025_Noon.AddHours(1), Rate = 1.0, Origin = "Scheduled",
                DataSource = "dexcom",
            },
            new TempBasalEntity
            {
                Id = tempBasalDuplicate, TenantId = TenantId, StartTimestamp = Nov10_2025_Noon,
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

        AddNonPrimaryLink(RecordType.SensorGlucose, sensorDuplicate);
        AddNonPrimaryLink(RecordType.Bolus, bolusDuplicate);
        AddNonPrimaryLink(RecordType.TempBasal, tempBasalDuplicate);
        AddNonPrimaryLink(RecordType.CarbIntake, carbDuplicate);
        _context.SaveChanges();
        _statements.Clear();
    }

    private void AddNonPrimaryLink(RecordType recordType, Guid recordId) =>
        _context.LinkedRecords.Add(new LinkedRecordEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            CanonicalId = Guid.NewGuid(),
            RecordType = RecordTypeKeys.Key(recordType),
            RecordId = recordId,
            SourceTimestamp = new DateTimeOffset(Nov10_2025_Noon).ToUnixTimeMilliseconds(),
            DataSource = "glooko",
            IsPrimary = false,
        });

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

    private sealed class StatementRecorder : DbCommandInterceptor
    {
        private readonly List<string> _statements = [];

        public IReadOnlyList<string> All => _statements;

        public void Clear() => _statements.Clear();

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            _statements.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _statements.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
