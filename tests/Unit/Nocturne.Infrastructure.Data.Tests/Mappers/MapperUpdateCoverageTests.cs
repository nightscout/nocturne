using FluentAssertions;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Mappers;

/// <summary>
/// The create paths upsert through <c>UpdateEntity</c>, so a field it forgets is a field a re-post
/// silently fails to change. Each mapper's update is held to its own <c>ToEntity</c>.
/// </summary>
[Trait("Category", "Unit")]
public class MapperUpdateCoverageTests
{
    [Fact]
    public void FoodMapper_UpdateEntity_SetsEveryFieldToEntityDoes_ExceptTheIdentityLinkAndBookkeepingColumnsAStoredRowKeeps()
    {
        var food = new Food
        {
            Id = "5f8d0d55b54764421b7156c3",
            Type = "quickpick",
            Category = "Breakfast",
            Subcategory = "Cereal",
            Name = "Muesli",
            Portion = 45.5,
            Carbs = 31.25,
            Fat = 7.5,
            Protein = 4.25,
            Energy = 880,
            Gi = 1,
            Unit = "g",
            Foods = [new QuickPickFood { Name = "Oats", Portion = 30, Carbs = 20 }],
            HideAfterUse = true,
            Hidden = true,
            Position = 7,
        };

        var updated = new FoodEntity();
        FoodMapper.UpdateEntity(updated, food);

        updated.Should().BeEquivalentTo(FoodMapper.ToEntity(food), opts => opts
            .Excluding(e => e.Id)
            .Excluding(e => e.TenantId)
            .Excluding(e => e.OriginalId)
            .Excluding(e => e.ExternalSource)
            .Excluding(e => e.ExternalId)
            .Excluding(e => e.SysCreatedAt)
            .Excluding(e => e.SysUpdatedAt)
            .Excluding(e => e.AdditionalPropertiesJson));
    }

    [Fact]
    public void SettingsMapper_UpdateEntity_SetsEveryFieldToEntityDoes_ExceptTheIdentityAndBookkeepingColumnsAStoredRowKeeps()
    {
        var settings = new Settings
        {
            Id = "5f8d0d55b54764421b7156c3",
            Key = "displayUnits",
            Value = "mg/dl",
            CreatedAt = "2026-06-10T08:00:00.000Z",
            Mills = 1_780_000_000_000,
            UtcOffset = 600,
            SrvCreated = new DateTimeOffset(2026, 6, 10, 8, 0, 0, TimeSpan.Zero),
            SrvModified = new DateTimeOffset(2026, 6, 11, 8, 0, 0, TimeSpan.Zero),
            App = "nocturne",
            Device = "phone",
            EnteredBy = "tester",
            Version = 3,
            IsActive = false,
            Notes = "a note",
        };

        var updated = new SettingsEntity();
        SettingsMapper.UpdateEntity(updated, settings);

        updated.Should().BeEquivalentTo(SettingsMapper.ToEntity(settings), opts => opts
            .Excluding(e => e.Id)
            .Excluding(e => e.TenantId)
            .Excluding(e => e.OriginalId)
            .Excluding(e => e.SysCreatedAt)
            .Excluding(e => e.SysUpdatedAt)
            .Excluding(e => e.AdditionalPropertiesJson));
    }
}
