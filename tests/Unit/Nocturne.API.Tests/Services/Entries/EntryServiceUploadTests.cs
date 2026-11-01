using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Entries;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Entries;

/// <summary>
/// <see cref="IEntryService.UpsertUploadAsync"/>: what a processed v1 upload batch writes, echoes and
/// reports as moving glucose.
/// </summary>
[Trait("Category", "Unit")]
public class EntryServiceUploadTests
{
    private readonly Mock<IEntryStore> _store = new();
    private readonly Mock<IEntryDecomposer> _decomposer = new();
    private readonly Mock<ILogger<EntryService>> _logger = new();
    private readonly EntryService _sut;
    private List<Entry> _written = [];

    public EntryServiceUploadTests()
    {
        _decomposer
            .Setup(x => x.DecomposeBatchAsync(It.IsAny<IReadOnlyList<Entry>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<Entry>, WriteOrigin, CancellationToken>((entries, _, _) => _written = [.. entries])
            .ReturnsAsync(new DecompositionResult());
        StubStoredAt();

        _sut = new EntryService(_store.Object, _decomposer.Object, Mock.Of<IEntryCache>(), _logger.Object);
    }

    [Fact]
    public async Task NothingStored_WritesAndEchoesEveryEntryAndMovesTheGlucoseOfEach()
    {
        var batch = new[]
        {
            new Entry { Id = "new-1", Sgv = 120, Mills = 1000, Device = "Dexcom G7", Type = "sgv" },
            new Entry { Id = "new-2", Sgv = 125, Mills = 2000, Device = "Dexcom G7", Type = "sgv" },
        };

        var upload = await _sut.UpsertUploadAsync(batch, "Loop");

        _written.Should().Equal(batch);
        upload.Response.Should().Equal(batch);
        upload.GlucoseMoved.Should().Equal(batch);
    }

    [Fact]
    public async Task ProbesTheWholeBatchInOnePass_KeyedOnDeviceTypeAndMills()
    {
        IReadOnlyList<EntryDuplicateProbe>? probed = null;
        _store
            .Setup(x => x.CheckDuplicatesAsync(It.IsAny<IReadOnlyList<EntryDuplicateProbe>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<EntryDuplicateProbe>, CancellationToken>((probes, _) => probed = probes)
            .ReturnsAsync((IReadOnlyList<EntryDuplicateProbe> probes, CancellationToken _) =>
                probes.Select(_ => (StoredEntryMatch?)null).ToArray());

        await _sut.UpsertUploadAsync(
            [
                new Entry { Id = "a", Sgv = 120, Mills = 1000, Device = "Dexcom G7", Type = "sgv" },
                new Entry { Id = "b", Mbg = 100, Mills = 2000, Type = "mbg" },
                new Entry { Id = "c", Sgv = 130, Mills = 3000, Type = null },
            ],
            uploader: null);

        probed.Should().Equal(
            new EntryDuplicateProbe("Dexcom G7", "sgv", 1000),
            new EntryDuplicateProbe(null, "mbg", 2000),
            new EntryDuplicateProbe(null, "sgv", 3000));
        _store.Verify(
            x => x.CheckDuplicatesAsync(It.IsAny<IReadOnlyList<EntryDuplicateProbe>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AProbeResultPerEntryIsRequired()
    {
        _store
            .Setup(x => x.CheckDuplicatesAsync(It.IsAny<IReadOnlyList<EntryDuplicateProbe>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var act = () => _sut.UpsertUploadAsync([new Entry { Id = "a", Sgv = 120, Mills = 1000, Type = "sgv" }], null);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AllDuplicates_AreEchoedAndWrittenUnderTheStoredIds()
    {
        var stored1 = new Entry { Id = "stored-1", Sgv = 164, Mills = 1000, Device = "Dexcom G7", Type = "sgv" };
        var stored2 = new Entry { Id = "stored-2", Sgv = 158, Mills = 2000, Device = "Dexcom G7", Type = "sgv" };
        StubStoredAt((1000L, stored1), (2000L, stored2));

        var upload = await _sut.UpsertUploadAsync(
            [
                new Entry { Id = "sent-1", Sgv = 164, Mills = 1000, Device = "Dexcom G7", Type = "sgv" },
                new Entry { Id = "sent-2", Sgv = 158, Mills = 2000, Device = "Dexcom G7", Type = "sgv" },
            ],
            null);

        upload.Response.Select(e => e.Id).Should().Equal("stored-1", "stored-2");
        _written.Select(e => e.Id).Should().Equal("stored-1", "stored-2");
    }

    [Fact]
    public async Task ResendOfAReadingWithNoLegacyId_EchoesTheStoredRowWithoutWriting()
    {
        var stored = new Entry { Id = Guid.CreateVersion7().ToString(), Sgv = 164, Mills = 1000, Device = "Dexcom G7", Type = "sgv" };
        StubMatchesAt((1000L, new StoredEntryMatch(stored, LegacyId: null)));

        var upload = await _sut.UpsertUploadAsync(
            [new Entry { Id = "sent", Sgv = 170, Mills = 1000, Device = "Dexcom G7", Type = "sgv" }],
            null);

        upload.Response.Should().ContainSingle().Which.Should().BeSameAs(stored);
        upload.GlucoseMoved.Should().BeEmpty();
        _written.Should().BeEmpty();
    }

    [Fact]
    public async Task ResendOfAReadingStoredUnderAUuidLegacyId_IsWrittenUnderThatId()
    {
        var legacyId = Guid.NewGuid().ToString().ToUpperInvariant();
        var stored = new Entry { Id = legacyId, Sgv = 164, Mgdl = 164, Mills = 1000, Device = "Trio", Type = "sgv" };
        StubStoredAt((1000L, stored));

        var upload = await _sut.UpsertUploadAsync(
            [new Entry { Id = "sent", Sgv = 170, Mills = 1000, Device = "Trio", Type = "sgv" }],
            null);

        var echoed = upload.Response.Should().ContainSingle().Subject;
        echoed.Sgv.Should().Be(170);
        echoed.Id.Should().Be(legacyId);
        _written.Should().ContainSingle().Which.Id.Should().Be(legacyId);
    }

    [Fact]
    public async Task Resend_KeepsTheStoredFieldsItLeavesOut()
    {
        var stored = new Entry
        {
            Id = "stored-1", Sgv = 164, Mgdl = 164, Mills = 1000, Device = "xDrip-DexcomG6", Type = "sgv",
            Direction = "Flat", Noise = 1, Filtered = 164000, Unfiltered = 166000, Rssi = 100,
        };
        StubStoredAt((1000L, stored));

        await _sut.UpsertUploadAsync(
            [new Entry { Id = "sent", Sgv = 170, Mills = 1000, Type = "sgv", Direction = "FortyFiveUp" }],
            null);

        var written = _written.Should().ContainSingle().Subject;
        written.Sgv.Should().Be(170);
        written.Mgdl.Should().Be(170);
        written.Direction.Should().Be("FortyFiveUp");
        written.Device.Should().Be("xDrip-DexcomG6");
        written.Noise.Should().Be(1);
        written.Filtered.Should().Be(164000);
        written.Unfiltered.Should().Be(166000);
        written.Rssi.Should().Be(100);
    }

    [Theory]
    [InlineData(164, 164, false)]
    [InlineData(170, 170, true)]
    [InlineData(170, 164, true)]
    [InlineData(164, 170, false)]
    public async Task Resend_MovesGlucoseOnlyWhenTheStoredValueChanges(double sgv, double staleMgdl, bool moved)
    {
        var stored = new Entry { Id = "stored-1", Sgv = 164, Mgdl = 164, Mills = 1000, Device = "Dexcom G7", Type = "sgv" };
        StubStoredAt((1000L, stored));

        var upload = await _sut.UpsertUploadAsync(
            [new Entry { Id = "sent", Sgv = sgv, Mgdl = staleMgdl, Mills = 1000, Device = "Dexcom G7", Type = "sgv" }],
            null);

        upload.GlucoseMoved.Should().HaveCount(moved ? 1 : 0);
        upload.GlucoseMoved.Select(e => e.Id).Should().OnlyContain(id => id == "stored-1");
    }

    [Fact]
    public async Task MixedDuplicateAndNew_KeepsOneResponsePerEntryInOrder()
    {
        var storedDuplicate = new Entry { Id = "stored-dup", Sgv = 130, Mills = 2000, Device = "Dexcom G7", Type = "sgv" };
        StubStoredAt((2000L, storedDuplicate));

        var upload = await _sut.UpsertUploadAsync(
            [
                new Entry { Id = "new-1", Sgv = 120, Mills = 1000, Device = "Dexcom G7", Type = "sgv" },
                new Entry { Id = "sent-dup", Sgv = 130, Mills = 2000, Device = "Dexcom G7", Type = "sgv" },
                new Entry { Id = "new-3", Sgv = 140, Mills = 3000, Device = "Dexcom G7", Type = "sgv" },
            ],
            null);

        upload.Response.Select(e => e.Id).Should().Equal("new-1", "stored-dup", "new-3");
        _written.Select(e => e.Id).Should().Equal("new-1", "stored-dup", "new-3");
        upload.GlucoseMoved.Select(e => e.Id).Should().Equal("new-1", "new-3");
    }

    [Fact]
    public async Task LargeAllStoredBatch_ReportsTheUploaderOnce()
    {
        await _sut.UpsertUploadAsync(StoredBacklog(120), "Loop/57 CFNetwork Darwin");

        VerifyInformationLogged("re-sending stored readings", Times.Once());
        VerifyInformationLogged("Loop/57 CFNetwork Darwin", Times.Once());
    }

    [Fact]
    public async Task ReuploadLine_StripsControlCharactersFromTheUserAgent()
    {
        await _sut.UpsertUploadAsync(StoredBacklog(120), "Loop/57\r\nLogRecord.Body: forged");

        VerifyInformationLogged("Loop/57", Times.Once());
        VerifyInformationLogged("\n", Times.Never());
        VerifyInformationLogged("\r", Times.Never());
    }

    [Fact]
    public async Task ReuploadLine_CapsAndFoldsAwkwardUserAgents()
    {
        await _sut.UpsertUploadAsync(StoredBacklog(120), "Loop/57\u202e\u2028" + new string('x', 400));

        _logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) =>
                    v.ToString()!.Contains("Loop/57")
                    && !v.ToString()!.Contains('\u202e')
                    && !v.ToString()!.Contains('\u2028')
                    && !v.ToString()!.Contains(new string('x', 250))),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once());
    }

    [Fact]
    public async Task SmallAllStoredBatch_IsNotReported()
    {
        await _sut.UpsertUploadAsync(StoredBacklog(20), "Loop");

        VerifyInformationLogged("re-sending stored readings", Times.Never());
    }

    [Fact]
    public async Task LargeBatchMostlyNew_IsNotReported()
    {
        var batch = Batch(120);
        StubStoredAt(batch.Take(60).Select(e => (e.Mills, new Entry { Id = $"stored-{e.Mills}", Mills = e.Mills, Type = "sgv" })).ToArray());

        await _sut.UpsertUploadAsync(batch, "Loop");

        VerifyInformationLogged("re-sending stored readings", Times.Never());
    }

    private static Entry[] Batch(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new Entry { Id = $"sent-{i}", Sgv = 100 + i, Mills = 1000 + i, Device = "Dexcom G7", Type = "sgv" })
            .ToArray();

    private Entry[] StoredBacklog(int count)
    {
        var batch = Batch(count);
        StubStoredAt(batch.Select(e => (e.Mills, new Entry { Id = $"stored-{e.Mills}", Mills = e.Mills, Type = "sgv" })).ToArray());
        return batch;
    }

    private void VerifyInformationLogged(string fragment, Times times) =>
        _logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains(fragment)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            times);

    /// <summary>The store holds the given entries under their own ids as legacy ids. See <see cref="StubMatchesAt"/>.</summary>
    private void StubStoredAt(params (long Mills, Entry Stored)[] stored) =>
        StubMatchesAt(stored.Select(x => (x.Mills, new StoredEntryMatch(x.Stored, x.Stored.Id))).ToArray());

    /// <summary>
    /// The store holds the given matches, keyed by the probed <see cref="Entry.Mills"/>, and nothing
    /// else, answering one result per probe in the order probed.
    /// </summary>
    private void StubMatchesAt(params (long Mills, StoredEntryMatch Match)[] stored)
    {
        var byMills = stored.ToDictionary(x => x.Mills, x => x.Match);
        _store
            .Setup(x => x.CheckDuplicatesAsync(It.IsAny<IReadOnlyList<EntryDuplicateProbe>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<EntryDuplicateProbe> probes, CancellationToken _) =>
                probes.Select(probe => byMills.GetValueOrDefault(probe.Mills)).ToArray());
    }
}
