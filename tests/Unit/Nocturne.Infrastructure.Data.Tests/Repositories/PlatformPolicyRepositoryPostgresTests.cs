using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// <see cref="PlatformPolicyRepository"/> on PostgreSQL: each switch round-trips, and an update that
/// names one switch leaves the other as it was saved.
/// </summary>
[Trait("Category", "Integration")]
public class PlatformPolicyRepositoryPostgresTests(PlatformPolicyRepositoryPostgresTests.Database database)
    : IClassFixture<PlatformPolicyRepositoryPostgresTests.Database>
{
    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync() =>
            AppConnectionString = (await SharedPostgres.CreateMigratedDatabaseAsync("platform_policy_repo")).AppConnectionString;

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private NocturneDbContext Context() =>
        TestContexts.Context(database.AppConnectionString);

    private PlatformPolicyRepository Policies() => new(new SuppliedContextFactory(Context), TimeProvider.System);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Both_switches_round_trip(bool allowOwnerSlugChange, bool restrictTeamCreation)
    {
        var saved = await Policies().UpdateAsync(new PlatformPolicyUpdate(allowOwnerSlugChange, restrictTeamCreation), default);

        var expected = new PlatformPolicy(allowOwnerSlugChange, restrictTeamCreation);
        saved.Should().Be(expected);
        (await Policies().GetAsync(default)).Should().Be(expected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Saving_the_slug_switch_alone_keeps_the_team_switch(bool restrictTeamCreation)
    {
        await Policies().UpdateAsync(new PlatformPolicyUpdate(false, restrictTeamCreation), default);

        await Policies().UpdateAsync(new PlatformPolicyUpdate(AllowOwnerSlugChange: true), default);

        (await Policies().GetAsync(default)).Should().Be(new PlatformPolicy(true, restrictTeamCreation));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Saving_the_team_switch_alone_keeps_the_slug_switch(bool allowOwnerSlugChange)
    {
        await Policies().UpdateAsync(new PlatformPolicyUpdate(allowOwnerSlugChange, false), default);

        await Policies().UpdateAsync(new PlatformPolicyUpdate(RestrictTeamCreationToAdmins: true), default);

        (await Policies().GetAsync(default)).Should().Be(new PlatformPolicy(allowOwnerSlugChange, true));
    }

    [Fact]
    public async Task Two_updates_of_different_switches_at_once_both_land()
    {
        await Policies().UpdateAsync(new PlatformPolicyUpdate(false, false), default);

        await Task.WhenAll(
            Policies().UpdateAsync(new PlatformPolicyUpdate(AllowOwnerSlugChange: true), default),
            Policies().UpdateAsync(new PlatformPolicyUpdate(RestrictTeamCreationToAdmins: true), default));

        (await Policies().GetAsync(default)).Should().Be(new PlatformPolicy(true, true));
    }
}
