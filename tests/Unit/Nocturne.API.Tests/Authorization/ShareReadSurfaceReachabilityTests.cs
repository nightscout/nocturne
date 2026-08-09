using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Nocturne.API.Attributes;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Guards that the report/data read surface stays reachable by the anonymous public-share
/// principal. A share is deliberately <c>IsAuthenticated: false</c>, so any <c>IAuthorizeData</c>
/// attribute (<c>[Authorize]</c> and friends) on these controllers 401s every share regardless of
/// the categories the owner granted — which is how nightscout/nocturne#635 shipped: the report
/// pages called <c>[Authorize]</c>-gated endpoints and threw 401 on a share granted every
/// category. Reachability here means gated by <see cref="RequireScopeAttribute"/> (which admits an
/// anonymous share holding the required read scope) plus the <c>HasPermissions</c> fallback
/// policy, with per-category share RLS restricting the rows underneath.
/// </summary>
/// <remarks>
/// The inverse guards already exist: <see cref="V4WriteScopeGatingTests"/> proves no write action
/// under the V4 namespace is executable with read-only credentials, and
/// <see cref="RequireScopeAttributeShareTests"/> pins that <see cref="RequireScopeAttribute"/>
/// admits an anonymous share only for all-read requirements. This test pins the third leg: the
/// read gates on the share-facing surface name a scope a share can actually be granted, and no
/// authentication requirement sits in front of them.
/// </remarks>
public class ShareReadSurfaceReachabilityTests
{
    private static readonly string[] ReadVerbs = ["GET", "HEAD"];

    /// <summary>
    /// The share-facing read surface: controller type → the read scope its read actions must
    /// require. These are the endpoints the dashboard and the reports menu fetch from, each
    /// governing data in a <see cref="ShareDataCategories"/> category (or, for the analytics
    /// controllers, aggregating over such data behind <see cref="OAuthScopes.ReportsRead"/>).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ShareReadableControllers =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Nocturne.API.Controllers.V4.Glucose.SensorGlucoseController"] = OAuthScopes.GlucoseRead,
            ["Nocturne.API.Controllers.V4.Glucose.MeterGlucoseController"] = OAuthScopes.GlucoseRead,
            ["Nocturne.API.Controllers.V4.Glucose.BGCheckController"] = OAuthScopes.GlucoseRead,
            ["Nocturne.API.Controllers.V4.Glucose.CalibrationController"] = OAuthScopes.GlucoseRead,
            ["Nocturne.API.Controllers.V4.Treatments.BolusController"] = OAuthScopes.TreatmentsRead,
            ["Nocturne.API.Controllers.V4.Treatments.NutritionController"] = OAuthScopes.TreatmentsRead,
            ["Nocturne.API.Controllers.V4.Treatments.BasalInjectionController"] = OAuthScopes.TreatmentsRead,
            ["Nocturne.API.Controllers.V4.Treatments.BolusCalculationController"] = OAuthScopes.TreatmentsRead,
            ["Nocturne.API.Controllers.V4.Treatments.NoteController"] = OAuthScopes.TreatmentsRead,
            ["Nocturne.API.Controllers.V4.Treatments.TempBasalController"] = OAuthScopes.TreatmentsRead,
            ["Nocturne.API.Controllers.V4.Devices.DeviceEventController"] = OAuthScopes.DevicesRead,
            ["Nocturne.API.Controllers.V4.Devices.ApsSnapshotController"] = OAuthScopes.DevicesRead,
            ["Nocturne.API.Controllers.V4.Devices.PumpSnapshotController"] = OAuthScopes.DevicesRead,
            ["Nocturne.API.Controllers.V4.Devices.UploaderSnapshotController"] = OAuthScopes.DevicesRead,
            ["Nocturne.API.Controllers.V4.Devices.BatteryController"] = OAuthScopes.DevicesRead,
            ["Nocturne.API.Controllers.V4.Health.HeartRateController"] = OAuthScopes.HeartRateRead,
            ["Nocturne.API.Controllers.V4.Health.StepCountController"] = OAuthScopes.StepCountRead,
            ["Nocturne.API.Controllers.V4.Analytics.SensorIntegrityController"] = OAuthScopes.ReportsRead,
            ["Nocturne.API.Controllers.V4.Analytics.StatisticsController"] = OAuthScopes.ReportsRead,
            // Feeds the data quality report. Its rows are hidden from shares by RLS
            // (compression_low_suggestions has no ShareDataCategories entry), so the gate decides
            // error-vs-empty-list, not what a share can see.
            ["Nocturne.API.Controllers.V4.TenantAdmin.CompressionLowController"] = OAuthScopes.GlucoseRead,
        };

    /// <summary>
    /// Reads that serve the share surface through <c>[AllowAnonymous]</c> plus the
    /// <c>HasPermissions</c> fallback rather than a scope gate, keyed <c>Controller.Action</c>.
    /// The tracker tables carry no <see cref="ShareDataCategories"/> category, so RLS hides their
    /// rows from a share and the endpoints return the public-visibility set — but they must stay
    /// reachable, because the calendar renders them beside glucose a share can see.
    /// </summary>
    private static readonly string[] AnonymouslyReachableReads =
    [
        "TrackersController.GetDefinitions",
        "TrackersController.GetActiveInstances",
        "TrackersController.GetInstanceHistory",
        "TrackersController.GetUpcomingInstances",
    ];

    [Fact]
    public void AnonymouslyReachableReads_StayAnonymouslyReachable()
    {
        var offenders = new List<string>();

        foreach (var name in AnonymouslyReachableReads)
        {
            var (typeName, actionName) = (name.Split('.')[0], name.Split('.')[1]);
            var type = ApiAssembly.GetTypes().SingleOrDefault(t => t.Name == typeName);
            if (type is null)
            {
                offenders.Add($"{name}: controller not found");
                continue;
            }

            var action = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .SingleOrDefault(m => m.Name == actionName);
            if (action is null)
            {
                offenders.Add($"{name}: action not found");
                continue;
            }

            if (!action.GetCustomAttributes(inherit: true).OfType<AllowAnonymousAttribute>().Any())
                offenders.Add($"{name}: lost [AllowAnonymous], so every public share 401s on it");
        }

        offenders.Should().BeEmpty(
            "these reads back share-visible views and the share principal is IsAuthenticated: false:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Actions whose required scope deviates from their controller's, keyed
    /// <c>Controller.Action</c>. The two statistics endpoints the dashboard and calendar render
    /// (multi-period stats, punch card) are part of the glucose surface, not the reports surface,
    /// so a glucose-only share (the <see cref="TenantPermissions.DefaultPublicShareScopes"/>
    /// default) keeps its dashboard.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ActionScopeOverrides =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["StatisticsController.GetMultiPeriodStatistics"] = OAuthScopes.GlucoseRead,
            ["StatisticsController.GetPunchCardData"] = OAuthScopes.GlucoseRead,
        };

    private static Assembly ApiAssembly => typeof(RequireScopeAttribute).Assembly;

    [Fact]
    public void ShareReadSurface_CarriesNoAuthenticationRequirement()
    {
        foreach (var type in Controllers())
        {
            var offenders = new List<string>();

            if (type.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>().Any())
                offenders.Add($"{type.Name} (class)");

            offenders.AddRange(Actions(type)
                .Where(a => a.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>().Any())
                .Select(a => $"{type.Name}.{a.Name}"));

            offenders.Should().BeEmpty(
                "the public-share principal is IsAuthenticated: false, so [Authorize] on the "
                + "share-facing read surface 401s every share (nightscout/nocturne#635); gate with "
                + "[RequireScope] instead");
        }
    }

    /// <summary>
    /// Read (GET/HEAD) actions on the listed controllers that are deliberately write-gated and so
    /// member-only, keyed by action name with the rationale. Anything else write-gated on a read
    /// verb fails <see cref="EveryShareReadAction_RequiresItsCategoryReadScope"/>, so a read
    /// cannot silently drop off the share surface by picking up a write gate.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> MemberOnlyReadActions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // The recycle bin exists to feed Restore; records the owner deleted are not part of
            // the share surface (see the remark on V4CrudControllerBase.ListDeleted).
            ["ListDeleted"] = "deleted-record enumeration is an editing surface",
        };

    [Fact]
    public void EveryShareReadAction_RequiresItsCategoryReadScope()
    {
        // Sweeps every action, not just read verbs: the statistics compute endpoints are reads
        // exposed over POST, and an ungated one would otherwise ship reachable by any non-empty
        // trie. An action is outside the share read surface only when it carries an explicit
        // write gate (V4WriteScopeGatingTests owns that surface).
        var violations = new List<string>();

        foreach (var type in Controllers())
        {
            var classScopes = ScopesOf(type.GetCustomAttributes(inherit: true)).ToList();
            var shareReadActions = 0;

            foreach (var action in Actions(type))
            {
                var actionAttributes = action.GetCustomAttributes(inherit: true).ToList();
                var isWriteGated = actionAttributes.OfType<RequireDeclaredWriteScopeAttribute>().Any()
                    || ScopesOf(actionAttributes).Any(s => !s.EndsWith(".read", StringComparison.Ordinal));

                if (isWriteGated)
                {
                    if (VerbsOf(action).Overlaps(ReadVerbs) && !MemberOnlyReadActions.ContainsKey(action.Name))
                        violations.Add($"{type.Name}.{action.Name}: a write-gated read verb drops off the share "
                            + "surface — list it in MemberOnlyReadActions if that is intended");
                    continue;
                }

                shareReadActions++;

                var expected = ActionScopeOverrides.TryGetValue($"{type.Name}.{action.Name}", out var o)
                    ? o
                    : ShareReadableControllers[type.FullName!];

                var effective = classScopes.Concat(ScopesOf(actionAttributes)).ToList();

                if (!effective.Contains(expected))
                    violations.Add($"{type.Name}.{action.Name}: must require {expected}");

                // A gate is share-satisfiable only when every scope it names is a read scope: a
                // requirement naming anything else makes RequireScope demand authentication.
                if (effective.Any(s => !s.EndsWith(".read", StringComparison.Ordinal)))
                    violations.Add($"{type.Name}.{action.Name}: names a non-read scope, which excludes the anonymous share");
            }

            if (shareReadActions == 0)
                violations.Add($"{type.Name}: listed as share-readable but exposes no share-readable action");
        }

        violations.Should().BeEmpty(
            "every action on the share read surface must require its category's read scope:\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void EveryRequiredScope_IsGrantableToAShare()
    {
        // A gate naming a scope outside PublicShareScopes can never be satisfied by a share, which
        // would silently turn "share-reachable" into "member-only" without any test failing.
        var scopes = ShareReadableControllers.Values.Concat(ActionScopeOverrides.Values).Distinct();
        scopes.Should().OnlyContain(s => TenantPermissions.PublicShareScopes.Contains(s));
    }

    [Fact]
    public void ListedControllers_Exist()
    {
        foreach (var (name, _) in ShareReadableControllers)
            ApiAssembly.GetType(name).Should().NotBeNull($"{name} is listed but does not exist");

        foreach (var (key, _) in ActionScopeOverrides)
        {
            var (controller, action) = (key.Split('.')[0], key.Split('.')[1]);
            Controllers().Should().Contain(
                t => t.Name == controller && Actions(t).Any(a => a.Name == action),
                $"{key} is listed in ActionScopeOverrides but no such action exists");
        }
    }

    [Fact]
    public void EveryListedController_Resolves()
    {
        // Controllers() drops names that no longer resolve, so a rename or a move between
        // namespaces would otherwise retire a controller from this guard silently.
        var missing = ShareReadableControllers.Keys.Where(n => ApiAssembly.GetType(n) is null);

        missing.Should().BeEmpty("a listed controller that no longer resolves stops being guarded");
    }

    private static IEnumerable<Type> Controllers() =>
        ShareReadableControllers.Keys
            .Select(n => ApiAssembly.GetType(n))
            .Where(t => t is not null)
            .Cast<Type>();

    private static IEnumerable<MethodInfo> Actions(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName && m.GetCustomAttributes(inherit: true)
                .OfType<IActionHttpMethodProvider>().Any());

    private static HashSet<string> VerbsOf(MethodInfo action) =>
        action.GetCustomAttributes(inherit: true)
            .OfType<HttpMethodAttribute>()
            .SelectMany(a => a.HttpMethods)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> ScopesOf(IEnumerable<object> attributes) =>
        attributes.OfType<RequireScopeAttribute>().SelectMany(a => a.Scopes);
}
