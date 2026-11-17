using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Admin;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Identity;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Admin;

/// <summary>
/// The demo delete endpoints purge the demo tenant they find, as that tenant, and answer 404 when
/// there is none. What a purge deletes and reports is <c>DemoDataPurgeParityTests</c>' concern.
/// </summary>
[Trait("Category", "Unit")]
public class DemoAdminPurgeTests
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-0000000000de");

    private readonly Mock<ITenantRepository> _tenants = new();
    private readonly Mock<IDemoDataPurge> _purge = new();

    private DemoAdminController Controller() =>
        new(Mock.Of<ITenantService>(), null!, _tenants.Object, Mock.Of<ITenantMembershipRepository>(), _purge.Object);

    private void DemoTenantExists() =>
        _tenants
            .Setup(t => t.FindDemoTenantAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Record(TenantId));

    private static TenantRecord Record(Guid id) =>
        new(
            id, "demo", "Nocturne Demo", true, true, false, false, null, null, null, null, null, null, null, null,
            null, null, null, DateTime.UtcNow, DateTime.UtcNow, null);

    [Fact]
    public async Task DeleteEntries_PurgesTheDemoTenantAndReturnsTheCount()
    {
        DemoTenantExists();
        _purge
            .Setup(p => p.PurgeEntriesAsync(It.Is<TenantContext>(t => t.TenantId == TenantId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(7);

        var result = await Controller().DeleteEntries(CancellationToken.None);

        ((DemoDeleteResultDto)((OkObjectResult)result).Value!).DeletedCount.Should().Be(7);
    }

    [Fact]
    public async Task DeleteTreatments_PurgesTheDemoTenantAndReturnsTheCount()
    {
        DemoTenantExists();
        _purge
            .Setup(p => p.PurgeTreatmentsAsync(It.Is<TenantContext>(t => t.TenantId == TenantId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(11);

        var result = await Controller().DeleteTreatments(CancellationToken.None);

        ((DemoDeleteResultDto)((OkObjectResult)result).Value!).DeletedCount.Should().Be(11);
    }

    [Fact]
    public async Task WithNoDemoTenant_BothDeletesAnswerNotFoundAndPurgeNothing()
    {
        _tenants
            .Setup(t => t.FindDemoTenantAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((TenantRecord?)null);
        var controller = Controller();

        (await controller.DeleteEntries(CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        (await controller.DeleteTreatments(CancellationToken.None)).Should().BeOfType<NotFoundResult>();

        _purge.VerifyNoOtherCalls();
    }
}
