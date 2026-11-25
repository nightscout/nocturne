using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>A <see cref="TeamStaffAnnouncer"/> for a test of a service that recounts after an owner change or a deletion and has no team to find.</summary>
internal static class InertTeamStaff
{
    public static TeamStaffAnnouncer Announcer() => Announcer(new Mock<ITeamRepository>());

    /// <summary>The announcer over <paramref name="teams"/>, which a test can verify a recount read.</summary>
    public static TeamStaffAnnouncer Announcer(Mock<ITeamRepository> teams)
    {
        teams.Setup(t => t.ListAccessOfTenantAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        teams.Setup(t => t.ListStaffSitesOfSubjectAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        return new TeamStaffAnnouncer(
            teams.Object, Mock.Of<ITenantMembershipRepository>(),
            Sites(),
            TimeProvider.System, NullLogger<TeamStaffAnnouncer>.Instance);
    }

    /// <summary>Site notices that find no site, so nothing is filed or broadcast.</summary>
    public static SiteNotices Sites() =>
        new(Mock.Of<ITenantRepository>(), new ServiceCollection().BuildServiceProvider());
}
