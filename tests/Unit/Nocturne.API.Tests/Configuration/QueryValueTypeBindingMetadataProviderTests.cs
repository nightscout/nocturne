using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Controllers.V4.Audit;
using Nocturne.API.Tests.Infrastructure;

namespace Nocturne.API.Tests.Configuration;

/// <summary>
/// Pins the convention that a <c>[FromQuery]</c> parameter of a non-nullable value type with no
/// default is mandatory, and that a nullable one or one with a default is not.
/// </summary>
/// <remarks>
/// MVC's own answer to a missing query value is <c>default</c>, so without the convention an
/// omitted date reads as 0001-01-01 and the action answers 200 over an empty window. The rule is
/// registered once, so it holds for every action rather than the ones someone remembered to
/// attribute.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class QueryValueTypeBindingMetadataProviderTests
    : IClassFixture<AuthenticationTestFactory>
{
    private readonly ModelMetadataProvider _metadata;
    private readonly HttpClient _client;

    public QueryValueTypeBindingMetadataProviderTests(AuthenticationTestFactory factory)
    {
        // GetMetadataForParameter lives on the base class, not on the registered interface.
        _metadata = (ModelMetadataProvider)factory.Services
            .GetRequiredService<IModelMetadataProvider>();
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add(
            "api-secret",
            TestDatabaseSeeder.Sha1Hex(AuthenticationTestFactory.ApiSecret));
    }

    public static TheoryData<string, bool> Parameters => new()
    {
        // Non-nullable, no default: mandatory.
        { "from", true },
        { "to", true },
        // Nullable, and value types carrying a default: the caller may omit them.
        { "subjectId", false },
        { "limit", false },
        { "offset", false },
    };

    [Theory]
    [MemberData(nameof(Parameters))]
    public void QueryParameterOfAValueType_IsMandatoryOnlyWhenItCannotBeOmitted(
        string name, bool expected)
    {
        var parameter = typeof(AuditController)
            .GetMethod(nameof(AuditController.GetMutationAuditLog))!
            .GetParameters()
            .Single(p => p.Name == name);

        _metadata.GetMetadataForParameter(parameter)
            .IsBindingRequired.Should().Be(expected);
    }

    [Fact]
    public async Task AGetMissingItsRequiredDate_Answers400NamingTheParameter()
    {
        var response = await _client.GetAsync("/api/v4/audit/mutations?to=2026-01-02T00:00:00Z");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        problem!.Errors.Should().ContainKey("from");
    }

    [Fact]
    public async Task AGetCarryingBothDates_ReachesTheAction()
    {
        var response = await _client.GetAsync(
            "/api/v4/audit/mutations?from=2026-01-01T00:00:00Z&to=2026-01-02T00:00:00Z");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
