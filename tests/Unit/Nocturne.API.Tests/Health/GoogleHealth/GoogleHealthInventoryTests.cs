using Nocturne.API.Services.Health.GoogleHealth;
using Nocturne.Connectors.GoogleHealth.Services;
using Nocturne.Core.Models.Health;
using Xunit;

namespace Nocturne.API.Tests.Health.GoogleHealth;

public class GoogleHealthInventoryTests
{
    private static GoogleHealthInventory.Context Context(
        string[]? selected = null, bool previewRequired = false, string? errorCode = null,
        string[]? errorTypes = null, bool cycleTracking = true, string[]? scopes = null) => new(
        scopes ?? [GoogleHealthClient.ActivityScope, GoogleHealthClient.MetricsScope],
        selected ?? [],
        previewRequired,
        errorCode,
        errorTypes ?? [],
        cycleTracking);

    private static GoogleHealthDataTypeStatus Type(GoogleHealthCategory[] categories, string dataType) =>
        categories.SelectMany(category => category.Types).Single(type => type.DataType == dataType);

    [Fact]
    public void Without_a_scan_supported_types_are_not_scanned_and_selectable_only_when_granted()
    {
        var categories = GoogleHealthInventory.Categories(null, Context());

        Assert.Equal(GoogleHealthTypeState.NotScanned, Type(categories, "steps").State);
        Assert.True(Type(categories, "steps").Selectable);
        Assert.False(Type(categories, "sleep").Selectable);
        Assert.Equal(GoogleHealthTypeState.NoReadonlyScope, Type(categories, "menstrual-period").State);
        Assert.Equal(GoogleHealthTypeState.NotYetSupported, Type(categories, "body-fat").State);
    }

    [Theory]
    [InlineData(true, 3, false, null, GoogleHealthTypeState.Available, GoogleHealthFound.Found)]
    [InlineData(true, 0, false, null, GoogleHealthTypeState.AvailableNoData, GoogleHealthFound.None)]
    [InlineData(true, 3, true, null, GoogleHealthTypeState.Importing, GoogleHealthFound.Found)]
    [InlineData(true, 0, true, null, GoogleHealthTypeState.ImportingNoData, GoogleHealthFound.None)]
    [InlineData(true, 3, true, "rate_limited", GoogleHealthTypeState.NeedsAttention, GoogleHealthFound.Found)]
    [InlineData(false, 0, true, null, GoogleHealthTypeState.PermissionNotGranted, GoogleHealthFound.NoPermission)]
    public void A_scanned_type_reports_its_import_state(
        bool granted, int count, bool selected, string? statusError, GoogleHealthTypeState state, GoogleHealthFound found)
    {
        var categories = GoogleHealthInventory.Categories(
            [new GoogleHealthInventoryItem("steps", count, null)],
            Context(selected ? ["steps"] : [], errorCode: statusError,
                scopes: granted ? null : [GoogleHealthClient.MetricsScope]));

        Assert.Equal(state, Type(categories, "steps").State);
        Assert.Equal(found, Type(categories, "steps").Found);
    }

    [Fact]
    public void A_failed_scan_is_reported_and_not_selectable()
    {
        var categories = GoogleHealthInventory.Categories(
            [new GoogleHealthInventoryItem("steps", 0, "google_unavailable")], Context());

        var steps = Type(categories, "steps");
        Assert.Equal(GoogleHealthTypeState.ReadFailed, steps.State);
        Assert.Equal(GoogleHealthFound.ScanFailed, steps.Found);
        Assert.Equal("google_unavailable", steps.ErrorCode);
        Assert.False(steps.Selectable);
    }

    [Fact]
    public void An_error_naming_other_types_leaves_this_one_importing()
    {
        var categories = GoogleHealthInventory.Categories(
            [new GoogleHealthInventoryItem("steps", 1, null)],
            Context(["steps"], errorCode: "partial_consent", errorTypes: ["sleep"]));

        Assert.Equal(GoogleHealthTypeState.Importing, Type(categories, "steps").State);
    }

    [Fact]
    public void A_scan_from_a_wider_grant_does_not_make_a_type_selectable_after_the_grant_narrows()
    {
        var categories = GoogleHealthInventory.Categories(
            [new GoogleHealthInventoryItem("steps", 5, null)],
            Context(["steps"], scopes: [GoogleHealthClient.MetricsScope]));

        var steps = Type(categories, "steps");
        Assert.Equal(GoogleHealthTypeState.PermissionNotGranted, steps.State);
        Assert.False(steps.Selectable);
    }

    [Fact]
    public void Cycle_tracking_is_left_out_when_the_patient_record_says_male()
    {
        Assert.DoesNotContain(GoogleHealthInventory.Categories(null, Context(cycleTracking: false)),
            category => category.Kind == GoogleHealthCategoryKind.CycleTracking);
        Assert.Contains(GoogleHealthInventory.Categories(null, Context()),
            category => category.Kind == GoogleHealthCategoryKind.CycleTracking);
    }
}
