using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Controllers.V4.Profiles;
using Nocturne.API.Services.ProfileActivations;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Profiles;

[Trait("Category", "Unit")]
public class ProfileActivationsControllerWindowTests
{
    private static readonly DateTimeOffset Now = new(2031, 5, 6, 7, 8, 9, TimeSpan.Zero);

    private readonly Mock<IProfileActivationRepository> _activations = new();
    private readonly FakeTimeProvider _clock = new(Now);

    private ProfileActivationsController NewController() =>
        new(_activations.Object, Mock.Of<IProfileActivationResolver>(), null!, _clock);

    [Fact]
    public async Task The_default_window_ends_now_and_starts_thirty_days_earlier()
    {
        _activations
            .Setup(r => r.GetOverlappingAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await NewController().GetActivations();

        _activations.Verify(r => r.GetOverlappingAsync(
            Now.UtcDateTime.AddDays(-30), Now.UtcDateTime, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task An_omitted_start_is_thirty_days_before_the_requested_end()
    {
        var to = new DateTime(2030, 1, 31, 0, 0, 0, DateTimeKind.Utc);
        _activations
            .Setup(r => r.GetOverlappingAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await NewController().GetActivations(to: to);

        _activations.Verify(r => r.GetOverlappingAsync(
            to.AddDays(-30), to, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
