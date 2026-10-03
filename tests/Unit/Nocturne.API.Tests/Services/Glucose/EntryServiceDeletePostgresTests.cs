using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V1;
using Nocturne.API.Services.Entries;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Platform;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using V3EntriesController = Nocturne.API.Controllers.V3.EntriesController;

namespace Nocturne.API.Tests.Services.Glucose;

/// <summary>
/// <see cref="EntryService.DeleteEntryAsync"/> on PostgreSQL under the runtime role and Row Level
/// Security: an entry is deleted by any id <see cref="EntryReadService.GetByIdAsync"/> resolves, which
/// v1 and v3 both serve and both delete by.
/// </summary>
[Trait("Category", "Integration")]
public class EntryServiceDeletePostgresTests(EntryServiceDeletePostgresTests.Database database)
    : IClassFixture<EntryServiceDeletePostgresTests.Database>
{
    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("entry_delete_served_id");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private sealed class Factory(Func<NocturneDbContext> create) : ITenantDbContextFactory
    {
        public ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default) => ValueTask.FromResult(create());
    }

    private sealed record Setup(
        EntryService Service,
        EntriesController Controller,
        V3EntriesController V3Controller,
        EntryDecomposer Decomposer,
        EntryReadService Store,
        SensorGlucoseRepository SensorGlucose,
        MeterGlucoseRepository MeterGlucose,
        CalibrationRepository Calibration,
        Mock<IDataEventSink<Entry>> Events,
        Func<NocturneDbContext> Db);

    private NocturneDbContext Context(Guid tenant) =>
        new(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(database.AppConnectionString, npgsql => npgsql.EnableRetryOnFailure(
                maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(1), errorCodesToAdd: null))
            .AddInterceptors(
                new TenantConnectionInterceptor(), new MutationAuditInterceptor(Mock.Of<IHttpContextAccessor>()))
            .Options) { TenantId = tenant, AuditContext = _audit };

    private readonly IAuditContext _audit = Mock.Of<IAuditContext>();

    private async Task<Setup> CreateAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'entry-delete-test', true, now(), now())
                """;
            cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
            cmd.Parameters.Add(new NpgsqlParameter("@slug", $"entry-delete-{tenant:N}"));
            await cmd.ExecuteNonQueryAsync();
        }

        var factory = new Factory(() => Context(tenant));
        var audit = _audit;
        var events = new Mock<IDataEventSink<Entry>>();
        var sensorGlucose = new SensorGlucoseRepository(
            factory, Mock.Of<IDeduplicationService>(), audit, NullLogger<SensorGlucoseRepository>.Instance,
            entrySink: events.Object);
        var meterGlucose = new MeterGlucoseRepository(
            factory, audit, NullLogger<MeterGlucoseRepository>.Instance, entrySink: events.Object);
        var calibration = new CalibrationRepository(
            factory, audit, NullLogger<CalibrationRepository>.Instance, entrySink: events.Object);

        var config = new Mock<IGlucoseProcessingConfigProvider>();
        config.Setup(c => c.GetSourceDefaultsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GlucoseProcessingSourceDefault>());
        config.Setup(c => c.GetPreferredProcessingAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((GlucoseProcessing?)null);

        var decomposer = new EntryDecomposer(
            Context(tenant), sensorGlucose, meterGlucose, calibration, new GlucoseProcessingResolver(config.Object),
            Mock.Of<IPatientDeviceStamper>(), audit, NullLogger<EntryDecomposer>.Instance);
        var store = new EntryReadService(
            sensorGlucose, meterGlucose, calibration, TestDoubles.CanonicalGlucosePassThrough.Create(),
            Mock.Of<IDemoModeService>(), NullLogger<EntryReadService>.Instance);
        var service = new EntryService(
            store, decomposer, Mock.Of<IEntryCache>(), events.Object, NullLogger<EntryService>.Instance);

        var documents = new Mock<IDocumentProcessingService>();
        documents.Setup(d => d.ProcessDocuments(It.IsAny<IEnumerable<Entry>>()))
            .Returns<IEnumerable<Entry>>(entries => entries);
        var controller = new EntriesController(
            service, documents.Object, Mock.Of<IProcessingStatusService>(), Mock.Of<ICanonicalAlertEvaluator>(),
            NullLogger<EntriesController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var v3Controller = new V3EntriesController(
            documents.Object, service, Mock.Of<ICanonicalAlertEvaluator>(), NullLogger<V3EntriesController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        return new Setup(
            service, controller, v3Controller, decomposer, store, sensorGlucose, meterGlucose, calibration, events,
            () => Context(tenant));
    }

    private static string ServedId(Entry entry) =>
        JsonDocument.Parse(JsonSerializer.Serialize(entry)).RootElement.GetProperty("_id").GetString()!;

    private static long Mills() => DateTimeOffset.UtcNow.AddMinutes(-Random.Shared.Next(10, 10_000)).ToUnixTimeMilliseconds();

    [Fact]
    public async Task An_entry_with_a_legacy_id_is_deleted_by_it_and_broadcast()
    {
        var setup = await CreateAsync();
        const string legacyId = "507f1f77bcf86cd799439011";
        await setup.Service.CreateEntriesAsync([new Entry { Id = legacyId, Type = "sgv", Sgv = 120, Mills = Mills() }]);

        (await setup.Service.DeleteEntryAsync(legacyId)).Should().BeTrue();

        (await setup.Service.GetEntryByIdAsync(legacyId)).Should().BeNull();
        setup.Events.Verify(
            e => e.OnDeletedAsync(It.Is<Entry>(d => d.Id == legacyId), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    public enum Id { Served, Uuid }

    [Theory]
    [InlineData(Id.Served)]
    [InlineData(Id.Uuid)]
    public async Task A_sensor_reading_stored_without_a_legacy_id_is_deleted_by_the_id_it_is_read_by(Id shape)
    {
        var setup = await CreateAsync();
        var stored = await setup.SensorGlucose.CreateAsync(
            new SensorGlucose { Timestamp = DateTime.UtcNow.AddMinutes(-7), Mgdl = 140, Device = "connector" },
            WriteOrigin.Live);
        var id = shape == Id.Served ? MongoObjectId.FromGuid(stored.Id) : stored.Id.ToString("N");
        var read = await setup.Service.GetEntryByIdAsync(id);
        read.Should().NotBeNull();
        ServedId(read!).Should().Be(MongoObjectId.FromGuid(stored.Id));

        (await setup.Service.DeleteEntryAsync(id)).Should().BeTrue();

        (await setup.Service.GetEntryByIdAsync(id)).Should().BeNull();
        (await setup.SensorGlucose.GetByIdAsync(stored.Id)).Should().BeNull();
        setup.Events.Verify(
            e => e.OnDeletedAsync(It.Is<Entry>(d => ServedId(d) == ServedId(read!)), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task A_meter_reading_stored_without_a_legacy_id_is_deleted_by_its_served_id()
    {
        var setup = await CreateAsync();
        var stored = await setup.MeterGlucose.CreateAsync(
            new MeterGlucose { Timestamp = DateTime.UtcNow.AddMinutes(-9), Mgdl = 150 }, WriteOrigin.Live);

        (await setup.Service.DeleteEntryAsync(MongoObjectId.FromGuid(stored.Id))).Should().BeTrue();

        (await setup.MeterGlucose.GetByIdAsync(stored.Id)).Should().BeNull();
    }

    [Fact]
    public async Task A_calibration_stored_without_a_legacy_id_is_deleted_by_its_served_id()
    {
        var setup = await CreateAsync();
        var stored = await setup.Calibration.CreateAsync(
            new Calibration { Timestamp = DateTime.UtcNow.AddMinutes(-15), Slope = 850, Intercept = 30000, Scale = 1 },
            WriteOrigin.Live);

        (await setup.Service.DeleteEntryAsync(MongoObjectId.FromGuid(stored.Id))).Should().BeTrue();

        (await setup.Calibration.GetByIdAsync(stored.Id)).Should().BeNull();
    }

    [Fact]
    public async Task A_record_deleted_after_it_was_resolved_deletes_nothing_and_broadcasts_once()
    {
        var setup = await CreateAsync();
        var created = await setup.SensorGlucose.CreateAsync(
            new SensorGlucose { Timestamp = DateTime.UtcNow.AddMinutes(-17), Mgdl = 133 }, WriteOrigin.Live);
        var stored = await setup.Store.GetStoredByIdAsync(MongoObjectId.FromGuid(created.Id));
        stored.Should().NotBeNull();
        await setup.SensorGlucose.DeleteAsync(created.Id, WriteOrigin.Live);

        (await setup.Decomposer.DeleteStoredAsync(stored!, WriteOrigin.Live)).Should().Be(0);

        setup.Events.Verify(
            e => e.OnDeletedAsync(It.IsAny<Entry>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task A_v1_entry_posted_without_an_id_is_read_and_deleted_by_the_id_the_post_returns()
    {
        var setup = await CreateAsync();
        var posted = await setup.Controller.CreateEntries(
            JsonSerializer.SerializeToElement(new { type = "sgv", sgv = 173, date = Mills() }));
        var body = JsonSerializer.SerializeToElement(posted.Result.Should().BeAssignableTo<ObjectResult>().Subject.Value);
        var id = body[0].GetProperty("_id").GetString()!;

        var got = await setup.Controller.GetEntry(id);
        JsonSerializer.SerializeToElement(got.Result.Should().BeOfType<OkObjectResult>().Subject.Value)
            .EnumerateArray().Select(e => e.GetProperty("_id").GetString()).Should().Equal(id);

        var deleted = await setup.Controller.DeleteEntry(id);
        JsonSerializer.SerializeToElement(deleted.Should().BeOfType<OkObjectResult>().Subject.Value)
            .GetProperty("n").GetInt64().Should().Be(1);

        (await setup.Service.GetEntryByIdAsync(id)).Should().BeNull();
    }

    [Theory]
    [InlineData("0123456789abcdef01234567")]
    [InlineData("0199a1b2c3d47e5f8a9b0c1d2e3f4a5b")]
    public async Task An_unknown_id_deletes_nothing(string id)
    {
        var setup = await CreateAsync();
        var stored = await setup.SensorGlucose.CreateAsync(
            new SensorGlucose { Timestamp = DateTime.UtcNow.AddMinutes(-5), Mgdl = 110 }, WriteOrigin.Live);

        (await setup.Service.DeleteEntryAsync(id)).Should().BeFalse();

        (await setup.SensorGlucose.GetByIdAsync(stored.Id)).Should().NotBeNull();
    }

    public enum Api { V1, V3 }

    [Theory]
    [InlineData(Api.V1, "N")]
    [InlineData(Api.V3, "N")]
    [InlineData(Api.V3, "D")]
    public async Task An_entry_whose_legacy_id_is_a_uuid_is_read_and_deleted_by_it(Api api, string format)
    {
        var setup = await CreateAsync();
        var legacyId = Guid.CreateVersion7().ToString(format);
        await setup.Service.CreateEntriesAsync([new Entry { Id = legacyId, Type = "sgv", Sgv = 131, Mills = Mills() }]);

        if (api == Api.V1)
        {
            var got = await setup.Controller.GetEntry(legacyId);
            JsonSerializer.SerializeToElement(got.Result.Should().BeOfType<OkObjectResult>().Subject.Value)
                .GetArrayLength().Should().Be(1);
            (await setup.Controller.DeleteEntry(legacyId)).Should().BeOfType<OkObjectResult>();
        }
        else
        {
            (await setup.V3Controller.GetEntry(legacyId)).Result.Should().BeOfType<OkObjectResult>();
            (await setup.V3Controller.DeleteEntry(legacyId)).Should().BeOfType<NoContentResult>();
        }

        (await setup.Service.GetEntryByIdAsync(legacyId)).Should().BeNull();
        setup.Events.Verify(
            e => e.OnDeletedAsync(It.Is<Entry>(d => d.Id == legacyId), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData("N")]
    [InlineData("D")]
    public async Task A_row_uuid_resolves_before_a_legacy_id_that_spells_it(string format)
    {
        var setup = await CreateAsync();
        var byUuid = await setup.SensorGlucose.CreateAsync(
            new SensorGlucose { Timestamp = DateTime.UtcNow.AddMinutes(-11), Mgdl = 101 }, WriteOrigin.Live);
        var id = byUuid.Id.ToString(format);
        await setup.Service.CreateEntriesAsync([new Entry { Id = id, Type = "sgv", Sgv = 202, Mills = Mills() }]);

        (await setup.Service.GetEntryByIdAsync(id))!.Sgv.Should().Be(101);
        (await setup.Service.DeleteEntryAsync(id)).Should().BeTrue();

        (await setup.SensorGlucose.GetByIdAsync(byUuid.Id)).Should().BeNull();
        (await setup.Service.GetEntryByIdAsync(id))!.Sgv.Should().Be(202);
    }

    [Fact]
    public async Task A_delete_by_uuid_marks_the_row_deleted_by_the_user_as_a_delete_by_legacy_id_does()
    {
        var setup = await CreateAsync();
        const string legacyId = "5f1e2d3c4b5a697887766554";
        await setup.Service.CreateEntriesAsync([new Entry { Id = legacyId, Type = "sgv", Sgv = 120, Mills = Mills() }]);
        var unkeyed = await setup.SensorGlucose.CreateAsync(
            new SensorGlucose { Timestamp = DateTime.UtcNow.AddMinutes(-13), Mgdl = 99 }, WriteOrigin.Live);

        (await setup.Service.DeleteEntryAsync(legacyId)).Should().BeTrue();
        (await setup.Service.DeleteEntryAsync(unkeyed.Id.ToString("N"))).Should().BeTrue();

        await using var db = setup.Db();
        var flags = await db.SensorGlucose.IgnoreQueryFilters()
            .Select(e => new { e.LegacyId, e.DeletedAt, DeletedByUser = EF.Property<bool>(e, "DeletedByUser") })
            .ToListAsync();
        flags.Should().HaveCount(2).And.OnlyContain(f => f.DeletedAt != null && f.DeletedByUser);
    }
}
