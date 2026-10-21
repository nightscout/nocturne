using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Effects;
using Nocturne.API.Services.Entries;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Tests.Services.Entries;

[Trait("Category", "Unit")]
public class SignalREntryEventSinkTests
{
    private readonly Mock<ITenantCache> _cache = new();
    private readonly SignalREntryEventSink _sut;

    public SignalREntryEventSinkTests()
    {
        var sideEffects = new WriteSideEffectsService(
            _cache.Object,
            Mock.Of<ISignalRBroadcastService>(),
            Mock.Of<IDecompositionPipeline>(),
            [],
            NullLogger<WriteSideEffectsService>.Instance);
        _sut = new SignalREntryEventSink(sideEffects, NullLogger<SignalREntryEventSink>.Instance);
    }

    private static Entry Reading(string type, DateTime utc) => new()
    {
        Type = type,
        Mills = new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
    };

    private List<string> ClearedFamilies() =>
        _cache.Invocations
            .Where(i => i.Method.Name == nameof(ITenantCache.RemoveFamilyAsync))
            .Select(i => (string)i.Arguments[0])
            .ToList();

    [Fact]
    public async Task OnWrittenAsync_BackfillIntoACompletedYear_ClearsOnlyThatYear()
    {
        await _sut.OnWrittenAsync([Reading("sgv", new DateTime(2023, 6, 15, 12, 0, 0, DateTimeKind.Utc))]);

        ClearedFamilies().Should().BeEquivalentTo(["ehba1c:2023"]);
    }

    [Fact]
    public async Task OnWrittenAsync_ReadingNearYearEnd_ClearsTheYearItsWindowReaches()
    {
        await _sut.OnWrittenAsync([Reading("mbg", new DateTime(2023, 12, 20, 12, 0, 0, DateTimeKind.Utc))]);

        ClearedFamilies().Should().BeEquivalentTo(["ehba1c:2023", "ehba1c:2024"]);
    }

    [Fact]
    public async Task OnWrittenAsync_ReadingsInTwoYears_ClearsBothAndNoOthers()
    {
        await _sut.OnWrittenAsync(
        [
            Reading("sgv", new DateTime(2021, 6, 15, 12, 0, 0, DateTimeKind.Utc)),
            Reading("sgv", new DateTime(2023, 6, 15, 12, 0, 0, DateTimeKind.Utc)),
        ]);

        ClearedFamilies().Should().BeEquivalentTo(["ehba1c:2021", "ehba1c:2023"]);
    }

    [Fact]
    public async Task OnWrittenAsync_Calibration_ClearsNothing()
    {
        await _sut.OnWrittenAsync([Reading("cal", new DateTime(2023, 6, 15, 12, 0, 0, DateTimeKind.Utc))]);

        ClearedFamilies().Should().BeEmpty();
    }

    [Fact]
    public async Task OnBulkDeletedAsync_ClearsEveryEHbA1cYear()
    {
        await _sut.OnBulkDeletedAsync(5);

        ClearedFamilies().Should().Contain(TenantCacheFamily.EHbA1c);
    }
}
