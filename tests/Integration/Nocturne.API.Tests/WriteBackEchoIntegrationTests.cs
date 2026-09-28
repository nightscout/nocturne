using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Nightscout write-back sends a record upstream under a 24-hex id: its uuid-shaped legacy id's
/// prefix, or, with no legacy id, its own uuid's prefix (older write-backs sent the raw uuid). The
/// Nightscout connector pulls the copy back through the same decomposers a v1 upload reaches, so
/// replaying that copy as a v1 upload exercises the round trip end to end against Postgres (#1804).
/// </summary>
[Trait("Category", "Integration")]
public class WriteBackEchoIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    private sealed record V1Record(string Id, string Device);

    /// <remarks>
    /// The pulled-back copy is moved past the window in which the v1 upload path drops an entry
    /// matching a stored one on device, type, value and time before any id is looked at. The
    /// connector path runs no such check, and the copy is matched here by id alone.
    /// </remarks>
    private static object Reading(string device, long date, int sgv, string id) =>
        new { _id = id, type = "sgv", sgv, date, dateString = DateTimeOffset.FromUnixTimeMilliseconds(date).ToString("O"), device };

    private async Task<List<V1Record>> V1Async(string collection, string device)
    {
        var body = await AuthenticatedClient.GetFromJsonAsync<JsonElement>($"/api/v1/{collection}.json?count=1000");
        return body.EnumerateArray()
            .Where(r => r.TryGetProperty("device", out var d) && d.GetString() == device)
            .Select(r => new V1Record(r.GetProperty("_id").GetString()!, device))
            .ToList();
    }

    private async Task<List<(Guid Id, double Mgdl)>> LiveSensorReadingsAsync(string device)
    {
        var body = await AuthenticatedClient.GetFromJsonAsync<JsonElement>(
            $"/api/v4/glucose/sensor?limit=1000&device={Uri.EscapeDataString(device)}");
        return body.GetProperty("data").EnumerateArray()
            .Select(r => (r.GetProperty("id").GetGuid(), r.GetProperty("mgdl").GetDouble()))
            .ToList();
    }

    private async Task<int> LiveApsSnapshotsAsync(string device)
    {
        var body = await AuthenticatedClient.GetFromJsonAsync<JsonElement>(
            $"/api/v4/device-status/aps?limit=1000&device={Uri.EscapeDataString(device)}");
        return body.GetProperty("data").GetArrayLength();
    }

    private async Task<Guid> CreateUnkeyedReadingAsync(string device, long date)
    {
        var created = await AuthenticatedClient.PostAsJsonAsync("/api/v4/glucose/sensor", new
        {
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(date),
            device,
            mgdl = 111,
        });
        created.IsSuccessStatusCode.Should().BeTrue();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static long PastTheV1DuplicateWindow(long date) => date + (long)TimeSpan.FromMinutes(6).TotalMilliseconds;

    /// <summary>
    /// The uuid is what write-back sends today; the 24-hex prefix is what every v1/v3 read serves
    /// for the reading, and so what a client echoing a read sends.
    /// </summary>
    public static TheoryData<string> WireForms => new() { "uuid", "prefix" };

    private static string WireId(Guid id, string form) => form == "uuid" ? id.ToString() : MongoObjectId.FromGuid(id);

    [Theory]
    [MemberData(nameof(WireForms))]
    public async Task APulledBackReading_UpdatesTheReadingItCameFrom(string form)
    {
        var device = $"echo-{Guid.NewGuid():N}";
        var date = DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeMilliseconds();
        var id = await CreateUnkeyedReadingAsync(device, date);
        var servedAs = (await V1Async("entries", device)).Single().Id;

        var echoed = await AuthenticatedClient.PostAsJsonAsync(
            "/api/v1/entries", new[] { Reading(device, PastTheV1DuplicateWindow(date), sgv: 112, WireId(id, form)) });

        echoed.IsSuccessStatusCode.Should().BeTrue();
        (await LiveSensorReadingsAsync(device)).Should().Equal((id, 112d));
        (await V1Async("entries", device)).Select(r => r.Id).Should().Equal(servedAs);
    }

    [Theory]
    [MemberData(nameof(WireForms))]
    public async Task APulledBackReadingTheUserDeleted_StaysDeleted(string form)
    {
        var device = $"echo-{Guid.NewGuid():N}";
        var date = DateTimeOffset.UtcNow.AddMinutes(-40).ToUnixTimeMilliseconds();
        var id = await CreateUnkeyedReadingAsync(device, date);
        (await AuthenticatedClient.DeleteAsync($"/api/v4/glucose/sensor/{id}")).IsSuccessStatusCode.Should().BeTrue();

        await AuthenticatedClient.PostAsJsonAsync("/api/v1/entries", new[] { Reading(device, PastTheV1DuplicateWindow(date), sgv: 112, WireId(id, form)) });

        (await LiveSensorReadingsAsync(device)).Should().BeEmpty();
        (await V1Async("entries", device)).Should().BeEmpty();
    }

    [Fact]
    public async Task APulledBackStatus_UpdatesTheStatusItCameFrom()
    {
        var device = $"openaps://echo-{Guid.NewGuid():N}";
        var at = DateTimeOffset.UtcNow.AddMinutes(-20);
        var created = await AuthenticatedClient.PostAsJsonAsync("/api/v4/device-status/aps", new[]
        {
            new { timestamp = at, device, iob = 1.5 },
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var stored = await V1Async("devicestatus", device);
        stored.Should().ContainSingle();
        var wireId = stored[0].Id;

        var echoed = await AuthenticatedClient.PostAsJsonAsync("/api/v1/devicestatus", new
        {
            _id = wireId,
            device,
            created_at = at.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            openaps = new { iob = new { iob = 1.5, timestamp = at.ToString("O") } },
        });

        echoed.IsSuccessStatusCode.Should().BeTrue();
        (await LiveApsSnapshotsAsync(device)).Should().Be(1);
        (await V1Async("devicestatus", device)).Select(r => r.Id).Should().Equal(wireId);
    }

    [Fact]
    public async Task AnIdLessStatusUpload_IsStoredUnderTheIdItIsServedAndWrittenBackUnder()
    {
        var device = $"openaps://idless-{Guid.NewGuid():N}";
        var at = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var status = new { device, created_at = at, openaps = new { iob = new { iob = 0.4, timestamp = at } } };

        var created = await AuthenticatedClient.PostAsJsonAsync("/api/v1/devicestatus", status);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("_id").GetString();

        id.Should().MatchRegex("^[0-9a-f]{24}$");
        (await V1Async("devicestatus", device)).Select(r => r.Id).Should().Equal(id);

        await AuthenticatedClient.PostAsJsonAsync("/api/v1/devicestatus", new { _id = id, status.device, status.created_at, status.openaps });
        (await LiveApsSnapshotsAsync(device)).Should().Be(1);
    }

    [Theory]
    [InlineData("0198C2A4-1F3B-7C2D-9E55-{0}")]
    [InlineData("0198c2a4-1f3b-7c2d-9e55-{0}")]
    public async Task APulledBackReadingWithAUuidLegacyId_UpdatesTheReadingItCameFrom(string legacyIdFormat)
    {
        var device = $"echo-{Guid.NewGuid():N}";
        var legacyId = string.Format(legacyIdFormat, Guid.NewGuid().ToString("N")[..12]);
        var date = DateTimeOffset.UtcNow.AddMinutes(-50).ToUnixTimeMilliseconds();
        (await AuthenticatedClient.PostAsJsonAsync("/api/v1/entries", new[] { Reading(device, date, sgv: 111, legacyId) }))
            .IsSuccessStatusCode.Should().BeTrue();
        var id = (await LiveSensorReadingsAsync(device)).Single().Id;

        await AuthenticatedClient.PostAsJsonAsync(
            "/api/v1/entries",
            new[] { Reading(device, PastTheV1DuplicateWindow(date), sgv: 112, MongoObjectId.FromGuid(Guid.Parse(legacyId))) });

        (await LiveSensorReadingsAsync(device)).Should().Equal((id, 112d));
    }

    [Fact]
    public async Task APulledBackReadingWithAUuidLegacyIdTheUserDeleted_StaysDeleted()
    {
        var device = $"echo-{Guid.NewGuid():N}";
        var legacyId = Guid.NewGuid().ToString();
        var date = DateTimeOffset.UtcNow.AddMinutes(-55).ToUnixTimeMilliseconds();
        await AuthenticatedClient.PostAsJsonAsync("/api/v1/entries", new[] { Reading(device, date, sgv: 111, legacyId) });
        var id = (await LiveSensorReadingsAsync(device)).Single().Id;
        (await AuthenticatedClient.DeleteAsync($"/api/v4/glucose/sensor/{id}")).IsSuccessStatusCode.Should().BeTrue();

        await AuthenticatedClient.PostAsJsonAsync(
            "/api/v1/entries",
            new[] { Reading(device, PastTheV1DuplicateWindow(date), sgv: 112, MongoObjectId.FromGuid(Guid.Parse(legacyId))) });

        (await LiveSensorReadingsAsync(device)).Should().BeEmpty();
    }

    /// <summary>A slot no other test writes a bolus into, since the fixture's tenant is shared.</summary>
    private static DateTimeOffset UniqueSlot() =>
        new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(Random.Shared.Next(0, 150_000) * 20);

    private async Task<List<(Guid Id, double Insulin)>> LiveBolusesAsync(DateTimeOffset slot)
    {
        var from = Uri.EscapeDataString(slot.AddMinutes(-1).ToString("O"));
        var to = Uri.EscapeDataString(slot.AddMinutes(10).ToString("O"));
        var body = await AuthenticatedClient.GetFromJsonAsync<JsonElement>($"/api/v4/insulin/boluses?limit=50&from={from}&to={to}");
        return body.GetProperty("data").EnumerateArray()
            .Select(r => (r.GetProperty("id").GetGuid(), r.GetProperty("insulin").GetDouble()))
            .ToList();
    }

    private async Task<Guid> CreateV4BolusAsync(DateTimeOffset slot)
    {
        var created = await AuthenticatedClient.PostAsJsonAsync("/api/v4/insulin/boluses", new { timestamp = slot, insulin = 2.5 });
        created.IsSuccessStatusCode.Should().BeTrue();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> EchoBolusAsync(Guid id, string form, DateTimeOffset slot) =>
        AuthenticatedClient.PostAsJsonAsync("/api/v1/treatments", new[]
        {
            new { _id = WireId(id, form), eventType = "Correction Bolus", insulin = 3.0, created_at = slot.AddMinutes(6).ToString("O") },
        });

    [Theory]
    [MemberData(nameof(WireForms))]
    public async Task APulledBackV4NativeTreatment_UpdatesTheTreatmentItCameFrom(string form)
    {
        var slot = UniqueSlot();
        var id = await CreateV4BolusAsync(slot);

        (await EchoBolusAsync(id, form, slot)).IsSuccessStatusCode.Should().BeTrue();

        (await LiveBolusesAsync(slot)).Should().Equal((id, 3d));
    }

    [Theory]
    [MemberData(nameof(WireForms))]
    public async Task APulledBackV4NativeTreatmentTheUserDeleted_StaysDeleted(string form)
    {
        var slot = UniqueSlot();
        var id = await CreateV4BolusAsync(slot);
        (await AuthenticatedClient.DeleteAsync($"/api/v4/insulin/boluses/{id}")).IsSuccessStatusCode.Should().BeTrue();

        await EchoBolusAsync(id, form, slot);

        (await LiveBolusesAsync(slot)).Should().BeEmpty();
    }

    /// <summary>
    /// Nightscout 15.0.7+ gives a written-back copy a <c>_id</c> of its own, so the Nightscout
    /// connector hands it on with the key write-back sent as its
    /// <see cref="ProcessableDocumentBase.UpstreamIdentifier"/>. These publish such copies through
    /// the in-process connector publisher, the path the connector's pulls take.
    /// </summary>
    private async Task PublishAsync(Func<IConnectorPublisher, Task<bool>> publish)
    {
        using var scope = Fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantAccessor>().SetTenant(
            new TenantContext(Fixture.TenantId, ApiIntegrationTestFixture.TenantSlug, "Integration", true, false));
        scope.ServiceProvider.GetRequiredService<NocturneDbContext>().TenantId = Fixture.TenantId;
        (await publish(scope.ServiceProvider.GetRequiredService<IConnectorPublisher>())).Should().BeTrue();
    }

    private const string ConnectorSource = "nightscout-connector";

    private static Entry PulledReading(string device, long date, int sgv, string? identifier) => new()
    {
        Id = MongoObjectId.NewObjectId(),
        UpstreamIdentifier = identifier,
        Type = "sgv",
        Sgv = sgv,
        Mills = date,
        Device = device,
        DataSource = ConnectorSource,
    };

    /// <summary>
    /// A copy the connector pulls of a reading write-back may have sent upstream is that reading's
    /// write-back echo. It lands on the reading and changes nothing, attribution included: a reading
    /// re-attributed to the connector would no longer be written back.
    /// </summary>
    [Theory]
    [InlineData("legacy-{0}")]
    [InlineData("0198C2A4-1F3B-7C2D-9E55-{0}")]
    public async Task APulledCopyUnderAMintedId_LandsOnTheReadingItsIdentifierNamesAndChangesNothing(string legacyIdFormat)
    {
        var device = $"echo-{Guid.NewGuid():N}";
        var legacyId = string.Format(legacyIdFormat, Guid.NewGuid().ToString("N")[..12]);
        var date = DateTimeOffset.UtcNow.AddMinutes(-70).ToUnixTimeMilliseconds();
        await AuthenticatedClient.PostAsJsonAsync("/api/v1/entries", new[] { Reading(device, date, sgv: 111, legacyId) });
        var id = (await LiveSensorReadingsAsync(device)).Single().Id;
        var source = await SensorDataSourceAsync(id);

        await PublishAsync(p => p.Glucose.PublishEntriesAsync([PulledReading(device, date, 114, legacyId)], ConnectorSource, WriteOrigin.Live));

        (await LiveSensorReadingsAsync(device)).Should().Equal((id, 111d));
        (await SensorDataSourceAsync(id)).Should().Be(source).And.NotBe(ConnectorSource);
    }

    private async Task<string?> SensorDataSourceAsync(Guid id) =>
        (await AuthenticatedClient.GetFromJsonAsync<JsonElement>($"/api/v4/glucose/sensor/{id}"))
            .DataSourceOrNull();

    private async Task<int?> PublishRecentAsync(Func<IConnectorPublisher, Task<int?>> publish)
    {
        using var scope = Fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantAccessor>().SetTenant(
            new TenantContext(Fixture.TenantId, ApiIntegrationTestFixture.TenantSlug, "Integration", true, false));
        scope.ServiceProvider.GetRequiredService<NocturneDbContext>().TenantId = Fixture.TenantId;
        return await publish(scope.ServiceProvider.GetRequiredService<IConnectorPublisher>());
    }

    /// <summary>
    /// The recent path compares what a catch-up re-read against what is stored. A written-back
    /// copy is stored under the identifier it came back with, or, written back before identifiers
    /// were sent, under the uuid-shaped legacy id its prefix stands for, so it is not written again.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARecentReadingWrittenBack_IsNotPublishedAgain(bool withIdentifier)
    {
        var device = $"echo-{Guid.NewGuid():N}";
        var legacyId = Guid.NewGuid().ToString();
        var date = DateTimeOffset.UtcNow.AddMinutes(-85).ToUnixTimeMilliseconds();
        await AuthenticatedClient.PostAsJsonAsync("/api/v1/entries", new[] { Reading(device, date, sgv: 111, legacyId) });
        var pulled = PulledReading(device, date, 111, withIdentifier ? legacyId : null);
        if (!withIdentifier)
            pulled.Id = MongoObjectId.FromGuid(Guid.Parse(legacyId));

        var written = await PublishRecentAsync(p => p.Glucose.PublishRecentEntriesAsync([pulled], ConnectorSource, WriteOrigin.Live));

        written.Should().Be(0);
        (await LiveSensorReadingsAsync(device)).Should().ContainSingle();
    }

    [Fact]
    public async Task ARecentStatusWrittenBack_IsNotPublishedAgain()
    {
        var device = $"openaps://echo-{Guid.NewGuid():N}";
        var legacyId = $"loop_status_{Guid.NewGuid():N}";
        var at = DateTimeOffset.UtcNow.AddMinutes(-25).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        (await AuthenticatedClient.PostAsJsonAsync("/api/v1/devicestatus", new
        {
            _id = legacyId, device, created_at = at, openaps = new { iob = new { iob = 0.7, timestamp = at } },
        })).IsSuccessStatusCode.Should().BeTrue();
        var pulled = new DeviceStatus
        {
            Id = MongoObjectId.NewObjectId(),
            UpstreamIdentifier = legacyId,
            Device = device,
            CreatedAt = at,
            OpenAps = new OpenApsStatus(),
        };

        var written = await PublishRecentAsync(p => p.Device.PublishRecentDeviceStatusAsync([pulled], ConnectorSource, WriteOrigin.Live));

        written.Should().Be(0);
        (await LiveApsSnapshotsAsync(device)).Should().Be(1);
    }

    [Fact]
    public async Task APulledCopyUnderAMintedIdOfAReadingTheUserDeleted_StaysDeleted()
    {
        var device = $"echo-{Guid.NewGuid():N}";
        var date = DateTimeOffset.UtcNow.AddMinutes(-75).ToUnixTimeMilliseconds();
        var id = await CreateUnkeyedReadingAsync(device, date);
        (await AuthenticatedClient.DeleteAsync($"/api/v4/glucose/sensor/{id}")).IsSuccessStatusCode.Should().BeTrue();

        await PublishAsync(p => p.Glucose.PublishEntriesAsync([PulledReading(device, date, 114, id.ToString())], ConnectorSource, WriteOrigin.Live));

        (await LiveSensorReadingsAsync(device)).Should().BeEmpty();
    }

    [Fact]
    public async Task APulledReadingWhoseIdentifierNamesNoStoredRecord_IsStoredUnderItsId()
    {
        var device = $"echo-{Guid.NewGuid():N}";
        var date = DateTimeOffset.UtcNow.AddMinutes(-80).ToUnixTimeMilliseconds();
        var pulled = PulledReading(device, date, 120, Guid.NewGuid().ToString());

        await PublishAsync(p => p.Glucose.PublishEntriesAsync([pulled], ConnectorSource, WriteOrigin.Live));

        var stored = await AuthenticatedClient.GetFromJsonAsync<JsonElement>(
            $"/api/v4/glucose/sensor?limit=10&device={Uri.EscapeDataString(device)}");
        stored.GetProperty("data").EnumerateArray().Select(r => r.GetProperty("legacyId").GetString())
            .Should().Equal(pulled.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APulledTreatmentUnderAMintedId_LandsOnTheV4NativeTreatmentItsIdentifierNames(bool deletedByUser)
    {
        var slot = UniqueSlot();
        var id = await CreateV4BolusAsync(slot);
        if (deletedByUser)
            (await AuthenticatedClient.DeleteAsync($"/api/v4/insulin/boluses/{id}")).IsSuccessStatusCode.Should().BeTrue();
        var pulled = new Treatment
        {
            Id = MongoObjectId.NewObjectId(),
            UpstreamIdentifier = id.ToString(),
            EventType = "Correction Bolus",
            Insulin = 3.0,
            CreatedAt = slot.AddMinutes(6).ToString("O"),
            DataSource = ConnectorSource,
        };

        await PublishAsync(p => p.Treatments.PublishTreatmentsAsync([pulled], ConnectorSource, WriteOrigin.Live));

        var live = await LiveBolusesAsync(slot);
        if (deletedByUser)
            live.Should().BeEmpty();
        else
            live.Should().Equal((id, 2.5d));
    }

    private async Task<JsonElement> BolusAsync(Guid id) =>
        await AuthenticatedClient.GetFromJsonAsync<JsonElement>($"/api/v4/insulin/boluses/{id}");

    /// <summary>
    /// Nightscout re-mints the <c>_id</c> of a treatment written back with an identifier, so every
    /// sync pulls the copy back under an id Nocturne never stored. Keyed by that id, the republish
    /// rule saw a new treatment each sync, and the write re-attributed the bolus to the connector:
    /// write-back then skipped it, so an edit made in Nocturne stayed local and the next sync
    /// restored the stale upstream dose over it. Resolved to the bolus, the copy is its echo.
    /// </summary>
    [Fact]
    public async Task AWrittenBackTreatment_KeepsItsSourceAcrossSyncs_AndANocturneEditSurvivesTheStaleCopy()
    {
        var slot = UniqueSlot();
        var id = await CreateV4BolusAsync(slot);
        var source = (await BolusAsync(id)).DataSourceOrNull();
        var mintedUpstream = MongoObjectId.NewObjectId();
        Treatment Copy() => new()
        {
            Id = mintedUpstream,
            UpstreamIdentifier = id.ToString(),
            EventType = "Correction Bolus",
            Insulin = 2.5,
            CreatedAt = slot.ToString("O"),
            DataSource = ConnectorSource,
        };

        await PublishAsync(p => p.Treatments.PublishTreatmentsAsync([Copy()], ConnectorSource, WriteOrigin.Live));
        (await PublishRecentAsync(p => p.Treatments.PublishRecentTreatmentsAsync([Copy()], ConnectorSource, WriteOrigin.Live)))
            .Should().Be(0);

        var afterSyncs = await BolusAsync(id);
        afterSyncs.DataSourceOrNull().Should().Be(source).And.NotBe(ConnectorSource);
        afterSyncs.GetProperty("legacyId").GetString().Should().Be(id.ToString());

        (await AuthenticatedClient.PutAsJsonAsync($"/api/v4/insulin/boluses/{id}", new { timestamp = slot, insulin = 4.0, dataSource = source }))
            .IsSuccessStatusCode.Should().BeTrue();

        await PublishAsync(p => p.Treatments.PublishTreatmentsAsync([Copy()], ConnectorSource, WriteOrigin.Live));
        (await PublishRecentAsync(p => p.Treatments.PublishRecentTreatmentsAsync([Copy()], ConnectorSource, WriteOrigin.Live)))
            .Should().Be(0);

        (await LiveBolusesAsync(slot)).Should().Equal((id, 4d));
        (await BolusAsync(id)).DataSourceOrNull().Should().Be(source);
    }

    /// <summary>
    /// A treatment the connector stores that comes back under a new <c>_id</c> carrying its stored
    /// key as identifier is written under that key, so its fingerprint is stamped there and the next
    /// catch-up finds it unchanged.
    /// </summary>
    [Fact]
    public async Task AConnectorTreatmentReKeyedUpstream_IsFingerprintedUnderItsStoredId()
    {
        var slot = UniqueSlot();
        var storedKey = MongoObjectId.NewObjectId();
        var reKeyed = MongoObjectId.NewObjectId();
        Treatment Pulled(string id, string? identifier, double insulin) => new()
        {
            Id = id,
            UpstreamIdentifier = identifier,
            EventType = "Correction Bolus",
            Insulin = insulin,
            CreatedAt = slot.ToString("O"),
            DataSource = ConnectorSource,
        };

        await PublishAsync(p => p.Treatments.PublishTreatmentsAsync([Pulled(storedKey, null, 2.5)], ConnectorSource, WriteOrigin.Live));
        await PublishAsync(p => p.Treatments.PublishTreatmentsAsync([Pulled(reKeyed, storedKey, 3.0)], ConnectorSource, WriteOrigin.Live));

        (await PublishRecentAsync(p => p.Treatments.PublishRecentTreatmentsAsync([Pulled(reKeyed, storedKey, 3.0)], ConnectorSource, WriteOrigin.Live)))
            .Should().Be(0);
        var live = await LiveBolusesAsync(slot);
        live.Should().ContainSingle().Which.Insulin.Should().Be(3.0);
        (await BolusAsync(live[0].Id)).GetProperty("legacyId").GetString().Should().Be(storedKey);
    }
    /// <summary>Runs <paramref name="import"/> as the Nightscout migration does, straight through a decomposer.</summary>
    private async Task ImportAsync(Func<IServiceProvider, Task> import)
    {
        using var scope = Fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantAccessor>().SetTenant(
            new TenantContext(Fixture.TenantId, ApiIntegrationTestFixture.TenantSlug, "Integration", true, false));
        scope.ServiceProvider.GetRequiredService<NocturneDbContext>().TenantId = Fixture.TenantId;
        await import(scope.ServiceProvider);
    }

    private async Task<Guid> UploadBolusAsync(DateTimeOffset slot, string id, double insulin = 2.5)
    {
        (await AuthenticatedClient.PostAsJsonAsync("/api/v1/treatments", new[]
        {
            new { _id = id, eventType = "Correction Bolus", insulin, created_at = slot.ToString("O") },
        })).IsSuccessStatusCode.Should().BeTrue();
        return (await LiveBolusesAsync(slot)).Single().Id;
    }

    /// <summary>
    /// A treatment goes upstream under the coercion of its key as both <c>_id</c> and
    /// <c>identifier</c>, and Nightscout 15.0.7+ keeps the identifier under a <c>_id</c> of its own.
    /// The copy lands on the treatment its identifier names, whatever the key's shape: an ObjectId
    /// as it is, a uuid as its prefix, anything else as its hash, which Postgres resolves through its
    /// own index. It is the treatment's echo, so nothing changes.
    /// </summary>
    [Theory]
    [InlineData("objectid")]
    [InlineData("uuid")]
    [InlineData("other")]
    public async Task APulledCopyOfATreatmentWrittenBackUnderItsCoercedKey_LandsOnItAndChangesNothing(string shape)
    {
        var slot = UniqueSlot();
        var legacyId = shape switch
        {
            "objectid" => MongoObjectId.NewObjectId(),
            "uuid" => Guid.NewGuid().ToString(),
            _ => $"syn-{Guid.NewGuid():N}",
        };
        var id = await UploadBolusAsync(slot, legacyId);
        var source = (await BolusAsync(id)).DataSourceOrNull();
        var wire = MongoObjectId.Coerce(legacyId)!;

        await PublishAsync(p => p.Treatments.PublishTreatmentsAsync(
            [PulledBolus(slot, MongoObjectId.NewObjectId(), wire, insulin: 4)], ConnectorSource, WriteOrigin.Live));

        (await LiveBolusesAsync(slot)).Should().Equal((id, 2.5d));
        (await BolusAsync(id)).DataSourceOrNull().Should().Be(source).And.NotBe(ConnectorSource);
    }

    private static Treatment PulledBolus(DateTimeOffset slot, string id, string? identifier, double insulin) => new()
    {
        Id = id,
        UpstreamIdentifier = identifier,
        EventType = "Correction Bolus",
        Insulin = insulin,
        CreatedAt = slot.ToString("O"),
        DataSource = ConnectorSource,
    };

    /// <summary>
    /// An earlier write-back sent an edit under the id the treatment is served by, its own uuid's
    /// prefix, whatever its legacy id; that copy lands on the treatment too, rather than beside it.
    /// </summary>
    [Fact]
    public async Task APulledCopyUnderTheIdATreatmentIsServedBy_LandsOnIt()
    {
        var slot = UniqueSlot();
        var id = await UploadBolusAsync(slot, $"syn-{Guid.NewGuid():N}");

        await PublishAsync(p => p.Treatments.PublishTreatmentsAsync(
            [PulledBolus(slot, MongoObjectId.NewObjectId(), MongoObjectId.FromGuid(id), insulin: 4)], ConnectorSource, WriteOrigin.Live));

        (await LiveBolusesAsync(slot)).Should().Equal((id, 2.5d));
    }

    /// <summary>
    /// A treatment a Nightscout migration imported was never written back: the upstream document is
    /// the record's own, and an edit made to it there, such as AAPS marking a dose invalid, reaches
    /// Nocturne on the next pull, whatever form of its key the copy carries.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUpstreamEditOfAMigratedTreatment_UpdatesIt(bool underAHash)
    {
        var slot = UniqueSlot();
        var legacyId = underAHash ? $"syn-{Guid.NewGuid():N}" : MongoObjectId.NewObjectId();
        await ImportAsync(sp => sp.GetRequiredService<ITreatmentDecomposer>().DecomposeBatchAsync(
            [new Treatment { Id = legacyId, EventType = "Correction Bolus", Insulin = 2.5, CreatedAt = slot.ToString("O") }],
            WriteOrigin.Backfill));
        var id = (await LiveBolusesAsync(slot)).Single().Id;
        var wire = MongoObjectId.Coerce(legacyId)!;

        await PublishAsync(p => p.Treatments.PublishTreatmentsAsync(
            [PulledBolus(slot, underAHash ? MongoObjectId.NewObjectId() : legacyId, underAHash ? wire : null, insulin: 1.5)],
            ConnectorSource, WriteOrigin.Live));

        (await LiveBolusesAsync(slot)).Should().Equal((id, 1.5d));
    }

    [Fact]
    public async Task AnUpstreamEditOfAMigratedTempBasal_ShortensIt()
    {
        var slot = UniqueSlot();
        var legacyId = MongoObjectId.NewObjectId();
        Treatment TempBasal(double duration, string? source) => new()
        {
            Id = legacyId, EventType = "Temp Basal", Absolute = 0.4, Rate = 0.4, Duration = duration,
            CreatedAt = slot.ToString("O"), DataSource = source,
        };
        await ImportAsync(sp => sp.GetRequiredService<ITreatmentDecomposer>().DecomposeBatchAsync([TempBasal(30, null)], WriteOrigin.Backfill));

        await PublishAsync(p => p.Treatments.PublishTreatmentsAsync([TempBasal(12, ConnectorSource)], ConnectorSource, WriteOrigin.Live));

        TimeSpan? duration = null;
        await ImportAsync(async sp => duration = await sp.GetRequiredService<NocturneDbContext>().TempBasals.AsNoTracking()
            .Where(t => t.LegacyId == legacyId)
            .Select(t => t.EndTimestamp - t.Timestamp)
            .SingleAsync());
        duration.Should().Be(TimeSpan.FromMinutes(12));
    }

    [Fact]
    public async Task AnUpstreamEditOfAMigratedReading_UpdatesIt()
    {
        var device = $"echo-{Guid.NewGuid():N}";
        var date = DateTimeOffset.UtcNow.AddMinutes(-95).ToUnixTimeMilliseconds();
        var legacyId = MongoObjectId.NewObjectId();
        await ImportAsync(sp => sp.GetRequiredService<IEntryDecomposer>().DecomposeBatchAsync(
            [new Entry { Id = legacyId, Type = "sgv", Sgv = 111, Mills = date, Device = device }], WriteOrigin.Backfill));
        var id = (await LiveSensorReadingsAsync(device)).Single().Id;

        var pulled = PulledReading(device, date, 118, null);
        pulled.Id = legacyId;

        await PublishAsync(p => p.Glucose.PublishEntriesAsync([pulled], ConnectorSource, WriteOrigin.Live));

        (await LiveSensorReadingsAsync(device)).Should().Equal((id, 118d));
    }
}

internal static class WriteBackEchoJson
{
    public static string? DataSourceOrNull(this JsonElement record) =>
        record.TryGetProperty("dataSource", out var source) ? source.GetString() : null;
}
