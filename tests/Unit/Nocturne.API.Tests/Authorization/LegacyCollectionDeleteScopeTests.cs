using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Routing;
using Nocturne.API.Attributes;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Guards the scope a Nightscout-compat collection delete gates on. A DELETE of one record must
/// name that collection's readwrite scope — not <c>*</c>, which would 403 the ordinary uploader
/// grant, and not a read scope, which would let a follower delete. The V1 query-driven bulk deletes
/// stay on <c>*</c>: they empty a collection in one request.
/// </summary>
/// <remarks>
/// AAPS is the case this exists for. Its OAuth client asks for the per-category readwrite scopes
/// (<see cref="KnownOAuthClients"/>), so a delete gated on <c>*</c> left it retrying a queue of
/// deletions against a 403 forever, with only a superuser token as a workaround.
/// </remarks>
public class LegacyCollectionDeleteScopeTests
{
    /// <summary>
    /// The readwrite scope governing each legacy collection controller, keyed by controller name.
    /// </summary>
    private static readonly Dictionary<string, string> GoverningScopeByController = new(StringComparer.Ordinal)
    {
        ["EntriesController"] = OAuthScopes.GlucoseReadWrite,
        ["TreatmentsController"] = OAuthScopes.TreatmentsReadWrite,
        ["DeviceStatusController"] = OAuthScopes.DevicesReadWrite,
        ["FoodController"] = OAuthScopes.FoodReadWrite,
        ["ProfileController"] = OAuthScopes.TherapyReadWrite,
        ["SettingsController"] = OAuthScopes.TherapyReadWrite,
    };

    /// <summary>
    /// Legacy controllers whose DELETE actions are deliberately outside the per-collection rule,
    /// with the reason. Keeping them here rather than merely absent from
    /// <see cref="GoverningScopeByController"/> is what lets
    /// <see cref="EveryLegacyControllerWithADelete_IsEitherGovernedOrExempt"/> fail when a governed
    /// controller is dropped instead of silently skipping it.
    /// </summary>
    private static readonly Dictionary<string, string> ExemptControllers = new(StringComparer.Ordinal)
    {
        ["ActivityController"] =
            "the legacy activity collection is a merged plane over heart rate, step count and sleep, "
            + "so no single category scope governs it; it needs a per-record guard like the V4 one",
        ["NotificationsController"] =
            "clearing admin notifications is a tenant-administration action, not a data category",
        ["AuthorizationController"] =
            "subject and role deletion is tenant administration, gated by [RequireAdmin]",
    };

    private static readonly string[] LegacyNamespaces =
    [
        "Nocturne.API.Controllers.V1",
        "Nocturne.API.Controllers.V2",
        "Nocturne.API.Controllers.V3",
    ];

    /// <summary>
    /// One row per legacy collection DELETE: the action, the scope it requires today, and the scope
    /// its blast radius warrants — the collection's readwrite scope for a single-record delete,
    /// <see cref="OAuthScopes.FullAccess"/> for a query-driven bulk delete (no <c>{id}</c> in its
    /// route).
    /// </summary>
    private static List<(string Action, string Required, string Expected)> ScanLegacyDeletes()
    {
        var rows = new List<(string, string, string)>();

        foreach (var controller in ControllerActionReflection.GetControllers())
        {
            if (!LegacyNamespaces.Contains(controller.Namespace, StringComparer.Ordinal))
                continue;
            if (!GoverningScopeByController.TryGetValue(controller.Name, out var governing))
                continue;

            foreach (var action in ControllerActionReflection.GetActionMethods(controller))
            {
                var isDelete = action.GetCustomAttributes()
                    .OfType<IActionHttpMethodProvider>()
                    .SelectMany(a => a.HttpMethods)
                    .Contains("DELETE", StringComparer.OrdinalIgnoreCase);

                if (!isDelete)
                    continue;

                var byId = ControllerActionReflection.GetRoutes(controller, action)
                    .Any(route => route.Contains("{id}", StringComparison.Ordinal));

                var required = action.GetCustomAttribute<RequireScopeAttribute>() is { } attribute
                    ? string.Join(",", attribute.Scopes)
                    : "<none>";

                rows.Add((
                    $"{controller.Namespace![^2..]}.{controller.Name}.{action.Name}",
                    required,
                    byId ? governing : OAuthScopes.FullAccess));
            }
        }

        return rows;
    }

    public static TheoryData<string, string, string> LegacyDeletes()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var (action, required, expected) in ScanLegacyDeletes())
        {
            data.Add(action, required, expected);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(LegacyDeletes))]
    public void LegacyCollectionDelete_GatesOnTheScopeItsBlastRadiusWarrants(
        string action, string required, string expected)
    {
        required.Should().Be(expected, $"{action} must require {expected}");
    }

    /// <summary>
    /// Closes the theory's skip channel: a controller dropped from
    /// <see cref="GoverningScopeByController"/> would otherwise vanish from the scan, taking its
    /// delete endpoints out of the guard while every remaining case still passed.
    /// </summary>
    [Fact]
    public void EveryLegacyControllerWithADelete_IsEitherGovernedOrExempt()
    {
        var ungoverned = ControllerActionReflection.GetControllers()
            .Where(c => LegacyNamespaces.Contains(c.Namespace, StringComparer.Ordinal))
            .Where(c => ControllerActionReflection.GetActionMethods(c)
                .Any(a => a.GetCustomAttributes()
                    .OfType<IActionHttpMethodProvider>()
                    .SelectMany(h => h.HttpMethods)
                    .Contains("DELETE", StringComparer.OrdinalIgnoreCase)))
            .Select(c => c.Name)
            .Where(name => !GoverningScopeByController.ContainsKey(name)
                           && !ExemptControllers.ContainsKey(name))
            .ToList();

        ungoverned.Should().BeEmpty(
            "a legacy controller that deletes must either name the collection scope governing it or "
            + "be listed as exempt with a reason. Unclassified: " + string.Join(", ", ungoverned));
    }

    [Fact]
    public void TheScan_FindsBothHalvesOfTheDeleteSurface()
    {
        // Without this the theory above passes vacuously if the reflection query stops matching:
        // zero discovered cases is not a failure on its own.
        var rows = ScanLegacyDeletes();

        rows.Select(row => row.Action.Split('.')[1]).Distinct()
            .Should().BeEquivalentTo(GoverningScopeByController.Keys,
                "every governed controller must contribute at least one delete to the scan");
        rows.Should().Contain(row => row.Expected == OAuthScopes.FullAccess,
            "the bulk-delete half of the split must be exercised too");
        rows.Should().Contain(row => row.Expected == OAuthScopes.TreatmentsReadWrite,
            "the single-record half must be exercised too");
    }

    [Fact]
    public void AapsTypicalScopes_CoverTheTreatmentDeleteGate()
    {
        var aaps = KnownOAuthClients.MatchBySoftwareId("info.nightscout.androidaps");
        var granted = OAuthScopes.Normalize(aaps!.TypicalScopes);

        OAuthScopes.SatisfiesScope(granted, OAuthScopes.TreatmentsReadWrite).Should().BeTrue();
    }
}
