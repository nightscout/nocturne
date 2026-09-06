using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class TempBasalRepositorySyncUpsertTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly NocturneDbContext _context;
    private readonly TempBasalRepository _repository;

    public TempBasalRepositorySyncUpsertTests()
    {
        var dbName = $"tempbasal_sync_upsert_tests_{Guid.NewGuid()}";
        _context = TestDbContextFactory.CreateInMemoryContext(dbName);
        _context.TenantId = TenantA;
        _repository = new TempBasalRepository(
            new TestTenantDbContextFactory(_context),
            new Mock<IDeduplicationService>().Object,
            new Mock<IAuditContext>().Object,
            NullLogger<TempBasalRepository>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private static TempBasal CreateRecord(string? syncIdentifier = null, double rate = 1.0, DateTime? end = null) =>
        new()
        {
            StartTimestamp = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            EndTimestamp = end,
            UtcOffset = 0,
            Rate = rate,
            Origin = TempBasalOrigin.Algorithm,
            DataSource = "trio",
            SyncIdentifier = syncIdentifier,
        };

    [Fact]
    public async Task CreateAsync_WithMatchingSyncKey_UpdatesInPlace()
    {
        await _repository.CreateAsync(CreateRecord("sync-1", rate: 1.0), WriteOrigin.Live);

        var retry = CreateRecord("sync-1", rate: 2.5);
        var result = await _repository.CreateAsync(retry, WriteOrigin.Live);

        _context.TempBasals.Count().Should().Be(1);
        _context.TempBasals.Single().Rate.Should().Be(2.5);
        result.Rate.Should().Be(2.5);
    }

    [Fact]
    public async Task CreateAsync_WithMatchingSyncKey_KeepsStoredPatientDeviceIdWhenRetryCarriesNone()
    {
        var patientDeviceId = Guid.CreateVersion7();
        var stamped = CreateRecord("sync-1");
        stamped.PatientDeviceId = patientDeviceId;
        await _repository.CreateAsync(stamped, WriteOrigin.Live);

        var result = await _repository.CreateAsync(CreateRecord("sync-1", rate: 2.5), WriteOrigin.Live);

        _context.TempBasals.Single().PatientDeviceId.Should().Be(patientDeviceId);
        result.PatientDeviceId.Should().Be(patientDeviceId);
    }

    [Fact]
    public async Task CreateAsync_WithMatchingSyncKey_TakesRequestedPatientDeviceIdOverStoredOne()
    {
        var stamped = CreateRecord("sync-1");
        stamped.PatientDeviceId = Guid.CreateVersion7();
        await _repository.CreateAsync(stamped, WriteOrigin.Live);

        var restamped = CreateRecord("sync-1");
        restamped.PatientDeviceId = Guid.CreateVersion7();
        var result = await _repository.CreateAsync(restamped, WriteOrigin.Live);

        _context.TempBasals.Single().PatientDeviceId.Should().Be(restamped.PatientDeviceId);
        result.PatientDeviceId.Should().Be(restamped.PatientDeviceId);
    }

    [Fact]
    public async Task CreateAsync_WithMatchingSyncKey_KeepsStoredLinksAndImportedIdentityWhenRetryCarriesNone()
    {
        var seed = CreateRecord("sync-1");
        seed.DeviceId = Guid.CreateVersion7();
        seed.LegacyId = "legacy-1";
        seed.InsulinContext = new TreatmentInsulinContext { InsulinName = "Fiasp", Dia = 6, Peak = 55 };
        seed.AdditionalProperties = new Dictionary<string, object?> { ["pumpMode"] = "closedLoop" };
        await _repository.CreateAsync(seed, WriteOrigin.Live);

        var result = await _repository.CreateAsync(CreateRecord("sync-1", rate: 2.5), WriteOrigin.Live);

        var stored = _context.TempBasals.Single();
        stored.DeviceId.Should().Be(seed.DeviceId);
        stored.LegacyId.Should().Be("legacy-1");
        stored.AdditionalPropertiesJson.Should().Contain("closedLoop");
        result.InsulinContext!.InsulinName.Should().Be("Fiasp");
    }

    [Fact]
    public async Task CreateAsync_WithMatchingSyncKey_ClearsFieldsTheRequestCanCarry()
    {
        var seed = CreateRecord("sync-1", end: new DateTime(2026, 5, 1, 12, 30, 0, DateTimeKind.Utc));
        seed.ScheduledRate = 0.8;
        seed.PumpRecordId = "pump-1";
        seed.CorrelationId = Guid.CreateVersion7();
        await _repository.CreateAsync(seed, WriteOrigin.Live);

        await _repository.CreateAsync(CreateRecord("sync-1", rate: 2.5), WriteOrigin.Live);

        var stored = _context.TempBasals.Single();
        stored.EndTimestamp.Should().BeNull();
        stored.ScheduledRate.Should().BeNull();
        stored.PumpRecordId.Should().BeNull();
        stored.CorrelationId.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_WithDifferentSyncKey_Inserts()
    {
        await _repository.CreateAsync(CreateRecord("sync-1"), WriteOrigin.Live);
        await _repository.CreateAsync(CreateRecord("sync-2"), WriteOrigin.Live);

        _context.TempBasals.Count().Should().Be(2);
    }

    [Fact]
    public async Task CreateAsync_WithoutSyncKey_AlwaysInserts()
    {
        await _repository.CreateAsync(CreateRecord(syncIdentifier: null), WriteOrigin.Live);
        await _repository.CreateAsync(CreateRecord(syncIdentifier: null), WriteOrigin.Live);

        _context.TempBasals.Count().Should().Be(2);
    }
}
