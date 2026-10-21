using FluentAssertions;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.Core.Models.Tests.Authorization;

[Trait("Category", "Unit")]
public class ShareDataCategoriesTests
{
    [Fact]
    public void Csv_GlucoseOnlyShare_YieldsGlucoseScopeOnly()
    {
        ShareDataCategories.ComputeVisibleCategoriesCsv(new[] { Scope.GlucoseRead })
            .Should().Be("glucose.read");
    }

    [Fact]
    public void Csv_GlucoseAndTreatments_OrdinalSortedAndDeterministic()
    {
        ShareDataCategories.ComputeVisibleCategoriesCsv(
            new[] { Scope.TreatmentsRead, Scope.GlucoseRead })
            .Should().Be("glucose.read,treatments.read");
    }

    [Fact]
    public void Csv_ReadWriteScope_SatisfiesTheReadCategory()
    {
        ShareDataCategories.ComputeVisibleCategoriesCsv(new[] { Scope.TreatmentsReadWrite })
            .Should().Be("treatments.read");
    }

    [Fact]
    public void Csv_FullAccess_UnlocksEveryCategorizedScope()
    {
        var csv = ShareDataCategories.ComputeVisibleCategoriesCsv(new[] { Scope.FullAccess });

        csv.Split(',').Should().BeEquivalentTo(ShareDataCategories.GoverningScopes);
    }

    [Fact]
    public void Csv_NoScopes_IsEmpty()
    {
        ShareDataCategories.ComputeVisibleCategoriesCsv(Array.Empty<string>())
            .Should().BeEmpty();
    }

    [Fact]
    public void Csv_NonShareableScope_UnlocksNothing()
    {
        // therapy.read is a real scope but not publicly shareable; it must not
        // unlock any categorized table for a share.
        ShareDataCategories.ComputeVisibleCategoriesCsv(new[] { Scope.TherapyRead })
            .Should().BeEmpty();
    }

    [Fact]
    public void GoverningScopeFor_GovernedTable_ReturnsItsScope()
    {
        ShareDataCategories.GoverningScopeFor("boluses").Should().Be(Scope.TreatmentsRead);
    }

    [Fact]
    public void GoverningScopeFor_HiddenTable_ReturnsNull()
    {
        // therapy_settings is ITenantScoped but not share-categorized.
        ShareDataCategories.GoverningScopeFor("therapy_settings").Should().BeNull();
    }

    [Fact]
    public void GovernedTables_HasNoTableUnderTwoScopes()
    {
        var allTables = ShareDataCategories.GovernedTables.Values.SelectMany(t => t).ToList();
        allTables.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void RecencyColumnFor_TimeSeriesTable_ReturnsItsColumn()
    {
        ShareDataCategories.RecencyColumnFor("boluses").Should().Be("timestamp");
        ShareDataCategories.RecencyColumnFor("temp_basals").Should().Be("start_timestamp");
        ShareDataCategories.RecencyColumnFor("connector_food_entries").Should().Be("consumed_at");
    }

    [Fact]
    public void RecencyColumnFor_CatalogTable_ReturnsNull()
    {
        // The food database is catalog data with no per-row time — deliberately unclamped.
        ShareDataCategories.RecencyColumnFor("foods").Should().BeNull();
    }

    [Fact]
    public void RecencyColumnFor_UngovernedTable_ReturnsNull()
    {
        ShareDataCategories.RecencyColumnFor("therapy_settings").Should().BeNull();
    }

    [Fact]
    public void EveryGovernedTable_HasARecencyClassification()
    {
        // The type initializer enforces this too; the test states the invariant where a
        // reviewer will see it: a governed table must decide its clamp column (or opt out
        // with an explicit null) — it cannot be forgotten.
        var governed = ShareDataCategories.GovernedTables.Values.SelectMany(t => t);
        governed.Should().OnlyContain(t => ShareDataCategories.RecencyColumns.ContainsKey(t));
    }

    [Fact]
    public void RecencyColumns_ReferenceOnlyGovernedTables()
    {
        var governed = ShareDataCategories.GovernedTables.Values.SelectMany(t => t).ToHashSet();
        ShareDataCategories.RecencyColumns.Keys.Should().OnlyContain(t => governed.Contains(t),
            "a recency entry for an ungoverned table is stale and would mislead");
    }

    [Fact]
    public void StateSpans_AreHiddenFromSharesButClampedForMembersByOverlap()
    {
        ShareDataCategories.GoverningScopeFor("state_spans").Should().BeNull();
        ShareDataCategories.RecencyColumnFor("state_spans").Should().BeNull();
        ShareDataCategories.SpanEndColumnFor("state_spans").Should().Be("end_timestamp");
    }

    [Fact]
    public void HiddenSpanEndColumns_ReferenceOnlyUngovernedTablesWithoutARecencyColumn()
    {
        ShareDataCategories.HiddenSpanEndColumns.Keys.Should().OnlyContain(t =>
            ShareDataCategories.GoverningScopeFor(t) == null && ShareDataCategories.RecencyColumnFor(t) == null);
    }

    [Fact]
    public void Notes_AreHiddenFromSharesButClampedForMembers()
    {
        ShareDataCategories.GoverningScopeFor("notes").Should().BeNull();
        ShareDataCategories.RecencyColumnFor("notes").Should().Be("timestamp");
    }

    [Theory]
    [InlineData("sleep_biometric_samples", "timestamp", false)]
    [InlineData("body_weights", "mills", true)]
    [InlineData("lab_hba1c_results", "measured_at", false)]
    [InlineData("system_events", "mills", true)]
    [InlineData("in_app_notifications", "created_at", false)]
    [InlineData("device_clock_observations", "observed_at", false)]
    public void HiddenTimeSeries_AreClampedForMembersByTheirRecencyColumn(string table, string column, bool millis)
    {
        ShareDataCategories.GoverningScopeFor(table).Should().BeNull();
        ShareDataCategories.RecencyColumnFor(table).Should().Be(column);
        ShareDataCategories.RecencyIsUnixMillis(table).Should().Be(millis);
        ShareDataCategories.SpanEndColumnFor(table).Should().BeNull();
    }

    [Theory]
    [InlineData("sleep_sessions", "end_time")]
    [InlineData("sleep_stages", "end_time")]
    [InlineData("alert_excursions", "ended_at")]
    [InlineData("alert_instances", "resolved_at")]
    [InlineData("tracker_instances", "completed_at")]
    public void HiddenSpans_AreClampedForMembersByOverlap(string table, string end)
    {
        ShareDataCategories.GoverningScopeFor(table).Should().BeNull();
        ShareDataCategories.RecencyColumnFor(table).Should().BeNull();
        ShareDataCategories.SpanEndColumnFor(table).Should().Be(end);
    }

    [Fact]
    public void TreatmentFoods_AreClampedForMembersThroughTheirCarbIntake()
    {
        ShareDataCategories.GoverningScopeFor("treatment_foods").Should().BeNull();
        ShareDataCategories.RecencyColumnFor("treatment_foods").Should().BeNull();
        ShareDataCategories.SpanEndColumnFor("treatment_foods").Should().BeNull();
        ShareDataCategories.ParentRecencyFor("treatment_foods")
            .Should().Be(new ParentRecency("carb_intakes", "carb_intake_id"));
    }

    [Fact]
    public void HiddenParentRecency_ReferencesUngovernedChildrenOfTimestampedParents()
    {
        ShareDataCategories.HiddenParentRecency.Should().AllSatisfy(entry =>
        {
            ShareDataCategories.GoverningScopeFor(entry.Key).Should().BeNull();
            ShareDataCategories.RecencyColumnFor(entry.Key).Should().BeNull();
            ShareDataCategories.SpanEndColumnFor(entry.Key).Should().BeNull();
            ShareDataCategories.RecencyColumnFor(entry.Value.Table).Should().NotBeNull();
            ShareDataCategories.RecencyIsUnixMillis(entry.Value.Table).Should().BeFalse();
        });
    }

    [Fact]
    public void UnixMillisRecencyTables_HaveAHiddenRecencyColumn()
    {
        ShareDataCategories.UnixMillisRecencyTables
            .Should().OnlyContain(t => ShareDataCategories.HiddenRecencyColumns.ContainsKey(t));
    }

    [Fact]
    public void HiddenRecencyColumns_ReferenceOnlyUngovernedTables()
    {
        ShareDataCategories.HiddenRecencyColumns.Keys
            .Should().OnlyContain(t => ShareDataCategories.GoverningScopeFor(t) == null,
                "a governed table declares its clamp in RecencyColumns");
    }
}
