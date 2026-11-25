using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.Alerts.Corpus;
using Nocturne.API.Services.V4;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.API.Tests.Services.Alerts;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// A V1 delete of the newest site change, through the decomposer and its announcement, evaluates the
/// site-age rule that was waiting on it against the site change reads now show.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TreatmentDecomposerSiteAgeEvaluationTests(AlertFactEvaluationDatabase database)
    : IClassFixture<AlertFactEvaluationDatabase>, IDisposable
{
    private readonly AlertFactEvaluationStack _stack = new(database);

    public void Dispose() => _stack.Dispose();

    [NativeFact]
    public async Task A_v1_delete_of_the_newest_site_change_evaluates_the_waiting_site_age_rule()
    {
        var (tenant, rule) = await _stack.SeedTenantAsync(
            "delete", AlertConditionType.SiteAge, AlertFactEvaluationStack.SiteAgeOver72Hours);
        var older = _stack.WholeSecondsAgo(TimeSpan.FromDays(5));
        var newest = older.AddDays(3);
        await _stack.SeedSiteChangeAsync(tenant.TenantId, older, "nightscout");
        await _stack.SeedSiteChangeAsync(tenant.TenantId, newest, "nightscout", legacyId: "treat-site");

        await _stack.WriteReadingAsync(tenant, 120, TimeSpan.Zero);
        (await _stack.NextPassAsync(tenant.TenantId)).SiteChangeAt.Should().Be(newest);

        await using (var scope = TenantJobScope.Open(_stack.Provider, tenant, "test:v1-delete"))
        await using (var db = _stack.Context(tenant.TenantId))
            (await Decomposer(scope.ServiceProvider, db).DeleteByLegacyIdAsync("treat-site", WriteOrigin.Live))
                .Should().Be(1);
        _stack.PassTheDebounce();

        var moved = await _stack.NextPassAsync(tenant.TenantId);
        moved.Rules.Select(r => r.Id).Should().Equal(rule);
        moved.SiteChangeAt.Should().Be(older);
    }

    private static TreatmentDecomposer Decomposer(IServiceProvider scope, NocturneDbContext db)
    {
        var contexts = scope.GetRequiredService<INocturneDbContextFactory>();
        return TreatmentDecomposerDoubles.Build(
            contexts,
            deviceEvent: scope.GetRequiredService<IDeviceEventRepository>(),
            profileActivations: ProfileActivationTestDeps.Repository(db),
            profileActivationWriter: ProfileActivationTestDeps.Writer(db),
            audit: scope.GetRequiredService<IAuditContext>(),
            changes: scope.GetRequiredService<IRecordChangePublisher>());
    }
}
