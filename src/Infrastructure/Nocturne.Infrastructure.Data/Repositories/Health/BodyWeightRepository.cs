using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories.Health;

/// <summary>
/// Body weight keys on epoch milliseconds rather than an observation <see cref="DateTime"/>, so it
/// has none of the reads of <see cref="ObservedHealthSeriesRepository{TModel,TEntity}"/>.
/// </summary>
public sealed class BodyWeightRepository
    : HealthSeriesRepository<BodyWeight, BodyWeightEntity>, IBodyWeightRepository
{
    public BodyWeightRepository(INocturneDbContextFactory contexts, TimeProvider time)
        : base(contexts, time)
    {
    }

    internal BodyWeightRepository(DbUnitOfWork unit, TimeProvider time)
        : base(unit, time)
    {
    }

    protected override DbSet<BodyWeightEntity> Set(NocturneDbContext db) => db.BodyWeights;

    protected override BodyWeight ToDomain(BodyWeightEntity entity) => BodyWeightMapper.ToDomainModel(entity);

    protected override BodyWeightEntity ToEntity(BodyWeight model) => BodyWeightMapper.ToEntity(model);

    protected override void Apply(BodyWeightEntity entity, BodyWeight model) => BodyWeightMapper.UpdateEntity(entity, model);

    protected override BodyWeight AnswerHiddenRow(BodyWeight stored, BodyWeight sent)
    {
        stored.CreatedAt = sent.CreatedAt;
        return stored;
    }

    protected override IOrderedQueryable<BodyWeightEntity> NewestFirst(IQueryable<BodyWeightEntity> query) =>
        query.OrderByDescending(b => b.Mills);

    protected override IQueryable<BodyWeightEntity> ObservedIn(
        IQueryable<BodyWeightEntity> query, DateTime from, DateTime to)
    {
        var firstMills = from.ToMills();
        var lastMills = to.ToMills();
        return query.Where(b => b.Mills >= firstMills && b.Mills < lastMills);
    }

    /// <inheritdoc />
    public async Task<bool> AnyMeasuredInAsync(
        DateTime from, DateTime to, BodyWeightMeasure measure, CancellationToken ct = default)
    {
        await using var lease = await OpenAsync(ct);
        var query = ObservedIn(lease.Context.BodyWeights.AsNoTracking(), from, to);
        query = measure switch
        {
            BodyWeightMeasure.BodyFat => query.Where(b => b.BodyFatPercent != null),
            BodyWeightMeasure.LeanMass => query.Where(b => b.LeanMassKg != null),
            _ => query,
        };
        return await query.AnyAsync(ct);
    }
}
