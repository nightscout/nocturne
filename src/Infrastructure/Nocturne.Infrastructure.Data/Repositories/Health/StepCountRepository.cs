using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories.Health;

public sealed class StepCountRepository(INocturneDbContextFactory contexts, TimeProvider time)
    : ObservedHealthSeriesRepository<StepCount, StepCountEntity>(contexts, time)
{
    protected override DbSet<StepCountEntity> Set(NocturneDbContext db) => db.StepCounts;

    protected override StepCount ToDomain(StepCountEntity entity) => StepCountMapper.ToDomainModel(entity);

    protected override StepCountEntity ToEntity(StepCount model) => StepCountMapper.ToEntity(model);

    protected override void Apply(StepCountEntity entity, StepCount model) => StepCountMapper.UpdateEntity(entity, model);
}
