using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V3;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V3;

/// <summary>
/// A v3 treatment uploaded without an identifier is stored under an ObjectId, which the create
/// response returns and every later lookup resolves verbatim; AAPS keeps the returned identifier as
/// the record's Nightscout id and edits and deletes by it.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentCreateIdentityTests
{
    private const long At = 1_760_000_000_000;

    private readonly List<Treatment> _written = [];
    private readonly Mock<ITreatmentService> _treatmentService = new();
    private readonly TreatmentsController _controller;

    public TreatmentCreateIdentityTests()
    {
        var processing = new Mock<IDocumentProcessingService>();
        processing.Setup(p => p.ProcessTreatment(It.IsAny<Treatment>())).Returns<Treatment>(t => t);
        _treatmentService
            .Setup(s => s.CreateTreatmentsAsync(It.IsAny<IEnumerable<Treatment>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Treatment> treatments, CancellationToken _) =>
            {
                var list = treatments.ToList();
                _written.AddRange(list);
                return new BulkWrite<Treatment>(list, 0);
            });
        _controller = new TreatmentsController(
            Mock.Of<ITreatmentStore>(),
            processing.Object,
            _treatmentService.Object,
            NullLogger<TreatmentsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private static Treatment Bolus(double insulin = 0.6) =>
        new() { EventType = "Correction Bolus", Insulin = insulin, Mills = At, EnteredBy = "AAPS-e2e" };

    [Fact]
    public async Task A_created_treatment_without_an_identifier_gets_an_object_id_the_response_returns()
    {
        var result = await _controller.CreateTreatment(Bolus());

        var stored = _written.Should().ContainSingle().Subject;
        MongoObjectId.IsObjectId(stored.Id).Should().BeTrue();
        var created = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.RouteValues!["id"].Should().Be(stored.Id);
        created.Value.Should().BeOfType<Treatment>().Which.Identifier.Should().Be(stored.Id);
    }

    [Fact]
    public async Task The_identifier_is_derived_from_the_event_so_a_re_upload_lands_on_the_same_record()
    {
        await _controller.CreateTreatment(Bolus());
        await _controller.CreateTreatment(Bolus());
        await _controller.CreateTreatment(Bolus(insulin: 0.7));

        _written.Should().HaveCount(3);
        _written[0].Id.Should().Be(MongoObjectId.Coerce(TreatmentDecomposer.ComputeSyntheticId(Bolus())));
        _written[1].Id.Should().Be(_written[0].Id);
        _written[2].Id.Should().NotBe(_written[0].Id);
    }

    [Fact]
    public async Task A_sync_identifier_names_the_event()
    {
        var syncIdentifier = "0b6a3f1e-2c4d-4e5f-8a9b-1c2d3e4f5a6b";
        var treatment = Bolus();
        treatment.SyncIdentifier = syncIdentifier;

        await _controller.CreateTreatment(treatment);

        _written.Should().ContainSingle().Which.Id.Should().Be(MongoObjectId.Coerce(syncIdentifier));
    }

    [Fact]
    public async Task A_treatment_with_no_event_identity_gets_a_fresh_object_id()
    {
        await _controller.CreateTreatments(
        [
            new Treatment { Notes = "no event type", Mills = At },
            new Treatment { Notes = "no event type", Mills = At },
        ]);

        _written.Should().HaveCount(2);
        _written.Should().OnlyContain(t => MongoObjectId.IsObjectId(t.Id));
        _written[0].Id.Should().NotBe(_written[1].Id);
    }

    [Fact]
    public async Task A_created_treatment_keeps_its_uploaded_id()
    {
        var treatment = Bolus();
        treatment.Id = "5f1a2b3c4d5e6f7a8b9c0d1e";

        await _controller.CreateTreatment(treatment);

        _written.Should().ContainSingle().Which.Id.Should().Be("5f1a2b3c4d5e6f7a8b9c0d1e");
    }

    [Fact]
    public async Task A_bulk_create_gives_each_treatment_without_an_identifier_an_object_id()
    {
        await _controller.CreateTreatments(
        [
            Bolus(0.5),
            new Treatment { EventType = "Carb Correction", Carbs = 12, Mills = At, Id = "" },
            new Treatment { EventType = "Note", Notes = "kept", Mills = At, Id = "client-7f3c2a91" },
        ]);

        _written.Should().HaveCount(3);
        _written.Take(2).Should().OnlyContain(t => MongoObjectId.IsObjectId(t.Id));
        _written[0].Id.Should().NotBe(_written[1].Id);
        _written[2].Id.Should().Be("client-7f3c2a91");
    }

    [Fact]
    public async Task A_duplicate_of_a_stored_treatment_answers_with_the_identifier_it_is_served_under()
    {
        var storedId = MongoObjectId.NewObjectId();
        _treatmentService
            .Setup(s => s.GetTreatmentByIdAsync(storedId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Treatment { Id = storedId, EventType = "Correction Bolus", Mills = At });
        var treatment = Bolus();
        treatment.Id = storedId;

        var result = await _controller.CreateTreatment(treatment);

        _written.Should().BeEmpty();
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeEquivalentTo(new
        {
            status = 200,
            identifier = storedId,
            isDeduplication = true,
            deduplicatedIdentifier = storedId,
        });
    }
}
