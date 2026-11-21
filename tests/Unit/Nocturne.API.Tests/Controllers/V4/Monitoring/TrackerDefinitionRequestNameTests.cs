using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using Nocturne.API.Controllers.V4.Monitoring;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Monitoring;

/// <summary>
/// Model binding refuses a blank tracker name on update with the message it gives create; an update
/// that posts no name keeps the stored one.
/// </summary>
[Trait("Category", "Unit")]
public class TrackerDefinitionRequestNameTests
{
    private static string[] NameErrors(object request) =>
        Validate(request).Where(r => r.MemberNames.Contains("Name")).Select(r => r.ErrorMessage!).ToArray();

    private static List<ValidationResult> Validate(object request)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        return results;
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void ABlankNameIsRefusedOnUpdate_WithTheMessageCreateGives(string name)
    {
        var onCreate = NameErrors(new CreateTrackerDefinitionRequest { Name = name });

        onCreate.Should().ContainSingle();
        NameErrors(new UpdateTrackerDefinitionRequest { Name = name }).Should().Equal(onCreate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Sensor")]
    public void ANameThatIsPostedOrLeftOut_IsAcceptedOnUpdate(string? name) =>
        NameErrors(new UpdateTrackerDefinitionRequest { Name = name }).Should().BeEmpty();
}
