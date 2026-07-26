using System.Reflection;
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
/// declares through <see cref="IWriteScopedController.WriteScope"/>.
/// </summary>
/// <remarks>
/// The V1/V2/V3 counterpart lives in <see cref="WriteEndpointScopeEnforcementTests"/>.
/// </remarks>
public class V4WriteScopeGatingTests
{
    private static readonly string[] WriteVerbs = ["POST", "PUT", "PATCH", "DELETE"];

    /// <summary>
    /// The write scope each V4 CRUD controller is expected to declare. Derived from the data
    /// category the record's table belongs to in <see cref="ShareDataCategories"/> (the read-side
    /// classification) and from the scope the equivalent V1 endpoint requires. Asserted exhaustively,
    /// so a new V4 CRUD controller must be added here with a deliberate category.
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
    public void EveryV4CrudController_DeclaresItsExpectedWriteScope()
    {
        var controllers = CrudControllers().ToList();

        controllers.Select(t => t.Name).Should().BeEquivalentTo(ExpectedWriteScopes.Keys,
            "every V4 CRUD controller must be mapped to a data category in ExpectedWriteScopes");

        foreach (var controller in controllers)
        {
            var declared = ((IWriteScopedController)Instantiate(controller)).WriteScope;

            declared.Should().Be(ExpectedWriteScopes[controller.Name],
                $"{controller.Name} must gate its writes on its own data category");
            OAuthScopes.SatisfiesScope([OAuthScopes.GlucoseRead, OAuthScopes.TreatmentsRead, OAuthScopes.DevicesRead], declared)
                .Should().BeFalse($"{controller.Name}'s write scope must not be satisfiable by read-only scopes");
        }
    }

    [Fact]
    public void EveryV4WriteAction_IsScopeGated()
    {
        var violations = new List<string>();
        var writeActionsChecked = 0;

        foreach (var controller in V4ControllerBaseSubclasses())
        {
            foreach (var action in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                var verbs = action.GetCustomAttributes<HttpMethodAttribute>(inherit: true)
                    .SelectMany(a => a.HttpMethods)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (!verbs.Overlaps(WriteVerbs))
                    continue;

                writeActionsChecked++;

                var attributes = action.GetCustomAttributes(inherit: true);
                var gated = attributes.Any(a => a is RequireDeclaredWriteScopeAttribute
                                                     or RequireScopeAttribute
                                                     or RequirePermissionAttribute);

                if (!gated)
                    violations.Add($"{controller.Name}.{action.Name} [{string.Join(",", verbs)}]");
            }
        }

        // Sanity: the scan must find the write surface, or the assertion below passes vacuously.
        writeActionsChecked.Should().BeGreaterThan(40,
            "the reflection scan should discover the V4 base-controller write endpoints");

        violations.Should().BeEmpty(
            "every write action on a V4 base controller must carry [RequireDeclaredWriteScope] "
            + "(base CRUD actions and their overrides) or an explicit [RequireScope]. Unprotected: "
            + string.Join("; ", violations));
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private static IActionResult? Evaluate(object controller, bool authenticated, params string[] grantedScopes)
    {
        var httpContext = new DefaultHttpContext();
        if (authenticated)
            httpContext.Items["AuthContext"] = new AuthContext { IsAuthenticated = true };
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>(grantedScopes);

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var context = new ActionExecutingContext(
            actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller);

        new RequireDeclaredWriteScopeAttribute().OnActionExecuting(context);
        return context.Result;
    }

    private static Assembly ApiAssembly => typeof(RequireDeclaredWriteScopeAttribute).Assembly;

    private static IEnumerable<Type> CrudControllers() =>
        V4ControllerBaseSubclasses().Where(t => typeof(IWriteScopedController).IsAssignableFrom(t));

    private static IEnumerable<Type> V4ControllerBaseSubclasses() =>
        ApiAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && DerivesFromV4Base(t));

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

    /// <summary>Builds a controller with every constructor dependency mocked.</summary>
    private static object Instantiate(Type controller)
    {
        var constructor = controller.GetConstructors().Single();
        var arguments = constructor.GetParameters()
            .Select(p => ((Mock)Activator.CreateInstance(typeof(Mock<>).MakeGenericType(p.ParameterType))!).Object)
            .ToArray();

        return constructor.Invoke(arguments);
    }

    private static object NewSensorGlucoseController() =>
        Instantiate(typeof(Nocturne.API.Controllers.V4.Glucose.SensorGlucoseController));

    private static object NewBolusController() =>
        Instantiate(typeof(Nocturne.API.Controllers.V4.Treatments.BolusController));

    /// <summary>Stands in for a controller that never declared a write scope.</summary>
    private sealed class UndeclaredController : ControllerBase;

    /// <summary>Stands in for a controller whose declaration is present but empty.</summary>
    private sealed class EmptyScopeController : ControllerBase, IWriteScopedController
    {
        public string WriteScope => string.Empty;
    }
}
