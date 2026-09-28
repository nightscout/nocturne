using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V3;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V3;

/// <summary>
/// A v3 entry uploaded without an identifier is stored under a fresh ObjectId, which the create
/// response returns and every later lookup resolves verbatim; AAPS keeps the returned identifier as
/// the record's Nightscout id.
/// </summary>
[Trait("Category", "Unit")]
public class EntryCreateIdentityTests
{
    private readonly List<Entry> _written = [];
    private readonly EntriesController _controller;

    public EntryCreateIdentityTests()
    {
        var processing = new Mock<IDocumentProcessingService>();
        processing.Setup(p => p.ProcessEntry(It.IsAny<Entry>())).Returns<Entry>(e => e);
        var entryService = new Mock<IEntryService>();
        entryService
            .Setup(s => s.CreateEntriesAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Entry> entries, WriteOrigin _, CancellationToken _) =>
            {
                var list = entries.ToList();
                _written.AddRange(list);
                return new BulkWrite<Entry>(list, 0);
            });
        _controller = new EntriesController(
            processing.Object,
            entryService.Object,
            Mock.Of<ICanonicalAlertEvaluator>(),
            NullLogger<EntriesController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    [Fact]
    public async Task A_created_entry_without_an_identifier_gets_an_object_id_the_response_returns()
    {
        var result = await _controller.CreateEntry(new Entry { Type = "sgv", Sgv = 120 });

        var stored = _written.Should().ContainSingle().Subject;
        MongoObjectId.IsObjectId(stored.Id).Should().BeTrue();
        var created = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.RouteValues!["id"].Should().Be(stored.Id);
    }

    [Fact]
    public async Task A_created_entry_keeps_its_uploaded_id()
    {
        await _controller.CreateEntry(new Entry { Id = "5f1a2b3c4d5e6f7a8b9c0d1e", Type = "sgv", Sgv = 120 });

        _written.Should().ContainSingle().Which.Id.Should().Be("5f1a2b3c4d5e6f7a8b9c0d1e");
    }

    [Fact]
    public async Task A_bulk_create_gives_each_entry_without_an_identifier_its_own_object_id()
    {
        await _controller.CreateEntries(
        [
            new Entry { Type = "sgv", Sgv = 120 },
            new Entry { Type = "sgv", Sgv = 125, Id = "" },
            new Entry { Type = "sgv", Sgv = 130, Id = "dexcom_7f3c2a91" },
        ]);

        _written.Should().HaveCount(3);
        _written.Take(2).Should().OnlyContain(e => MongoObjectId.IsObjectId(e.Id));
        _written[0].Id.Should().NotBe(_written[1].Id);
        _written[2].Id.Should().Be("dexcom_7f3c2a91");
    }
}
