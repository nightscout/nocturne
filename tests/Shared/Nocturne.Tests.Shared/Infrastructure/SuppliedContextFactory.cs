using Nocturne.Infrastructure.Data;

namespace Nocturne.Tests.Shared.Infrastructure;

/// <summary>
/// A <see cref="SingleReaderDbContextFactory"/> whose contexts come from <paramref name="create"/>, called on
/// every request for a context. Each pin then sets the tenant or subject on what it returned, so a
/// <paramref name="create"/> that returns one fixed instance has that instance re-pinned on every request.
/// </summary>
public sealed class SuppliedContextFactory(Func<NocturneDbContext> create) : SingleReaderDbContextFactory
{
    /// <inheritdoc />
    public override ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default) =>
        ValueTask.FromResult(create());
}
