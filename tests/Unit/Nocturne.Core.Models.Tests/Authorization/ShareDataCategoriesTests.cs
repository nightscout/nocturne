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
        ShareDataCategories.ComputeVisibleCategoriesCsv(new[] { OAuthScopes.GlucoseRead })
            .Should().Be("glucose.read");
    }

    [Fact]
    public void Csv_GlucoseAndTreatments_OrdinalSortedAndDeterministic()
    {
        ShareDataCategories.ComputeVisibleCategoriesCsv(
            new[] { OAuthScopes.TreatmentsRead, OAuthScopes.GlucoseRead })
            .Should().Be("glucose.read,treatments.read");
    }

    [Fact]
    public void Csv_ReadWriteScope_SatisfiesTheReadCategory()
    {
        ShareDataCategories.ComputeVisibleCategoriesCsv(new[] { OAuthScopes.TreatmentsReadWrite })
            .Should().Be("treatments.read");
    }

    [Fact]
    public void Csv_FullAccess_UnlocksEveryCategorizedScope()
    {
        var csv = ShareDataCategories.ComputeVisibleCategoriesCsv(new[] { OAuthScopes.FullAccess });

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
        ShareDataCategories.ComputeVisibleCategoriesCsv(new[] { OAuthScopes.TherapyRead })
            .Should().BeEmpty();
    }

    [Fact]
    public void GoverningScopeFor_GovernedTable_ReturnsItsScope()
    {
        ShareDataCategories.GoverningScopeFor("boluses").Should().Be(OAuthScopes.TreatmentsRead);
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
}
