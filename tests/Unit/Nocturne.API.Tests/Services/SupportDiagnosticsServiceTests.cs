using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services;
using Nocturne.API.Services.Connectors;
using Nocturne.API.Services.Profiles;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Services;

/// <summary>
/// The snapshot is read by support as a statement of how the tenant is configured, so a field it
/// could not read has to arrive empty rather than carrying the default nobody chose.
/// </summary>
[Trait("Category", "Unit")]
public class SupportDiagnosticsServiceTests
{
    private static readonly Guid TenantId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    [Fact]
    public async Task GetAsync_leavesTheTenantConfigurationBlankWhenTheSettingsReadFails()
    {
        var connectorHealth = new Mock<IConnectorHealthService>();
        connectorHealth
            .Setup(c => c.GetConnectorStatusesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var factory = new Mock<ITenantDbContextFactory>();
        factory
            .Setup(f => f.CreateAsync(It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.FromResult(NewContext()));

        var broken = NewContext();
        var uiSettings = new UISettingsService(broken, NullLogger<UISettingsService>.Instance);
        await broken.DisposeAsync();

        var sut = new SupportDiagnosticsService(
            connectorHealth.Object,
            uiSettings,
            Mock.Of<ITherapySettingsResolver>(),
            factory.Object,
            NullLogger<SupportDiagnosticsService>.Instance
        );

        var snapshot = await sut.GetAsync();

        snapshot.GlucoseUnits.Should().BeNull();
        snapshot.TimeFormat.Should().BeNull();
        snapshot.DataSourcePriority.Should().BeNull();
    }

    private static NocturneDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new NocturneDbContext(options) { TenantId = TenantId };
    }
}
