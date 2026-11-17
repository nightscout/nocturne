using FluentAssertions;
using Moq;
using Nocturne.API.Hubs;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts;
using Nocturne.Core.Contracts.DeviceStatuses;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Queries;
using Xunit;

namespace Nocturne.API.Tests.Hubs;

/// <summary>
/// <see cref="DataHub.LoadRetro"/> reads every collection from the 48 hours before
/// <c>loadedMills</c>, and caps the device statuses it sends.
/// </summary>
[Trait("Category", "Unit")]
public class DataHubRetroWindowTests
{
    private static readonly long FortyEightHours = (long)TimeSpan.FromHours(48).TotalMilliseconds;

    private sealed record DeviceStatusRead(FindQuery Find, int Count, int Skip, bool Descending);

    private static async Task<(string? EntriesFind, string? TreatmentsFind, DeviceStatusRead? DeviceStatuses)>
        LoadRetroAsync(long loadedMills, params string[] scopes)
    {
        string? entriesFind = null;
        string? treatmentsFind = null;
        DeviceStatusRead? deviceStatusRead = null;

        var entryService = new Mock<IEntryService>();
        entryService
            .Setup(s => s.GetEntriesAsync(
                It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback((string? find, int? _, int? _, CancellationToken _) => entriesFind = find)
            .ReturnsAsync([]);
        var treatmentService = new Mock<ITreatmentService>();
        treatmentService
            .Setup(s => s.GetTreatmentsAsync(
                It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback((string? find, int? _, int? _, CancellationToken _) => treatmentsFind = find)
            .ReturnsAsync([]);
        var deviceStatusService = new Mock<IDeviceStatusService>();
        deviceStatusService
            .Setup(s => s.GetDeviceStatusesAsync(
                It.IsAny<FindQuery>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>(),
                It.IsAny<bool>(), It.IsAny<ReadSortField>()))
            .Callback((FindQuery find, int count, int skip, CancellationToken _, bool descending, ReadSortField _) =>
                deviceStatusRead = new DeviceStatusRead(find, count, skip, descending))
            .ReturnsAsync([]);

        var (hub, _) = DataHubTestHarness.CreateHub(
            entryService.Object,
            treatmentService.Object,
            deviceStatusService.Object,
            Mock.Of<IHubTokenAuthorizer>());
        HubAuthorizationState.Grant(
            hub.Context,
            new HubAuthorization(
                DataHubTestHarness.Tenant,
                Scope.Normalize(scopes),
                HubCredentialKind.Subject,
                DataHubTestHarness.Subject,
                HistoryClamped: false));

        await hub.LoadRetro(new RetroLoadRequest { LoadedMills = loadedMills });

        return (entriesFind, treatmentsFind, deviceStatusRead);
    }

    [Fact]
    public async Task Device_statuses_are_read_from_the_48_hours_before_loaded_mills()
    {
        var loadedMills = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds();

        var (_, _, deviceStatuses) = await LoadRetroAsync(
            loadedMills, Scope.GlucoseRead, Scope.DevicesRead);

        deviceStatuses.Should().NotBeNull();
        deviceStatuses!.Find.FromMills.Should().Be(loadedMills - FortyEightHours);
        deviceStatuses.Find.ToMills.Should().Be(loadedMills - 1);
        deviceStatuses.Skip.Should().Be(0);
    }

    [Fact]
    public async Task Device_statuses_are_capped_at_what_a_48_hour_window_holds()
    {
        var (_, _, deviceStatuses) = await LoadRetroAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Scope.GlucoseRead, Scope.DevicesRead);

        var statusesPerFiveMinutes = FortyEightHours / TimeSpan.FromMinutes(5).TotalMilliseconds;
        deviceStatuses.Should().NotBeNull();
        deviceStatuses!.Count.Should().BeInRange((int)statusesPerFiveMinutes, 1000 - 1);
        deviceStatuses.Descending.Should().BeTrue("the cap must drop the oldest statuses of the window");
    }

    [Fact]
    public async Task Device_statuses_share_the_window_of_entries_and_treatments()
    {
        var loadedMills = DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeMilliseconds();

        var (entriesFind, treatmentsFind, deviceStatuses) = await LoadRetroAsync(
            loadedMills, Scope.GlucoseRead, Scope.TreatmentsRead, Scope.DevicesRead);

        var window = FindQuery.ParseDeviceStatus(entriesFind);
        treatmentsFind.Should().Be(entriesFind);
        deviceStatuses.Should().NotBeNull();
        deviceStatuses!.Find.FromMills.Should().Be(window.FromMills);
        deviceStatuses.Find.ToMills.Should().Be(window.ToMills);
    }

    [Fact]
    public async Task Device_statuses_are_not_read_without_the_devices_scope()
    {
        var (_, _, deviceStatuses) = await LoadRetroAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Scope.GlucoseRead);

        deviceStatuses.Should().BeNull();
    }
}
