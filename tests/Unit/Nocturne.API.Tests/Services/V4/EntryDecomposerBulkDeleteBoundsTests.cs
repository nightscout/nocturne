using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services.V4;

[Trait("Category", "Unit")]
public class EntryDecomposerBulkDeleteBoundsTests : IDisposable
{
    private const long Bound = 1_780_000_000_000;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly SqliteTestDatabase _db;
    private readonly Nocturne.Infrastructure.Data.NocturneDbContext _context;

    public EntryDecomposerBulkDeleteBoundsTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId);
        _context = _db.CreateContext();
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private static DateTime At(long mills) => DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime;

    private EntryDecomposer CreateDecomposer()
    {
        var factory = new TestNocturneDbContextFactory(_context);
        var audit = Mock.Of<IAuditContext>();
        return new EntryDecomposer(
            new SensorGlucoseRepository(factory, Mock.Of<IDeduplicationService>(), audit, NullLogger<SensorGlucoseRepository>.Instance),
            new MeterGlucoseRepository(factory, audit, NullLogger<MeterGlucoseRepository>.Instance),
            new CalibrationRepository(factory, audit, NullLogger<CalibrationRepository>.Instance),
            Mock.Of<IGlucoseProcessingResolver>(),
            Mock.Of<IPatientDeviceStamper>(),
            audit,
            NullLogger<EntryDecomposer>.Instance);
    }

    private void SeedAround(long mills)
    {
        foreach (var offset in new long[] { -1, 0, 1 })
        {
            var timestamp = At(mills + offset);
            _context.SensorGlucose.Add(new SensorGlucoseEntity { Id = Guid.CreateVersion7(), TenantId = TenantId, Timestamp = timestamp, Mgdl = 120 });
            _context.MeterGlucose.Add(new MeterGlucoseEntity { Id = Guid.CreateVersion7(), TenantId = TenantId, Timestamp = timestamp, Mgdl = 120 });
            _context.Calibrations.Add(new CalibrationEntity { Id = Guid.CreateVersion7(), TenantId = TenantId, Timestamp = timestamp, Slope = 1.0 });
        }

        _context.SaveChanges();
    }

    private async Task<long[]> RemainingOffsetsAsync(long mills)
    {
        await using var verify = _db.CreateContext();
        var sg = await verify.SensorGlucose.Select(e => e.Timestamp).ToListAsync();
        var mg = await verify.MeterGlucose.Select(e => e.Timestamp).ToListAsync();
        var cal = await verify.Calibrations.Select(e => e.Timestamp).ToListAsync();
        return sg.Concat(mg).Concat(cal)
            .Select(t => new DateTimeOffset(t, TimeSpan.Zero).ToUnixTimeMilliseconds() - mills)
            .Order()
            .ToArray();
    }

    [Theory]
    [InlineData("$lte", "1,1,1")]
    [InlineData("$lt", "0,0,0,1,1,1")]
    [InlineData("$eq", "-1,-1,-1,1,1,1")]
    public async Task BulkDeleteAsync_DeletesExactlyTheEntriesTheDateFilterMatches(string op, string remainingOffsets)
    {
        SeedAround(Bound);
        var find = op == "$eq" ? $"find[date]={Bound}" : $"find[date][{op}]={Bound}";
        var expectedRemaining = remainingOffsets.Split(',').Select(long.Parse).ToArray();

        await CreateDecomposer().BulkDeleteAsync(find, WriteOrigin.Live);

        var remaining = await RemainingOffsetsAsync(Bound);
        remaining.Should().Equal(expectedRemaining);
    }
}
