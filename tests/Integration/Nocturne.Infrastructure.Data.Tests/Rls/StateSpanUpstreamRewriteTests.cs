using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// A source's edit of an override written onto its stored span, in a tenant's scope under FORCE ROW LEVEL
/// SECURITY: the rewrite and the succession it settles commit, and the span takes the source's fingerprint.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class StateSpanUpstreamRewriteTests : IAsyncLifetime
{
    private const string Source = "connector";

    private static readonly DateTime Origin = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly RlsCompletenessFixture _fx;
    private ServiceProvider _provider = null!;
    private Guid _tenant;

    public StateSpanUpstreamRewriteTests(RlsCompletenessFixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        _provider = TenantReaderData.BuildProvider(_fx.AppConnectionString);
        _tenant = await TenantReaderData.SeedTenantAsync(_fx, _provider);
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task A_span_whose_start_moves_and_end_shortens_settles_its_neighbours()
    {
        var before = await SeedChainAsync();

        await WithRepositoryAsync(r => r.RewriteFromUpstreamAsync(Source, [Span("b", 3, endHour: 4)]));

        var rows = await RowsAsync();
        rows["a"].EndTimestamp.Should().Be(Origin.AddHours(3));
        rows["a"].SupersededById.Should().Be(rows["b"].Id);
        rows["b"].StartTimestamp.Should().Be(Origin.AddHours(3));
        rows["b"].EndTimestamp.Should().Be(Origin.AddHours(4));
        rows["b"].SupersededById.Should().BeNull();
        (rows["c"].EndTimestamp, rows["c"].SupersededById, rows["c"].UpdatedAt)
            .Should().Be((before["c"].EndTimestamp, before["c"].SupersededById, before["c"].UpdatedAt));
    }

    [Fact]
    public async Task The_span_takes_the_fingerprint_and_a_second_identical_rewrite_writes_nothing()
    {
        await SeedChainAsync();
        var fingerprints = new Dictionary<(string? Source, string LegacyId), string?> { [(Source, "b")] = "edited" };

        await RewriteUnderAsync(fingerprints);
        var first = await RowsAsync();
        await RewriteUnderAsync(fingerprints);

        var second = await RowsAsync();
        second["b"].UpstreamFingerprint.Should().Be("edited");
        second.Values.Select(r => (r.LegacyId, r.UpdatedAt))
            .Should().BeEquivalentTo(first.Values.Select(r => (r.LegacyId, r.UpdatedAt)));
    }

    /// <summary>
    /// D [6,8) ended by A, a span of another preset, and A [8,10) ended by B. The upload moves A to 5 with
    /// no end, so D runs on to B, and A now ends where D starts.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_span_whose_start_moves_before_the_span_it_ended_releases_it_by_either_writer(bool republish)
    {
        foreach (var (id, hour, state) in new[] { ("d", 6, "Custom"), ("a", 8, "Eating"), ("b", 10, "Custom") })
            await WithRepositoryAsync(r => r.UpsertStateSpanAsync(Span(id, hour, state: state)));
        var before = await RowsAsync();
        var moved = Span("a", 5, state: "Eating");

        await WithRepositoryAsync(async r => republish
            ? (object)await r.RewriteFromUpstreamAsync(Source, [moved])
            : await r.BulkUpsertAsync([moved]));

        var rows = await RowsAsync();
        (rows["d"].EndTimestamp, rows["d"].SupersededById).Should().Be((Origin.AddHours(10), rows["b"].Id));
        (rows["a"].StartTimestamp, rows["a"].EndTimestamp, rows["a"].SupersededById)
            .Should().Be((Origin.AddHours(5), Origin.AddHours(6), rows["d"].Id));
        (rows["b"].EndTimestamp, rows["b"].SupersededById, rows["b"].UpdatedAt)
            .Should().Be((before["b"].EndTimestamp, before["b"].SupersededById, before["b"].UpdatedAt));
    }

    /// <summary>The batch arrives latest first, so the rows are written in another order than their ends.</summary>
    [Fact]
    public async Task One_settle_ends_each_span_at_its_own_successor()
    {
        await WithRepositoryAsync(r => r.BulkUpsertAsync([Span("z", 5), Span("y", 3), Span("x", 1)]));

        var rows = await RowsAsync();
        (rows["x"].EndTimestamp, rows["x"].SupersededById).Should().Be((Origin.AddHours(3), rows["y"].Id));
        (rows["y"].EndTimestamp, rows["y"].SupersededById).Should().Be((Origin.AddHours(5), rows["z"].Id));
    }

    private async Task RewriteUnderAsync(IReadOnlyDictionary<(string? Source, string LegacyId), string?> fingerprints)
    {
        using var scope = UpstreamFingerprintScope.Open(fingerprints);
        await WithRepositoryAsync(r => r.RewriteFromUpstreamAsync(Source, [Span("b", 3, endHour: 4)]));
    }

    /// <summary>A ended by B at 2:00, and B ended by C at 5:00.</summary>
    private async Task<Dictionary<string, StateSpanEntity>> SeedChainAsync()
    {
        foreach (var (id, hour) in new[] { ("a", 1), ("b", 2), ("c", 5) })
            await WithRepositoryAsync(r => r.UpsertStateSpanAsync(Span(id, hour)));

        var rows = await RowsAsync();
        rows["a"].SupersededById.Should().Be(rows["b"].Id);
        rows["b"].SupersededById.Should().Be(rows["c"].Id);
        return rows;
    }

    private static StateSpan Span(string originalId, int startHour, int? endHour = null, string state = "Custom") => new()
    {
        Category = StateSpanCategory.Override,
        State = state,
        StartTimestamp = Origin.AddHours(startHour),
        EndTimestamp = endHour is { } end ? Origin.AddHours(end) : null,
        Source = Source,
        OriginalId = originalId,
    };

    private async Task<Dictionary<string, StateSpanEntity>> RowsAsync()
    {
        await using var read = await _provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(_tenant);
        return await read.StateSpans.AsNoTracking().Where(s => s.Source == Source).ToDictionaryAsync(s => s.LegacyId!);
    }

    private async Task<T> WithRepositoryAsync<T>(Func<StateSpanRepository, Task<T>> write)
    {
        await using var scope = _provider.CreateAsyncScope();
        TenantReaderData.Resolve(scope.ServiceProvider, _tenant);
        return await write(new StateSpanRepository(
            scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>(),
            new Mock<IDuplicateLinker>().Object,
            new Mock<IAuditContext>().Object,
            NullLogger<StateSpanRepository>.Instance));
    }
}
