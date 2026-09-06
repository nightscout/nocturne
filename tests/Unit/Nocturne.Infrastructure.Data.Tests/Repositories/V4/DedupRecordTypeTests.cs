using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

/// <summary>
/// Holds each dedup participant's read-visibility record type equal to the one it hands
/// <c>DeduplicateBatchAsync</c>; a mismatch hides another type's duplicates and shows its own.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class DedupRecordTypeTests : IDisposable
{
    private readonly NocturneDbContext _context;
    private readonly TestTenantDbContextFactory _factory;
    private readonly IDeduplicationService _dedup = new Mock<IDeduplicationService>().Object;
    private readonly IAuditContext _audit = new Mock<IAuditContext>().Object;

    public DedupRecordTypeTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext();
        _context.TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        _factory = new TestTenantDbContextFactory(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(RecordType.SensorGlucose)]
    [InlineData(RecordType.Bolus)]
    [InlineData(RecordType.BolusCalculation)]
    [InlineData(RecordType.CarbIntake)]
    [InlineData(RecordType.BGCheck)]
    [InlineData(RecordType.DeviceEvent)]
    [InlineData(RecordType.Note)]
    public void Participant_declares_the_record_type_it_deduplicates(RecordType recordType)
    {
        Declared(recordType).Should().Be(recordType);
    }

    private RecordType? Declared(RecordType recordType) => recordType switch
    {
        RecordType.SensorGlucose => new SensorGlucoseRepository(
            _factory, _dedup, _audit, NullLogger<SensorGlucoseRepository>.Instance).DedupRecordType,
        RecordType.Bolus => new BolusRepository(
            _factory, _dedup, _audit, NullLogger<BolusRepository>.Instance).DedupRecordType,
        RecordType.BolusCalculation => new BolusCalculationRepository(
            _factory, _dedup, _audit, NullLogger<BolusCalculationRepository>.Instance).DedupRecordType,
        RecordType.CarbIntake => new CarbIntakeRepository(
            _factory, _dedup, _audit, NullLogger<CarbIntakeRepository>.Instance).DedupRecordType,
        RecordType.BGCheck => new BGCheckRepository(
            _factory, _dedup, _audit, NullLogger<BGCheckRepository>.Instance).DedupRecordType,
        RecordType.DeviceEvent => new DeviceEventRepository(
            _factory, _dedup, _audit, NullLogger<DeviceEventRepository>.Instance).DedupRecordType,
        RecordType.Note => new NoteRepository(
            _factory, _dedup, _audit, NullLogger<NoteRepository>.Instance).DedupRecordType,
        _ => throw new ArgumentOutOfRangeException(nameof(recordType)),
    };
}
