using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// An alert or tracker service type is registered once. A second registration wins a single resolve
/// without a sound, and is returned beside the first by <c>IEnumerable&lt;T&gt;</c>, so it has to be
/// a decision listed here with its reason.
/// </summary>
[Trait("Category", "Integration")]
public class AlertRegistrationGuardTests(ServiceProviderValidationTests.MigratedDatabase fixture)
    : IClassFixture<ServiceProviderValidationTests.MigratedDatabase>
{
    /// <summary>Service types that several registrations share on purpose, each implementation distinct.</summary>
    private static readonly Dictionary<Type, string> SharedByDesign = new()
    {
        [typeof(Nocturne.Core.Contracts.Jobs.IScheduledJob)] =
            "one registration per job; the scheduler indexes them by name",
        [typeof(Nocturne.Core.Contracts.SetupHub.ISetupHubItem)] =
            "one registration per setup hub item; the hub orders them by key",
        [typeof(Nocturne.Core.Contracts.Notifications.INotificationActionHandler)] =
            "one registration per handler; notifications dispatch by handler notification type",
        [typeof(Nocturne.API.Reports.IReportDataset)] =
            "one registration per report dataset",
        [typeof(Microsoft.Extensions.Hosting.IHostedService)] =
            "one registration per hosted service, started in registration order",
        [typeof(Nocturne.Core.Contracts.Events.IRecordChangeSubscriber)] =
            "one registration per subscriber; the publisher runs them by stage",
    };

    private static bool IsAlertOrTracker(ServiceDescriptor descriptor) =>
        new[] { descriptor.ServiceType, descriptor.ImplementationType }
            .Any(type => type?.FullName is { } name
                && name.StartsWith("Nocturne.", StringComparison.Ordinal)
                && (name.Contains("Alert", StringComparison.Ordinal)
                    || name.Contains("Tracker", StringComparison.Ordinal)));

    [Fact]
    public async Task NoAlertOrTrackerServiceTypeIsRegisteredTwiceWithoutAReason()
    {
        var captured = await RegisteredServices.CaptureAsync(fixture);

        var registrations = captured.Where(d => !d.IsKeyedService).ToList();
        var alertTypes = registrations.Where(IsAlertOrTracker).Select(d => d.ServiceType).Distinct().ToList();
        alertTypes.Should().NotBeEmpty();

        var repeated = alertTypes
            .Where(type => registrations.Count(d => d.ServiceType == type) > 1)
            .ToList();

        repeated.Where(type => !SharedByDesign.ContainsKey(type)).Should().BeEmpty(
            "a service type registered twice needs a reason in SharedByDesign");
        SharedByDesign.Keys.Where(type => !repeated.Contains(type)).Should().BeEmpty(
            "a reason for a type that is no longer shared is stale");

        foreach (var type in repeated)
        {
            var implementations = registrations
                .Where(d => d.ServiceType == type && d.ImplementationType is not null)
                .Select(d => d.ImplementationType)
                .ToList();
            implementations.Should().OnlyHaveUniqueItems(
                $"{type.Name} is shared by design, but never by two registrations of one implementation");
        }
    }
}
