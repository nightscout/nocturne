using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data.Migrations;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;
using TreatmentModel = Nocturne.Core.Models.Treatment;
using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// The <c>retire-invalid-treatments</c> job deletes each live temp basal, profile activation, and
/// override or temporary target span that kept <c>isValid: false</c> from before such an upload was
/// stored deleted, as the upload deletes one now: as the system's delete, with the span it ended
/// reopened, the activation it ended settled again and another source's copy promoted.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class RetireInvalidTreatmentsJobIntegrationTests : ApiIntegrationTestBase
{
    private const string JobName = RetireInvalidUploadedTreatmentRows.JobName;

    public RetireInvalidTreatmentsJobIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    private readonly DateTimeOffset _at =
        DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddMinutes(-90).ToUnixTimeMilliseconds());

    private TenantContext Main => new(Fixture.TenantId, ApiIntegrationTestFixture.TenantSlug, "Integration", true, false);

    [Fact]
    public async Task It_deletes_only_the_live_records_that_kept_isValid_false_in_each_tenant_it_runs_for()
    {
        var other = await OtherTenantAsync();
        var (invalid, kept, absent, userDeleted) = (NewId(), NewId(), NewId(), NewId());
        var (otherInvalid, otherAbsent) = (NewId(), NewId());
        await StoreAsync(Main, TempBasal(invalid, _at), TempBasal(kept, _at.AddMinutes(30), isValid: true),
            TempBasal(absent, _at.AddMinutes(60)), TempBasal(userDeleted, _at.AddMinutes(90)));
        await StoreAsync(other, TempBasal(otherInvalid, _at), TempBasal(otherAbsent, _at.AddMinutes(30)));
        await KeepInvalidAsync("temp_basals", "additional_properties", "legacy_id", invalid, userDeleted, otherInvalid);
        await ExecuteAsync(
            $"UPDATE temp_basals SET deleted_at = now(), deleted_by_user = true WHERE legacy_id = '{userDeleted}'");
        var before = await TempBasalsAsync(invalid, kept, absent, userDeleted, otherInvalid, otherAbsent);

        await RunJobAsync(Main);

        var after = await TempBasalsAsync(invalid, kept, absent, userDeleted, otherInvalid, otherAbsent);
        after[invalid].DeletedAt.Should().NotBeNull();
        after[invalid].DeletedByUser.Should().BeFalse("a later upload of the id without isValid writes it again");
        after[invalid].Stamp.Should().BeOnOrAfter(after[invalid].DeletedAt!.Value, "a v3 history client is sent the delete");
        foreach (var untouched in new[] { kept, absent, userDeleted, otherInvalid, otherAbsent })
            after[untouched].Should().Be(before[untouched], "{0} keeps what it held", untouched);

        await RunJobAsync(other);

        var afterOther = await TempBasalsAsync(otherInvalid, otherAbsent);
        afterOther[otherInvalid].DeletedAt.Should().NotBeNull();
        afterOther[otherInvalid].DeletedByUser.Should().BeFalse();
        afterOther[otherAbsent].Should().Be(before[otherAbsent]);
    }

    [Fact]
    public void The_job_the_migration_requests_is_registered() =>
        Fixture.Services.GetServices<IScheduledJob>().Should().ContainSingle(j => j.Name == JobName)
            .Which.Enrolment.Should().Be(JobEnrolment.OnRequest);

    [Fact]
    public async Task A_second_run_deletes_nothing_more()
    {
        var invalid = NewId();
        await StoreAsync(Main, TempBasal(invalid, _at));
        await KeepInvalidAsync("temp_basals", "additional_properties", "legacy_id", invalid);
        await RunJobAsync(Main);
        var retired = await TempBasalsAsync(invalid);

        await RunJobAsync(Main);

        (await TempBasalsAsync(invalid)).Should().Equal(retired);
    }

    [Fact]
    public async Task A_retired_temp_basal_is_served_to_v3_history_as_deleted_and_an_upload_of_its_id_brings_it_back()
    {
        var invalid = NewId();
        await StoreAsync(Main, TempBasal(invalid, _at));
        await KeepInvalidAsync("temp_basals", "additional_properties", "legacy_id", invalid);

        await RunJobAsync(Main);

        await SettleHistoryAsync();
        var history = await GetAsync("/api/v3/treatments/history/0");
        history["result"]!.AsArray()
            .Should().Contain(t => (t!["identifier"] ?? t["_id"])!.GetValue<string>() == invalid
                && t["isValid"]!.GetValue<bool>() == false);

        await StoreAsync(Main, TempBasal(invalid, _at));

        (await QueryAsync("SELECT id FROM temp_basals WHERE legacy_id = @id AND deleted_at IS NULL", r => r.GetGuid(0),
            ("id", invalid))).Should().ContainSingle();
    }

    [Fact]
    public async Task The_override_a_retired_one_ended_runs_to_its_next_live_successor()
    {
        var (first, invalid, next) = (NewId(), NewId(), NewId());
        await StoreAsync(Main,
            Override(first, _at, "Running"), Override(invalid, _at.AddMinutes(20), "Eating"),
            Override(next, _at.AddMinutes(40), "Sleeping"));
        var spans = await SpansAsync(first, invalid, next);
        spans[first].End.Should().Be(_at.AddMinutes(20).UtcDateTime, "the next override ended it");
        spans[first].SupersededBy.Should().Be(spans[invalid].Id);
        await KeepInvalidAsync("state_spans", "metadata", "original_id", invalid);

        await RunJobAsync(Main);

        spans = await SpansAsync(first, invalid, next);
        spans[invalid].DeletedAt.Should().NotBeNull();
        spans[invalid].DeletedByUser.Should().BeFalse();
        spans[first].End.Should().Be(_at.AddMinutes(40).UtcDateTime);
        spans[first].SupersededBy.Should().Be(spans[next].Id);
    }

    [Fact]
    public async Task The_profile_activation_a_retired_one_ended_is_settled_again()
    {
        var (first, invalid) = (NewId(), NewId());
        await StoreAsync(Main, ProfileSwitch(first, _at, "Default"), ProfileSwitch(invalid, _at.AddMinutes(20), "Exercise"));
        var activations = await ActivationsAsync(first, invalid);
        activations[first].End.Should().Be(_at.AddMinutes(20).UtcDateTime, "the next switch ended it");
        await KeepInvalidAsync("profile_activations", "additional_properties", "legacy_id", invalid);

        await RunJobAsync(Main);

        activations = await ActivationsAsync(first, invalid);
        activations[invalid].DeletedAt.Should().NotBeNull();
        activations[invalid].DeletedByUser.Should().BeFalse();
        activations[first].End.Should().BeNull("nothing ends it once the switch after it is gone");
    }

    [Fact]
    public async Task The_override_before_two_retired_ones_runs_to_the_next_live_one()
    {
        var (first, retired, alsoRetired, next) = (NewId(), NewId(), NewId(), NewId());
        await StoreAsync(Main,
            Override(first, _at, "Running"), Override(retired, _at.AddMinutes(20), "Eating"),
            Override(alsoRetired, _at.AddMinutes(40), "Sleeping"), Override(next, _at.AddMinutes(60), "Walking"));
        (await SpansAsync(first))[first].End.Should().Be(_at.AddMinutes(20).UtcDateTime, "the next override ended it");
        await KeepInvalidAsync("state_spans", "metadata", "original_id", retired, alsoRetired);

        await RunJobAsync(Main);

        var spans = await SpansAsync(first, retired, alsoRetired, next);
        spans[retired].DeletedAt.Should().NotBeNull();
        spans[alsoRetired].DeletedAt.Should().NotBeNull();
        spans[first].End.Should().Be(_at.AddMinutes(60).UtcDateTime);
        spans[first].SupersededBy.Should().Be(spans[next].Id);
    }

    [Fact]
    public async Task The_profile_activation_before_two_retired_ones_is_settled_to_end_at_the_next_live_one()
    {
        var (first, retired, alsoRetired, next) = (NewId(), NewId(), NewId(), NewId());
        await StoreAsync(Main,
            ProfileSwitch(first, _at, "Default"), ProfileSwitch(retired, _at.AddMinutes(20), "Exercise"),
            ProfileSwitch(alsoRetired, _at.AddMinutes(40), "Sick"), ProfileSwitch(next, _at.AddMinutes(60), "Night"));
        (await ActivationsAsync(first))[first].End.Should().Be(_at.AddMinutes(20).UtcDateTime, "the next switch ended it");
        await KeepInvalidAsync("profile_activations", "additional_properties", "legacy_id", retired, alsoRetired);

        await RunJobAsync(Main);

        var activations = await ActivationsAsync(first, retired, alsoRetired, next);
        activations[retired].DeletedAt.Should().NotBeNull();
        activations[alsoRetired].DeletedAt.Should().NotBeNull();
        activations[first].End.Should().Be(_at.AddMinutes(60).UtcDateTime);
        activations[next].DeletedAt.Should().BeNull();
    }

    [Fact]
    public async Task Another_sources_copy_of_a_retired_temp_basal_becomes_its_groups_primary()
    {
        var invalid = NewId();
        await StoreAsync(Main, TempBasal(invalid, _at));
        var copy = await WithTenantScopeAsync(sp => sp.GetRequiredService<ITempBasalRepository>().CreateAsync(new V4Models.TempBasal
        {
            StartTimestamp = _at.UtcDateTime.AddSeconds(5),
            EndTimestamp = _at.UtcDateTime.AddSeconds(5).AddMinutes(30),
            LegacyId = "pump_temp_" + invalid,
            Device = "pump-connector",
            DataSource = "pump-connector",
            Rate = 0.8,
            Origin = V4Models.TempBasalOrigin.Algorithm,
        }, WriteOrigin.Live));
        var aaps = (await TempBasalsAsync(invalid))[invalid].Id;
        (await PrimariesAsync(aaps, copy.Id)).Should().Equal(
            new Dictionary<Guid, bool> { [aaps] = true, [copy.Id] = false }, "the earlier copy heads the group");
        var copyBefore = await StampOfAsync(copy.Id);
        await KeepInvalidAsync("temp_basals", "additional_properties", "legacy_id", invalid);

        await RunJobAsync(Main);

        (await PrimariesAsync(aaps, copy.Id)).Should().Equal(new Dictionary<Guid, bool> { [aaps] = false, [copy.Id] = true });
        (await IsDuplicateAsync(copy.Id)).Should().BeFalse();
        (await StampOfAsync(copy.Id)).Should().BeAfter(copyBefore, "a v3 history client is sent the promoted copy");
    }

    private sealed record TempBasalRow(Guid Id, DateTime? DeletedAt, bool DeletedByUser, DateTime Stamp, string? Kept);

    private sealed record SpanRow(Guid Id, DateTime? DeletedAt, bool DeletedByUser, DateTime? End, Guid? SupersededBy);

    private sealed record ActivationRow(Guid Id, DateTime? DeletedAt, bool DeletedByUser, DateTime? End);

    private static string NewId() => Guid.NewGuid().ToString("N")[..24];

    private TreatmentModel TempBasal(string id, DateTimeOffset at, bool? isValid = null) => new()
    {
        Id = id,
        EventType = "Temp Basal",
        Mills = at.ToUnixTimeMilliseconds(),
        Absolute = 0.8,
        Duration = 30,
        EnteredBy = "AndroidAPS",
        IsValid = isValid,
    };

    private static TreatmentModel Override(string id, DateTimeOffset at, string reason) => new()
    {
        Id = id,
        EventType = "Temporary Override",
        Mills = at.ToUnixTimeMilliseconds(),
        Reason = reason,
        EnteredBy = "AndroidAPS",
    };

    private static TreatmentModel ProfileSwitch(string id, DateTimeOffset at, string profile) => new()
    {
        Id = id,
        EventType = "Profile Switch",
        Mills = at.ToUnixTimeMilliseconds(),
        Profile = profile,
        EnteredBy = "AndroidAPS",
    };

    private async Task<TenantContext> OtherTenantAsync()
    {
        var slug = "retire-" + Guid.NewGuid().ToString("N")[..8];
        var id = await AuthTestHelpers.SeedTenantAsync(Fixture, slug, "Retire");
        return new TenantContext(id, slug, "Retire", true, false);
    }

    private async Task StoreAsync(TenantContext tenant, params TreatmentModel[] treatments)
    {
        using var scope = Fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantAccessor>().SetTenant(tenant);
        await scope.ServiceProvider.GetRequiredService<ITreatmentService>()
            .CreateTreatmentsAsync(treatments, WriteOrigin.Live, default);
    }

    /// <summary>The job as the scheduler runs it: in a scope opened for the tenant, reading as no one.</summary>
    private async Task RunJobAsync(TenantContext tenant)
    {
        await using var scope = TenantJobScope.Open(Fixture.Services, tenant, $"job:{JobName}");
        var job = scope.ServiceProvider.GetServices<IScheduledJob>().Single(j => j.Name == JobName);
        (await job.RunAsync(new JobRun(tenant, null, 0, scope.ServiceProvider, DateTimeOffset.UtcNow), default))
            .Should().Be(JobOutcome.Done);
    }

    /// <summary>Writes <c>isValid: false</c> into the rows of <paramref name="legacyIds"/>, as a write before it was stored deleted kept it.</summary>
    private Task KeepInvalidAsync(string table, string column, string legacyIdColumn, params string[] legacyIds) =>
        ExecuteAsync($$"""
            UPDATE {{table}} SET {{column}} = coalesce({{column}}, '{}'::jsonb) || '{"isValid":false}'::jsonb
            WHERE {{legacyIdColumn}} = ANY (@ids)
            """, legacyIds);

    private async Task<JsonNode> GetAsync(string url)
    {
        var response = await AuthenticatedClient.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private Task<Dictionary<string, TempBasalRow>> TempBasalsAsync(params string[] legacyIds) =>
        ByLegacyIdAsync(
            "SELECT legacy_id, id, deleted_at, deleted_by_user, sys_updated_at, additional_properties ->> 'isValid' FROM temp_basals WHERE legacy_id = ANY (@ids)",
            legacyIds,
            r => new TempBasalRow(r.GetGuid(1), Nullable(r, 2), r.GetBoolean(3), r.GetDateTime(4), r.IsDBNull(5) ? null : r.GetString(5)));

    private Task<Dictionary<string, SpanRow>> SpansAsync(params string[] legacyIds) =>
        ByLegacyIdAsync(
            "SELECT original_id, id, deleted_at, deleted_by_user, end_timestamp, superseded_by_id FROM state_spans WHERE original_id = ANY (@ids)",
            legacyIds,
            r => new SpanRow(r.GetGuid(1), Nullable(r, 2), r.GetBoolean(3), Nullable(r, 4), r.IsDBNull(5) ? null : r.GetGuid(5)));

    private Task<Dictionary<string, ActivationRow>> ActivationsAsync(params string[] legacyIds) =>
        ByLegacyIdAsync(
            "SELECT legacy_id, id, deleted_at, deleted_by_user, end_timestamp FROM profile_activations WHERE legacy_id = ANY (@ids)",
            legacyIds,
            r => new ActivationRow(r.GetGuid(1), Nullable(r, 2), r.GetBoolean(3), Nullable(r, 4)));

    private async Task<Dictionary<Guid, bool>> PrimariesAsync(params Guid[] recordIds)
    {
        var rows = await QueryAsync(
            "SELECT record_id, is_primary FROM linked_records WHERE record_type = 'tempbasal' AND record_id = ANY (@ids)",
            r => (r.GetGuid(0), r.GetBoolean(1)), ("ids", recordIds));
        return rows.ToDictionary(r => r.Item1, r => r.Item2);
    }

    private async Task<DateTime> StampOfAsync(Guid id) =>
        (await QueryAsync("SELECT sys_updated_at FROM temp_basals WHERE id = @id", r => r.GetDateTime(0),
            ("id", id))).Single();

    private async Task<bool> IsDuplicateAsync(Guid id) =>
        (await QueryAsync("SELECT is_duplicate FROM temp_basals WHERE id = @id", r => r.GetBoolean(0),
            ("id", id))).Single();

    private async Task<Dictionary<string, T>> ByLegacyIdAsync<T>(
        string sql, string[] legacyIds, Func<NpgsqlDataReader, T> read)
    {
        var rows = await QueryAsync(sql, r => (r.GetString(0), read(r)), ("ids", legacyIds));
        rows.Should().HaveCount(legacyIds.Length, "each id holds one row");
        return rows.ToDictionary(r => r.Item1, r => r.Item2);
    }

    private static DateTime? Nullable(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);

    private async Task ExecuteAsync(string sql, string[]? legacyIds = null)
    {
        await using var conn = await OpenPostgresAsync();
        await conn.ExecuteAsync(sql, legacyIds is null ? [] : [("ids", legacyIds)]);
    }

    private async Task<List<T>> QueryAsync<T>(string sql, Func<NpgsqlDataReader, T> read, params (string Name, object Value)[] parameters)
    {
        await using var conn = await OpenPostgresAsync();
        return await conn.QueryAsync(sql, read, parameters);
    }
}
