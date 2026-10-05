using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Controllers.V1;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V1;

/// <summary>
/// Nightscout clients update by PUT to the collection path with the id in the body. Without an
/// action on that template the router answers 405, so the route table itself is the contract.
/// </summary>
[Trait("Category", "Unit")]
public class CollectionPutRouteTests
{
    private static readonly IReadOnlyList<ActionDescriptor> Actions = BuildActions();

    [Theory]
    [InlineData("api/v1/treatments")]
    [InlineData("api/v1/activity")]
    public void Put_OnTheCollectionPath_IsRouted(string path)
    {
        Actions.Should().Contain(a =>
            string.Equals(a.AttributeRouteInfo!.Template, path, StringComparison.OrdinalIgnoreCase)
            && a.ActionConstraints!.OfType<HttpMethodActionConstraint>()
                .Any(c => c.HttpMethods.Contains("PUT")));
    }

    private static IReadOnlyList<ActionDescriptor> BuildActions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers().AddApplicationPart(typeof(TreatmentsController).Assembly);
        return services.BuildServiceProvider()
            .GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .Where(a => a.AttributeRouteInfo is not null && a.ActionConstraints is not null)
            .ToList();
    }
}
