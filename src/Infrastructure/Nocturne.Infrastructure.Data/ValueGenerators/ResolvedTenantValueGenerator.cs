using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.ValueGeneration;

namespace Nocturne.Infrastructure.Data.ValueGenerators;

/// <summary>
/// The tenant id of a row added with none, on a table whose primary key contains it. Without its
/// own generator EF fills a key column that is also a foreign key from the principal's generator,
/// so the row would name a new, nonexistent tenant. An unresolved context yields
/// <see cref="Guid.Empty"/>, which the save refuses.
/// </summary>
public sealed class ResolvedTenantValueGenerator : ValueGenerator<Guid>
{
    public override bool GeneratesTemporaryValues => false;

    public override bool GeneratesStableValues => true;

    public override Guid Next(EntityEntry entry) => ((NocturneDbContext)entry.Context).TenantId;
}
