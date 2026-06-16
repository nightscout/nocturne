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
/// BodyWeight dedup — distinct from StepCount/HeartRate because BodyWeight is mills-based
/// (not Timestamp) and its <c>syncIdentifier</c> flows through the model POST + the
/// <c>[NocturneOnly]</c> attribute rather than an Upsert request DTO.
/// </summary>
public class BodyWeightServiceDedupTests
{
    private static (BodyWeightService service, NocturneDbContext context) CreateService()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var context = new NocturneDbContext(options) { TenantId = Guid.Parse("33333333-3333-3333-3333-333333333333") };

        var processing = new Mock<IDocumentProcessingService>();
        processing
            .Setup(p => p.ProcessDocuments(It.IsAny<IEnumerable<BodyWeight>>()))
            .Returns((IEnumerable<BodyWeight> docs) => docs);

        var service = new BodyWeightService(
            context,
            processing.Object,
            Mock.Of<ISignalRBroadcastService>(),
            NullLogger<BodyWeightService>.Instance);

        return (service, context);
    }

    private static BodyWeight Reading(string syncId, decimal weightKg) => new()
    {
        Mills = 1_700_000_000_000,
        WeightKg = weightKg,
        DataSource = "prelude",
        SyncIdentifier = syncId,
    };

    [Fact]
    public async Task Repeated_upload_of_same_sync_id_updates_in_place()
    {
        var (service, context) = CreateService();

        await service.CreateBodyWeightsAsync([Reading("prelude-weight-abc", 80.5m)]);
        await service.CreateBodyWeightsAsync([Reading("prelude-weight-abc", 81.2m)]);

        var rows = await context.BodyWeights.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle();
        rows[0].WeightKg.Should().Be(81.2m);
    }

    [Fact]
    public async Task Distinct_sync_ids_insert_separate_rows()
    {
        var (service, context) = CreateService();

        await service.CreateBodyWeightsAsync([Reading("w1", 80m), Reading("w2", 81m)]);

        (await context.BodyWeights.AsNoTracking().ToListAsync()).Should().HaveCount(2);
    }
}
