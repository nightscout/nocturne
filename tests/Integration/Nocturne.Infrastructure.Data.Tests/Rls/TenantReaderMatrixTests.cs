using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// What each kind of reader sees of one tenant, through <see cref="INocturneDbContextFactory"/>, the
/// interceptor and the live PostgreSQL policies: a categorised table, a catalogue table, a hidden
/// table clamped by recency and the span table clamped by overlap (<see cref="TenantReaderData"/>).
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class TenantReaderMatrixTests : IAsyncLifetime
{
    private static readonly string StepsAndFood =
        ShareDataCategories.ComputeVisibleCategoriesCsv([Scope.StepCountRead, Scope.FoodRead]);

    private readonly RlsCompletenessFixture _fx;
    private ServiceProvider _provider = null!;
    private Guid _tenant;

    public TenantReaderMatrixTests(RlsCompletenessFixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        _provider = TenantReaderData.BuildProvider(_fx.AppConnectionString);
        _tenant = await TenantReaderData.SeedTenantAsync(_fx, _provider);
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Owner_SeesEverything()
    {
        var counts = await ReadInScopeAsync(_ => { }, f => f.CreateAsync());

        counts.Should().Be(ReaderCounts.Owner);
    }

    [Fact]
    public async Task ClampedMember_SeesTheLast24Hours_AndSpansOverlappingThem()
    {
        var counts = await ReadInScopeAsync(
            s => s.GetRequiredService<ICategoryReadContext>().ClampMemberHistory(),
            f => f.CreateAsync());

        counts.Should().Be(ReaderCounts.Clamped);
    }

    [Fact]
    public async Task ClampedMember_ThroughATenantPin_KeepsTheClamp()
    {
        var counts = await ReadInScopeAsync(
            s => s.GetRequiredService<ICategoryReadContext>().ClampMemberHistory(),
            f => f.CreateForTenantAsync(_tenant));

        counts.Should().Be(ReaderCounts.Clamped);
    }

    [Fact]
    public async Task ShareWithCategoriesAndFullHistory_SeesItsCategories_AndNoHiddenTable()
    {
        var counts = await ReadInScopeAsync(
            s => TenantReaderData.MarkShare(s, StepsAndFood, fullHistory: true),
            f => f.CreateAsync());

        counts.Should().Be(new ReaderCounts(Steps: 2, Foods: 1, Notes: 0, Spans: 0));
    }

    [Fact]
    public async Task ShareWithCategoriesClamped_SeesTheLast24HoursOfItsCategories_AndAllTheCatalogue()
    {
        var counts = await ReadInScopeAsync(
            s => TenantReaderData.MarkShare(s, StepsAndFood, fullHistory: false),
            f => f.CreateAsync());

        counts.Should().Be(new ReaderCounts(Steps: 1, Foods: 1, Notes: 0, Spans: 0));
    }

    [Fact]
    public async Task ShareWithNoCsv_SeesNothing()
    {
        var counts = await ReadInScopeAsync(
            s => TenantReaderData.MarkShare(s, csv: null, fullHistory: true),
            f => f.CreateAsync());

        counts.Should().Be(ReaderCounts.Nothing);
    }

    [Fact]
    public async Task ATenantPin_InAShare_StaysNarrowedAsTheShare()
    {
        var counts = await ReadInScopeAsync(
            s =>
            {
                TenantReaderData.Resolve(s, Guid.NewGuid());
                TenantReaderData.MarkShare(s, StepsAndFood, fullHistory: false);
            },
            f => f.CreateForTenantAsync(_tenant));

        counts.Should().Be(new ReaderCounts(Steps: 1, Foods: 1, Notes: 0, Spans: 0));
    }

    [Fact]
    public async Task JobScope_SeesItsTenant()
    {
        await using var scope = _provider.CreateAsyncScope();
        TenantReaderData.Resolve(scope.ServiceProvider, _tenant);
        await using var scoped = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync();

        (await TenantReaderData.CountAsync(scoped, _tenant)).Should().Be(ReaderCounts.Owner);
    }

    [Fact]
    public async Task RootTenantPin_SeesItsTenant()
    {
        await using var context = await _provider.GetRequiredService<INocturneDbContextFactory>()
            .CreateForTenantAsync(_tenant);

        (await TenantReaderData.CountAsync(context, _tenant)).Should().Be(ReaderCounts.Owner);
    }

    [Fact]
    public async Task SubjectPin_ReachesNoTenantRows_AndCarriesTheSubject()
    {
        var subject = Guid.NewGuid();
        await using var scope = _provider.CreateAsyncScope();
        TenantReaderData.Resolve(scope.ServiceProvider, _tenant);
        await using var context = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>()
            .CreateForSubjectAsync(subject);

        (await TenantReaderData.CountAsync(context, _tenant)).Should().Be(ReaderCounts.Nothing);
        (await TenantReaderData.SettingAsync(context, "app.current_subject_id")).Should().Be(subject.ToString());
    }

    [Fact]
    public async Task Platform_ReachesNoTenantRows_EvenInAResolvedScope()
    {
        var counts = await ReadInScopeAsync(_ => { }, f => f.CreatePlatformAsync());

        counts.Should().Be(ReaderCounts.Nothing);
    }

    [Fact]
    public async Task SystemPin_InAClampedShare_ReadsAsNoReader()
    {
        var counts = await ReadInScopeAsync(
            s => TenantReaderData.MarkShare(s, csv: null, fullHistory: false),
            f => f.CreateSystemPinnedAsync(_tenant));

        counts.Should().Be(ReaderCounts.Owner);
    }

    [Fact]
    public async Task SystemPin_InAClampedShare_WritesTheGucsOfNoReader()
    {
        await using var scope = _provider.CreateAsyncScope();
        TenantReaderData.Resolve(scope.ServiceProvider, Guid.NewGuid());
        TenantReaderData.MarkShare(scope.ServiceProvider, StepsAndFood, fullHistory: false);
        scope.ServiceProvider.GetRequiredService<ICategoryReadContext>().ClampMemberHistory();

        await using var context = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>()
            .CreateSystemPinnedAsync(_tenant);

        (await TenantReaderData.SettingAsync(context, "app.current_tenant_id")).Should().Be(_tenant.ToString());
        (await TenantReaderData.SettingAsync(context, "app.is_share")).Should().Be("false");
        (await TenantReaderData.SettingAsync(context, "app.visible_categories")).Should().BeEmpty();
        (await TenantReaderData.SettingAsync(context, "app.share_full_history")).Should().Be("false");
        (await TenantReaderData.SettingAsync(context, "app.history_clamped")).Should().Be("false");
    }

    [Fact]
    public async Task RootResolvedFactory_IgnoresEveryScope()
    {
        await using var share = _provider.CreateAsyncScope();
        TenantReaderData.Resolve(share.ServiceProvider, _tenant);
        TenantReaderData.MarkShare(share.ServiceProvider, csv: null, fullHistory: false);

        await using var context = await _provider.GetRequiredService<INocturneDbContextFactory>().CreateAsync();

        (await TenantReaderData.CountAsync(context, _tenant)).Should().Be(ReaderCounts.Nothing,
            "a root-resolved factory has no tenant, whatever any scope resolved");
    }

    /// <summary>
    /// Reads <see cref="_tenant"/> through a context the scope's factory creates, the scope resolved
    /// to it first and then shaped by <paramref name="arrange"/>.
    /// </summary>
    private async Task<ReaderCounts> ReadInScopeAsync(
        Action<IServiceProvider> arrange,
        Func<INocturneDbContextFactory, ValueTask<NocturneDbContext>> create)
    {
        await using var scope = _provider.CreateAsyncScope();
        TenantReaderData.Resolve(scope.ServiceProvider, _tenant);
        arrange(scope.ServiceProvider);
        await using var context = await create(scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>());
        return await TenantReaderData.CountAsync(context, _tenant);
    }
}
