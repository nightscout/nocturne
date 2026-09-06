using FluentValidation.TestHelper;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Validators.Alerts;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Validators.Alerts;

/// <summary>
/// Pins the request-boundary rejection of RoleRestricted tracker visibility, which has no view
/// rule behind it and would hide the tracker from everyone but its owner and admins.
/// </summary>
[Trait("Category", "Unit")]
public class TrackerDefinitionRequestValidatorTests
{
    private readonly CreateTrackerDefinitionRequestValidator _create = new();
    private readonly UpdateTrackerDefinitionRequestValidator _update = new();

    [Theory]
    [InlineData(TrackerVisibility.Public)]
    [InlineData(TrackerVisibility.Private)]
    public void Create_AcceptsImplementedVisibilities(TrackerVisibility visibility)
    {
        var result = _create.TestValidate(
            new CreateTrackerDefinitionRequest { Name = "Sensor", Visibility = visibility });

        result.ShouldNotHaveValidationErrorFor(x => x.Visibility);
    }

    [Fact]
    public void Create_RejectsRoleRestricted()
    {
        var result = _create.TestValidate(new CreateTrackerDefinitionRequest
        {
            Name = "Sensor",
            Visibility = TrackerVisibility.RoleRestricted,
        });

        result.ShouldHaveValidationErrorFor(x => x.Visibility)
            .WithErrorMessage(CreateTrackerDefinitionRequestValidator.RoleRestrictedMessage);
    }

    [Fact]
    public void Create_RejectsUndefinedVisibility()
    {
        var result = _create.TestValidate(new CreateTrackerDefinitionRequest
        {
            Name = "Sensor",
            Visibility = (TrackerVisibility)99,
        });

        result.ShouldHaveValidationErrorFor(x => x.Visibility);
    }

    [Theory]
    [InlineData(TrackerVisibility.Public)]
    [InlineData(TrackerVisibility.Private)]
    [InlineData(null)]
    public void Update_AcceptsImplementedAndOmittedVisibilities(TrackerVisibility? visibility)
    {
        var result = _update.TestValidate(
            new UpdateTrackerDefinitionRequest { Visibility = visibility });

        result.ShouldNotHaveValidationErrorFor(x => x.Visibility);
    }

    [Fact]
    public void Update_RejectsRoleRestricted()
    {
        var result = _update.TestValidate(new UpdateTrackerDefinitionRequest
        {
            Visibility = TrackerVisibility.RoleRestricted,
        });

        result.ShouldHaveValidationErrorFor(x => x.Visibility)
            .WithErrorMessage(CreateTrackerDefinitionRequestValidator.RoleRestrictedMessage);
    }

    [Fact]
    public void Update_RejectsUndefinedVisibility()
    {
        var result = _update.TestValidate(new UpdateTrackerDefinitionRequest
        {
            Visibility = (TrackerVisibility)99,
        });

        result.ShouldHaveValidationErrorFor(x => x.Visibility);
    }
}
