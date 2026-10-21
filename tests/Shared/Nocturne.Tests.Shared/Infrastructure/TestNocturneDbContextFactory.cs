using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Tests.Shared.Infrastructure;

/// <summary>
/// A test-only <see cref="INocturneDbContextFactory"/> that creates fresh
/// <see cref="NocturneDbContext"/> instances per call, sharing the same underlying
/// data store (EF InMemory database name or SQLite connection) as the seed context.
/// Each instance returned from <see cref="CreateAsync"/> suppresses disposal so that
/// repository <c>await using</c> scopes do not close the shared connection or drop
/// the in-memory database. The test fixture owns the context lifetime.
/// </summary>
public sealed class TestNocturneDbContextFactory : INocturneDbContextFactory
{
    // EF Core 10 stores DbContextOptions on IDbContextServices, which is accessible via
    // the private DbContext.ContextServices property.
    private static readonly PropertyInfo ContextServicesProp =
        typeof(DbContext).GetProperty(
            "ContextServices",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMemberException(
            "DbContext", "ContextServices");

    private static readonly PropertyInfo ContextOptionsProp =
        ContextServicesProp.PropertyType.GetProperty("ContextOptions")
        ?? throw new MissingMemberException(
            ContextServicesProp.PropertyType.Name, "ContextOptions");

    private readonly DbContextOptions<NocturneDbContext> _options;
    private readonly Guid _tenantId;

    /// <summary>
    /// Initialises the factory from an existing context by extracting its options
    /// and tenant identifier.
    /// </summary>
    public TestNocturneDbContextFactory(NocturneDbContext context)
    {
        var services = ContextServicesProp.GetValue(context)
            ?? throw new InvalidOperationException(
                "DbContext.ContextServices returned null. " +
                "Ensure the context has been fully initialised before wrapping.");

        _options = (DbContextOptions<NocturneDbContext>)ContextOptionsProp.GetValue(services)!;
        _tenantId = context.TenantId;
    }

    /// <inheritdoc />
    public ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default) => Create(_tenantId);

    /// <inheritdoc />
    public ValueTask<NocturneDbContext> CreateForTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        Create(tenantId);

    /// <inheritdoc />
    public ValueTask<NocturneDbContext> CreateForSubjectAsync(Guid subjectId, CancellationToken ct = default)
    {
        var ctx = new NonDisposingNocturneDbContext(_options) { TenantId = Guid.Empty, SubjectId = subjectId };
        return ValueTask.FromResult<NocturneDbContext>(ctx);
    }

    /// <inheritdoc />
    public ValueTask<NocturneDbContext> CreatePlatformAsync(CancellationToken ct = default) => Create(Guid.Empty);

    /// <inheritdoc />
    public ValueTask<NocturneDbContext> CreateSystemPinnedAsync(Guid tenantId, CancellationToken ct = default) =>
        Create(tenantId);

    private ValueTask<NocturneDbContext> Create(Guid tenantId) =>
        ValueTask.FromResult<NocturneDbContext>(new NonDisposingNocturneDbContext(_options) { TenantId = tenantId });

    /// <summary>
    /// A <see cref="NocturneDbContext"/> subclass that overrides both the synchronous and
    /// asynchronous <c>Dispose</c> methods to be no-ops. This allows test fixtures to
    /// control the context lifetime independently of repository <c>await using</c> scopes.
    /// </summary>
    private sealed class NonDisposingNocturneDbContext(DbContextOptions<NocturneDbContext> options)
        : NocturneDbContext(options)
    {
        public override void Dispose() { /* suppress — test fixture owns the lifetime */ }
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
