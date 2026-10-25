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
        governed.Should().OnlyContain(t =>
            ShareDataCategories.RecencyColumns.ContainsKey(t)
            ^ ShareDataCategories.GovernedSpanEndColumns.ContainsKey(t));
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
    [InlineData("body_weights", "mills", true)]
    [InlineData("lab_hba1c_results", "measured_at", false)]
    [InlineData("system_events", "mills", true)]
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
    [InlineData("tracker_instances", "completed_at")]
    [InlineData("in_app_notifications", "archived_at")]
    public void HiddenSpans_AreClampedForMembersByOverlap(string table, string end)
    {
        ShareDataCategories.GoverningScopeFor(table).Should().BeNull();
        ShareDataCategories.RecencyColumnFor(table).Should().BeNull();
        ShareDataCategories.SpanEndColumnFor(table).Should().Be(end);
    }

    /// <summary>
    /// A share holding alerts.read sees an alert that is open however long ago it started, and a
    /// clamped member still does: both are clamped by overlap.
    /// </summary>
    [Theory]
    [InlineData("alert_excursions", "ended_at")]
    [InlineData("alert_instances", "resolved_at")]
    public void AlertSpans_AreSharedUnderAlertsReadAndClampedByOverlap(string table, string end)
    {
        ShareDataCategories.GoverningScopeFor(table).Should().Be(Scope.AlertsRead);
        ShareDataCategories.RecencyColumnFor(table).Should().BeNull();
        ShareDataCategories.SpanEndColumnFor(table).Should().Be(end);
    }

    [Fact]
    public void AlertRules_AreSharedUnderAlertsReadUnclamped()
    {
        ShareDataCategories.GoverningScopeFor("alert_rules").Should().Be(Scope.AlertsRead);
        ShareDataCategories.RecencyColumnFor("alert_rules").Should().BeNull();
        ShareDataCategories.SpanEndColumnFor("alert_rules").Should().BeNull();
    }

    [Fact]
    public void InAppNotifications_StayVisibleWhileOpenForAWeekFromWhenTheyWereRaised()
    {
        ShareDataCategories.SpanEndColumnFor("in_app_notifications").Should().Be("archived_at");
        ShareDataCategories.OpenSpanBoundFor("in_app_notifications").Should().Be(new OpenSpanBound("created_at", 7));
        ShareDataCategories.HiddenOpenSpanBounds.Keys
            .Should().OnlyContain(t => ShareDataCategories.SpanEndColumnFor(t) != null);
    }

    [Theory]
    [InlineData("treatment_foods", "carb_intakes", "carb_intake_id")]
    [InlineData("sleep_stages", "sleep_sessions", "sleep_session_id")]
    [InlineData("sleep_biometric_samples", "sleep_sessions", "sleep_session_id")]
    public void PartsOfARow_AreClampedForMembersThroughTheirParent(string table, string parent, string foreignKey)
    {
        ShareDataCategories.GoverningScopeFor(table).Should().BeNull();
        ShareDataCategories.RecencyColumnFor(table).Should().BeNull();
        ShareDataCategories.SpanEndColumnFor(table).Should().BeNull();
        ShareDataCategories.ParentRecencyFor(table).Should().Be(new ParentRecency(parent, foreignKey));
    }

    [Fact]
    public void HiddenParentRecency_ReferencesUngovernedChildrenOfTimestampedParents()
    {
        ShareDataCategories.HiddenParentRecency.Should().AllSatisfy(entry =>
        {
            ShareDataCategories.GoverningScopeFor(entry.Key).Should().BeNull();
            ShareDataCategories.RecencyColumnFor(entry.Key).Should().BeNull();
            ShareDataCategories.SpanEndColumnFor(entry.Key).Should().BeNull();
            (ShareDataCategories.RecencyColumnFor(entry.Value.Table)
                ?? ShareDataCategories.SpanEndColumnFor(entry.Value.Table)).Should().NotBeNull();
            ShareDataCategories.RecencyIsUnixMillis(entry.Value.Table).Should().BeFalse();
            ShareDataCategories.ParentRecencyFor(entry.Value.Table).Should().BeNull();
        });
    }

    [Fact]
    public void CompressionLowSuggestions_AreHiddenFromSharesButClampedForMembersByTheirEnd()
    {
        ShareDataCategories.GoverningScopeFor("compression_low_suggestions").Should().BeNull();
        ShareDataCategories.RecencyColumnFor("compression_low_suggestions").Should().Be("end_mills");
        ShareDataCategories.RecencyIsUnixMillis("compression_low_suggestions").Should().BeTrue();
        ShareDataCategories.RecencyIsUnixMillis("notes").Should().BeFalse();
    }

    [Fact]
    public void UnixMillisRecencyTables_HaveARecencyColumn()
    {
        ShareDataCategories.UnixMillisRecencyTables
            .Should().OnlyContain(t => ShareDataCategories.RecencyColumnFor(t) != null);
    }

    [Fact]
    public void HiddenRecencyColumns_ReferenceOnlyUngovernedTables()
    {
        ShareDataCategories.HiddenRecencyColumns.Keys
            .Should().OnlyContain(t => ShareDataCategories.GoverningScopeFor(t) == null,
                "a governed table declares its clamp in RecencyColumns");
    }
}
