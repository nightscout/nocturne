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
    /// controllers, aggregating over such data behind <see cref="Scope.ReportsRead"/>).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ShareReadableControllers =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Nocturne.API.Controllers.V4.Glucose.SensorGlucoseController"] = Scope.GlucoseRead,
            ["Nocturne.API.Controllers.V4.Glucose.MeterGlucoseController"] = Scope.GlucoseRead,
            ["Nocturne.API.Controllers.V4.Glucose.BGCheckController"] = Scope.GlucoseRead,
            ["Nocturne.API.Controllers.V4.Glucose.CalibrationController"] = Scope.GlucoseRead,
            ["Nocturne.API.Controllers.V4.Treatments.BolusController"] = Scope.TreatmentsRead,
            ["Nocturne.API.Controllers.V4.Treatments.NutritionController"] = Scope.TreatmentsRead,
            ["Nocturne.API.Controllers.V4.Treatments.BasalInjectionController"] = Scope.TreatmentsRead,
            ["Nocturne.API.Controllers.V4.Treatments.BolusCalculationController"] = Scope.TreatmentsRead,
            ["Nocturne.API.Controllers.V4.Treatments.NoteController"] = Scope.TreatmentsRead,
            ["Nocturne.API.Controllers.V4.Treatments.TempBasalController"] = Scope.TreatmentsRead,
            ["Nocturne.API.Controllers.V4.Devices.DeviceEventController"] = Scope.DevicesRead,
            ["Nocturne.API.Controllers.V4.Devices.ApsSnapshotController"] = Scope.DevicesRead,
            ["Nocturne.API.Controllers.V4.Devices.PumpSnapshotController"] = Scope.DevicesRead,
            ["Nocturne.API.Controllers.V4.Devices.UploaderSnapshotController"] = Scope.DevicesRead,
            ["Nocturne.API.Controllers.V4.Devices.BatteryController"] = Scope.DevicesRead,
            ["Nocturne.API.Controllers.V4.Health.HeartRateController"] = Scope.HeartRateRead,
            ["Nocturne.API.Controllers.V4.Health.StepCountController"] = Scope.StepCountRead,
            ["Nocturne.API.Controllers.V4.Analytics.SensorIntegrityController"] = Scope.ReportsRead,
            ["Nocturne.API.Controllers.V4.Analytics.StatisticsController"] = Scope.ReportsRead,
            ["Nocturne.API.Controllers.V4.Analytics.DataOverviewController"] = Scope.ReportsRead,
            // Backs the steps and heart-rate reports, which are not member-only, so its gate must
            // admit a share holding any one of its categories;
            // ActogramReadScopeGuard empties the rest. Listed against glucose because the default
            // share grant is glucose-only and the report still renders its glucose overlay.
            ["Nocturne.API.Controllers.V4.Analytics.ActogramController"] = Scope.GlucoseRead,
            // The dashboard chart, the day-in-review pages and the forecast overlay. Each merges
            // several categories, so its gate is an OR across them and the matching read-scope
            // guard empties the rest; listed against glucose because that is the default share
            // grant and the glucose series is what those pages render from it.
            ["Nocturne.API.Controllers.V4.Analytics.ChartDataController"] = Scope.GlucoseRead,
            ["Nocturne.API.Controllers.V4.Analytics.RetrospectiveController"] = Scope.GlucoseRead,
            ["Nocturne.API.Controllers.V4.Analytics.PredictionController"] = Scope.GlucoseRead,
            // Feeds the data quality report. Its rows are hidden from shares by RLS
            // (compression_low_suggestions has no ShareDataCategories entry), so the gate decides
            // error-vs-empty-list, not what a share can see.
            ["Nocturne.API.Controllers.V4.TenantAdmin.CompressionLowController"] = Scope.GlucoseRead,
        };

    /// <summary>
    /// Reads that serve the share surface through the <c>HasPermissions</c> fallback rather than a
    /// scope gate, keyed <c>Controller.Action</c>. The tracker tables carry no
    /// <see cref="ShareDataCategories"/> category, so RLS hides their rows from a share and the
    /// endpoints return the public-visibility set — but they must stay reachable, because the
    /// calendar renders them beside glucose a share can see.
    /// </summary>
    /// <remarks>
    /// Reachability here means the absence of an <see cref="IAuthorizeData"/> requirement, not the
    /// presence of <c>[AllowAnonymous]</c>. The fallback policy admits the share subject on its own
    /// (its trie carries the tenant's public read scopes) while rejecting a bare unauthenticated
    /// caller, whose trie is empty — so leaning on the fallback keeps the share working and closes
    /// the anonymous read that <c>[AllowAnonymous]</c> left open on a private tenant. Either shape
    /// passes; an authentication requirement is what 401s every share.
    /// </remarks>
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

            // [AllowAnonymous] on the action wins over anything on the controller, so only look at
            // the class when the action has not opted out.
            var gates = action.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>().ToList();
            if (!action.GetCustomAttributes(inherit: true).OfType<AllowAnonymousAttribute>().Any())
                gates.AddRange(type.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>());

            if (gates.Count > 0)
                offenders.Add($"{name}: carries an authentication requirement, so every public share 401s on it");
        }

        offenders.Should().BeEmpty(
            "these reads back share-visible views and the share principal is IsAuthenticated: false:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Actions whose required scope deviates from their controller's, keyed
    /// <c>Controller.Action</c>. The two statistics endpoints the dashboard and calendar render
    /// (multi-period stats, punch card) are part of the glucose surface, not the reports surface,
    /// so a glucose-only share (the <see cref="Scope.DefaultPublicShareScopes"/>
    /// default) keeps its dashboard.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ActionScopeOverrides =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["StatisticsController.GetMultiPeriodStatistics"] = Scope.GlucoseRead,
            ["StatisticsController.GetPunchCardData"] = Scope.GlucoseRead,
            // Basal delivery is wholly the treatment category, so neither action is broadened to
            // the OR its controller's mixed payloads need.
            ["ChartDataController.GetBasalSeries"] = Scope.TreatmentsRead,
            ["RetrospectiveController.GetBasalTimeline"] = Scope.TreatmentsRead,
        };

    /// <summary>
    /// Read actions on a share-facing controller that require a read scope outside
    /// <see cref="Scope.PublicShareScopes"/>, keyed <c>Controller.Action</c> with the
    /// scope, so no share can reach them however the owner configures the link. Listing one is a
    /// deliberate narrowing and is exempt from
    /// <see cref="EveryRequiredScope_IsGrantableToAShare"/>; everything else on these controllers
    /// must name a scope a share can hold.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> MemberOnlyReadScopeActions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // The resolved therapy profile — basal schedule, ISF, carb ratio, targets — served for
            // an on-device oref run. V1/V3 profile reads require the same scope, and the sharing UI
            // deliberately offers no therapy category.
            ["PredictionController.GetProfileSnapshot"] = Scope.TherapyRead,
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

                var key = $"{type.Name}.{action.Name}";
                var expected = MemberOnlyReadScopeActions.TryGetValue(key, out var m) ? m
                    : ActionScopeOverrides.TryGetValue(key, out var o) ? o
                    : ShareReadableControllers[type.FullName!];

                var effective = classScopes.Concat(ScopesOf(actionAttributes)).ToList();

                if (!effective.Contains(expected))
                    violations.Add($"{type.Name}.{action.Name}: must require {expected}");

                // A gate is share-satisfiable only when every scope it names is a read scope: a
                // requirement naming anything else makes RequireScope demand authentication.
                if (effective.Any(s => !s.EndsWith(".read", StringComparison.Ordinal)))
                    violations.Add($"{type.Name}.{action.Name}: names a non-read scope, which excludes the anonymous share");

                // A multi-category gate must be an OR. An AND over categories a share cannot hold
                // all of refuses every share, and the checks above cannot see it: the scope list is
                // unchanged and each scope is still share-grantable on its own.
                var conjunctions = actionAttributes.Concat(type.GetCustomAttributes(inherit: true))
                    .OfType<RequireScopeAttribute>()
                    .Where(a => a.RequiresAll && a.Scopes.Count > 1);
                if (conjunctions.Any())
                    violations.Add($"{type.Name}.{action.Name}: requires ALL of its categories, so a share "
                        + "granted only some of them is refused the whole endpoint");
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
        scopes.Should().OnlyContain(s => Scope.PublicShareScopes.Contains(s));

        // The converse for the exemptions: an action listed as member-only whose scope a share can
        // in fact hold is not member-only, and the entry is hiding a gate nobody is checking.
        MemberOnlyReadScopeActions.Values.Should()
            .OnlyContain(s => !Scope.PublicShareScopes.Contains(s));
    }

    [Fact]
    public void ListedControllers_Exist()
    {
        foreach (var (name, _) in ShareReadableControllers)
            ApiAssembly.GetType(name).Should().NotBeNull($"{name} is listed but does not exist");

        foreach (var (key, _) in ActionScopeOverrides.Concat(MemberOnlyReadScopeActions))
        {
            var (controller, action) = (key.Split('.')[0], key.Split('.')[1]);
            Controllers().Should().Contain(
                t => t.Name == controller && Actions(t).Any(a => a.Name == action),
                $"{key} is listed as a per-action scope but no such action exists");
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
