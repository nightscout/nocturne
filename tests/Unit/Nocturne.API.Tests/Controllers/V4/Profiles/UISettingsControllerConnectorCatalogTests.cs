using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Controllers.V4.Profiles;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Xunit;
using static Nocturne.API.Tests.Controllers.V4.Profiles.UISettingsControllerHarness;

namespace Nocturne.API.Tests.Controllers.V4.Profiles;

/// <summary>
/// Coverage for the connector catalog <see cref="UISettingsController"/> serves on every read. It is
/// build-time metadata, so no tenant may store a snapshot of it — including the tenant whose client
/// reads the whole document and saves it straight back.
/// </summary>
[Trait("Category", "Unit")]
public class UISettingsControllerConnectorCatalogTests
{
    [Fact]
    public async Task SaveUISettings_doesNotPersistTheCatalogTheReadInjected()
    {
        var database = NewDatabase();
        var controller = NewController(database);

        var served = OkValue<UISettingsConfiguration>((await controller.GetUISettings()).Result);
        served.Services.AvailableServices.Add(
            new AvailableService { Id = "dexcom-connector", Name = "Dexcom Share" }
        );

        await controller.SaveUISettings(served);

        var rows = StoredValues(database);
        rows.Should().NotBeEmpty();
        rows.Where(v => v.Contains("availableServices", StringComparison.Ordinal))
            .Should()
            .BeEmpty();
        rows.Where(v => v.Contains("dexcom-connector", StringComparison.Ordinal))
            .Should()
            .BeEmpty();
    }

    [Fact]
    public async Task GetUISettings_servesTheCatalogFromTheRegistryNotTheStoredSection()
    {
        var database = NewDatabase();
        var controller = NewController(database);
        var settings = new UISettingsConfiguration();
        settings.Services.AvailableServices.Add(
            new AvailableService { Id = "retired-connector", Name = "Retired" }
        );
        settings.Services.SyncSettings.AutoSync = false;
        await controller.SaveUISettings(settings);

        var served = OkValue<UISettingsConfiguration>((await controller.GetUISettings()).Result);

        served
            .Services.AvailableServices.Should()
            .BeEquivalentTo(ConnectorMetadataService.GetAvailableServices());
        served.Services.AvailableServices.Should().NotContain(s => s.Id == "retired-connector");
        served.Services.SyncSettings.AutoSync.Should().BeFalse();
    }

    private static List<string> StoredValues(NocturneDbContext database)
    {
        return database
            .Settings.AsNoTracking()
            .Where(s => s.Value != null)
            .Select(s => s.Value!)
            .ToList();
    }
}
