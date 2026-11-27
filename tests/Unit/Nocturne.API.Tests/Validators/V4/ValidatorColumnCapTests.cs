using System.ComponentModel.DataAnnotations;
using System.Reflection;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Validators;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Nocturne.API.Validators.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Validators.V4;

/// <summary>
/// A V4 write body never carries a string longer than the column it is stored in unanswered: its
/// validator caps it, so the value is refused as a 400 instead of failing the write. A column marked
/// <see cref="FitToColumnAttribute"/> is exempt only from needing a cap, since a connector or a legacy
/// upload writes it and is never refused. A validator that does cap one caps it at most at the column,
/// so the client is never told a longer value is accepted and then sees it cut.
/// </summary>
/// <remarks>
/// Every type a <c>[FromBody]</c> parameter of a V4 controller action carries (the body, and the
/// element types of the lists and objects inside it) that holds a string has a registered validator,
/// which its parent's validator runs over it. An action whose body need not be is listed by action
/// in <see cref="NotARecordWrite"/> or <see cref="NotYetCapped"/> with its reason, so an exemption
/// is exactly as wide as its reason; an entry fails once its action is gone or its body would pass
/// without it. A type whose name points to an entity (<see cref="EntityStemsByRequestStem"/> where the
/// name differs) also names the column of every string it carries, and its validator caps each one
/// at most at its column.
/// </remarks>
[Trait("Category", "Unit")]
public class ValidatorColumnCapTests
{
    private static readonly IModel Model =
        new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>().UseNpgsql("Host=unused").Options).Model;

    private static readonly Dictionary<string, string[]> EntityStemsByRequestStem = new()
    {
        ["Meal"] = ["Bolus", "CarbIntake"],
        ["PageView"] = ["PageViewCount"],
        ["ReservoirReport"] = ["PumpSnapshot", "DeviceEvent"],
        ["Activity"] = ["StateSpan"],
        ["CarbIntakeFood"] = ["TreatmentFood"],
        ["Notification"] = ["InAppNotification"],
        ["StartTrackerInstance"] = ["TrackerInstance"],
        ["CompleteTrackerInstance"] = ["TrackerInstance"],
        ["ApplyPreset"] = ["TrackerInstance"],
        ["NotificationThreshold"] = ["TrackerNotificationThreshold"],
        ["ConnectorFoodEntryImport"] = ["ConnectorFoodEntry"],
        ["ConnectorFoodImport"] = ["Food"],
        ["TimezoneEntry"] = ["TimezoneTimeline"],
        ["CoachMark"] = ["CoachMarkState"],
        ["DismissSetupStrip"] = ["Tenant"],
        ["ConfirmSetupDevice"] = ["PatientDevice"],
        ["AddSetupInsulin"] = ["PatientInsulin"],
        ["AddSetupTracker"] = ["TrackerDefinition"],
        ["Acknowledge"] = ["AlertExcursion"],
        ["MarkDelivered"] = ["AlertDelivery"],
        ["MarkFailed"] = ["AlertDelivery"],
        ["DiscrepancyAnalysisDto"] = ["DiscrepancyAnalysis"],
        ["DiscrepancyDetailDto"] = ["DiscrepancyDetail"],
    };

    private static readonly Dictionary<string, string> ColumnByRequestProperty = new()
    {
        ["CreateStateSpanRequest.OriginalId"] = "LegacyId",
        ["UpsertActivityRequest.Type"] = "State",
        ["UpsertActivityRequest.EnteredBy"] = "Source",
        ["UpsertHeartRateRequest.App"] = "EnteredBy",
        ["UpsertStepCountRequest.App"] = "EnteredBy",
        ["ApplyPresetRequest.OverrideNotes"] = "StartNotes",
        ["ConnectorFoodImport.BrandName"] = "Category",
        ["DismissSetupStripRequest.Revision"] = "SetupStripDismissedRevision",
        ["MarkFailedRequest.Error"] = "LastError",
    };

    private static readonly Dictionary<string, string> NotStoredVerbatim = new()
    {
        ["UpsertSensorGlucoseRequest.GlucoseProcessing"] = "parsed to an enum and stored as its name, so no value can overflow the column",
        ["ConnectorFoodImport.Unit"] = "cut to the length of the column on save",
        ["BodyWeight.Id"] = ParsedId,
        ["Food.Id"] = ParsedId,
        ["SleepSession.Id"] = "parsed to a GUID and never stored as text",
        ["CarbIntakeFoodRequest.FoodId"] = "looked up in the food catalogue and never stored",
    };

    private const string ParsedId = "parsed to a GUID, and kept as the original id only when it is a 24-character ObjectId";

    private const string ComputedResult = "not stored: the action returns a computed result";

    private const string NotStored = "not stored: the action parses it or passes it on, and keeps none of its strings";

    private const string StoredWhole = "stored whole as JSON in a jsonb or unbounded text column";

    private const string CappedByTheAction = "capped by the action or its service before it is stored";

    private const string DevelopmentOnly = "a development-only endpoint, absent outside Development";

    private const string IdentityAdministration = "an identity or tenant administration body, capped in its own change";

    private const string CarriesCredentials = "a body that carries credentials, capped in an owner-reviewed change";

    private static readonly Dictionary<string, string> NotARecordWrite = new()
    {
        ["AlertReplayController.FindFirings"] = ComputedResult,
        ["AlertReplayController.Replay"] = ComputedResult,
        ["AlertReplayController.ReplayDryRun"] = ComputedResult,
        ["AlertRulesController.TestFireDryRun"] = ComputedResult,
        ["CompatibilityController.TestApiComparison"] = ComputedResult,
        ["MigrationController.TestConnection"] = ComputedResult,
        ["OidcProviderAdminController.TestUnsaved"] = ComputedResult,
        ["StatisticsController.AnalyzeGlucoseData"] = ComputedResult,
        ["StatisticsController.AnalyzeGlucoseDataExtended"] = ComputedResult,
        ["StatisticsController.AssessAgainstTargets"] = ComputedResult,
        ["StatisticsController.AssessDataSufficiency"] = ComputedResult,
        ["StatisticsController.CalculateAveragedStats"] = ComputedResult,
        ["StatisticsController.CalculateGlucoseDistribution"] = ComputedResult,
        ["StatisticsController.CalculateGlycemicVariability"] = ComputedResult,
        ["StatisticsController.CalculateGRI"] = ComputedResult,
        ["StatisticsController.CalculateOverallAverages"] = ComputedResult,
        ["StatisticsController.CalculateSiteChangeImpact"] = ComputedResult,
        ["StatisticsController.CalculateTimeInRange"] = ComputedResult,
        ["StatisticsController.CalculateTreatmentSummary"] = ComputedResult,
        ["StatisticsController.CleanTreatmentData"] = ComputedResult,
        ["StatisticsController.ValidateTreatmentData"] = ComputedResult,
        ["WebhookSettingsController.TestWebhookSettings"] = ComputedResult,
        ["AnalyticsController.TrackCustomEvent"] = NotStored,
        ["CareLinkConnectController.Complete"] = NotStored,
        ["CareLinkConnectController.Start"] = NotStored,
        ["CompressionLowController.TriggerDetection"] = NotStored,
        ["ContentContributionsController.AcceptRelayedContribution"] = NotStored,
        ["ContentContributionsController.SubmitContribution"] = NotStored,
        ["GlucoseProcessingSettingsController.SetPreference"] = NotStored,
        ["SetupTherapyController.EnterTherapySettings"] = NotStored,
        ["SystemController.Heartbeat"] = NotStored,
        ["TranslationsController.AcceptRelayedContribution"] = NotStored,
        ["TranslationsController.SubmitContribution"] = NotStored,
        ["TranslationsController.SubmitDrafts"] = NotStored,
        ["WebhookSettingsController.SaveWebhookSettings"] = NotStored,
        ["GlucoseProcessingSettingsController.SetSourceDefaults"] = StoredWhole,
        ["UISettingsController.AddOrUpdateAlarmProfile"] = StoredWhole,
        ["UISettingsController.PatchDataQualitySettings"] = StoredWhole,
        ["UISettingsController.PatchFeatureSettings"] = StoredWhole,
        ["UISettingsController.PatchNotificationSettings"] = StoredWhole,
        ["UISettingsController.SaveAlarmConfiguration"] = StoredWhole,
        ["UISettingsController.SaveUISettings"] = StoredWhole,
        ["UserPreferencesController.UpdatePreferences"] = StoredWhole,
        ["CustomReportsController.CreateCustomReport"] = CappedByTheAction,
        ["CustomReportsController.UpdateCustomReport"] = CappedByTheAction,
        ["ExternalNotificationsController.Send"] = CappedByTheAction,
        ["TenantNotificationsController.Send"] = CappedByTheAction,
        ["TranslationsController.UpsertDrafts"] = CappedByTheAction,
        ["DevAdminController.CreateTenant"] = DevelopmentOnly,
        ["DevAdminController.ImportScopedSnapshot"] = DevelopmentOnly,
        ["DevAdminController.ImportSnapshot"] = DevelopmentOnly,
        ["DevAdminController.SeedTenant"] = DevelopmentOnly,
        ["DevAuthController.LoginJson"] = DevelopmentOnly,
    };

    private static readonly Dictionary<string, string> NotYetCapped = new()
    {
        ["AlertInvitesController.CreateInvite"] = IdentityAdministration,
        ["AlertRulesController.CreateRule"] = CarriesCredentials,
        ["AlertRulesController.UpdateRule"] = CarriesCredentials,
        ["ChatIdentityController.ClaimLink"] = IdentityAdministration,
        ["ChatIdentityController.UpdateLink"] = IdentityAdministration,
        ["ChatIdentityDirectoryController.AcknowledgeAsLinkedMember"] = IdentityAdministration,
        ["ChatIdentityDirectoryController.CreatePending"] = IdentityAdministration,
        ["ChatIdentityDirectoryController.RevokeByPlatformUser"] = IdentityAdministration,
        ["GuestLinkController.ActivateGuestLink"] = IdentityAdministration,
        ["GuestLinkController.CreateGuestLink"] = IdentityAdministration,
        ["MemberInviteController.CreateInvite"] = IdentityAdministration,
        ["MemberInviteController.SetTeamStaffRule"] = IdentityAdministration,
        ["MembershipRequestController.CreateRequest"] = IdentityAdministration,
        ["MigrationController.StartMigration"] = CarriesCredentials,
        ["MyTenantsController.CreateTenant"] = IdentityAdministration,
        ["OidcProviderAdminController.Create"] = IdentityAdministration,
        ["OidcProviderAdminController.Update"] = IdentityAdministration,
        ["PlatformController.CreateTenant"] = IdentityAdministration,
        ["ProfileController.CreateTherapySettings"] = CarriesCredentials,
        ["ProfileController.UpdateTherapySettings"] = CarriesCredentials,
        ["RoleController.CreateRole"] = IdentityAdministration,
        ["RoleController.UpdateRole"] = IdentityAdministration,
        ["SetupAlertsController.SaveAlertSetup"] = CarriesCredentials,
        ["SetupController.CreateTenant"] = IdentityAdministration,
        ["SetupController.OwnerComplete"] = IdentityAdministration,
        ["SetupController.OwnerOidc"] = IdentityAdministration,
        ["SetupController.OwnerOptions"] = IdentityAdministration,
        ["TeamsController.ApproveAccessLink"] = IdentityAdministration,
        ["TeamsController.CreateAccessLink"] = IdentityAdministration,
        ["TeamsController.CreateStaffInvite"] = IdentityAdministration,
        ["TeamsController.SetStaffReach"] = IdentityAdministration,
        ["TeamsController.UpdateStaff"] = IdentityAdministration,
        ["TenantController.AttachOidcIdentity"] = IdentityAdministration,
        ["TenantController.ConfirmMemberEmail"] = IdentityAdministration,
        ["TenantController.Create"] = IdentityAdministration,
        ["TenantController.Provision"] = IdentityAdministration,
        ["TenantController.Update"] = IdentityAdministration,
        ["TenantCreationInviteAdminController.Create"] = IdentityAdministration,
        ["TenantCreationInviteController.Redeem"] = IdentityAdministration,
        ["TenantDirectGrantController.Create"] = IdentityAdministration,
        ["TenantSettingsController.ChangeSubdomain"] = IdentityAdministration,
        ["TenantSettingsController.SetUnitsAndTimezone"] = IdentityAdministration,
    };

    private static readonly Dictionary<string, string> NotStoredInAColumn = new()
    {
        ["CreateLabHbA1cResultRequest.Unit"] = "refused unless it is percent or mmol/mol, and converted to the stored percent value",
        ["Food.CreatedAt"] = "read back from the food's own created timestamp and never stored from the body",
        ["BodyWeight.UpstreamIdentifier"] = "read from an upstream document and never mapped onto the body weight entity",
        ["CreateProfileActivationRequest.Notes"] = "kept in the activation's additional properties, which are jsonb",
        ["UpsertActivityRequest.Description"] = "kept in the state span's metadata, which is jsonb",
        ["UpsertActivityRequest.Intensity"] = "kept in the state span's metadata, which is jsonb",
        ["UpsertActivityRequest.Notes"] = "kept in the state span's metadata, which is jsonb",
        ["UpsertActivityRequest.DistanceUnits"] = "kept in the state span's metadata, which is jsonb",
        ["UpsertActivityRequest.EnergyUnits"] = "kept in the state span's metadata, which is jsonb",
        ["UpsertActivityRequest.Name"] = "kept in the state span's metadata, which is jsonb",
        ["Food.Foods"] = StoredWhole,
        ["CreateClockFaceRequest.Config"] = StoredWhole,
        ["UpdateClockFaceRequest.Config"] = StoredWhole,
        ["CreateNotificationRequest.Actions"] = StoredWhole,
        ["CreateNotificationRequest.ResolutionConditions"] = StoredWhole,
        ["BasalSchedule.Entries"] = StoredWhole,
        ["CarbRatioSchedule.Entries"] = StoredWhole,
        ["SensitivitySchedule.Entries"] = StoredWhole,
        ["TargetRangeSchedule.Entries"] = StoredWhole,
        ["MarkFailedRequest.Error"] = "stored as the delivery's last error, an unbounded text column",
        ["UpsertApsSnapshotRequest.SuggestedJson"] = StoredWhole,
        ["UpsertApsSnapshotRequest.EnactedJson"] = StoredWhole,
        ["UpsertApsSnapshotRequest.PredictedDefaultJson"] = StoredWhole,
        ["UpsertApsSnapshotRequest.PredictedIobJson"] = StoredWhole,
        ["UpsertApsSnapshotRequest.PredictedZtJson"] = StoredWhole,
        ["UpsertApsSnapshotRequest.PredictedCobJson"] = StoredWhole,
        ["UpsertApsSnapshotRequest.PredictedUamJson"] = StoredWhole,
        ["UpsertApsSnapshotRequest.LoopJson"] = StoredWhole,
        ["UpsertApsSnapshotRequest.OverrideJson"] = StoredWhole,
    };

    private static readonly string[] RequestVerbs = ["Create", "Update", "Upsert"];

    private static readonly Type[] ValidatorTypes = typeof(CreateBolusRequestValidator).Assembly.GetTypes()
        .Where(t => t is { IsAbstract: false, IsClass: true } && typeof(IValidator).IsAssignableFrom(t))
        .OrderBy(t => t.Name)
        .ToArray();

    private static readonly Dictionary<Type, Type> ValidatorTypeByRequest = ValidatorTypes
        .GroupBy(RequestTypeOf)
        .ToDictionary(g => g.Key, g => g.First());

    public static TheoryData<Type> Validators()
    {
        var data = new TheoryData<Type>();
        foreach (var type in ValidatorTypes.Where(t => t.Namespace == typeof(CreateBolusRequestValidator).Namespace
                                                       || EntityTypesFor(RequestTypeOf(t)).Count > 0))
        {
            data.Add(type);
        }

        return data;
    }

    public static TheoryData<string> CheckedActions()
    {
        var data = new TheoryData<string>();
        foreach (var action in ActionBodies().Select(a => a.Action).Where(a => !IsExempt(a)).Distinct().Order())
        {
            data.Add(action);
        }

        return data;
    }

    public static TheoryData<Type> Requests()
    {
        var data = new TheoryData<Type>();
        foreach (var type in MappedBodies())
        {
            data.Add(type);
        }

        return data;
    }

    private static bool IsExempt(string action) => NotARecordWrite.ContainsKey(action) || NotYetCapped.ContainsKey(action);

    private static IEnumerable<Type> CheckedTypes() =>
        ActionBodies().Where(a => !IsExempt(a.Action)).SelectMany(a => Carried(a.Body)).Select(c => c.Type).Distinct();

    private static IEnumerable<Type> MappedBodies() =>
        CheckedTypes().Where(t => EntityTypesFor(t).Count > 0).OrderBy(t => t.Name);

    private static HashSet<Type> ExemptBodies()
    {
        var checkedTypes = CheckedTypes().ToHashSet();
        return [.. ActionBodies()
            .Where(a => IsExempt(a.Action))
            .SelectMany(a => Carried(a.Body))
            .Select(c => c.Type)
            .Where(t => !checkedTypes.Contains(t))];
    }

    private static IEnumerable<(string Action, Type Body)> ActionBodies() =>
        typeof(CreateBolusRequestValidator).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ControllerBase).IsAssignableFrom(t)
                        && t.Namespace?.StartsWith("Nocturne.API.Controllers.V4", StringComparison.Ordinal) == true)
            .SelectMany(c => c.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .SelectMany(m => m.GetParameters()
                    .Where(p => p.IsDefined(typeof(FromBodyAttribute), inherit: true))
                    .Select(p => (Action: $"{c.Name}.{m.Name}", Body: ElementTypeOf(p.ParameterType)))))
            .Where(a => IsCarriedType(a.Body))
            .Distinct();

    private static bool IsCarriedType(Type type) =>
        type is { IsClass: true } && type != typeof(string) && type.Namespace?.StartsWith("Nocturne", StringComparison.Ordinal) == true;

    private static Type ElementTypeOf(Type body)
    {
        if (body.IsArray)
        {
            return body.GetElementType()!;
        }

        var enumerable = body != typeof(string)
            ? body.GetInterfaces().Append(body).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            : null;
        return enumerable?.GetGenericArguments()[0] ?? body;
    }

    /// <summary>
    /// The body and every type it carries, each with the property of its parent it is reached through. A property
    /// listed in <see cref="NotStoredInAColumn"/> is not entered: what it holds is stored whole.
    /// </summary>
    private static IEnumerable<(Type? Parent, PropertyInfo? Via, Type Type)> Carried(Type body)
    {
        var seen = new HashSet<Type> { body };
        var pending = new Queue<(Type? Parent, PropertyInfo? Via, Type Type)>([(null, null, body)]);
        while (pending.TryDequeue(out var next))
        {
            yield return next;
            foreach (var property in next.Type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(p => !NotStoredInAColumn.ContainsKey($"{next.Type.Name}.{p.Name}")))
            {
                var child = ElementTypeOf(property.PropertyType);
                if (IsCarriedType(child) && seen.Add(child))
                {
                    pending.Enqueue((next.Type, property, child));
                }
            }
        }
    }

    private static PropertyInfo[] Strings(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.PropertyType == typeof(string)).ToArray();

    private static int? AnnotatedCap(PropertyInfo property) =>
        property.GetCustomAttribute<MaxLengthAttribute>()?.Length ?? property.GetCustomAttribute<StringLengthAttribute>()?.MaximumLength;

    /// <summary>
    /// The strings of the type with no length rule: no length validator in its validator, no <c>[MaxLength]</c> or
    /// <c>[StringLength]</c> (which MVC's model validation runs wherever a body carries the type), and no entry in
    /// <see cref="NotStoredVerbatim"/> or <see cref="NotStoredInAColumn"/>.
    /// </summary>
    private static IEnumerable<PropertyInfo> Unruled(Type type)
    {
        var caps = ValidatorCaps(type);
        return Strings(type).Where(p => caps.GetValueOrDefault(p.Name) is not { Count: > 0 }
                                        && AnnotatedCap(p) is null
                                        && !NotStoredVerbatim.ContainsKey($"{type.Name}.{p.Name}")
                                        && !NotStoredInAColumn.ContainsKey($"{type.Name}.{p.Name}"));
    }

    /// <summary>
    /// What the guard reports for an action's body with only the per-property lists consulted: a string with no length
    /// rule, a cap above its column, and a validated type its parent's validator does not run.
    /// </summary>
    private static List<string> Violations(Type body)
    {
        var violations = new List<string>();
        foreach (var (parent, via, type) in Carried(body))
        {
            var validated = ValidatorTypeByRequest.ContainsKey(type);
            var unruled = Unruled(type).Select(p => p.Name).ToList();
            if (unruled.Count > 0 && !validated)
            {
                violations.Add($"{type.Name} carries a string and has no validator, so a value longer than its column fails the write as a 500");
                continue;
            }

            violations.AddRange(unruled.Select(name =>
                $"{type.Name}.{name} has no length rule and is not listed as kept outside a bounded column"));

            violations.AddRange(ColumnViolations(type));

            if (validated && parent is not null && !RunsAValidatorOver(parent, via!))
            {
                violations.Add($"no validator of {parent.Name} runs one over {parent.Name}.{via!.Name}");
            }
        }

        return violations;
    }

    private static bool RunsAValidatorOver(Type parent, PropertyInfo via) =>
        ValidatorTypeByRequest.TryGetValue(parent, out var parentValidatorType)
        && ValidatorOf(parentValidatorType).CreateDescriptor().GetValidatorsForMember(via.Name).Any(v => v.Validator is IChildValidatorAdaptor);

    private static Dictionary<string, List<int>> ValidatorCaps(Type requestType) =>
        ValidatorTypeByRequest.TryGetValue(requestType, out var validatorType)
            ? ValidatorOf(validatorType).CreateDescriptor().GetMembersWithValidators().Where(group => group.Key is not null).ToDictionary(
                group => group.Key,
                group => group.Select(g => g.Validator).OfType<ILengthValidator>().Select(l => l.Max).Where(max => max > 0).ToList())
            : [];

    private static List<string> ColumnViolations(Type requestType)
    {
        var caps = ValidatorCaps(requestType);

        var violations = new List<string>();
        foreach (var (property, entityType, column, length, fitsToColumn) in BoundedColumns(requestType))
        {
            var found = caps.GetValueOrDefault(property.Name) ?? [];
            if (AnnotatedCap(property) is { } annotated)
            {
                found = [.. found, annotated];
            }

            var cap = found.Count > 0 ? found.Min() : (int?)null;
            if (cap is null && !fitsToColumn)
            {
                violations.Add($"{requestType.Name}.{property.Name} has no length rule, but {entityType.ClrType.Name}.{column.Name} holds {length}");
            }
            else if (cap > length)
            {
                violations.Add($"{requestType.Name}.{property.Name} is capped at {cap}, but {entityType.ClrType.Name}.{column.Name} holds {length}{(fitsToColumn ? " and cuts a longer value on save" : string.Empty)}");
            }
        }

        return violations;
    }

    private static IValidator ValidatorOf(Type validatorType) => (IValidator)Activator.CreateInstance(validatorType)!;

    [Theory]
    [MemberData(nameof(CheckedActions))]
    public void Every_type_an_action_body_carries_that_holds_a_string_is_validated(string action)
    {
        var violations = ActionBodies().Where(a => a.Action == action).SelectMany(a => Violations(a.Body)).Distinct();

        violations.Should().BeEmpty(
            $"{action} stores what its body carries unless it is listed in {nameof(NotARecordWrite)} or {nameof(NotYetCapped)} with its reason");
    }

    [Fact]
    public void An_exempt_action_exists_and_its_body_would_fail_without_its_entry()
    {
        var violationsByAction = ActionBodies()
            .GroupBy(a => a.Action)
            .ToDictionary(g => g.Key, g => g.SelectMany(a => Violations(a.Body)).Any());

        var stale = NotARecordWrite.Keys.Select(k => (List: nameof(NotARecordWrite), Key: k))
            .Concat(NotYetCapped.Keys.Select(k => (List: nameof(NotYetCapped), Key: k)))
            .Where(e => !violationsByAction.GetValueOrDefault(e.Key))
            .Select(e => $"{e.List}[{e.Key}]");

        stale.Should().BeEmpty("an exempt action exists and its body carries a string no validator caps");
    }

    [Fact]
    public void Every_listed_property_belongs_to_a_checked_body()
    {
        var properties = CheckedTypes()
            .SelectMany(b => b.GetProperties().Select(p => $"{b.Name}.{p.Name}"))
            .ToHashSet();

        var stale = ColumnByRequestProperty.Keys.Select(k => (List: nameof(ColumnByRequestProperty), Key: k))
            .Concat(NotStoredVerbatim.Keys.Select(k => (List: nameof(NotStoredVerbatim), Key: k)))
            .Concat(NotStoredInAColumn.Keys.Select(k => (List: nameof(NotStoredInAColumn), Key: k)))
            .Where(e => !properties.Contains(e.Key))
            .Select(e => $"{e.List}[{e.Key}]");

        stale.Should().BeEmpty("a list entry names a property of a type an action body carries");
    }

    [Theory]
    [MemberData(nameof(Validators))]
    public void A_string_stored_in_a_bounded_column_that_refuses_a_longer_value_has_a_cap_within_it(Type validatorType)
    {
        var requestType = RequestTypeOf(validatorType);
        if (ExemptBodies().Contains(requestType))
        {
            return;
        }

        EntityTypesFor(requestType).Should().NotBeEmpty($"{validatorType.Name} must name the entities its request is stored in");

        ColumnViolations(requestType).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Requests))]
    public void Every_string_of_a_write_body_names_the_column_it_is_stored_in(Type requestType)
    {
        var entityTypes = EntityTypesFor(requestType);

        var unplaced = requestType.GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .Where(p => !NotStoredVerbatim.ContainsKey($"{requestType.Name}.{p.Name}") && !NotStoredInAColumn.ContainsKey($"{requestType.Name}.{p.Name}"))
            .Where(p => entityTypes.All(e => e.FindProperty(ColumnNameOf(requestType, p)) is null))
            .Select(p => p.Name);

        unplaced.Should().BeEmpty(
            $"each string of {requestType.Name} must match a column of its entities, map to one in {nameof(ColumnByRequestProperty)}, or be listed in {nameof(NotStoredInAColumn)}");
    }

    private static Type RequestTypeOf(Type validatorType) => validatorType.BaseType!.GetGenericArguments()[0];

    private static IEnumerable<(PropertyInfo Property, IEntityType EntityType, IProperty Column, int Length, bool FitsToColumn)> BoundedColumns(Type requestType)
    {
        var entityTypes = EntityTypesFor(requestType);
        foreach (var property in requestType.GetProperties()
                     .Where(p => p.PropertyType == typeof(string) && !NotStoredVerbatim.ContainsKey($"{requestType.Name}.{p.Name}")))
        {
            var columnName = ColumnNameOf(requestType, property);
            foreach (var entityType in entityTypes)
            {
                // A property with no same-named column (a value kept in jsonb, or stored under another name) is not checked.
                var column = entityType.FindProperty(columnName);
                if (column?.GetMaxLength() is not { } length)
                {
                    continue;
                }

                yield return (property, entityType, column, length,
                    column.PropertyInfo?.IsDefined(typeof(FitToColumnAttribute), inherit: true) == true);
            }
        }
    }

    private static string ColumnNameOf(Type requestType, PropertyInfo property) =>
        ColumnByRequestProperty.GetValueOrDefault($"{requestType.Name}.{property.Name}", property.Name);

    private static List<IEntityType> EntityTypesFor(Type requestType)
    {
        var name = requestType.Name;
        var stem = name;
        if (name.EndsWith("Request", StringComparison.Ordinal))
        {
            var verb = RequestVerbs.FirstOrDefault(v => name.StartsWith(v, StringComparison.Ordinal));
            stem = name[(verb?.Length ?? 0)..^"Request".Length];
        }

        var entityStems = EntityStemsByRequestStem.GetValueOrDefault(stem, [stem]);

        return [.. Model.GetEntityTypes().Where(e => entityStems.Any(s => e.ClrType.Name == s + "Entity"))];
    }
}
