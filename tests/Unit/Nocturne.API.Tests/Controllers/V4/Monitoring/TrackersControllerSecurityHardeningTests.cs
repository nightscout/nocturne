using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Services.Monitoring;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Monitoring;

/// <summary>
/// Pins the two tracker security-hardening invariants:
/// <list type="bullet">
/// <item><description>The tracker read actions are not <c>[AllowAnonymous]</c>, so the fallback
/// authorization policy gates them and a private tenant exposes no tracker to a bare
/// unauthenticated caller (whose permission trie is empty).</description></item>
/// <item><description>A create request that omits visibility defaults to
/// <see cref="TrackerVisibility.Private"/>, and an update that omits visibility keeps the current
/// value — so no request ever makes a tracker Public by omission.</description></item>
/// </list>
/// </summary>
[Trait("Category", "Unit")]
public class TrackersControllerSecurityHardeningTests
{
    private const string OwnerId = "11111111-1111-1111-1111-111111111111";

    private readonly Mock<ITrackerRepository> _repository = new();

    private TrackersController CreateController(string subjectId)
    {
        var controller = new TrackersController(
            _repository.Object,
            Mock.Of<ISignalRBroadcastService>(),
            Mock.Of<ITrackerAlertRuleSyncService>(),
            Mock.Of<ITenantDbContextFactory>(),
            Mock.Of<IAlertAcknowledgementService>(),
            Mock.Of<ILogger<TrackersController>>()
        );

        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = Guid.Parse(subjectId),
        };

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    // ── Part A: the anonymous-read path ─────────────────────────────────────────────────────

    /// <summary>
    /// The read actions must not carry <c>[AllowAnonymous]</c>. <c>[AllowAnonymous]</c> skips the
    /// fallback policy, which is the only thing that rejects a bare unauthenticated request on a
    /// private tenant (empty permission trie). Re-adding it reopens the anonymous read hole, so
    /// this test fails.
    /// </summary>
    [Theory]
    [InlineData(nameof(TrackersController.GetDefinitions))]
    [InlineData(nameof(TrackersController.GetDefinition))]
    [InlineData(nameof(TrackersController.GetActiveInstances))]
    [InlineData(nameof(TrackersController.GetUpcomingInstances))]
    [InlineData(nameof(TrackersController.GetInstanceHistory))]
    public void ReadAction_IsNotAllowAnonymous(string actionName)
    {
        var method = typeof(TrackersController).GetMethod(
            actionName,
            BindingFlags.Public | BindingFlags.Instance);
        method.Should().NotBeNull($"{actionName} must exist on the controller");

        method!.GetCustomAttributes(inherit: true).OfType<IAllowAnonymous>().Should().BeEmpty(
            $"{actionName} must be gated by the fallback policy, not [AllowAnonymous]");
        typeof(TrackersController).GetCustomAttributes(inherit: true).OfType<IAllowAnonymous>()
            .Should().BeEmpty("the controller must not be [AllowAnonymous]");
    }

    // ── Part B: the visibility default ──────────────────────────────────────────────────────

    [Fact]
    public void CreateRequest_DefaultsToPrivate()
    {
        new CreateTrackerDefinitionRequest().Visibility.Should().Be(TrackerVisibility.Private);
    }

    [Fact]
    public async Task CreateDefinition_WithNoVisibilitySpecified_PersistsPrivate()
    {
        TrackerDefinitionEntity? persisted = null;
        _repository
            .Setup(x => x.CreateDefinitionAsync(
                It.IsAny<TrackerDefinitionEntity>(), It.IsAny<CancellationToken>()))
            .Callback<TrackerDefinitionEntity, CancellationToken>((e, _) => persisted = e)
            .ReturnsAsync((TrackerDefinitionEntity e, CancellationToken _) => e);

        var controller = CreateController(OwnerId);

        // A caller who never mentions visibility must not create a Public tracker.
        var result = await controller.CreateDefinition(new CreateTrackerDefinitionRequest
        {
            Name = "Sensor",
        });

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        persisted.Should().NotBeNull();
        persisted!.Visibility.Should().Be(TrackerVisibility.Private);
    }

    [Fact]
    public async Task UpdateDefinition_WithNoVisibilitySpecified_KeepsExistingVisibility()
    {
        var existing = new TrackerDefinitionEntity
        {
            Id = Guid.NewGuid(),
            UserId = OwnerId,
            Name = "Sensor",
            Visibility = TrackerVisibility.Public,
        };

        _repository
            .Setup(x => x.GetDefinitionByIdAsync(existing.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        TrackerDefinitionEntity? persisted = null;
        _repository
            .Setup(x => x.UpdateDefinitionAsync(
                existing.Id, It.IsAny<TrackerDefinitionEntity>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, TrackerDefinitionEntity, CancellationToken>((_, e, _) => persisted = e)
            .ReturnsAsync((Guid _, TrackerDefinitionEntity e, CancellationToken _) => e);

        var controller = CreateController(OwnerId);

        // Omitting visibility is null-means-keep, so an unrelated edit must not privatize (or
        // publicise) the tracker.
        var result = await controller.UpdateDefinition(
            existing.Id,
            new UpdateTrackerDefinitionRequest { Name = "Sensor v2" });

        result.Result.Should().BeOfType<OkObjectResult>();
        persisted.Should().NotBeNull();
        persisted!.Visibility.Should().Be(TrackerVisibility.Public);
    }
}
