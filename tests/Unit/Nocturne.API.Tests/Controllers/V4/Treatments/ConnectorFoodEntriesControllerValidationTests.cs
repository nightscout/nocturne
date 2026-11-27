using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Treatments;
using Nocturne.API.Tests.Controllers.V4.Base;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Treatments;

/// <summary>
/// The import body is a root array, which the auto-validation filter does not enter, so the action runs each
/// entry's validator itself before it stores anything.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ConnectorFoodEntriesControllerValidationTests
{
    [Fact]
    public async Task An_entry_with_a_meal_name_longer_than_its_column_is_refused_at_its_index_before_anything_is_stored()
    {
        var service = new Mock<IConnectorFoodEntryService>(MockBehavior.Strict);
        var controller = RegisteredValidators.On(new ConnectorFoodEntriesController(service.Object));

        var result = await controller.ImportEntries(
        [
            new ConnectorFoodEntryImport { MealName = "Lunch" },
            new ConnectorFoodEntryImport { MealName = new string('a', ColumnLengths.ConnectorFoodEntry.MealName + 1) },
        ]);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(400);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().StartWith("Food entry at index 1 is invalid: MealName: ");
    }
}
