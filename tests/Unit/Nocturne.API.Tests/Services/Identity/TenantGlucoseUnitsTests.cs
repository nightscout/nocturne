using FluentAssertions;
using Moq;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Configuration;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// The tenant's units are the owner's saved display units, else the default chosen in onboarding,
/// else mg/dL.
/// </summary>
[Trait("Category", "Unit")]
public class TenantGlucoseUnitsTests
{
    private readonly Mock<ITenantAppearanceReader> _appearance = new();
    private readonly Mock<ITenantService> _tenants = new();
    private ITenantAccessor _accessor = MockTenantAccessor.Create().Object;

    private TenantGlucoseUnits Units(string? appearanceUnits, string? setupDefault)
    {
        _appearance
            .Setup(a => a.GetTenantAppearanceAsync(MockTenantAccessor.DefaultTenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserDisplayPreferences { GlucoseUnits = appearanceUnits });
        _tenants
            .Setup(t => t.GetDefaultGlucoseUnitsAsync(MockTenantAccessor.DefaultTenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(setupDefault);
        return new TenantGlucoseUnits(_accessor, _appearance.Object, _tenants.Object);
    }

    [Theory]
    [InlineData("mmol", null, "mmol", true)]
    [InlineData("mmol", "mg/dl", "mmol", true)]
    [InlineData("mg/dl", "mmol", "mg/dl", false)]
    [InlineData(null, "mmol", "mmol", true)]
    [InlineData(null, "mg/dl", "mg/dl", false)]
    [InlineData(null, null, GlucoseUnitDefaults.MgDl, false)]
    public async Task Resolves_AppearanceThenSetupDefaultThenMgDl(
        string? appearanceUnits, string? setupDefault, string expected, bool mmol)
    {
        var units = Units(appearanceUnits, setupDefault);

        (await units.ResolveAsync()).Should().Be(expected);
        (await units.UsesMmolAsync()).Should().Be(mmol);
    }

    [Fact]
    public async Task WithNoResolvedTenant_FallsBackToMgDlWithoutReadingASetupDefault()
    {
        var accessor = new Mock<ITenantAccessor>();
        accessor.SetupGet(a => a.TenantId).Returns(Guid.Empty);
        _accessor = accessor.Object;
        _appearance
            .Setup(a => a.GetTenantAppearanceAsync(Guid.Empty, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserDisplayPreferences());

        var units = new TenantGlucoseUnits(_accessor, _appearance.Object, _tenants.Object);

        (await units.ResolveAsync()).Should().Be(GlucoseUnitDefaults.MgDl);
        _tenants.Verify(
            t => t.GetDefaultGlucoseUnitsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
