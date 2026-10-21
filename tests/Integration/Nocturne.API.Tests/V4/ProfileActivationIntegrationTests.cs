using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.V4;

/// <summary>
/// An AAPS Profile Switch and the effective switch that confirms it, uploaded as AAPS uploads them,
/// read back on v1, v3 and v4 and through the profile resolvers.
/// </summary>
[Trait("Category", "Integration")]
public class ProfileActivationIntegrationTests : ApiIntegrationTestBase
{
    public ProfileActivationIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    private const string BaseProfileJson =
        """{"units":"mg/dl","dia":5,"timezone":"UTC","sens":[{"time":"00:00","value":40}],"carbratio":[{"time":"00:00","value":10}],"basal":[{"time":"00:00","value":1.0}],"target_low":[{"time":"00:00","value":100}],"target_high":[{"time":"00:00","value":110}]}""";

    private const string EffectiveProfileJson =
        """{"units":"mg/dl","dia":5,"timezone":"UTC","sens":[{"time":"00:00","value":36}],"carbratio":[{"time":"00:00","value":9}],"basal":[{"time":"00:00","value":1.1}],"target_low":[{"time":"00:00","value":100}],"target_high":[{"time":"00:00","value":110}]}""";

    private static long MinutesAgo(int minutes) => DateTimeOffset.UtcNow.AddMinutes(-minutes).ToUnixTimeMilliseconds();

    private static string Iso(long mills) =>
        DateTimeOffset.FromUnixTimeMilliseconds(mills).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private static object ProfileSwitchV1(string id, long mills) => new
    {
        _id = id,
        eventType = "Profile Switch",
        created_at = Iso(mills),
        enteredBy = "openaps://AndroidAPS",
        isValid = true,
        profile = "Rhys (110%)",
        originalProfileName = "Rhys",
        originalDuration = 0,
        duration = 0,
        timeshift = 0,
        percentage = 110,
        profileJson = BaseProfileJson,
    };

    private static object EffectiveSwitchV1(string id, long mills) => new
    {
        _id = id,
        eventType = "Note",
        created_at = Iso(mills),
        enteredBy = "openaps://AndroidAPS",
        isValid = true,
        notes = "Rhys (110%)",
        profileJson = EffectiveProfileJson,
        originalProfileName = "Rhys",
        originalCustomizedName = "Rhys (110%)",
        originalTimeshift = 0,
        originalPercentage = 110,
        originalDuration = 0,
        originalEnd = mills,
    };

    private static async Task<JsonNode> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static async Task PostV1Async(HttpClient client, params object[] treatments)
    {
        var post = await client.PostAsJsonAsync("/api/v1/treatments", treatments);
        post.StatusCode.Should().Be(HttpStatusCode.OK, await post.Content.ReadAsStringAsync());
    }

    private async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = Fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantAccessor>().SetTenant(
            new TenantContext(Fixture.TenantId, ApiIntegrationTestFixture.TenantSlug, "Integration", true, false));
        return await action(scope.ServiceProvider);
    }

    [Fact]
    public async Task A_switch_and_its_confirmation_round_trip_under_the_activations_own_ids()
    {
        var client = CreateAuthenticatedClient();
        var switchMills = MinutesAgo(60);
        var confirmedMills = switchMills + 30_000;
        await PostV1Async(client, ProfileSwitchV1("6500000000000000000000a1", switchMills));
        await PostV1Async(client, EffectiveSwitchV1("6500000000000000000000a2", confirmedMills));

        var activations = (await GetAsync(client, $"/api/v4/profile/activations?from={Uri.EscapeDataString(Iso(switchMills - 60_000))}")).AsArray();
        activations.Should().HaveCount(2);
        var requested = activations.Single(a => a!["phase"]!.GetValue<string>() == "Requested")!;
        var effective = activations.Single(a => a!["phase"]!.GetValue<string>() == "Effective")!;
        requested["endMills"]!.GetValue<long>().Should().Be(confirmedMills, "the confirmation ends the request");
        effective["baseProfileName"]!.GetValue<string>().Should().Be("Rhys");
        effective["percentage"]!.GetValue<int>().Should().Be(110);

        var servedIds = activations.Select(a => Guid.Parse(a!["id"]!.GetValue<string>())).ToList();
        var v1 = (await GetAsync(client, "/api/v1/treatments")).AsArray();
        v1.Select(t => t!["_id"]!.GetValue<string>()).Should().BeEquivalentTo(
            servedIds.Select(MongoObjectId.FromGuid), "a treatment is served under its activation's id");
        v1.Single(t => t!["eventType"]!.GetValue<string>() == "Note")!["originalCustomizedName"]!
            .GetValue<string>().Should().Be("Rhys (110%)");
        var servedSwitch = v1.Single(t => t!["eventType"]!.GetValue<string>() == "Profile Switch")!;
        servedSwitch["profile"]!.GetValue<string>().Should().Be("Rhys (110%)");
        servedSwitch["profileJson"]!.GetValue<string>().Should().Be(BaseProfileJson);

        var v3 = (await GetAsync(client, "/api/v3/treatments"))["result"]!.AsArray();
        v3.Select(t => t!["identifier"]!.GetValue<string>()).Should().BeEquivalentTo(
            servedIds.Select(MongoObjectId.FromGuid), "v3 serves the same identifier v1 does");
    }

    [Fact]
    public async Task The_confirmation_is_in_force_and_its_profile_is_read_as_uploaded()
    {
        var client = CreateAuthenticatedClient();
        var switchMills = MinutesAgo(60);
        await PostV1Async(client, ProfileSwitchV1("6500000000000000000000b1", switchMills));
        await PostV1Async(client, EffectiveSwitchV1("6500000000000000000000b2", switchMills + 30_000));

        var active = await GetAsync(client, "/api/v4/profile/activations/active");
        active["phase"]!.GetValue<string>().Should().Be("Effective");

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var resolved = await WithScopeAsync(async sp => (
            Source: (await sp.GetRequiredService<IProfileActivationResolver>().GetAtAsync(now))!.Source,
            Basal: await sp.GetRequiredService<IBasalRateResolver>().GetBasalRateAsync(now),
            Sensitivity: await sp.GetRequiredService<ISensitivityResolver>().GetSensitivityAsync(now)));

        resolved.Source.Should().Be(ProfileScheduleSource.CustomizedSnapshot);
        resolved.Basal.Should().BeApproximately(1.1, 1e-9, "110% is already in the effective profile");
        resolved.Sensitivity.Should().BeApproximately(36, 1e-9);
    }

    [Fact]
    public async Task Only_the_activations_in_force_are_listed_as_applied()
    {
        var client = CreateAuthenticatedClient();
        var switchMills = MinutesAgo(60);
        await PostV1Async(client, ProfileSwitchV1("6500000000000000000000d1", switchMills));
        await PostV1Async(client, EffectiveSwitchV1("6500000000000000000000d2", switchMills + 30_000));

        var applied = (await GetAsync(client,
            $"/api/v4/profile/activations?applied=true&from={Uri.EscapeDataString(Iso(switchMills + 60_000))}")).AsArray();

        applied.Should().ContainSingle().Which!["phase"]!.GetValue<string>().Should().Be("Effective",
            "the request is read from what the pump reported running, so only the report applies");
    }

    [Fact]
    public async Task A_profile_state_span_is_refused_with_where_switches_live()
    {
        var response = await CreateAuthenticatedClient().PostAsJsonAsync("/api/v4/state-spans", new
        {
            category = "Profile",
            state = "Active",
            startMills = MinutesAgo(10),
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("/api/v4/profile/activations");
    }

    [Fact]
    public async Task No_activation_answers_no_content()
    {
        var response = await CreateAuthenticatedClient().GetAsync("/api/v4/profile/activations/active");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task History_pages_activations_by_their_modification_and_delivers_their_deletes()
    {
        var client = CreateAuthenticatedClient();
        var switchMills = MinutesAgo(60);
        await PostV1Async(client, ProfileSwitchV1("6500000000000000000000c1", switchMills));
        var first = (await GetAsync(client, "/api/v3/treatments/history/0"))["result"]!.AsArray();
        var served = first.Should().ContainSingle().Subject!;
        var identifier = served["identifier"]!.GetValue<string>();
        var cursor = served["srvModified"]!.GetValue<long>();

        (await GetAsync(client, $"/api/v3/treatments/history/{cursor + 1}"))["result"]!.AsArray().Should().BeEmpty();

        (await client.DeleteAsync($"/api/v3/treatments/{identifier}")).StatusCode.Should().Be(HttpStatusCode.OK);

        var after = (await GetAsync(client, $"/api/v3/treatments/history/{cursor + 1}"))["result"]!.AsArray();
        var tombstone = after.Should().ContainSingle().Subject!;
        tombstone["identifier"]!.GetValue<string>().Should().Be(identifier);
        tombstone["isValid"]!.GetValue<bool>().Should().BeFalse();

        await PostV1Async(client, ProfileSwitchV1("6500000000000000000000c1", switchMills));
        (await GetAsync(client, "/api/v1/treatments")).AsArray().Should().BeEmpty("a deleted switch is not brought back by a re-upload");
    }

    [Fact]
    public async Task The_chart_labels_each_stretch_with_its_activation()
    {
        var client = CreateAuthenticatedClient();
        var switchMills = MinutesAgo(60);
        await PostV1Async(client, ProfileSwitchV1("6500000000000000000000d1", switchMills));

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var chart = await GetAsync(client, $"/api/v4/chartdata/dashboard?startTime={now - 3 * 3_600_000}&endTime={now}");
        var span = chart["profileSpans"]!.AsArray().Should().ContainSingle().Subject!;
        span["metadata"].Should().BeNull();
        span["profileActivation"]!["baseProfileName"]!.GetValue<string>().Should().Be("Rhys");
        span["profileActivation"]!["percentage"]!.GetValue<int>().Should().Be(110);
    }
}
