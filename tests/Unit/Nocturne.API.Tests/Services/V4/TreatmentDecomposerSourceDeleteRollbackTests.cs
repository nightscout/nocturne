using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// <see cref="TreatmentDecomposer.DeleteFromSourceAsync"/> and <see cref="TreatmentDecomposer.DeleteByLegacyIdAsync"/>
/// sweep their tables in one transaction: a failure on a later table must leave the rows an earlier
/// table already deleted live.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentDecomposerSourceDeleteRollbackTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private const string Connector = "nightscout-connector";
    private const string Other = "other-connector";
    private static readonly DateTime At = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly FailingLinkPromote _failure = new();
    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly TreatmentDecomposer _decomposer;

    public TreatmentDecomposerSourceDeleteRollbackTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId, "test", _failure);
        _context = _db.CreateContext();

        _decomposer = new(
            _context,
            Mock.Of<IBolusRepository>(), Mock.Of<ITempBasalRepository>(),
            Mock.Of<ICarbIntakeRepository>(), Mock.Of<IBGCheckRepository>(), Mock.Of<INoteRepository>(),
            Mock.Of<IDeviceEventRepository>(), Mock.Of<IBolusCalculationRepository>(),
            Mock.Of<IStateSpanService>(),
            Mock.Of<ITreatmentFoodService>(),
            Mock.Of<IDeviceService>(),
            Mock.Of<IPatientDeviceStamper>(),
            Mock.Of<IProfileDecomposer>(),
            Mock.Of<IActiveProfileResolver>(),
            Mock.Of<IPatientInsulinRepository>(),
            new AuditContext { IsSystem = true },
            NullLogger<TreatmentDecomposer>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_repoint_failing_on_the_carb_table_rolls_back_the_bolus_table_deleted_before_it()
    {
        var bolus = Guid.CreateVersion7();
        var carb = Guid.CreateVersion7();
        var otherCarb = Guid.CreateVersion7();
        _context.Boluses.Add(new BolusEntity
        {
            Id = bolus, TenantId = TenantId, LegacyId = "t-1", DataSource = Connector, Insulin = 1, Timestamp = At,
        });
        _context.CarbIntakes.AddRange(
            new CarbIntakeEntity { Id = carb, TenantId = TenantId, LegacyId = "t-1", DataSource = Connector, Carbs = 20, Timestamp = At },
            new CarbIntakeEntity { Id = otherCarb, TenantId = TenantId, LegacyId = "t-9", DataSource = Other, Carbs = 20, Timestamp = At });
        var canonical = Guid.CreateVersion7();
        _context.LinkedRecords.AddRange(Link(canonical, carb, Connector, isPrimary: true), Link(canonical, otherCarb, Other, isPrimary: false));
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        _failure.Armed = true;
        var act = () => _decomposer.DeleteFromSourceAsync(Connector, new HashSet<string> { "t-1" });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("injected repoint failure");
        _failure.Armed = false;
        await using var read = _db.CreateContext();
        (await read.Boluses.IgnoreQueryFilters().SingleAsync(b => b.Id == bolus)).DeletedAt.Should().BeNull();
        (await read.CarbIntakes.IgnoreQueryFilters().SingleAsync(c => c.Id == carb)).DeletedAt.Should().BeNull();
        (await read.LinkedRecords.SingleAsync(l => l.IsPrimary)).RecordId.Should().Be(carb);
    }

    [Fact]
    public async Task A_failure_on_the_carb_table_rolls_back_a_legacy_id_delete_of_the_bolus_table_swept_before_it()
    {
        var bolus = Guid.CreateVersion7();
        var carb = Guid.CreateVersion7();
        _context.Boluses.Add(new BolusEntity
        {
            Id = bolus, TenantId = TenantId, LegacyId = "t-2", DataSource = Connector, Insulin = 1, Timestamp = At,
        });
        _context.CarbIntakes.Add(new CarbIntakeEntity
        {
            Id = carb, TenantId = TenantId, LegacyId = "t-2", DataSource = Connector, Carbs = 20, Timestamp = At,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        _failure.FailOn = "UPDATE \"carb_intakes\"";
        var act = () => _decomposer.DeleteByLegacyIdAsync("t-2", WriteOrigin.Live);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("injected write failure");
        _failure.FailOn = null;
        await using var read = _db.CreateContext();
        (await read.Boluses.IgnoreQueryFilters().SingleAsync(b => b.Id == bolus)).DeletedAt.Should().BeNull();
        (await read.CarbIntakes.IgnoreQueryFilters().SingleAsync(c => c.Id == carb)).DeletedAt.Should().BeNull();
    }

    [Fact]
    public async Task A_rolled_back_legacy_id_delete_reaches_the_pipeline_caller_as_an_error_not_as_nothing_deleted()
    {
        _context.Boluses.Add(new BolusEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, LegacyId = "t-3", DataSource = Connector, Insulin = 1, Timestamp = At,
        });
        _context.CarbIntakes.Add(new CarbIntakeEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, LegacyId = "t-3", DataSource = Connector, Carbs = 20, Timestamp = At,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        var services = new ServiceCollection().AddSingleton<IDecomposer<Treatment>>(_decomposer).BuildServiceProvider();
        var pipeline = new DecompositionPipeline(services, NullLogger<DecompositionPipeline>.Instance);

        _failure.FailOn = "UPDATE \"carb_intakes\"";
        var act = () => pipeline.DeleteByLegacyIdAsync<Treatment>("t-3", WriteOrigin.Live);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("injected write failure");
    }

    private static LinkedRecordEntity Link(Guid canonical, Guid recordId, string source, bool isPrimary) => new()
    {
        Id = Guid.CreateVersion7(), TenantId = TenantId, CanonicalId = canonical,
        RecordType = "carbintake", RecordId = recordId,
        SourceTimestamp = new DateTimeOffset(At).ToUnixTimeMilliseconds(),
        DataSource = source, IsPrimary = isPrimary, SysCreatedAt = DateTime.UtcNow,
    };

    /// <summary>
    /// Armed, fails the second <c>UPDATE</c> of <c>linked_records</c>: a repoint's promote, after its
    /// demote. With <see cref="FailOn"/> set, fails the first statement starting with it.
    /// </summary>
    private sealed class FailingLinkPromote : DbCommandInterceptor
    {
        private int _linkUpdates;

        public bool Armed { get; set; }
        public string? FailOn { get; set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Check(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Check(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Check(DbCommand command)
        {
            if (FailOn is { } prefix && command.CommandText.TrimStart().StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidOperationException("injected write failure");
            if (Armed && command.CommandText.TrimStart().StartsWith("UPDATE \"linked_records\"", StringComparison.Ordinal)
                && ++_linkUpdates == 2)
            {
                throw new InvalidOperationException("injected repoint failure");
            }
        }
    }
}
