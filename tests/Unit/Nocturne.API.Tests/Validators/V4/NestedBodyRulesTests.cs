using FluentAssertions;
using FluentValidation;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Validators.Admin;
using Nocturne.API.Validators.V4;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Validators.V4;

/// <summary>
/// A string a body carries inside a list or object it holds is refused at its column by the body's own
/// validator, since the auto-validation filter runs only the validator of the body.
/// </summary>
[Trait("Category", "Unit")]
public class NestedBodyRulesTests
{
    private static readonly string TooLongThreshold = new('a', ColumnLengths.TrackerNotificationThreshold.Description + 1);

    [Fact]
    public void A_tracker_definition_threshold_description_longer_than_its_column_is_refused()
    {
        var thresholds = new List<CreateNotificationThresholdRequest> { new() { Description = TooLongThreshold } };

        Errors(new CreateTrackerDefinitionRequestValidator(), new CreateTrackerDefinitionRequest { Name = "Sensor", NotificationThresholds = thresholds })
            .Should().Contain("NotificationThresholds[0].Description");
        Errors(new UpdateTrackerDefinitionRequestValidator(), new UpdateTrackerDefinitionRequest { NotificationThresholds = thresholds })
            .Should().Contain("NotificationThresholds[0].Description");
    }

    [Fact]
    public void An_imported_food_name_longer_than_the_food_column_is_refused()
    {
        var entry = new ConnectorFoodEntryImport { Food = new ConnectorFoodImport { Name = new string('a', ColumnLengths.Food.Name + 1) } };

        Errors(new ConnectorFoodEntryImportValidator(), entry).Should().Contain("Food.Name");
    }

    [Fact]
    public void A_forwarded_discrepancy_detail_longer_than_its_column_is_refused()
    {
        var forwarded = new ForwardedDiscrepancyDto
        {
            SourceId = "eu-1",
            Analysis = new DiscrepancyAnalysisDto
            {
                Discrepancies = [new DiscrepancyDetailDto { Field = new string('a', ColumnLengths.DiscrepancyDetail.Field + 1) }],
            },
        };

        Errors(new ForwardedDiscrepancyDtoValidator(), forwarded).Should().Contain("Analysis.Discrepancies[0].Field");
    }

    [Fact]
    public void A_forwarded_summary_is_capped_as_it_is_stored_with_its_source()
    {
        var sourceId = "eu-1";
        var room = ColumnLengths.DiscrepancyAnalysis.Summary - $"[{sourceId}] ".Length;

        Errors(new ForwardedDiscrepancyDtoValidator(), Forwarded(sourceId, new string('a', room))).Should().NotContain("SourceId");
        Errors(new ForwardedDiscrepancyDtoValidator(), Forwarded(sourceId, new string('a', room + 1))).Should().Contain("SourceId");
    }

    private static ForwardedDiscrepancyDto Forwarded(string sourceId, string summary) =>
        new() { SourceId = sourceId, Analysis = new DiscrepancyAnalysisDto { Summary = summary } };

    private static List<string> Errors<T>(IValidator<T> validator, T request) =>
        validator.Validate(request).Errors.Select(f => f.PropertyName).ToList();
}
