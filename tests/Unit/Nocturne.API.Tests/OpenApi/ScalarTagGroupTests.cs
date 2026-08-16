using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Configuration;
using Nocturne.API.OpenApi;
using Nocturne.API.Tests.Authorization;

namespace Nocturne.API.Tests.OpenApi;

/// <summary>
/// Guards that every tag a controller publishes into an OpenAPI document is claimed by exactly one
/// <c>x-tagGroups</c> entry in <see cref="ScalarExtensionsDocumentTransformer"/>.
/// </summary>
/// <remarks>
/// Scalar builds its sidebar from <c>x-tagGroups</c> when the extension is present, so a tag no
/// group lists is dropped from the navigation even though its operations are in the served spec.
/// The failure is silent — the endpoints work, they are just undiscoverable — which is how the
/// whole <c>Sleep</c> surface (<c>api/v4/sleep/sessions</c> and <c>api/v4/sleep/report</c>) went
/// missing from the docs. The transformer's own filtering (<c>tags.Where(usedTags.Contains)</c>)
/// only prunes groups down to the tags in use; it never adopts a tag that no group claims, so
/// nothing catches an orphan at runtime.
/// </remarks>
public class ScalarTagGroupTests
{
    [Theory]
    [InlineData("nocturne")]
    [InlineData("nightscout")]
    public void EveryPublishedTag_IsClaimedByATagGroup(string documentName)
    {
        var groupedTags = ScalarExtensionsDocumentTransformer.TagGroupsFor(documentName)
            .SelectMany(group => group.Value)
            .ToHashSet(StringComparer.Ordinal);

        var orphans = PublishedTags(documentName)
            .Where(entry => !groupedTags.Contains(entry.Tag))
            .Select(entry => $"{entry.Tag} (published by {entry.Controller})")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToList();

        orphans.Should().BeEmpty(
            $"Scalar renders the '{documentName}' sidebar from x-tagGroups, so operations under an "
            + "ungrouped tag are served but never shown. Either add the tag to a group in "
            + "ScalarExtensionsDocumentTransformer, or give the controller a [Tags] attribute "
            + $"naming a grouped tag:\n  {string.Join("\n  ", orphans)}");
    }

    [Theory]
    [InlineData("nocturne")]
    [InlineData("nightscout")]
    public void NoTag_IsClaimedByTwoTagGroups(string documentName)
    {
        var duplicated = ScalarExtensionsDocumentTransformer.TagGroupsFor(documentName)
            .SelectMany(group => group.Value.Select(tag => (Group: group.Key, Tag: tag)))
            .GroupBy(entry => entry.Tag, StringComparer.Ordinal)
            .Where(byTag => byTag.Count() > 1)
            .Select(byTag => $"{byTag.Key} -> {string.Join(", ", byTag.Select(e => e.Group))}")
            .ToList();

        duplicated.Should().BeEmpty(
            "a tag listed in two groups renders its operations twice in the Scalar sidebar:\n  "
            + string.Join("\n  ", duplicated));
    }

    /// <summary>
    /// The tags the controllers of <paramref name="documentName"/> publish, resolved the way the
    /// runtime Microsoft.OpenApi pipeline does: an explicit <c>[Tags]</c> attribute when the
    /// controller carries one, otherwise the API explorer's default of the controller name minus
    /// its "Controller" suffix.
    /// </summary>
    /// <remarks>
    /// The build-time NSwag document is unaffected by <c>[Tags]</c> —
    /// <see cref="ControllerNameTagOperationProcessor"/> overwrites every tag with the controller
    /// name so the TypeScript client stays one class per controller — so nothing here changes the
    /// generated client.
    /// </remarks>
    private static IEnumerable<(string Tag, string Controller)> PublishedTags(string documentName)
    {
        foreach (var type in ControllerActionReflection.GetControllers())
        {
            if (!InDocument(documentName, type.Namespace ?? string.Empty))
                continue;
            if (!IsVisibleToApiExplorer(type))
                continue;

            var explicitTags = type.GetCustomAttributes(inherit: true).OfType<ITagsMetadata>()
                .SelectMany(attribute => attribute.Tags)
                .ToList();

            if (explicitTags.Count == 0)
            {
                yield return (DefaultTag(type), type.Name);
                continue;
            }

            foreach (var tag in explicitTags)
                yield return (tag, type.Name);
        }
    }

    private static bool InDocument(string documentName, string controllerNamespace) =>
        documentName == "nightscout"
            ? ApiDocumentMembership.InNightscoutDocument(controllerNamespace)
            : ApiDocumentMembership.InNocturneDocument(controllerNamespace);

    private static string DefaultTag(Type controller) =>
        controller.Name.EndsWith("Controller", StringComparison.Ordinal)
            ? controller.Name[..^"Controller".Length]
            : controller.Name;

    /// <summary>
    /// Whether any of <paramref name="controller"/>'s actions reach the OpenAPI document. A
    /// controller-level <c>IgnoreApi = true</c> hides the whole controller unless an action opts
    /// back in — which <c>MetadataController</c> does, existing only to pull types into the spec.
    /// </summary>
    private static bool IsVisibleToApiExplorer(Type controller)
    {
        var controllerSetting = controller.GetCustomAttributes(inherit: true)
            .OfType<ApiExplorerSettingsAttribute>()
            .FirstOrDefault();

        if (controllerSetting?.IgnoreApi != true)
            return true;

        return ControllerActionReflection.GetActionMethods(controller)
            .Any(action => action.GetCustomAttributes(inherit: true)
                .OfType<ApiExplorerSettingsAttribute>()
                .Any(setting => !setting.IgnoreApi));
    }
}
