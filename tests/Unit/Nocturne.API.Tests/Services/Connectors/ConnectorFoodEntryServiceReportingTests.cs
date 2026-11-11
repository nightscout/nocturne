using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Connectors;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Connectors;

/// <summary>
/// A legacy treatment is served with the name of the food it was eaten as, so a re-import that
/// renames or restores a food has to tell the legacy read caches.
/// </summary>
[Trait("Category", "Unit")]
public class ConnectorFoodEntryServiceReportingTests : IDisposable
{
    private const string Connector = "myfitnesspal-connector";

    private readonly NocturneDbContext _db = new(new DbContextOptionsBuilder<NocturneDbContext>()
        .UseInMemoryDatabase($"connector-food-reporting-{Guid.NewGuid():N}").Options)
    {
        TenantId = Guid.CreateVersion7(),
    };
    private readonly RecordingLegacyReads _legacyReads = new();

    public void Dispose() => _db.Dispose();

    private ConnectorFoodEntryService Service() => new(
        new ConnectorFoodEntryRepository(
            _db.AsFactory(), NullLogger<ConnectorFoodEntryRepository>.Instance, _legacyReads),
        Mock.Of<IMealMatchingService>(),
        TimeProvider.System,
        NullLogger<ConnectorFoodEntryService>.Instance);

    private static ConnectorFoodEntryImport Import(string foodName) => new()
    {
        ConnectorSource = Connector,
        ExternalEntryId = "entry-1",
        ExternalFoodId = "food-1",
        ConsumedAt = DateTimeOffset.UtcNow,
        Food = new ConnectorFoodImport { ExternalId = "food-1", Name = foodName, Carbs = 10 },
    };

    [Fact]
    public async Task ARenamedFood_ReportsFoods()
    {
        await Service().ImportAsync(null, [Import("apple")]);
        _legacyReads.Models.Should().BeEmpty("a new food has no treatment pointing at it yet");

        await Service().ImportAsync(null, [Import("pear")]);

        _legacyReads.Models.Should().Equal(typeof(Food));
    }

    [Fact]
    public async Task AFoodRestoredAfterASystemDelete_ReportsFoods()
    {
        await Service().ImportAsync(null, [Import("apple")]);
        var food = await _db.Foods.SingleAsync();
        food.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await Service().ImportAsync(null, [Import("apple")]);

        _legacyReads.Models.Should().Equal(typeof(Food));
    }

    [Fact]
    public async Task AReimportThatChangesNoFood_ReportsNothing()
    {
        await Service().ImportAsync(null, [Import("apple")]);

        await Service().ImportAsync(null, [Import("apple")]);

        _legacyReads.Models.Should().BeEmpty();
    }
}
