using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Guards write-scope enforcement on the V4 plane. The V4 controllers carry only a class-level
/// <c>[Authorize]</c>, which read-only credentials satisfy — a guest link is issued read scopes
/// only (<c>GuestLinkService</c>) but is an authenticated session, and neither the share RLS policy
/// (<c>FOR SELECT</c>) nor the tenant policy's <c>WITH CHECK</c> blocks a write. Enforcement is
/// therefore <see cref="RequireDeclaredWriteScopeAttribute"/> reading the scope each controller
/// declares through <see cref="IWriteScopedController.WriteScope"/>, or an explicit
/// <see cref="RequireScopeAttribute"/> where a controller's writes span two data categories.
/// </summary>
/// <remarks>
/// <para>
/// The guard sweeps every <see cref="ControllerBase"/> subclass under
/// <c>Nocturne.API.Controllers.V4</c> by namespace rather than from a list of types, so a new
/// non-base V4 controller is under it the moment it exists. A write action there must carry a gate,
/// or be named in <see cref="GateExemptControllers"/> / <see cref="GateExemptWriteActions"/> with
/// the mechanism that governs it instead — and where that mechanism is an attribute or a namespace,
/// <see cref="EveryExemptionClaimingAnAttribute_ActuallyCarriesIt"/> asserts it is really there.
/// </para>
/// <para>
/// The V1/V2/V3 counterpart lives in <see cref="WriteEndpointScopeEnforcementTests"/>.
/// </para>
/// </remarks>
public class V4WriteScopeGatingTests
{
    private static readonly string[] WriteVerbs = ["POST", "PUT", "PATCH", "DELETE"];

    /// <summary>
    /// Every read scope in the taxonomy. Used to assert that no write action can be executed with
    /// read-only credentials (the guest-link, follower and public-share grant shape).
    /// </summary>
    private static readonly string[] AllReadScopes =
        OAuthScopes.AllScopes.Where(s => s.EndsWith(".read", StringComparison.Ordinal)).ToArray();

    /// <summary>The namespace the guard sweeps. Every controller under it is in scope.</summary>
    private const string V4Namespace = "Nocturne.API.Controllers.V4";

    /// <summary>
    /// Why a controller's write actions are not gated on a data-category scope. The category, not
    /// free prose, so the same decision cannot be restated three ways for three controllers.
    /// </summary>
    private static class NotDataCategory
    {
        /// <summary>Platform operator surface; the class carries [Authorize(Roles = "platform_admin")].</summary>
        public const string PlatformAdminRole = "platform-admin role, not a data scope";

        /// <summary>
        /// Tenant operator surface; [RequireAdmin] on the class or on every write action.
        /// </summary>
        public const string TenantAdminAttribute = "tenant-admin attribute, not a data scope";

        /// <summary>Service-to-service surface; the class carries [RequireInstanceKeyAuth].</summary>
        public const string InstanceKey = "instance-key service credential, not a data scope";

        /// <summary>Registered only when the API runs in Development.</summary>
        public const string DevelopmentOnly = "dev-only controller, absent outside Development";

        /// <summary>First-run tenant creation, before any tenant or credential exists.</summary>
        public const string Setup = "pre-tenant setup, no credential to scope";

        /// <summary>
        /// Writes membership, invite, session, role, share or linked-account state — the caller's own
        /// identity and delegation graph, not patient records. Governed by the RBAC permission atoms
        /// (members.manage, roles.manage, sharing.manage), which are a separate vocabulary from the
        /// data-category scopes and are not reachable through SatisfiesScope.
        /// </summary>
        public const string IdentityAndDelegation = "identity/delegation state, governed by RBAC atoms";

        /// <summary>
        /// Caller-supplied import of raw nutritional records from a connector's companion app
        /// (MyFitnessPal, Glooko). It writes connector_food_entries, so it is a data write; which
        /// write scope should govern it is an open question this entry records rather than answers.
        /// </summary>
        public const string ConnectorFoodImport = "connector food-entry import; governing write scope undecided";

        /// <summary>
        /// Connector configuration, gated on <see cref="TenantPermissions.TenantSettings"/> — an
        /// administration atom, absent from <see cref="OAuthScopes.AllScopes"/>, so no data-category
        /// scope names it. Asserted per controller by
        /// <see cref="EveryExemptionClaimingAnAttribute_ActuallyCarriesIt"/>, and behaviourally by
        /// <see cref="ConnectorConfigurationScopeTests"/>.
        /// </summary>
        public const string TenantSettingsScope = "connector configuration, gated on tenant.settings";

        /// <summary>
        /// The same tenant-administration gate as <see cref="TenantSettingsScope"/>, enforced in the
        /// handler because the action also admits the CareLink desktop link token, whose scope is
        /// outside the OAuth vocabulary and resolves to an empty set — no attribute can express it.
        /// Every controller filed under this must have a test that drives the real handler, which
        /// <see cref="HandlerGuardedControllers_HaveABehaviouralTest"/> asserts.
        /// </summary>
        public const string ConnectorHandlerGuard = "tenant.settings enforced in the handler";

        /// <summary>
        /// POST whose handler computes and returns: a statistic over the values in the request
        /// body, a report render, or a replay over stored history for a window the body supplies.
        /// Nothing is persisted, so there is no write to scope.
        /// </summary>
        public const string ComputesAndReturns = "computes and returns, persists nothing";

        /// <summary>
        /// Per-user or per-tenant presentation or authoring state with no patient
        /// observation in it: what the caller has seen, how they want it shown, and work
        /// in progress they have not submitted anywhere.
        /// </summary>
        public const string PresentationState = "presentation state, no patient data";

        /// <summary>
        /// The required scope depends on the record being written, so no attribute scan can see it.
        /// The controller calls a per-record guard in the handler instead. Every action filed under
        /// this reason must be covered by a route-level test that drives the real gate, so the
        /// exemption is asserted rather than trusted — see
        /// <see cref="PerRecordGuardedActions_AreCoveredByARouteLevelTest"/>.
        /// </summary>
        public const string PerRecordGuard = "per-record scope enforced in the handler";

        /// <summary>
        /// Mints a capability rather than storing an observation, and the vocabulary that should
        /// govern it is split: the permission atoms are <c>sharing.manage</c>/<c>sharing.guest</c>
        /// while the OAuth scope is <c>sharing.readwrite</c>, which no seed role maps to and
        /// <see cref="OAuthScopes.Normalize"/> keeps only for a client that was granted it
        /// directly. Requiring either would strip the capability from every non-owner role, so it
        /// stays ungated until the vocabulary is unified. NOT presentation state — the capability
        /// this mints serves patient glucose to an anonymous caller.
        /// </summary>
        public const string SplitSharingVocabulary = "sharing capability, vocabulary split between atom and scope";

        /// <summary>
        /// Deliberately <c>[AllowAnonymous]</c>. There is no credential to scope, so a scope gate
        /// cannot apply; whether the endpoint should be anonymous at all is the anonymous-endpoint
        /// audit's question, not this taxonomy's.
        /// </summary>
        public const string AnonymousByDeclaration = "anonymous endpoint, no credential to scope";

        /// <summary>
        /// Tenant-wide operational configuration or an operational log row: audit and analytics
        /// collection settings, glucose-processing preferences, system events. Not a patient record,
        /// and the taxonomy has no scope that names it — <see cref="ShareDataCategories"/> classifies
        /// tables of observations. Needs a scope of its own before it can be gated.
        /// </summary>
        public const string TenantOperationalConfig = "tenant operational config, no scope in the taxonomy";

        /// <summary>Sends to an external service and persists no tenant row.</summary>
        public const string OutboundOnly = "outbound call, persists no tenant data";
    }

    /// <summary>
    /// Controllers under <see cref="V4Namespace"/> whose write actions are governed by something
    /// other than a data-category write scope. Listed per controller with the mechanism that governs
    /// it instead, because the whole controller shares one answer;
    /// <see cref="GateExemptWriteActions"/> carries the per-action exceptions. A controller absent
    /// from both must gate every write action, so a new V4 controller fails the guard until someone
    /// decides which of these it is.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> GateExemptControllers =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AccessRequestController"] = NotDataCategory.PlatformAdminRole,
            ["ConnectorAdminController"] = NotDataCategory.PlatformAdminRole,
            ["DemoAdminController"] = NotDataCategory.PlatformAdminRole,
            ["OidcProviderAdminController"] = NotDataCategory.PlatformAdminRole,
            ["PlatformSettingsController"] = NotDataCategory.PlatformAdminRole,
            ["SubjectAdminController"] = NotDataCategory.PlatformAdminRole,
            ["TenantController"] = NotDataCategory.PlatformAdminRole,

            ["DeduplicationController"] = NotDataCategory.TenantAdminAttribute,
            ["MigrationController"] = NotDataCategory.TenantAdminAttribute,

            // Both repeat [RequireAdmin] per action rather than carrying it on the class.
            // ServicesController's data deletions and sync triggers are behind it, so they are not
            // reachable by a read-only session despite having no scope gate.
            ["CompatibilityController"] = NotDataCategory.TenantAdminAttribute,
            ["ServicesController"] = NotDataCategory.TenantAdminAttribute,

            ["ChatIdentityDirectoryController"] = NotDataCategory.InstanceKey,

            ["DevAdminController"] = NotDataCategory.DevelopmentOnly,
            ["DevAuthController"] = NotDataCategory.DevelopmentOnly,

            ["SetupController"] = NotDataCategory.Setup,
            ["PlatformController"] = NotDataCategory.Setup,
            ["MyTenantsController"] = NotDataCategory.Setup,

            ["AvatarController"] = NotDataCategory.IdentityAndDelegation,
            ["ChatIdentityController"] = NotDataCategory.IdentityAndDelegation,
            ["ConnectedAppsController"] = NotDataCategory.IdentityAndDelegation,
            ["GuestLinkController"] = NotDataCategory.IdentityAndDelegation,
            ["MemberInviteController"] = NotDataCategory.IdentityAndDelegation,
            ["MembershipRequestController"] = NotDataCategory.IdentityAndDelegation,
            ["RoleController"] = NotDataCategory.IdentityAndDelegation,
            ["SessionsController"] = NotDataCategory.IdentityAndDelegation,
            ["ShareLinkController"] = NotDataCategory.IdentityAndDelegation,

            ["ConnectorFoodEntriesController"] = NotDataCategory.ConnectorFoodImport,

            ["ConfigurationController"] = NotDataCategory.TenantSettingsScope,
            ["MyFitnessPalSettingsController"] = NotDataCategory.TenantSettingsScope,
            ["WebhookSettingsController"] = NotDataCategory.TenantSettingsScope,

            ["CareLinkConnectController"] = NotDataCategory.ConnectorHandlerGuard,

            ["DebugController"] = NotDataCategory.ComputesAndReturns,
            ["StatisticsController"] = NotDataCategory.ComputesAndReturns,

            // Both replay actions compute over the STORED alert rules and glucose history — the
            // body supplies only the window — and return the events that would have fired.
            // AlertReplayService has no SaveChangesAsync and adds to no DbSet, so a POST here is a
            // read the body shape forced off GET. As a read it is gated: the class requires
            // alerts.read and AlertReplayReadScopeGuard drops the fact timelines outside the
            // caller's categories, asserted by AlertReplayReadScopeTests.
            ["AlertReplayController"] = NotDataCategory.ComputesAndReturns,

            ["ClockFacesController"] = NotDataCategory.SplitSharingVocabulary,
            ["CoachMarkController"] = NotDataCategory.PresentationState,
            ["UserPreferencesController"] = NotDataCategory.PresentationState,

            ["AnalyticsController"] = NotDataCategory.TenantOperationalConfig,
            ["AuditController"] = NotDataCategory.TenantOperationalConfig,
            ["GlucoseProcessingSettingsController"] = NotDataCategory.TenantOperationalConfig,
            ["SystemEventsController"] = NotDataCategory.TenantOperationalConfig,
            ["TenantSettingsController"] = NotDataCategory.TenantOperationalConfig,

            ["SupportController"] = NotDataCategory.OutboundOnly,
            ["SystemController"] = NotDataCategory.OutboundOnly,
        };

    /// <summary>
    /// Write actions under <see cref="V4Namespace"/> that deliberately carry no scope attribute,
    /// keyed <c>Controller.Action</c> with the reason. Any other ungated write action fails the
    /// guard, so a new non-base V4 controller cannot be added without either a gate or a decision
    /// recorded here.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> GateExemptWriteActions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // A single activity payload decomposes into a different data category per record, so the
            // required scope is not known until the handler has read the body. ActivityController
            // calls ActivityWriteScopeGuard.FindMissingScope per record instead, which no attribute
            // scan can see.
            ["ActivityController.CreateActivities"] = NotDataCategory.PerRecordGuard,
            ["ActivityController.UpdateActivity"] = NotDataCategory.PerRecordGuard,
            ["ActivityController.DeleteActivity"] = NotDataCategory.PerRecordGuard,

            // state_spans holds four data categories behind one table and the caller picks which by
            // setting Category in the body, so StateSpanWriteScopeGuard resolves the scope per
            // record. A flat controller scope under-gated three of the four — notably DataExclusion,
            // which decides whether glucose readings count towards analytics and reports.
            ["StateSpansController.CreateStateSpan"] = NotDataCategory.PerRecordGuard,
            ["StateSpansController.UpdateStateSpan"] = NotDataCategory.PerRecordGuard,
            ["StateSpansController.DeleteStateSpan"] = NotDataCategory.PerRecordGuard,

            // The rest of DiscrepancyController is [RequireAdmin]; the ingest route is deliberately
            // [AllowAnonymous], so there is no credential whose scopes could be checked.
            ["DiscrepancyController.IngestDiscrepancy"] = NotDataCategory.AnonymousByDeclaration,

            // The caller's own notification bookkeeping: MarkAsRead / MarkAllAsRead set read_at,
            // DismissNotification sets the archive flags. Each is confined to a row whose UserId is
            // the caller's subject (InAppNotificationService checks it, and the mark-all repository
            // query is keyed by user), and none changes alert state — archiving an alert.firing
            // notification neither acknowledges nor silences the excursion behind it. Same category
            // as CoachMarkController and UserPreferencesController above; the rest of
            // NotificationsController is gated on alerts.readwrite.
            ["NotificationsController.MarkAsRead"] = NotDataCategory.PresentationState,
            ["NotificationsController.MarkAllAsRead"] = NotDataCategory.PresentationState,
            ["NotificationsController.DismissNotification"] = NotDataCategory.PresentationState,

            // Both open a pull request on the upstream repository through the GitHub API and
            // persist no tenant row, the same shape as SupportController above. The relay route is
            // additionally [AllowAnonymous] by design: it is the ingress for instances that have no
            // PAT of their own, so there is no credential whose scopes could be checked.
            // The draft store holds the caller's own in-progress translations, keyed by their
            // subject id, and nothing else reads it. SubmitDrafts opens the same upstream PR as
            // SubmitContribution and then deletes the drafts it managed to apply.
            ["TranslationsController.UpsertDrafts"] = NotDataCategory.PresentationState,
            ["TranslationsController.ClearDrafts"] = NotDataCategory.PresentationState,
            ["TranslationsController.SubmitDrafts"] = NotDataCategory.PresentationState,

            ["TranslationsController.SubmitContribution"] = NotDataCategory.OutboundOnly,
            ["TranslationsController.AcceptRelayedContribution"] = NotDataCategory.AnonymousByDeclaration,
        };

    /// <summary>
    /// The write scope each single-category V4 controller requires, whether it declares it through
    /// <see cref="IWriteScopedController"/> or repeats a <see cref="RequireScopeAttribute"/> per
    /// action. Derived from the data category the record's table belongs to in
    /// <see cref="ShareDataCategories"/> (the read-side classification) and from the scope the
    /// equivalent V1 endpoint requires.
    /// <see cref="EveryNonBaseV4WriteAction_HasAnExpectedScopeOrAnExemption"/> keeps it exhaustive,
    /// so a new V4 controller must be added here with a deliberate category or exempted.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ExpectedWriteScopes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // glucose: sensor_glucose / meter_glucose / calibrations / bg_checks all sit under
            // glucose.read; v1 entries create requires glucose.readwrite.
            ["SensorGlucoseController"] = OAuthScopes.GlucoseReadWrite,
            ["MeterGlucoseController"] = OAuthScopes.GlucoseReadWrite,
            ["CalibrationController"] = OAuthScopes.GlucoseReadWrite,
            ["BGCheckController"] = OAuthScopes.GlucoseReadWrite,

            // glucose: accepting a compression-low suggestion writes a DataExclusion state span,
            // which decides whether the flagged readings count towards analytics and reports —
            // the category StateSpanWriteScopeGuard maps to glucose.readwrite. Dismiss, delete and
            // detection write the compression_low_suggestions rows that propose one.
            ["CompressionLowController"] = OAuthScopes.GlucoseReadWrite,

            // treatments: boluses / basal_injections / bolus_calculations sit under treatments.read;
            // notes are the V4 form of a legacy text treatment. v1 treatments create requires
            // treatments.readwrite.
            ["BolusController"] = OAuthScopes.TreatmentsReadWrite,
            ["BasalInjectionController"] = OAuthScopes.TreatmentsReadWrite,
            ["BolusCalculationController"] = OAuthScopes.TreatmentsReadWrite,
            ["NoteController"] = OAuthScopes.TreatmentsReadWrite,

            // devices: device_events sits under devices.read, matching the sibling snapshot
            // controllers (ApsSnapshotController's bulk write requires devices.readwrite).
            ["DeviceEventController"] = OAuthScopes.DevicesReadWrite,

            // therapy: therapy_settings and the basal / carb ratio / sensitivity / target range
            // schedules are the therapy category (therapy.read on the read side); v1 and v3 profile
            // writes require therapy.readwrite.
            ["ProfileController"] = OAuthScopes.TherapyReadWrite,

            // treatments: carb_intakes sits under treatments.read, POST /meals also writes a bolus,
            // and treatment_foods is keyed by carb intake (the food catalog is only read).
            ["NutritionController"] = OAuthScopes.TreatmentsReadWrite,

            // food: foods sits under food.read and user_food_favorites is the same category;
            // v1 and v3 food writes require food.readwrite.
            ["FoodsController"] = OAuthScopes.FoodReadWrite,

            // therapy: body_weights has no category scope of its own. The record is patient clinical
            // configuration written from the Patient Record settings form alongside therapy settings.
            ["BodyWeightController"] = OAuthScopes.TherapyReadWrite,

            // treatments: state_spans is the decomposed form of the legacy treatment events
            // (temporary target, profile switch, exercise, illness, travel) and of the temp-basal
            // spans V3 TreatmentsController writes. v1 activity writes require treatments.readwrite.

            // therapy: the timezone timeline is the same patient clinical configuration as the
            // timezone on patient_records, which PatientRecordController gates on therapy.readwrite.
            ["TimezoneTimelineController"] = OAuthScopes.TherapyReadWrite,

            // alerts: the tracker_* tables are monitoring state, not patient observations — a
            // definition's thresholds become managed alert rules and acking an instance acks an
            // alert excursion. v1/v2 notification writes require alerts.readwrite.
            ["TrackersController"] = OAuthScopes.AlertsReadWrite,

            // alerts: UISettingsConfiguration is tenant-wide and carries NotificationSettings, the
            // alarm thresholds and profiles that decide whether a low-glucose alert fires.
            ["UISettingsController"] = OAuthScopes.AlertsReadWrite,

            // alerts: the rest of the alert surface. A rule and its channels decide whether an
            // alert reaches anyone; acknowledging, snoozing and recording a delivery outcome close
            // or silence an excursion; a DND window and the tenant-wide manual toggle suppress
            // delivery outright; a custom sound is what an alert plays; an invite attaches a
            // follower to a rule channel; an in-app notification is a delivery channel. v1/v2
            // notification writes require alerts.readwrite.
            ["AlertRulesController"] = OAuthScopes.AlertsReadWrite,
            ["AlertsController"] = OAuthScopes.AlertsReadWrite,
            ["DndWindowsController"] = OAuthScopes.AlertsReadWrite,
            ["TenantAlertSettingsController"] = OAuthScopes.AlertsReadWrite,
            ["AlertCustomSoundsController"] = OAuthScopes.AlertsReadWrite,
            ["AlertInvitesController"] = OAuthScopes.AlertsReadWrite,
            ["NotificationsController"] = OAuthScopes.AlertsReadWrite,

            // devices: a reservoir report is stored as a manual-source pump_snapshots row, and a fill
            // additionally writes a device_events row. Both are the devices category.
            ["ReservoirReportsController"] = OAuthScopes.DevicesReadWrite,

            // Controllers that gate with a per-action [RequireScope] rather than a declaration. Their
            // categories are their own dedicated tables.
            ["SleepController"] = OAuthScopes.SleepReadWrite,
            ["HeartRateController"] = OAuthScopes.HeartRateReadWrite,
            ["StepCountController"] = OAuthScopes.StepCountReadWrite,
            ["TempBasalController"] = OAuthScopes.TreatmentsReadWrite,

            // client_devices is the member's own registered notification targets, not patient data.
            // The actions accept either member-personal capability scope; device.notify is the one
            // asserted, and neither is satisfiable by a read-only credential.
            ["ClientDevicesController"] = OAuthScopes.DeviceNotify,
        };

    /// <summary>
    /// Per-action expectations for controllers whose writes span two data categories, so a single
    /// declared scope would either over- or under-gate. Asserted exhaustively against the
    /// controller's write actions.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ExpectedActionScopes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // patient_records carries the clinical configuration (diabetes type, timezone) the
            // profile and bolus maths read; patient_insulins carries DIA / peak / curve, the inputs
            // to the IOB calculation. Both are therapy settings.
            ["PatientRecordController.UpdatePatientRecord"] = OAuthScopes.TherapyReadWrite,
            ["PatientRecordController.CreateInsulin"] = OAuthScopes.TherapyReadWrite,
            ["PatientRecordController.UpdateInsulin"] = OAuthScopes.TherapyReadWrite,
            ["PatientRecordController.DeleteInsulin"] = OAuthScopes.TherapyReadWrite,

            // patient_devices is the device registry (and CreateDevice/UpdateDevice resolve a row in
            // the `devices` master table), matching devices.readwrite on the v1/v3 device endpoints.
            ["PatientRecordController.CreateDevice"] = OAuthScopes.DevicesReadWrite,
            ["PatientRecordController.UpdateDevice"] = OAuthScopes.DevicesReadWrite,
            ["PatientRecordController.DeleteDevice"] = OAuthScopes.DevicesReadWrite,
            ["PatientRecordController.ReorderDevices"] = OAuthScopes.DevicesReadWrite,

            // Accepting a match writes a treatment_foods row keyed by the carb intake — a COB input,
            // the same table NutritionController gates on treatments.readwrite. Dismissing writes
            // only the connector_food_entries status, which is the food category.
            ["MealMatchingController.AcceptMatch"] = OAuthScopes.TreatmentsReadWrite,
            ["MealMatchingController.DismissMatch"] = OAuthScopes.FoodReadWrite,
        };

    [Fact]
    public void ReadOnlyGuestLinkScopes_CannotWriteGlucose()
    {
        // The maximum a guest link can hold: GuestLinkService.AllowedGuestScopes is read-only.
        var guestScopes = OAuthScopes.Normalize([OAuthScopes.HealthRead, OAuthScopes.TherapyRead, OAuthScopes.ReportsRead]);

        var result = Evaluate(NewSensorGlucoseController(), authenticated: true, guestScopes.ToArray());

        result.Should().BeOfType<ForbidResult>(
            "a read-only guest session must not be able to create, update, or delete a glucose reading");
    }

    [Fact]
    public void ReadScopedCredential_CannotWriteTreatments()
    {
        var result = Evaluate(NewBolusController(), authenticated: true, OAuthScopes.TreatmentsRead, OAuthScopes.GlucoseRead);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public void ReadWriteScopedCredential_CanWrite()
    {
        Evaluate(NewBolusController(), authenticated: true, OAuthScopes.TreatmentsReadWrite)
            .Should().BeNull();
        Evaluate(NewSensorGlucoseController(), authenticated: true, OAuthScopes.GlucoseReadWrite)
            .Should().BeNull();
    }

    [Fact]
    public void ReadWriteScopeForAnotherCategory_DoesNotUnlockWrites()
    {
        Evaluate(NewBolusController(), authenticated: true, OAuthScopes.GlucoseReadWrite)
            .Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public void FullAccessGrant_CanWrite()
    {
        // A legacy api-secret normalises to "*" — the uploaders that authenticate that way
        // (AAPS/Loop/Trio/xDrip+) must keep writing.
        Evaluate(NewSensorGlucoseController(), authenticated: true, OAuthScopes.FullAccess)
            .Should().BeNull();
    }

    [Fact]
    public void UnauthenticatedRequest_IsRejectedWith401()
    {
        Evaluate(NewSensorGlucoseController(), authenticated: false, OAuthScopes.GlucoseReadWrite)
            .Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public void ControllerDeclaringNoWriteScope_IsDenied()
    {
        // Fail closed: the filter denies rather than admits when there is no declaration to check,
        // including on a controller that does not implement IWriteScopedController at all.
        Evaluate(new UndeclaredController(), authenticated: true, OAuthScopes.FullAccess)
            .Should().BeOfType<ForbidResult>();
        Evaluate(new EmptyScopeController(), authenticated: true, OAuthScopes.FullAccess)
            .Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public void EveryWriteScopedController_DeclaresItsExpectedWriteScope()
    {
        var controllers = WriteScopedControllers().ToList();

        controllers.Select(t => t.Name).Should().BeSubsetOf(ExpectedWriteScopes.Keys,
            "every write-scoped V4 controller must be mapped to a data category in ExpectedWriteScopes");

        foreach (var controller in controllers)
        {
            var declared = ((IWriteScopedController)ScopeDeclarationInstance(controller)).WriteScope;

            declared.Should().Be(ExpectedWriteScopes[controller.Name],
                $"{controller.Name} must gate its writes on its own data category");
            OAuthScopes.SatisfiesScope(AllReadScopes, declared)
                .Should().BeFalse($"{controller.Name}'s write scope must not be satisfiable by read-only scopes");

            // A declared scope that is not in the taxonomy, or that no seed role can hold, silently
            // makes the controller owner-only: SatisfiesScope short-circuits on "*", so an owner
            // never notices. sharing.readwrite is the live example — a real constant that survives
            // Normalize but that no role maps to.
            OAuthScopes.AllScopes.Should().Contain(declared,
                $"{controller.Name} declares '{declared}', which is not a scope in the taxonomy");

            TenantPermissions.SeedRolePermissions
                .Where(role => role.Key != TenantPermissions.SeedRoles.Owner)
                .Should().Contain(
                    role => OAuthScopes.SatisfiesScope(
                        OAuthScopes.NormalizeMemberPermissions(role.Value), declared),
                    $"{controller.Name}'s write scope '{declared}' must be reachable by at least one "
                    + "non-owner seed role, or the controller is owner-only by accident");
        }
    }

    [Fact]
    public void MixedCategoryControllers_MapEveryWriteActionToItsCategory()
    {
        // A controller that writes two categories has per-action expectations, and they must stay
        // exhaustive: a new write action there has to name the category it mutates. The controllers
        // are derived from the map's own keys rather than listed, so adding one is a single edit.
        var mixedCategory = ExpectedActionScopes.Keys
            .Select(key => key.Split('.', 2)[0])
            .ToHashSet(StringComparer.Ordinal);

        var actions = V4Controllers()
            .Where(c => mixedCategory.Contains(c.Name))
            .SelectMany(c => WriteActions(c).Select(a => $"{c.Name}.{a.Name}"));

        actions.Should().BeEquivalentTo(ExpectedActionScopes.Keys,
            "every write action on a mixed-category controller must be mapped in ExpectedActionScopes");
    }

    [Fact]
    public void EveryV4WriteAction_IsScopeGated()
    {
        var violations = new List<string>();
        var readSatisfiable = new List<string>();
        var writeActionsChecked = 0;

        foreach (var controller in V4Controllers())
        {
            foreach (var action in WriteActions(controller))
            {
                writeActionsChecked++;

                if (IsGateExempt(controller, action))
                    continue;

                var attributes = action.GetCustomAttributes(inherit: true);
                var gated = attributes.Any(a => a is RequireDeclaredWriteScopeAttribute
                                                     or RequireScopeAttribute
                                                     or RequirePermissionAttribute);

                if (!gated)
                {
                    var verbs = HttpVerbs(action);
                    violations.Add($"{controller.Name}.{action.Name} [{string.Join(",", verbs)}]");
                    continue;
                }

                // A gate naming a read scope would admit read-only credentials, which the presence
                // check alone cannot catch.
                foreach (var required in RequiredScopes(controller, action))
                {
                    if (OAuthScopes.SatisfiesScope(AllReadScopes, required))
                        readSatisfiable.Add($"{controller.Name}.{action.Name} requires '{required}'");
                }
            }
        }

        // Sanity: the scan must find the write surface, or the assertions below pass vacuously. The
        // floor tracks the namespace sweep (301 write actions when it was widened), so a sweep that
        // silently narrows back to a subset fails here rather than passing on the remainder.
        writeActionsChecked.Should().BeGreaterThan(280,
            "the reflection scan should discover every write endpoint under " + V4Namespace);

        violations.Should().BeEmpty(
            "every write action under " + V4Namespace + " must carry [RequireDeclaredWriteScope] "
            + "(base CRUD actions and their overrides), an explicit [RequireScope], or an entry in "
            + "GateExemptWriteActions stating why. Unprotected: " + string.Join("; ", violations));

        readSatisfiable.Should().BeEmpty(
            "a write action must require a readwrite (or full-access) scope: " + string.Join("; ", readSatisfiable));
    }

    [Fact]
    public void EveryGateExemption_NamesALiveWriteAction()
    {
        // An exemption left behind after its action was gated, renamed, or deleted would silently
        // excuse a future action that reuses the name.
        var controllers = V4Controllers().ToList();

        var writeActions = controllers
            .SelectMany(c => WriteActions(c).Select(a => $"{c.Name}.{a.Name}"))
            .ToHashSet(StringComparer.Ordinal);
        GateExemptWriteActions.Keys.Should().BeSubsetOf(writeActions);

        var controllersWithWrites = controllers
            .Where(c => WriteActions(c).Any())
            .Select(c => c.Name)
            .ToHashSet(StringComparer.Ordinal);
        GateExemptControllers.Keys.Should().BeSubsetOf(controllersWithWrites);
    }

    /// <summary>
    /// The per-record exemptions are the one reason whose mechanism is a method call in the handler,
    /// which no attribute scan can see — delete the call and the sweep stays green. So each one must
    /// be covered by a test that drives the real handler. That coverage is listed here rather than
    /// discovered, so adding a per-record exemption without adding a behavioural test fails.
    /// </summary>
    [Fact]
    public void PerRecordGuardedActions_HaveABehaviouralTest()
    {
        var covered = new[]
        {
            // StateSpanWriteScopeTests drives all three through the real handler.
            "StateSpansController.CreateStateSpan",
            "StateSpansController.UpdateStateSpan",
            "StateSpansController.DeleteStateSpan",
            // ActivityWriteScopeGuardTests covers the guard; the handler wiring is asserted by
            // ActivityControllerScopeTests.
            "ActivityController.CreateActivities",
            "ActivityController.UpdateActivity",
            "ActivityController.DeleteActivity",
        };

        GateExemptWriteActions
            .Where(entry => entry.Value == NotDataCategory.PerRecordGuard)
            .Select(entry => entry.Key)
            .Should().BeEquivalentTo(covered,
                "every per-record-guarded action needs a test that drives its handler");
    }

    /// <summary>
    /// An attribute-routed action with no verb constraint answers EVERY verb, so it accepts POST
    /// while carrying no <c>HttpPost</c> for the write sweep to find. There are none today; this
    /// keeps it that way rather than leaving a silent hole in the sweep's selector.
    /// </summary>
    [Fact]
    public void EveryV4Action_ConstrainsItsVerb()
    {
        var unconstrained = V4Controllers()
            .SelectMany(c => ActionCandidates(c).Select(a => (Controller: c, Action: a)))
            .Where(x => x.Action.GetCustomAttributes(inherit: true).OfType<IRouteTemplateProvider>().Any()
                        || x.Action.GetCustomAttributes(inherit: true).OfType<IActionHttpMethodProvider>().Any())
            .Where(x => HttpVerbs(x.Action).Count == 0)
            .Select(x => $"{x.Controller.Name}.{x.Action.Name}")
            .ToList();

        unconstrained.Should().BeEmpty(
            "an action with no verb constraint answers POST too, so the write sweep would miss it");
    }

    /// <summary>
    /// The sweep selects by namespace, so a controller routed under <c>api/v4</c> from a different
    /// namespace would be invisible to it. Assert the two agree rather than relying on convention.
    /// </summary>
    [Fact]
    public void EveryControllerRoutedUnderApiV4_IsInTheV4Namespace()
    {
        var strays = ApiAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ControllerBase).IsAssignableFrom(t))
            .Where(t => !IsUnderV4Namespace(t))
            .Where(t => t.GetCustomAttributes<RouteAttribute>(inherit: true)
                .Any(r => r.Template.StartsWith("api/v4", StringComparison.OrdinalIgnoreCase)))
            .Select(t => $"{t.Namespace}.{t.Name}")
            .ToList();

        strays.Should().BeEmpty(
            "a controller routed under api/v4 outside the V4 namespace escapes the write-scope sweep");
    }

    [Fact]
    public void EveryExemptionClaimingAnAttribute_ActuallyCarriesIt()
    {
        // The reason a controller is exempt has to stay true. Where the mechanism is an attribute or
        // a namespace, assert it rather than trusting the prose: a controller that loses its
        // [Authorize(Roles = "platform_admin")] must fail here, not sit exempt on a stale claim.
        var broken = new List<string>();

        foreach (var (name, reason) in GateExemptControllers)
        {
            var controller = V4Controllers().Single(t => t.Name == name);
            var attributes = controller.GetCustomAttributes(inherit: true);

            var holds = reason switch
            {
                NotDataCategory.PlatformAdminRole => attributes
                    .OfType<AuthorizeAttribute>()
                    .Any(a => a.Roles?.Contains("platform_admin", StringComparison.Ordinal) == true),
                NotDataCategory.TenantAdminAttribute => CarriesAttribute(controller, "RequireAdminAttribute"),
                NotDataCategory.TenantSettingsScope => GovernsEveryWrite(controller,
                    a => a is RequireScopeAttribute scope
                         && scope.Scopes.Contains(TenantPermissions.TenantSettings, StringComparer.Ordinal)),
                NotDataCategory.InstanceKey => attributes
                    .Any(a => a.GetType().Name == "RequireInstanceKeyAuthAttribute"),
                NotDataCategory.DevelopmentOnly => controller.Namespace == V4Namespace + ".DevOnly",
                _ => true,
            };

            if (!holds)
                broken.Add($"{name} claims '{reason}' but does not carry it");
        }

        broken.Should().BeEmpty(string.Join("; ", broken));
    }

    /// <summary>
    /// Whether the named attribute governs every write action on the controller — present on the
    /// class, or repeated on each write action.
    /// </summary>
    private static bool CarriesAttribute(Type controller, string attributeTypeName) =>
        GovernsEveryWrite(controller, a => a.GetType().Name == attributeTypeName);

    /// <summary>
    /// Whether an attribute matching <paramref name="predicate"/> governs every write action on the
    /// controller — present on the class, or repeated on each write action.
    /// </summary>
    private static bool GovernsEveryWrite(Type controller, Func<object, bool> predicate) =>
        controller.GetCustomAttributes(inherit: true).Any(predicate)
        || WriteActions(controller).All(a => a.GetCustomAttributes(inherit: true).Any(predicate));

    /// <summary>
    /// <see cref="NotDataCategory.ConnectorHandlerGuard"/> is a method call in the handler, which no
    /// attribute scan can see — delete the call and the sweep stays green. So each controller filed
    /// under it must be covered by a test that drives the real handler. That coverage is listed here
    /// rather than discovered, so filing a controller under it without a behavioural test fails.
    /// </summary>
    [Fact]
    public void HandlerGuardedControllers_HaveABehaviouralTest()
    {
        // ConnectorConfigurationScopeTests drives Start and Complete through the real handler.
        var covered = new[] { "CareLinkConnectController" };

        GateExemptControllers
            .Where(entry => entry.Value == NotDataCategory.ConnectorHandlerGuard)
            .Select(entry => entry.Key)
            .Should().BeEquivalentTo(covered,
                "every handler-guarded connector controller needs a test that drives its handler");
    }

    private static bool IsGateExempt(Type controller, MethodInfo action) =>
        GateExemptControllers.ContainsKey(controller.Name)
        || GateExemptWriteActions.ContainsKey($"{controller.Name}.{action.Name}");

    [Theory]
    [MemberData(nameof(NonBaseWriteActions))]
    public void NonBaseWriteAction_AdmitsItsCategoryAndDeniesReadOnlyCredentials(
        string controllerTypeName, string actionName, string expectedScope)
    {
        var controller = ApiAssembly.GetType(controllerTypeName)!;

        // The maximum a guest link holds (GuestLinkService.AllowedGuestScopes, read-only).
        var guestScopes = OAuthScopes.Normalize([OAuthScopes.HealthRead, OAuthScopes.TherapyRead, OAuthScopes.ReportsRead]);

        EvaluateAction(controller, actionName, authenticated: true, guestScopes.ToArray())
            .Should().BeOfType<ForbidResult>("a read-only session must not reach this write action");

        EvaluateAction(controller, actionName, authenticated: true, expectedScope)
            .Should().BeNull($"a credential holding {expectedScope} must keep writing here");

        EvaluateAction(controller, actionName, authenticated: true, OAuthScopes.FullAccess)
            .Should().BeNull("a tenant owner and a legacy api-secret both normalise to \"*\"");

        EvaluateAction(controller, actionName, authenticated: false, expectedScope)
            .Should().BeOfType<UnauthorizedResult>();

        var otherCategory = expectedScope == OAuthScopes.GlucoseReadWrite
            ? OAuthScopes.FoodReadWrite
            : OAuthScopes.GlucoseReadWrite;
        EvaluateAction(controller, actionName, authenticated: true, otherCategory)
            .Should().BeOfType<ForbidResult>("another category's readwrite scope must not unlock this write");
    }

    /// <summary>
    /// Every write action on a non-base V4 controller, paired with the scope its category requires.
    /// Generated by reflection over <see cref="NonBaseV4Controllers"/> so a new controller or action
    /// is covered without editing the theory.
    /// <see cref="EveryNonBaseV4WriteAction_HasAnExpectedScopeOrAnExemption"/> keeps the mapping
    /// exhaustive, so an unmapped action fails loudly rather than dropping out of this data set.
    /// </summary>
    public static TheoryData<string, string, string> NonBaseWriteActions()
    {
        var data = new TheoryData<string, string, string>();

        foreach (var (controller, action, expected) in MappedNonBaseWriteActions())
        {
            data.Add(controller.FullName!, action.Name, expected);
        }

        return data;
    }

    [Fact]
    public void EveryNonBaseV4WriteAction_HasAnExpectedScopeOrAnExemption()
    {
        var unmapped = NonBaseV4Controllers()
            .SelectMany(c => WriteActions(c).Select(a => (Controller: c, Action: a)))
            .Where(x => !IsGateExempt(x.Controller, x.Action)
                        && !ExpectedActionScopes.ContainsKey($"{x.Controller.Name}.{x.Action.Name}")
                        && !ExpectedWriteScopes.ContainsKey(x.Controller.Name))
            .Select(x => $"{x.Controller.Name}.{x.Action.Name}")
            .ToList();

        unmapped.Should().BeEmpty(
            "every non-base V4 write action must name the data category it mutates, in "
            + "ExpectedWriteScopes (single-category controller) or ExpectedActionScopes "
            + "(mixed-category), or be exempted in GateExemptWriteActions. Unmapped: "
            + string.Join("; ", unmapped));
    }

    /// <summary>
    /// The non-base V4 write actions that have a declared scope expectation, with that scope.
    /// </summary>
    private static IEnumerable<(Type Controller, MethodInfo Action, string Expected)> MappedNonBaseWriteActions()
    {
        foreach (var controller in NonBaseV4Controllers().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            foreach (var action in WriteActions(controller))
            {
                if (IsGateExempt(controller, action))
                    continue;

                if (ExpectedActionScopes.TryGetValue($"{controller.Name}.{action.Name}", out var perAction))
                    yield return (controller, action, perAction);
                else if (ExpectedWriteScopes.TryGetValue(controller.Name, out var perController))
                    yield return (controller, action, perController);
            }
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private static IActionResult? Evaluate(object controller, bool authenticated, params string[] grantedScopes)
    {
        var actionContext = new ActionContext(
            NewHttpContext(authenticated, grantedScopes), new RouteData(), new ActionDescriptor());
        var context = new ActionExecutingContext(
            actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller);

        new RequireDeclaredWriteScopeAttribute().OnActionExecuting(context);
        return context.Result;
    }

    /// <summary>
    /// Runs the filters an action actually declares (authorization filters first, as MVC does), so
    /// the assertion covers both which gate is present and the scope it names.
    /// </summary>
    private static IActionResult? EvaluateAction(
        Type controller, string actionName, bool authenticated, params string[] grantedScopes)
    {
        var action = controller.GetMethod(actionName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"{controller.Name} has no action named {actionName}");
        var filters = action.GetCustomAttributes(inherit: true);

        var actionContext = new ActionContext(
            NewHttpContext(authenticated, grantedScopes), new RouteData(), new ActionDescriptor());

        foreach (var filter in filters.OfType<IAuthorizationFilter>())
        {
            var authorizationContext = new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());
            filter.OnAuthorization(authorizationContext);
            if (authorizationContext.Result is not null)
                return authorizationContext.Result;
        }

        var executingContext = new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            ScopeDeclarationInstance(controller));

        foreach (var filter in filters.OfType<IActionFilter>())
        {
            filter.OnActionExecuting(executingContext);
            if (executingContext.Result is not null)
                return executingContext.Result;
        }

        return null;
    }

    private static DefaultHttpContext NewHttpContext(bool authenticated, string[] grantedScopes)
    {
        var httpContext = new DefaultHttpContext();
        if (authenticated)
            httpContext.Items["AuthContext"] = new AuthContext { IsAuthenticated = true };
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>(grantedScopes);
        return httpContext;
    }

    /// <summary>The scopes an action's gate requires: the controller's declaration, or the explicit list.</summary>
    private static IEnumerable<string> RequiredScopes(Type controller, MethodInfo action)
    {
        var attributes = action.GetCustomAttributes(inherit: true);

        if (attributes.Any(a => a is RequireDeclaredWriteScopeAttribute)
            && ScopeDeclarationInstance(controller) is IWriteScopedController declared)
            yield return declared.WriteScope;

        foreach (var scope in attributes.OfType<RequireScopeAttribute>().SelectMany(a => a.Scopes))
            yield return scope;
    }

    private static Assembly ApiAssembly => typeof(RequireDeclaredWriteScopeAttribute).Assembly;

    private static IEnumerable<Type> WriteScopedControllers() =>
        ApiAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IWriteScopedController).IsAssignableFrom(t));

    private static IEnumerable<Type> V4ControllerBaseSubclasses() =>
        ApiAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && DerivesFromV4Base(t));

    /// <summary>
    /// Every concrete controller under <see cref="V4Namespace"/>. Swept by namespace rather than
    /// listed, so a new V4 controller is under the guard the moment it exists.
    /// </summary>
    private static IEnumerable<Type> V4Controllers() =>
        ApiAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && typeof(ControllerBase).IsAssignableFrom(t)
                        && IsUnderV4Namespace(t));

    /// <summary>
    /// The V4 controllers that cannot inherit
    /// <see cref="V4CrudControllerBase{TModel, TCreateRequest, TUpdateRequest, TRepository}"/>'s
    /// gated write actions and so carry their own gates.
    /// </summary>
    private static IEnumerable<Type> NonBaseV4Controllers() =>
        V4Controllers().Except(V4ControllerBaseSubclasses());

    private static bool IsUnderV4Namespace(Type type) =>
        type.Namespace == V4Namespace
        || type.Namespace?.StartsWith(V4Namespace + ".", StringComparison.Ordinal) == true;

    private static IEnumerable<MethodInfo> WriteActions(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(action => HttpVerbs(action).Overlaps(WriteVerbs));

    /// <summary>
    /// The verbs an action answers. Keyed off <see cref="IActionHttpMethodProvider"/> rather than
    /// <see cref="HttpMethodAttribute"/>, because <c>[AcceptVerbs]</c> implements the interface
    /// without deriving from the attribute and would otherwise be invisible to the sweep.
    /// </summary>
    private static HashSet<string> HttpVerbs(MethodInfo action) =>
        action.GetCustomAttributes(inherit: true)
            .OfType<IActionHttpMethodProvider>()
            .SelectMany(a => a.HttpMethods)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The methods MVC would treat as actions: declared on the controller itself rather than
    /// inherited from <see cref="ControllerBase"/>, not a property accessor, not <c>[NonAction]</c>.
    /// Used by <see cref="EveryV4Action_ConstrainsItsVerb"/>, since an action with no verb constraint
    /// answers every verb — including POST — and would otherwise be invisible to the write sweep.
    /// </summary>
    private static IEnumerable<MethodInfo> ActionCandidates(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName
                        && m.DeclaringType != typeof(ControllerBase)
                        && m.DeclaringType != typeof(object)
                        && !m.GetCustomAttributes(inherit: true).Any(a => a is NonActionAttribute));

    private static bool DerivesFromV4Base(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType
                && current.GetGenericTypeDefinition() is var definition
                && (definition == typeof(V4CrudControllerBase<,,,>) || definition == typeof(V4ReadOnlyControllerBase<,>)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// A controller instance for reading <see cref="IWriteScopedController.WriteScope"/> and for
    /// running the write-scope filters. The getters return a constant, and the filters touch nothing
    /// else, so the controller is left unconstructed — several of these controllers take a
    /// <c>NocturneDbContext</c>, which has no mockable constructor.
    /// </summary>
    private static object ScopeDeclarationInstance(Type controller) =>
        RuntimeHelpers.GetUninitializedObject(controller);

    private static object NewSensorGlucoseController() =>
        ScopeDeclarationInstance(typeof(Nocturne.API.Controllers.V4.Glucose.SensorGlucoseController));

    private static object NewBolusController() =>
        ScopeDeclarationInstance(typeof(Nocturne.API.Controllers.V4.Treatments.BolusController));

    /// <summary>Stands in for a controller that never declared a write scope.</summary>
    private sealed class UndeclaredController : ControllerBase;

    /// <summary>Stands in for a controller whose declaration is present but empty.</summary>
    private sealed class EmptyScopeController : ControllerBase, IWriteScopedController
    {
        public string WriteScope => string.Empty;
    }
}
