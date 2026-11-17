using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Tests.Shared.Infrastructure;

/// <summary>
/// Serves an <see cref="INocturneDbContextFactory"/> from a test's own
/// <see cref="IDbContextFactory{TContext}"/> (a mock, or one over SQLite or the in-memory provider).
/// </summary>
/// <remarks>
/// With a <c>scopeTenant</c>, <see cref="CreateAsync"/> reads as a scope resolved to that tenant;
/// without one, it hands back whatever tenant the test's factory pinned.
/// </remarks>
public sealed class RawContextFactoryAdapter(IDbContextFactory<NocturneDbContext> contexts, Guid? scopeTenant = null)
    : SingleReaderDbContextFactory
{
    /// <inheritdoc />
    public override async ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default)
    {
        var context = await contexts.CreateDbContextAsync(ct);
        if (scopeTenant is { } tenant)
            context.TenantId = tenant;
        return context;
    }
}

/// <summary>Adapts a test's raw context factory to <see cref="INocturneDbContextFactory"/>.</summary>
public static class RawContextFactoryAdapterExtensions
{
    /// <summary>Wraps <paramref name="contexts"/> in a <see cref="RawContextFactoryAdapter"/>.</summary>
    public static INocturneDbContextFactory AsNocturneFactory(this IDbContextFactory<NocturneDbContext> contexts) =>
        new RawContextFactoryAdapter(contexts);

    /// <summary>
    /// Wraps <paramref name="contexts"/> as the factory of a scope resolved to <paramref name="scopeTenant"/>.
    /// </summary>
    public static INocturneDbContextFactory AsNocturneFactory(
        this IDbContextFactory<NocturneDbContext> contexts, Guid scopeTenant) =>
        new RawContextFactoryAdapter(contexts, scopeTenant);
}
