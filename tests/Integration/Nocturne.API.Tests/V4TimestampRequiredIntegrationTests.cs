using System.Collections;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.API.Validators.V4;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Every V4 endpoint that writes a record answers a missing timestamp with <see cref="TimestampRule.Message"/>,
/// in the problem shape it answered in before the message was shared.
/// </summary>
[Collection(ApiIntegrationShards.Four)]
[Trait("Category", "Integration")]
public class V4TimestampRequiredIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    /// <summary>A <c>ProblemDetails</c> body carries the message in <c>detail</c>; a <c>ValidationProblemDetails</c> body, in <c>errors</c>.</summary>
    private enum Shape
    {
        Problem,
        Validation,
    }

    private sealed record RecordWrite(string Method, string Route, Type RequestType, bool IsArray)
    {
        public string Key => $"{Method} {Route}";

        public override string ToString() => $"{Key} ({RequestType.Name}{(IsArray ? "[]" : "")})";
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// The shape each record write answers a missing timestamp in, as the endpoint answered it before
    /// the message was shared. A bulk write checks inside the action, so it answers a problem. A write
    /// whose request has a validator is answered by auto-validation, before the action runs. Basal
    /// injection has no validator, so its single writes reach the base check, which answers a problem.
    /// </summary>
    private static readonly Dictionary<string, Shape> Expected = new()
    {
        ["POST api/v4/device-status/aps"] = Shape.Problem,
        ["POST api/v4/device-status/pump"] = Shape.Problem,
        ["POST api/v4/device-status/uploader"] = Shape.Problem,

        ["POST api/v4/glucose/calibrations"] = Shape.Validation,
        ["POST api/v4/glucose/calibrations/bulk"] = Shape.Problem,
        ["PUT api/v4/glucose/calibrations/{id:guid}"] = Shape.Validation,
        ["POST api/v4/glucose/meter"] = Shape.Validation,
        ["POST api/v4/glucose/meter/bulk"] = Shape.Problem,
        ["PUT api/v4/glucose/meter/{id:guid}"] = Shape.Validation,
        ["POST api/v4/glucose/sensor"] = Shape.Validation,
        ["POST api/v4/glucose/sensor/bulk"] = Shape.Problem,
        ["PUT api/v4/glucose/sensor/{id:guid}"] = Shape.Validation,

        ["POST api/v4/insulin/basal-injections"] = Shape.Problem,
        ["POST api/v4/insulin/basal-injections/bulk"] = Shape.Problem,
        ["PUT api/v4/insulin/basal-injections/{id:guid}"] = Shape.Problem,
        ["POST api/v4/insulin/boluses"] = Shape.Validation,
        ["POST api/v4/insulin/boluses/bulk"] = Shape.Problem,
        ["PUT api/v4/insulin/boluses/{id:guid}"] = Shape.Validation,
        ["POST api/v4/insulin/calculations"] = Shape.Validation,
        ["POST api/v4/insulin/calculations/bulk"] = Shape.Problem,
        ["PUT api/v4/insulin/calculations/{id:guid}"] = Shape.Validation,
        ["POST api/v4/insulin/temp-basals"] = Shape.Validation,
        ["POST api/v4/insulin/temp-basals/bulk"] = Shape.Problem,
        ["PUT api/v4/insulin/temp-basals/{id:guid}"] = Shape.Validation,

        ["POST api/v4/nutrition/carbs"] = Shape.Validation,
        ["POST api/v4/nutrition/carbs/bulk"] = Shape.Problem,
        ["PUT api/v4/nutrition/carbs/{id:guid}"] = Shape.Validation,
        ["POST api/v4/nutrition/meals"] = Shape.Validation,

        ["POST api/v4/observations/bg-checks"] = Shape.Validation,
        ["POST api/v4/observations/bg-checks/bulk"] = Shape.Problem,
        ["PUT api/v4/observations/bg-checks/{id:guid}"] = Shape.Validation,
        ["POST api/v4/observations/device-events"] = Shape.Validation,
        ["POST api/v4/observations/device-events/bulk"] = Shape.Problem,
        ["PUT api/v4/observations/device-events/{id:guid}"] = Shape.Validation,
        ["POST api/v4/observations/notes"] = Shape.Validation,
        ["POST api/v4/observations/notes/bulk"] = Shape.Problem,
        ["PUT api/v4/observations/notes/{id:guid}"] = Shape.Validation,
    };

    /// <summary>Update targets that must exist, because the action reads the stored record before it checks the timestamp.</summary>
    private static readonly Dictionary<Type, object> SeededBeforeUpdate = new()
    {
        [typeof(UpdateBasalInjectionRequest)] = new CreateBasalInjectionRequest { Timestamp = DateTimeOffset.UtcNow, Units = 10 },
    };

    [Fact]
    public async Task EveryRecordWrite_AnswersAMissingTimestampWithTheOneMessageInItsOwnShape()
    {
        var writes = RecordWrites();
        using var client = CreateAuthenticatedClient();

        var observed = new SortedDictionary<string, Shape>(StringComparer.Ordinal);
        var wrong = new List<string>();
        foreach (var write in writes)
        {
            var instance = Activator.CreateInstance(write.RequestType)!;
            var body = write.IsArray ? new[] { instance } : (object)instance;
            var route = write.Route;
            if (route.Contains("{id", StringComparison.Ordinal))
                route = System.Text.RegularExpressions.Regex.Replace(route, @"\{id[^}]*\}", (await ExistingIdAsync(client, write)).ToString());

            using var message = new HttpRequestMessage(new HttpMethod(write.Method), "/" + route)
            {
                Content = JsonContent.Create(body, body.GetType(), options: JsonOptions),
            };
            using var response = await client.SendAsync(message);
            var text = await response.Content.ReadAsStringAsync();

            if (response.StatusCode != HttpStatusCode.BadRequest)
            {
                wrong.Add($"{write}: {(int)response.StatusCode} {text}");
                continue;
            }

            if (ReadShape(text) is not { } shape)
            {
                wrong.Add($"{write}: the body has neither a detail nor errors: {text}");
                continue;
            }

            observed[write.Key] = shape;
            if (!text.Contains(TimestampRule.Message, StringComparison.Ordinal))
                wrong.Add($"{write}: the body does not carry the message: {text}");
        }

        wrong.Should().BeEmpty("a V4 record write refuses a missing timestamp with {0}", TimestampRule.Message);

        var differences = observed
            .Where(o => !Expected.TryGetValue(o.Key, out var shape) || shape != o.Value)
            .Select(o => $"{o.Key}: answered {o.Value}, expected {(Expected.TryGetValue(o.Key, out var e) ? e.ToString() : "nothing")}")
            .Concat(Expected.Keys.Except(observed.Keys).Select(k => $"{k}: expected but no endpoint has it"))
            .ToList();
        differences.Should().BeEmpty("each write answers in the problem shape it had before the message was shared");
    }

    [Fact]
    public void EveryRequestWithARecordHeader_IsReachedByARecordWrite()
    {
        var reached = RecordWrites().Select(w => w.RequestType).ToHashSet();

        var all = typeof(IV4RecordHeader).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IV4RecordHeader).IsAssignableFrom(t));

        all.Should().OnlyContain(t => reached.Contains(t), "a request with a record header is the body of a write");
    }

    private static Shape? ReadShape(string text)
    {
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return null;

        var hasErrors = document.RootElement.TryGetProperty("errors", out _);
        var hasDetail = document.RootElement.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String;
        return (hasErrors, hasDetail) switch
        {
            (true, false) => Shape.Validation,
            (false, true) => Shape.Problem,
            _ => null,
        };
    }

    private static async Task<Guid> ExistingIdAsync(HttpClient client, RecordWrite write)
    {
        if (!SeededBeforeUpdate.TryGetValue(write.RequestType, out var seed))
            return Guid.NewGuid();

        var collection = System.Text.RegularExpressions.Regex.Replace(write.Route, @"/\{id[^}]*\}$", "");
        using var created = await client.PostAsync("/" + collection, JsonContent.Create(seed, seed.GetType(), options: JsonOptions));
        created.StatusCode.Should().Be(HttpStatusCode.Created, "{0} needs a stored record to update", write);
        using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private List<RecordWrite> RecordWrites()
    {
        var writes = new List<RecordWrite>();
        foreach (var endpoint in Fixture.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var action = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>();
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
            if (action is null || methods is null)
                continue;

            var body = action.Parameters.FirstOrDefault(p => p.BindingInfo?.BindingSource == BindingSource.Body);
            if (body is null)
                continue;

            var bodyType = body.ParameterType;
            var isArray = bodyType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(bodyType);
            var requestType = isArray ? ElementTypeOf(bodyType, action) : bodyType;
            if (!typeof(IV4RecordHeader).IsAssignableFrom(requestType))
                continue;

            foreach (var method in methods)
                writes.Add(new RecordWrite(method, endpoint.RoutePattern.RawText!, requestType, isArray));
        }

        writes.Should().NotBeEmpty();
        return writes;
    }

    private static Type ElementTypeOf(Type enumerable, ControllerActionDescriptor action)
    {
        var elements = (enumerable.IsGenericType && enumerable.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                ? new[] { enumerable }
                : enumerable.GetInterfaces())
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .Select(i => i.GetGenericArguments()[0])
            .Distinct()
            .ToList();

        if (enumerable.IsArray)
            elements = [enumerable.GetElementType()!];

        return elements.Count == 1
            ? elements[0]
            : throw new InvalidOperationException(
                $"{action.DisplayName} takes a {enumerable} body, whose element type this test cannot read: teach it here.");
    }
}
