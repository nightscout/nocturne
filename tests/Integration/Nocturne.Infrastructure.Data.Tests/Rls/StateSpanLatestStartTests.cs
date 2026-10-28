using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class StateSpanLatestStartTests : IAsyncLifetime
{
    private static readonly DateTime Origin = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly string[] ActivityCategories = ["Exercise", "Illness", "Travel", "Motion"];

    private readonly RlsCompletenessFixture _fx;
    private ServiceProvider _provider = null!;
    private Guid _tenant;

    public StateSpanLatestStartTests(RlsCompletenessFixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        _provider = TenantReaderData.BuildProvider(_fx.AppConnectionString);
        _tenant = await TenantReaderData.SeedTenantAsync(_fx, _provider);
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Activity_IsTheGreatestStartAcrossTheActivityCategoriesOfTheSource()
    {
        await SeedAsync(
            Span("Exercise", "a", 1),
            Span("Illness", "a", 5),
            Span("Travel", "a", 3),
            Span("Motion", "a", 4),
            Span("PumpMode", "a", 9),
            Span("Exercise", "b", 8));

        (await LatestAsync(r => r.GetLatestActivityTimestampAsync("a"))).Should().Be(Origin.AddDays(5));
    }

    [Fact]
    public async Task NonActivity_IsTheGreatestStartAcrossEveryOtherCategoryOfTheSource()
    {
        var others = Enum.GetNames<StateSpanCategory>()
            .Except(ActivityCategories)
            .ToList();
        await SeedAsync(
            [
                .. others.Select((category, i) => Span(category, "a", i + 1)),
                Span("Exercise", "a", 50),
                Span(others[0], "b", 60),
            ]);

        (await LatestAsync(r => r.GetLatestNonActivityTimestampAsync("a"))).Should().Be(Origin.AddDays(others.Count));
    }

    [Fact]
    public async Task EachCategoryCanHoldTheGreatestStart()
    {
        foreach (var category in Enum.GetNames<StateSpanCategory>())
        {
            var source = $"only-{category}";
            await SeedAsync(Span(category, source, 2), Span(category, source, 1));
            var activity = ActivityCategories.Contains(category);
            var hit = await LatestAsync(r => activity
                ? r.GetLatestActivityTimestampAsync(source)
                : r.GetLatestNonActivityTimestampAsync(source));
            var miss = await LatestAsync(r => activity
                ? r.GetLatestNonActivityTimestampAsync(source)
                : r.GetLatestActivityTimestampAsync(source));

            hit.Should().Be(Origin.AddDays(2), category);
            miss.Should().BeNull(category);
        }
    }

    [Theory]
    [InlineData("TempBasal")]
    [InlineData("BasalDelivery")]
    public async Task NeitherWatermarkCountsACategoryOutsideTheEnum(string removed)
    {
        await SeedAsync(Span(removed, "a", 9), Span("PumpMode", "a", 4), Span("Exercise", "a", 3));

        (await LatestAsync(r => r.GetLatestNonActivityTimestampAsync("a"))).Should().Be(Origin.AddDays(4));
        (await LatestAsync(r => r.GetLatestActivityTimestampAsync("a"))).Should().Be(Origin.AddDays(3));
    }

    [Fact]
    public async Task DeletedSpansAreIgnored()
    {
        var deleted = Span("Exercise", "a", 7);
        deleted.DeletedAt = Origin;
        var deletedOther = Span("PumpMode", "a", 7);
        deletedOther.DeletedAt = Origin;
        await SeedAsync(Span("Exercise", "a", 2), deleted, Span("PumpMode", "a", 3), deletedOther);

        (await LatestAsync(r => r.GetLatestActivityTimestampAsync("a"))).Should().Be(Origin.AddDays(2));
        (await LatestAsync(r => r.GetLatestNonActivityTimestampAsync("a"))).Should().Be(Origin.AddDays(3));
    }

    [Fact]
    public async Task NothingMatching_IsNull()
    {
        await SeedAsync(Span("Exercise", "b", 2), Span("PumpMode", "b", 2));

        (await LatestAsync(r => r.GetLatestActivityTimestampAsync("a"))).Should().BeNull();
        (await LatestAsync(r => r.GetLatestNonActivityTimestampAsync("a"))).Should().BeNull();
    }

    private static StateSpanEntity Span(string category, string source, int day) => new()
    {
        Id = Guid.CreateVersion7(),
        Category = category,
        Source = source,
        StartTimestamp = Origin.AddDays(day),
    };

    private async Task SeedAsync(params StateSpanEntity[] spans)
    {
        await using var seed = await _provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(_tenant);
        seed.StateSpans.AddRange(spans);
        await seed.SaveChangesAsync();
    }

    private async Task<DateTime?> LatestAsync(Func<StateSpanRepository, Task<DateTime?>> read)
    {
        await using var scope = _provider.CreateAsyncScope();
        TenantReaderData.Resolve(scope.ServiceProvider, _tenant);
        return await read(new StateSpanRepository(
            scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>(),
            new Mock<IDeduplicationService>().Object,
            new Mock<IAuditContext>().Object,
            NullLogger<StateSpanRepository>.Instance,
            TimeProvider.System));
    }
}
