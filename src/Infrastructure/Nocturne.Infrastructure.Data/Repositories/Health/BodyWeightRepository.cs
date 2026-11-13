using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories.Health;

/// <summary>
/// Body weight keys on epoch milliseconds rather than an observation <see cref="DateTime"/>, so it
/// has none of the reads of <see cref="ObservedHealthSeriesRepository{TModel,TEntity}"/>.
/// </summary>
public sealed class BodyWeightRepository(INocturneDbContextFactory contexts, TimeProvider time)
    : HealthSeriesRepository<BodyWeight, BodyWeightEntity>(contexts, time)
{
    protected override DbSet<BodyWeightEntity> Set(NocturneDbContext db) => db.BodyWeights;

    protected override BodyWeight ToDomain(BodyWeightEntity entity) => BodyWeightMapper.ToDomainModel(entity);

    protected override BodyWeightEntity ToEntity(BodyWeight model) => BodyWeightMapper.ToEntity(model);

    protected override void Apply(BodyWeightEntity entity, BodyWeight model) => BodyWeightMapper.UpdateEntity(entity, model);

    protected override IOrderedQueryable<BodyWeightEntity> NewestFirst(IQueryable<BodyWeightEntity> query) =>
        query.OrderByDescending(b => b.Mills);
}
