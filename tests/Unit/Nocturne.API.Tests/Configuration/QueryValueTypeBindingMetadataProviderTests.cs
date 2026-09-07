using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Controllers.V4.Audit;
using Nocturne.API.Controllers.V4.Devices;
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
/// attribute — which is what the repo-wide sweep here reads off the real metadata provider.
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

    [Fact]
    public void EveryMandatoryQueryValueTypeAcrossTheApi_IsBindingRequired()
    {
        var parameters = MandatoryQueryValueTypes().ToList();

        // A sweep that discovers nothing would pass while guarding nothing.
        parameters.Should().HaveCountGreaterThan(30,
            "the scan should discover the query-bound value types across the controllers");

        var unguarded = parameters
            .Where(p => !_metadata.GetMetadataForParameter(p).IsBindingRequired)
            .Select(p => $"{p.Member.DeclaringType!.Name}.{p.Member.Name}.{p.Name}")
            .ToList();

        unguarded.Should().BeEmpty(
            "an omitted value would bind to default and run the action over a meaningless window "
            + "or id instead of answering 400. Unguarded: " + string.Join(", ", unguarded));
    }

    /// <summary>
    /// The two ways a caller is allowed to omit a query-bound value type. Neither may be made
    /// mandatory: a nullable range filter reads as "no filter", and a defaulted one as its default.
    /// </summary>
    public static TheoryData<Type, string, string> OptionalParameters => new()
    {
        // DateTime? with no default: the range filter every V4 list inherits.
        { typeof(ApsSnapshotController), "GetAll", "from" },
        // int with a default.
        { typeof(AuditController), nameof(AuditController.GetMutationAuditLog), "limit" },
    };

    [Theory]
    [MemberData(nameof(OptionalParameters))]
    public void AnOmittableQueryParameter_IsNotBindingRequired(
        Type controller, string action, string name)
    {
        _metadata.GetMetadataForParameter(ParameterOf(controller, action, name))
            .IsBindingRequired.Should().BeFalse();
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

    private static ParameterInfo ParameterOf(Type controller, string action, string name) =>
        controller.GetMethod(action)!.GetParameters().Single(p => p.Name == name);

    /// <summary>
    /// Every parameter the convention has to cover: declared on a controller GET action, bound
    /// from the query by an explicit attribute, a non-nullable value type, and with no default.
    /// Read off each declaring type so a base-class action is visited once.
    /// </summary>
    private static IEnumerable<ParameterInfo> MandatoryQueryValueTypes() =>
        typeof(Nocturne.API.Program).Assembly
            .GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttributes<HttpGetAttribute>().Any())
            .SelectMany(m => m.GetParameters())
            .Where(p => !p.HasDefaultValue
                && p.ParameterType.IsValueType
                && Nullable.GetUnderlyingType(p.ParameterType) is null
                && p.GetCustomAttributes()
                    .OfType<IBindingSourceMetadata>()
                    .Any(a => a.BindingSource == BindingSource.Query));
}
