using FluentAssertions;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Monitoring;

public class TrackerRepositoryPresetTests
{
    [Fact]
    public async Task A_created_preset_carries_the_name_of_its_definition()
    {
        var db = TestDbContextFactory.CreateInMemoryContext($"tracker_presets_{Guid.NewGuid()}");
        db.TenantId = Guid.CreateVersion7();
        db.Tenants.Add(new TenantEntity { Id = db.TenantId, Slug = "own", DisplayName = "Own" });
        var definition = new TrackerDefinitionEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = db.TenantId,
            Name = "G7 Sensor",
        };
        db.TrackerDefinitions.Add(definition);
        await db.SaveChangesAsync();
        var repository = new TrackerRepository(new TestNocturneDbContextFactory(db));

        var created = await repository.CreatePresetAsync(
            new TrackerPreset { CreatedBy = "user", Name = "Weekend", DefinitionId = definition.Id });

        created.Definition.Should().NotBeNull();
        created.Definition!.Name.Should().Be("G7 Sensor");
        TrackerPresetDto.From(created).DefinitionName.Should().Be("G7 Sensor");
    }
}
