using FluentAssertions;
using Nocturne.API.Controllers.V4.Platform;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Platform;

/// <summary>
/// System events are the alarms and notices a device raised, so writing one is a devices write and
/// needs <c>devices.readwrite</c>, as reading one needs <c>devices.read</c>.
/// </summary>
public sealed class SystemEventsControllerScopeTests
{
    private static readonly AuthContext Uploader = new() { IsAuthenticated = true, AuthType = AuthType.DirectGrant };

    [Theory]
    [InlineData(nameof(SystemEventsController.CreateSystemEvent))]
    [InlineData(nameof(SystemEventsController.DeleteSystemEvent))]
    public void A_caller_without_devices_readwrite_is_forbidden_before_the_handler(string action)
    {
        ActionGate.Evaluate(
                typeof(SystemEventsController),
                action,
                new HashSet<string> { Scope.DevicesRead, Scope.GlucoseReadWrite, Scope.TreatmentsReadWrite },
                Uploader)
            .ShouldBeScopeRefusal(Scope.DevicesReadWrite);

        ActionGate.Evaluate(
                typeof(SystemEventsController), action, new HashSet<string> { Scope.DevicesReadWrite }, Uploader)
            .Should().BeNull("a caller holding devices.readwrite reaches the handler");
    }
}
