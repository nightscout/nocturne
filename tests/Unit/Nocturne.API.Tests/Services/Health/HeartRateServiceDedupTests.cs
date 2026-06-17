using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Health;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Xunit;

namespace Nocturne.API.Tests.Services.Health;

/// <summary>
/// HeartRate counterpart of <see cref="StepCountServiceDedupTests"/> — the sync-identifier
/// upsert lives in the shared base, but this locks in that HeartRate's own service wiring
/// participates in it.
/// </summary>
public class HeartRateServiceDedupTests
{
    private static (HeartRateService service, NocturneDbContext context) CreateService()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var context = new NocturneDbContext(options) { TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222") };

        var processing = new Mock<IDocumentProcessingService>();
        processing
            .Setup(p => p.ProcessDocuments(It.IsAny<IEnumerable<HeartRate>>()))
            .Returns((IEnumerable<HeartRate> docs) => docs);

        var service = new HeartRateService(
            context,
            processing.Object,
            Mock.Of<ISignalRBroadcastService>(),
            NullLogger<HeartRateService>.Instance);

        return (service, context);
    }

    private static HeartRate Bucket(string syncId, int bpm) => new()
    {
        Timestamp = new DateTime(2026, 6, 16, 12, 0, 0, DateTimeKind.Utc),
        Bpm = bpm,
        DataSource = "prelude",
        SyncIdentifier = syncId,
    };

    [Fact]
    public async Task Repeated_upload_of_same_sync_id_updates_in_place()
    {
        var (service, context) = CreateService();

        await service.CreateHeartRatesAsync([Bucket("prelude-hr-5m-1000", 60)]);
        await service.CreateHeartRatesAsync([Bucket("prelude-hr-5m-1000", 72)]);

        var rows = await context.HeartRates.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle();
        rows[0].Bpm.Should().Be(72);
    }

    [Fact]
    public async Task Distinct_sync_ids_insert_separate_rows()
    {
        var (service, context) = CreateService();

        await service.CreateHeartRatesAsync([Bucket("a", 60), Bucket("b", 80)]);

        (await context.HeartRates.AsNoTracking().ToListAsync()).Should().HaveCount(2);
    }
}
