using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// A history-clamped member records rows dated before the 24-hour window, which PostgreSQL would
/// otherwise refuse (42501) by checking an <c>INSERT … RETURNING</c> or <c>UPDATE … WHERE</c> against
/// the SELECT policies, and still cannot read them back or reach a row the clamp hides.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class HistoryClampedWriteTests(RlsCompletenessFixture fx) : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private Guid _tenant;

    public async Task InitializeAsync()
    {
        _provider = TenantReaderData.BuildProvider(fx.AppConnectionString);
        _tenant = await TenantReaderData.SeedTenantAsync(fx, _provider);
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task ClampedMember_RecordsALabResultFromLastQuarter_ButDoesNotReadItBack()
    {
        await using var scope = ClampedScope();
        var repository = new LabHbA1cResultRepository(Factory(scope));

        var created = await repository.CreateAsync(new LabHbA1cResult
        {
            MeasuredAt = DateTime.UtcNow.AddDays(-90), ValuePercent = 6.8,
        });

        (await repository.GetAllAsync()).Should().NotContain(r => r.Id == created.Id);
        (await OwnerCountAsync("lab_hba1c_results", created.Id)).Should().Be(1);
    }

    [Fact]
    public async Task ClampedMember_RecordsBackdatedRowsOfEveryKindInOneSave()
    {
        await using var scope = ClampedScope();
        await using var db = await Factory(scope).CreateAsync();
        var old = DateTime.UtcNow.AddDays(-3);
        var weight = new BodyWeightEntity
        {
            Id = Guid.CreateVersion7(), Mills = new DateTimeOffset(old).ToUnixTimeMilliseconds(), WeightKg = 70,
        };
        var meal = new CarbIntakeEntity { Id = Guid.CreateVersion7(), Timestamp = old, Carbs = 45 };
        var food = new TreatmentFoodEntity { Id = Guid.CreateVersion7(), CarbIntakeId = meal.Id, Portions = 1, Carbs = 45 };
        db.BodyWeights.Add(weight);
        db.CarbIntakes.Add(meal);
        db.TreatmentFoods.Add(food);

        await db.SaveChangesAsync();

        (await db.BodyWeights.AnyAsync(w => w.Id == weight.Id)).Should().BeFalse("the clamp still hides it from its writer");
        (await TenantReaderData.SettingAsync(db, "app.history_clamped")).Should().Be("true");
        (await OwnerCountAsync("body_weights", weight.Id)).Should().Be(1);
        (await OwnerCountAsync("treatment_foods", food.Id)).Should().Be(1);
    }

    [Fact]
    public async Task ClampedMember_RecordsLastWeeksSleepSession()
    {
        await using var scope = ClampedScope();
        var repository = new SleepSessionRepository(Factory(scope));
        var start = DateTime.UtcNow.AddDays(-7);

        var created = await repository.UpsertSessionAsync(new SleepSession
        {
            StartTime = start, EndTime = start.AddHours(8), Type = SleepSessionType.Overnight,
            DetectionMethod = SleepDetectionMethod.Auto, Source = SleepSource.Fitbit,
            DurationMs = 28_800_000, TotalSleepMs = 25_200_000,
            Stages = [new SleepStageInterval { StartTime = start, EndTime = start.AddHours(1), Stage = SleepStageType.Light }],
            BiometricSamples = [new SleepBiometricSample { Timestamp = start, HeartRate = 58 }],
        });

        (await OwnerCountAsync("sleep_sessions", Guid.Parse(created.Id!))).Should().Be(1);
    }

    [Fact]
    public async Task ClampedMember_CompletesARunningTrackerAtATimeBeforeTheWindow()
    {
        var (_, running) = await SeedRunningTrackerAsync();
        await using var scope = ClampedScope();
        var repository = new TrackerRepository(Factory(scope));
        (await repository.GetInstanceByIdAsync(running)).Should().NotBeNull("a running tracker is inside the window");

        var completed = await repository.CompleteInstanceAsync(
            running, CompletionReason.Completed, completedAt: DateTime.UtcNow.AddDays(-2));

        completed.Should().NotBeNull();
        completed!.CompletedAt.Should().BeCloseTo(DateTime.UtcNow.AddDays(-2), TimeSpan.FromMinutes(1));
        (await repository.GetInstanceByIdAsync(running)).Should().BeNull("a run that ended two days ago is outside the window");
    }

    [Fact]
    public async Task ClampedMember_ReplacesARunningTrackerWithABackdatedRun_InOneTransaction()
    {
        var (definition, running) = await SeedRunningTrackerAsync();
        await using var scope = ClampedScope();
        var repository = new TrackerRepository(Factory(scope));
        var startedAt = DateTime.UtcNow.AddDays(-2);

        var started = await repository.ExecuteUnderDefinitionLockAsync(
            definition,
            async (tx, token) =>
            {
                (await tx.CompleteInstanceAsync(running, CompletionReason.Completed, completedAt: startedAt, cancellationToken: token))
                    .Should().NotBeNull();
                var next = await tx.StartInstanceAsync(definition, "member", startedAt: startedAt, cancellationToken: token);
                (await tx.GetInstanceByIdAsync(running, token)).Should().BeNull("the clamp is back once the write returns");
                return next;
            });

        (await repository.GetActiveInstancesForDefinitionAsync(definition)).Should().ContainSingle()
            .Which.Id.Should().Be(started.Id);
    }

    [Fact]
    public async Task ClampedMember_CannotUpdateAHiddenRowByAttachingIt()
    {
        var hidden = Guid.CreateVersion7();
        await using (var owner = await _provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(_tenant))
        {
            owner.BodyWeights.Add(new BodyWeightEntity
            {
                Id = hidden, Mills = DateTimeOffset.UtcNow.AddDays(-3).ToUnixTimeMilliseconds(), WeightKg = 70,
            });
            await owner.SaveChangesAsync();
        }

        await using var scope = ClampedScope();
        await using var db = await Factory(scope).CreateAsync();
        var stub = new BodyWeightEntity { Id = hidden, Mills = 1, WeightKg = 99, TenantId = _tenant };
        db.BodyWeights.Attach(stub);
        db.Entry(stub).Property(w => w.WeightKg).IsModified = true;

        var save = () => db.SaveChangesAsync();

        await save.Should().ThrowAsync<DbUpdateConcurrencyException>("an update keeps the clamp, so a hidden row is not found");
    }

    private async Task<(Guid Definition, Guid Running)> SeedRunningTrackerAsync()
    {
        await using var owner = await _provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(_tenant);
        var definition = new TrackerDefinitionEntity { Id = Guid.CreateVersion7(), CreatedBy = "member", Name = "site" };
        var running = new TrackerInstanceEntity
        {
            Id = Guid.CreateVersion7(), CreatedBy = "member", DefinitionId = definition.Id, StartedAt = DateTime.UtcNow.AddDays(-5),
        };
        owner.TrackerDefinitions.Add(definition);
        owner.TrackerInstances.Add(running);
        await owner.SaveChangesAsync();
        return (definition.Id, running.Id);
    }

    private AsyncServiceScope ClampedScope()
    {
        var scope = _provider.CreateAsyncScope();
        TenantReaderData.Resolve(scope.ServiceProvider, _tenant);
        scope.ServiceProvider.GetRequiredService<ICategoryReadContext>().ClampMemberHistory();
        return scope;
    }

    private static INocturneDbContextFactory Factory(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>();

    private async Task<long> OwnerCountAsync(string table, Guid id)
    {
        await using var conn = await fx.OpenAppConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT set_config('app.current_tenant_id', @tid, false); SELECT count(*) FROM {table} WHERE id = @id";
        cmd.Parameters.AddWithValue("tid", _tenant.ToString());
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.NextResultAsync();
        await reader.ReadAsync();
        return reader.GetInt64(0);
    }
}
