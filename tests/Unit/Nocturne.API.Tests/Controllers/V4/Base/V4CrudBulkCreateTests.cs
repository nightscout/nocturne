using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Controllers.V4.Treatments;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Base;

/// <summary>
/// The bulk create <see cref="V4CrudControllerBase{TModel,TCreateRequest,TUpdateRequest,TRepository}"/>
/// gives every derived controller, exercised at two of them: what reaches the repository, what comes
/// back, and what the cap and the timestamp guard reject.
/// </summary>
/// <remarks>
/// Whether a re-sent sync key updates a row or inserts one is the repository's to decide, and only
/// the types deriving from <c>SyncUpsertRepositoryBase</c> upsert on it (notes do not). What the
/// controller owes either way is to hand the request's own (DataSource, SyncIdentifier) to the
/// repository unaltered on every send, which is what these assert.
/// </remarks>
[Trait("Category", "Unit")]
public class V4CrudBulkCreateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    // ── Boluses ─────────────────────────────────────────────────────

    [Fact]
    public async Task Boluses_WritesEveryItemAndReturnsThem()
    {
        var repo = EchoingBolusRepository();

        var result = await Boluses(repo).CreateBulk(
        [
            new CreateBolusRequest { Timestamp = T0, Insulin = 1.0 },
            new CreateBolusRequest { Timestamp = T0.AddMinutes(5), Insulin = 2.5 },
            new CreateBolusRequest { Timestamp = T0.AddMinutes(10), Insulin = 3.0 },
        ]);

        var created = Created(result);
        created.Should().HaveCount(3);
        created.Select(b => b.Insulin).Should().Equal(1.0, 2.5, 3.0);
        created.Select(b => b.Timestamp).Should().Equal(T0.UtcDateTime, T0.AddMinutes(5).UtcDateTime, T0.AddMinutes(10).UtcDateTime);
    }

    [Fact]
    public async Task Boluses_OverTheCap_IsRejectedAndNothingIsWritten()
    {
        var repo = new Mock<IBolusRepository>();

        var result = await Boluses(repo).CreateBulk(
            [.. Enumerable.Repeat(0, V4BulkValidation.MaxItems + 1).Select(_ => new CreateBolusRequest { Timestamp = T0, Insulin = 1.0 })]);

        Rejected(result.Result, $"Bulk operations are limited to {V4BulkValidation.MaxItems} boluses per request");
        NothingWritten(repo);
    }

    [Fact]
    public async Task Boluses_AtTheCap_IsAccepted()
    {
        var repo = EchoingBolusRepository();

        var result = await Boluses(repo).CreateBulk(
            [.. Enumerable.Repeat(0, V4BulkValidation.MaxItems).Select(_ => new CreateBolusRequest { Timestamp = T0, Insulin = 1.0 })]);

        Created(result).Should().HaveCount(V4BulkValidation.MaxItems);
    }

    [Fact]
    public async Task Boluses_OneUnsetTimestamp_RejectsTheWholeBatch()
    {
        var repo = new Mock<IBolusRepository>();

        var result = await Boluses(repo).CreateBulk(
        [
            new CreateBolusRequest { Timestamp = T0, Insulin = 1.0 },
            new CreateBolusRequest { Timestamp = default, Insulin = 2.0 },
            new CreateBolusRequest { Timestamp = T0.AddMinutes(10), Insulin = 3.0 },
        ]);

        Rejected(result.Result, "Timestamp must be set on every bolus");
        NothingWritten(repo);
    }

    [Fact]
    public async Task Boluses_HandTheRequestsSyncKeysToTheRepository_OnEverySend()
    {
        var repo = EchoingBolusRepository();
        var written = new List<List<Bolus>>();
        repo.Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<Bolus>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Bolus> models, WriteOrigin _, CancellationToken _) =>
            {
                var batch = models.ToList();
                written.Add(batch);
                return batch;
            });

        CreateBolusRequest[] Payload() =>
        [
            new() { Timestamp = T0, Insulin = 1.0, DataSource = "trio", SyncIdentifier = "upstream-1" },
            new() { Timestamp = T0.AddMinutes(5), Insulin = 2.0, DataSource = "trio", SyncIdentifier = "upstream-2" },
        ];

        await Boluses(repo).CreateBulk(Payload());
        await Boluses(repo).CreateBulk(Payload());

        written.Should().HaveCount(2);
        written[0].Select(b => (b.DataSource, b.SyncIdentifier)).Should()
            .Equal(written[1].Select(b => (b.DataSource, b.SyncIdentifier)));
        written[1].Select(b => (b.DataSource, b.SyncIdentifier)).Should()
            .Equal(("trio", "upstream-1"), ("trio", "upstream-2"));
    }

    [Fact]
    public async Task Boluses_SyncIdentifierWithoutDataSource_IsRejected()
    {
        var repo = new Mock<IBolusRepository>();

        var result = await Boluses(repo).CreateBulk(
            [new CreateBolusRequest { Timestamp = T0, Insulin = 1.0, SyncIdentifier = "upstream-1" }]);

        Rejected(result.Result, "DataSource is required when SyncIdentifier is supplied");
        NothingWritten(repo);
    }

    // ── Notes ───────────────────────────────────────────────────────

    [Fact]
    public async Task Notes_WriteEveryItemAndReturnThem()
    {
        var repo = EchoingNoteRepository();

        var result = await Notes(repo).CreateBulk(
        [
            new UpsertNoteRequest { Timestamp = T0, Text = "site change" },
            new UpsertNoteRequest { Timestamp = T0.AddMinutes(5), Text = "felt low" },
        ]);

        var created = Created(result);
        created.Should().HaveCount(2);
        created.Select(n => n.Text).Should().Equal("site change", "felt low");
    }

    [Fact]
    public async Task Notes_OverTheCap_IsRejectedAndNothingIsWritten()
    {
        var repo = new Mock<INoteRepository>();

        var result = await Notes(repo).CreateBulk(
            [.. Enumerable.Repeat(0, V4BulkValidation.MaxItems + 1).Select(_ => new UpsertNoteRequest { Timestamp = T0 })]);

        Rejected(result.Result, $"Bulk operations are limited to {V4BulkValidation.MaxItems} notes per request");
        repo.Verify(
            r => r.BulkCreateAsync(It.IsAny<IEnumerable<Note>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Notes_OneUnsetTimestamp_RejectsTheWholeBatch()
    {
        var repo = new Mock<INoteRepository>();

        var result = await Notes(repo).CreateBulk(
        [
            new UpsertNoteRequest { Timestamp = T0, Text = "one" },
            new UpsertNoteRequest { Text = "two" },
        ]);

        Rejected(result.Result, "Timestamp must be set on every note");
        repo.Verify(
            r => r.BulkCreateAsync(It.IsAny<IEnumerable<Note>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Notes_HandTheRequestsSyncKeysToTheRepository_OnEverySend()
    {
        var repo = new Mock<INoteRepository>();
        var written = new List<List<Note>>();
        repo.Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<Note>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Note> models, WriteOrigin _, CancellationToken _) =>
            {
                var batch = models.ToList();
                written.Add(batch);
                return batch;
            });

        UpsertNoteRequest[] Payload() =>
        [
            new() { Timestamp = T0, Text = "one", DataSource = "trio", SyncIdentifier = "note-1" },
            new() { Timestamp = T0.AddMinutes(5), Text = "two", DataSource = "trio", SyncIdentifier = "note-2" },
        ];

        await Notes(repo).CreateBulk(Payload());
        await Notes(repo).CreateBulk(Payload());

        written.Should().HaveCount(2);
        written[1].Select(n => (n.DataSource, n.SyncIdentifier)).Should()
            .Equal(("trio", "note-1"), ("trio", "note-2"));
        written[0].Select(n => (n.DataSource, n.SyncIdentifier)).Should()
            .Equal(written[1].Select(n => (n.DataSource, n.SyncIdentifier)));
    }

    // ── Arrangement ─────────────────────────────────────────────────

    private static TModel[] Created<TModel>(ActionResult<TModel[]> result)
    {
        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status201Created);
        return objectResult.Value.Should().BeAssignableTo<TModel[]>().Subject;
    }

    private static void Rejected(ActionResult? result, string detail)
    {
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);

        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Detail.Should().Be(detail);
    }

    private static void NothingWritten(Mock<IBolusRepository> repo) => repo.Verify(
        r => r.BulkCreateAsync(It.IsAny<IEnumerable<Bolus>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
        Times.Never);

    private static Mock<IBolusRepository> EchoingBolusRepository()
    {
        var repo = new Mock<IBolusRepository>();
        repo.Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<Bolus>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Bolus> models, WriteOrigin _, CancellationToken _) => [.. models]);
        return repo;
    }

    private static Mock<INoteRepository> EchoingNoteRepository()
    {
        var repo = new Mock<INoteRepository>();
        repo.Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<Note>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Note> models, WriteOrigin _, CancellationToken _) => [.. models]);
        return repo;
    }

    private static BolusController Boluses(Mock<IBolusRepository> repo) =>
        WithContext(new BolusController(
            repo.Object,
            Mock.Of<IPatientInsulinRepository>(),
            Mock.Of<IPatientDeviceRepository>(),
            Mock.Of<IPatientDeviceStamper>()));

    private static NoteController Notes(Mock<INoteRepository> repo) =>
        WithContext(new NoteController(repo.Object));

    private static TController WithContext<TController>(TController controller)
        where TController : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }
}
