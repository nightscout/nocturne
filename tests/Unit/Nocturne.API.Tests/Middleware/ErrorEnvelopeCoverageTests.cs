using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Controllers.V1;
using Nocturne.API.Controllers.V3;
using Nocturne.API.Tests.Authorization;
using Xunit;
using V1StatusController = Nocturne.API.Controllers.V1.StatusController;
using V3StatusController = Nocturne.API.Controllers.V3.StatusController;

namespace Nocturne.API.Tests.Middleware;

/// <summary>
/// Every controller action answers its API version's error envelope through
/// <see cref="ErrorEnvelopeAttribute"/>, or is listed here, by action or by controller, with the reason it
/// does not.
/// </summary>
/// <remarks>
/// A new controller or action must choose: attribute it, or add it to a list. Every list only
/// shrinks. V1 to V3 and the partly converted V4 controllers are listed by action; the rest of V4
/// by controller, so a new action on one of those passes until its controller is converted.
/// </remarks>
[Trait("Category", "Unit")]
public class ErrorEnvelopeCoverageTests
{
    private const string ControllersNamespace = "Nocturne.API.Controllers.";

    /// <summary>
    /// V1 to V3 actions that answer a failure in a body of their own, with what that body is. Keyed
    /// by action, so a new action on one of these controllers has to choose.
    /// </summary>
    private static readonly Dictionary<(Type Controller, string Action), string> AnswersFailureItself = new()
    {
        [(typeof(AlexaController), nameof(AlexaController.HandleAlexaRequest))] =
            "A spoken Alexa error at 200, so the skill keeps working; 400 and 401 for a malformed or unauthorised request.",
        [(typeof(DebugController), nameof(DebugController.TestPostgreSqlConnection))] =
            "A 500 whose body is the diagnostic: the exception message, its inner message and a Failed status.",
        [(typeof(DebugController), nameof(DebugController.GetEntriesDirect))] =
            "A 500 whose body is the exception message alone, so a superuser sees why the read failed.",
        [(typeof(PebbleController), nameof(PebbleController.GetPebbleData))] =
            "A 500 carrying an empty Pebble response, the shape the watch face parses.",
        [(typeof(V1StatusController), nameof(V1StatusController.GetStatus))] =
            "A 200 status body with status error from its own catch, so a probe never reads as down.",
        [(typeof(LastModifiedController), nameof(LastModifiedController.GetLastModified))] =
            "A 200 V3 envelope with only the server time from its own catch, so a sync client keeps its shape.",
        [(typeof(V3StatusController), nameof(V3StatusController.GetStatus))] =
            "A 200 V3 status body with status error from its own catch, so a probe never reads as down.",
        [(typeof(VersionController), nameof(VersionController.GetVersion))] =
            "A 200 V3 version body with an unknown storage version from its own catch, so a probe never reads as down.",
    };

    /// <summary>Controllers whose protocol has an error body of its own, so the envelope would break clients.</summary>
    private static readonly Dictionary<Type, string> NeverEnvelope = new()
    {
        [typeof(OAuthController)] =
            "OAuth protocol endpoints answer the OAuthError body that RFC 6749 requires.",
    };

    /// <summary>
    /// Not yet converted; these answer the framework default ProblemDetails. Names are relative to
    /// <see cref="ControllersNamespace"/>.
    /// </summary>
    private static readonly HashSet<string> NotYetConverted =
    [
        "Authentication.DirectGrantController",
        "Authentication.LoginHandoffController",
        "Authentication.OidcController",
        "Authentication.PasskeyController",
        "Authentication.TotpController",
        "MetadataController",
        "V4.Account.AvatarController",
        "V4.Admin.DemoAdminController",
        "V4.Analytics.CgmComparisonController",
        "V4.Analytics.CorrelationController",
        "V4.Analytics.CurrentTherapyStateController",
        "V4.Analytics.CustomReportsController",
        "V4.Analytics.LabHbA1cController",
        "V4.Analytics.LoopAnalysisController",
        "V4.Analytics.SensorIntegrityController",
        "V4.Analytics.StateSpansController",
        "V4.Analytics.StatisticsController",
        "V4.Analytics.TreatmentLogController",
        "V4.Audit.AuditController",
        "V4.ClientDevices.ClientDevicesController",
        "V4.CoachMarkController",
        "V4.Connectors.CareLinkConnectController",
        "V4.Connectors.ConfigurationController",
        "V4.ConnectorStatusController",
        "V4.Demo.DemoSessionController",
        "V4.DeviceCatalogController",
        "V4.Devices.ApsSnapshotController",
        "V4.Devices.DeviceAgeController",
        "V4.Devices.DeviceEventController",
        "V4.Devices.PumpSnapshotController",
        "V4.Devices.ReservoirController",
        "V4.Devices.ReservoirReportsController",
        "V4.Devices.TransmitterStatusController",
        "V4.Devices.UploaderSnapshotController",
        "V4.DevOnly.DevAuthController",
        "V4.Glucose.BGCheckController",
        "V4.Glucose.CalibrationController",
        "V4.Glucose.MeterGlucoseController",
        "V4.Glucose.SensorGlucoseController",
        "V4.Health.ActivityController",
        "V4.Health.GoogleHealthController",
        "V4.Health.GoogleHealthOAuthCallbackController",
        "V4.Health.OuraController",
        "V4.Health.PatientRecordController",
        "V4.Health.PatientTimezoneController",
        "V4.Identity.BackupSignInPromptController",
        "V4.Identity.ChatIdentityController",
        "V4.Identity.ChatIdentityDirectoryController",
        "V4.Identity.ConnectedAppsController",
        "V4.Identity.GuestLinkController",
        "V4.Identity.LinkedPlatformsController",
        "V4.Identity.MemberInviteController",
        "V4.Identity.MemberRecoveryController",
        "V4.Identity.MembershipRequestController",
        "V4.Identity.MyPermissionsController",
        "V4.Identity.MyTenantsController",
        "V4.Identity.RealtimeAdmissionController",
        "V4.Identity.RoleController",
        "V4.Identity.SessionsController",
        "V4.Identity.SetupAlertsController",
        "V4.Identity.SetupDevicesController",
        "V4.Identity.SetupHubController",
        "V4.Identity.SetupTherapyController",
        "V4.Identity.ShareLinkController",
        "V4.Identity.TenantAppearanceController",
        "V4.Identity.TenantCreationInviteController",
        "V4.Identity.TenantSettingsController",
        "V4.InsulinCatalogController",
        "V4.Monitoring.AlertCustomSoundsController",
        "V4.Monitoring.AlertInvitesController",
        "V4.Monitoring.AlertReplayController",
        "V4.Monitoring.AlertRulesController",
        "V4.Monitoring.AlertsController",
        "V4.Monitoring.DndWindowsController",
        "V4.Monitoring.NotificationsController",
        "V4.Monitoring.TenantAlertSettingsController",
        "V4.Monitoring.TrackerLegacyFallbacksController",
        "V4.Monitoring.TrackersController",
        "V4.Platform.ContentContributionsController",
        "V4.Platform.ReleasesController",
        "V4.Platform.StatusController",
        "V4.Platform.SupportController",
        "V4.Platform.SystemController",
        "V4.Platform.SystemEventsController",
        "V4.Platform.TranslationsController",
        "V4.PlatformAdmin.AccessRequestController",
        "V4.PlatformAdmin.ConnectorAdminController",
        "V4.PlatformAdmin.DatabaseCapacityController",
        "V4.PlatformAdmin.JobLedgerController",
        "V4.PlatformAdmin.PlatformAccessController",
        "V4.PlatformAdmin.PlatformPolicyController",
        "V4.PlatformAdmin.PlatformSettingsController",
        "V4.PlatformAdmin.TenantController",
        "V4.PlatformAdmin.TenantCreationInviteAdminController",
        "V4.PlatformAdmin.TenantDirectGrantController",
        "V4.PlatformController",
        "V4.Profiles.ClockFaceAlertsController",
        "V4.Profiles.ClockFacesController",
        "V4.Profiles.GlucoseProcessingSettingsController",
        "V4.Profiles.MyFitnessPalSettingsController",
        "V4.Profiles.ProfileActivationsController",
        "V4.Profiles.ProfileController",
        "V4.Profiles.ProfileHistoryController",
        "V4.Profiles.UserPreferencesController",
        "V4.SetupController",
        "V4.Sleep.SleepReportController",
        "V4.SleepController",
        "V4.TenantAdmin.CompressionLowController",
        "V4.TenantAdmin.MigrationController",
        "V4.TenantAdmin.OidcProviderAdminController",
        "V4.TenantAdmin.RejectedUploadsController",
        "V4.TenantAdmin.SubjectAdminController",
        "V4.TimezoneTimelineController",
        "V4.TlsAuthorizationController",
        "V4.Treatments.BasalInjectionController",
        "V4.Treatments.BolusCalculationController",
        "V4.Treatments.BolusController",
        "V4.Treatments.CarbIntakeController",
        "V4.Treatments.ConnectorFoodEntriesController",
        "V4.Treatments.FoodsController",
        "V4.Treatments.NoteController",
        "V4.Treatments.NutritionController",
        "V4.Treatments.TempBasalController",
        "WellKnownController",
    ];

    /// <summary>
    /// The unattributed actions of V4 controllers that are attributed action by action, as
    /// <c>Controller.Action</c>. Names are relative to <see cref="ControllersNamespace"/>.
    /// </summary>
    private static readonly HashSet<string> PartlyConvertedActions =
    [
        "V4.Analytics.AnalyticsController.GetPrivacyInformation",
        "V4.Analytics.PredictionController.GetStatus",
        "V4.Connectors.WebhookSettingsController.GetWebhookSettings",
        "V4.Connectors.WebhookSettingsController.SaveWebhookSettings",
        "V4.DevOnly.DevAdminController.CreateTenant",
        "V4.DevOnly.DevAdminController.DeleteTenant",
        "V4.DevOnly.DevAdminController.EnterRecoveryMode",
        "V4.DevOnly.DevAdminController.ExportSnapshot",
        "V4.DevOnly.DevAdminController.ListTenants",
        "V4.DevOnly.DevAdminController.SeedSampleData",
        "V4.DevOnly.DevAdminController.SeedTenant",
        "V4.DevOnly.DevAdminController.SyncAll",
        "V4.Platform.CompatibilityController.GetConfiguration",
        "V4.Platform.CompatibilityController.TestApiComparison",
        "V4.Platform.ServicesController.DeleteConnectionRecords",
        "V4.Platform.ServicesController.GetApiInfo",
        "V4.Platform.ServicesController.GetAvailableConnectors",
        "V4.Platform.ServicesController.GetConnections",
        "V4.Platform.ServicesController.GetConnectorCapabilities",
        "V4.Platform.ServicesController.GetUploaderApps",
        "V4.Platform.ServicesController.GetUploaderSetup",
        "V4.Platform.ServicesController.ResetConnectorCursor",
        "V4.Platform.ServicesController.TriggerConnectorSync",
        "V4.Treatments.MealMatchingController.GetFoodEntry",
        "V4.Treatments.MealMatchingController.GetSuggestions",
    ];

    private static readonly Type[] Controllers = ControllerActionReflection.GetControllers().ToArray();

    private static readonly Regex VersionedRoute = new(@"^/api/v[123](/|$)", RegexOptions.Compiled);

    private static IEnumerable<MethodInfo> ActionsOf(Type controller) =>
        ControllerActionReflection.GetDeclaredActions(controller);

    private static bool HasEnvelope(Type controller, MethodInfo action) =>
        action.GetCustomAttribute<ErrorEnvelopeAttribute>() is not null
        || controller.GetCustomAttribute<ErrorEnvelopeAttribute>() is not null;

    private static string NameOf(Type controller) =>
        controller.FullName![ControllersNamespace.Length..];

    private static string ActionName(Type controller, MethodInfo action) => ActionName(controller, action.Name);

    private static string ActionName(Type controller, string action) => $"{NameOf(controller)}.{action}";

    private static string ControllerOf(string actionName) => actionName[..actionName.LastIndexOf('.')];

    private static bool IsExempt(Type controller, MethodInfo action) =>
        HasEnvelope(controller, action)
        || NeverEnvelope.ContainsKey(controller)
        || NotYetConverted.Contains(NameOf(controller))
        || AnswersFailureItself.ContainsKey((controller, action.Name))
        || PartlyConvertedActions.Contains(ActionName(controller, action));

    private static bool IsV1ToV3(Type controller) =>
        NameOf(controller).StartsWith("V1.", StringComparison.Ordinal)
        || NameOf(controller).StartsWith("V2.", StringComparison.Ordinal)
        || NameOf(controller).StartsWith("V3.", StringComparison.Ordinal)
        || ActionsOf(controller)
            .SelectMany(a => ControllerActionReflection.GetRoutes(controller, a))
            .Any(r => VersionedRoute.IsMatch(r));

    [Fact]
    public void TheScan_FindsTheControllerSurface()
    {
        Controllers.Should().HaveCountGreaterThan(150);
        Controllers.SelectMany(ActionsOf).Should().HaveCountGreaterThan(900);
    }

    [Fact]
    public void EveryAction_AnswersTheEnvelopeOrIsListedWithAReason()
    {
        var unlisted = Controllers
            .SelectMany(c => ActionsOf(c).Where(a => !IsExempt(c, a)).Select(a => ActionName(c, a)))
            .ToList();

        unlisted.Should().BeEmpty(
            "an action answers its version's error envelope through [ErrorEnvelope], or is added to a list "
                + "of ErrorEnvelopeCoverageTests with the reason it does not"
        );
    }

    [Fact]
    public void EveryListedController_StillHasAnActionWithoutTheEnvelope()
    {
        var needless = Controllers
            .Where(c => NeverEnvelope.ContainsKey(c) || NotYetConverted.Contains(NameOf(c)))
            .Where(c => ActionsOf(c).All(a => HasEnvelope(c, a)))
            .Select(NameOf)
            .ToList();

        needless.Should().BeEmpty("a listed controller whose every action has the envelope leaves the list");
    }

    [Fact]
    public void EveryListedAction_IsAnActionStillWithoutTheEnvelope()
    {
        var unattributed = Controllers
            .SelectMany(c => ActionsOf(c).Where(a => !HasEnvelope(c, a)).Select(a => (Controller: c, a.Name)))
            .ToList();

        var staleItself = AnswersFailureItself.Keys
            .Where(k => !unattributed.Contains((k.Controller, k.Action)))
            .Select(k => $"{NameOf(k.Controller)}.{k.Action}");
        var stalePartly = PartlyConvertedActions
            .Where(n => !unattributed.Any(a => $"{NameOf(a.Controller)}.{a.Name}" == n));

        staleItself.Concat(stalePartly).Should().BeEmpty(
            "a listed action that gained the attribute, or was deleted, leaves the list");
    }

    [Fact]
    public void EveryListedName_IsAController()
    {
        var known = Controllers.Select(NameOf).ToHashSet();

        NotYetConverted.Except(known).Should().BeEmpty("a deleted controller leaves the list");
        PartlyConvertedActions.Select(ControllerOf).Except(known).Should().BeEmpty("a deleted controller leaves the list");
        NeverEnvelope.Keys.Concat(AnswersFailureItself.Keys.Select(k => k.Controller))
            .Should().OnlyContain(t => Controllers.Contains(t));
    }

    [Fact]
    public void NoControllerIsListedAtTwoGranularities()
    {
        var wholly = NotYetConverted.Concat(NeverEnvelope.Keys.Select(NameOf)).ToList();
        wholly.Should().OnlyHaveUniqueItems();

        var byAction = PartlyConvertedActions.Select(ControllerOf)
            .Concat(AnswersFailureItself.Keys.Select(k => NameOf(k.Controller)))
            .Distinct();
        byAction.Intersect(wholly).Should().BeEmpty("a controller is listed whole or by action, not both");
    }

    [Fact]
    public void V1ToV3_AreListedByAction_NotByController()
    {
        var versioned = Controllers.Where(IsV1ToV3).Select(NameOf).ToHashSet();

        versioned.Intersect(NotYetConverted).Should().BeEmpty(
            "V1 to V3 are guarded completely: an action there is attributed or states why in AnswersFailureItself");
        versioned.Intersect(PartlyConvertedActions.Select(ControllerOf)).Should().BeEmpty();
        versioned.Intersect(NeverEnvelope.Keys.Select(NameOf)).Should().BeEmpty();
    }

    [Fact]
    public void AnActionListedByName_IsTheOnlyActionOfThatName()
    {
        var listed = AnswersFailureItself.Keys.Select(k => ActionName(k.Controller, k.Action))
            .Concat(PartlyConvertedActions);
        var declared = Controllers.SelectMany(c => ActionsOf(c).Select(a => ActionName(c, a))).ToList();

        listed.Where(name => declared.Count(d => d == name) != 1).Should().BeEmpty(
            "an entry exempts by method name, so an overload or a renamed action must be listed or attributed itself");
    }
}
