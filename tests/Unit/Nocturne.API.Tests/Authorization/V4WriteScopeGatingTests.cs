using System.Reflection;
using System.Runtime.CompilerServices;
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
/// <see cref="RequireScopeAttribute"/> where one controller's writes span two data categories.
/// </summary>
/// <remarks>
/// The V1/V2/V3 counterpart lives in <see cref="WriteEndpointScopeEnforcementTests"/>.
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

    /// <summary>
    /// V4 controllers that do not derive from <see cref="V4CrudControllerBase{TModel, TCreateRequest, TUpdateRequest, TRepository}"/>
    /// and so cannot inherit its gated write actions. Enumerated by type rather than by namespace
    /// sweep because <c>V4.Health.ActivityController</c> gates in the handler (its category depends
    /// on what the payload decomposes into, per record) and carries no attribute to find.
    /// </summary>
    private static readonly Type[] NonBaseControllersUnderGuard =
    [
        typeof(Nocturne.API.Controllers.V4.Profiles.ProfileController),
        typeof(Nocturne.API.Controllers.V4.Treatments.NutritionController),
        typeof(Nocturne.API.Controllers.V4.Treatments.FoodsController),
        typeof(Nocturne.API.Controllers.V4.Health.BodyWeightController),
        typeof(Nocturne.API.Controllers.V4.Health.PatientRecordController),
    ];

    /// <summary>
    /// The write scope each write-scoped V4 controller is expected to declare. Derived from the data
    /// category the record's table belongs to in <see cref="ShareDataCategories"/> (the read-side
    /// classification) and from the scope the equivalent V1 endpoint requires. Asserted exhaustively,
    /// so a new write-scoped V4 controller must be added here with a deliberate category.
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

        controllers.Select(t => t.Name).Should().BeEquivalentTo(ExpectedWriteScopes.Keys,
            "every write-scoped V4 controller must be mapped to a data category in ExpectedWriteScopes");

        foreach (var controller in controllers)
        {
            var declared = ((IWriteScopedController)ScopeDeclarationInstance(controller)).WriteScope;

            declared.Should().Be(ExpectedWriteScopes[controller.Name],
                $"{controller.Name} must gate its writes on its own data category");
            OAuthScopes.SatisfiesScope(AllReadScopes, declared)
                .Should().BeFalse($"{controller.Name}'s write scope must not be satisfiable by read-only scopes");
        }
    }

    [Fact]
    public void MixedCategoryController_MapsEveryWriteActionToItsCategory()
    {
        // PatientRecordController writes two categories, so its expectations are per action and must
        // stay exhaustive: a new write action there has to name the category it mutates.
        var actions = WriteActions(typeof(Nocturne.API.Controllers.V4.Health.PatientRecordController))
            .Select(a => $"PatientRecordController.{a.Name}");

        actions.Should().BeEquivalentTo(ExpectedActionScopes.Keys,
            "every write action on a mixed-category controller must be mapped in ExpectedActionScopes");
    }

    [Fact]
    public void EveryV4WriteAction_IsScopeGated()
    {
        var violations = new List<string>();
        var readSatisfiable = new List<string>();
        var writeActionsChecked = 0;

        foreach (var controller in V4ControllerBaseSubclasses().Concat(NonBaseControllersUnderGuard).Distinct())
        {
            foreach (var action in WriteActions(controller))
            {
                writeActionsChecked++;

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

        // Sanity: the scan must find the write surface, or the assertions below pass vacuously.
        writeActionsChecked.Should().BeGreaterThan(80,
            "the reflection scan should discover the V4 base-controller and non-base write endpoints");

        violations.Should().BeEmpty(
            "every write action on a guarded V4 controller must carry [RequireDeclaredWriteScope] "
            + "(base CRUD actions and their overrides) or an explicit [RequireScope]. Unprotected: "
            + string.Join("; ", violations));

        readSatisfiable.Should().BeEmpty(
            "a write action must require a readwrite (or full-access) scope: " + string.Join("; ", readSatisfiable));
    }

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
    /// Every write action on the five non-base V4 controllers, paired with the scope its category
    /// requires. Generated by reflection so a new write action is covered without editing the theory.
    /// </summary>
    public static TheoryData<string, string, string> NonBaseWriteActions()
    {
        var data = new TheoryData<string, string, string>();

        foreach (var controller in NonBaseControllersUnderGuard)
        {
            foreach (var action in WriteActions(controller))
            {
                var expected = ExpectedActionScopes.TryGetValue($"{controller.Name}.{action.Name}", out var perAction)
                    ? perAction
                    : ExpectedWriteScopes[controller.Name];

                data.Add(controller.FullName!, action.Name, expected);
            }
        }

        return data;
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

    private static IEnumerable<MethodInfo> WriteActions(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(action => HttpVerbs(action).Overlaps(WriteVerbs));

    private static HashSet<string> HttpVerbs(MethodInfo action) =>
        action.GetCustomAttributes<HttpMethodAttribute>(inherit: true)
            .SelectMany(a => a.HttpMethods)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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
