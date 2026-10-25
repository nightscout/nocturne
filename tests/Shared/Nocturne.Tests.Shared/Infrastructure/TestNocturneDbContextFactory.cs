using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Tests.Shared.Infrastructure;

/// <summary>
/// A test-only <see cref="INocturneDbContextFactory"/> that creates fresh
/// <see cref="NocturneDbContext"/> instances per call, sharing the same underlying
/// data store (EF InMemory database name or SQLite connection), tenant and read context
/// as the seed context, read when each context is created.
/// Each instance returned from <see cref="CreateAsync"/> suppresses disposal so that
/// repository <c>await using</c> scopes do not close the shared connection or drop
/// the in-memory database. The test fixture owns the context lifetime.
/// As an <see cref="IDbContextFactory{TContext}"/> it serves a repository, which pins its own
/// tenant, over the same store.
/// </summary>
public sealed class TestNocturneDbContextFactory : INocturneDbContextFactory, IDbContextFactory<NocturneDbContext>
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
    private readonly NocturneDbContext _source;

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
        _source = context;
    }

    /// <inheritdoc />
    public ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default) => Create(_source.TenantId);

    /// <inheritdoc />
    NocturneDbContext IDbContextFactory<NocturneDbContext>.CreateDbContext() => Create(_source.TenantId).Result;

    /// <inheritdoc />
    public ValueTask<NocturneDbContext> CreateForTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        Create(tenantId);

    /// <inheritdoc />
    public ValueTask<NocturneDbContext> CreateForSubjectAsync(Guid subjectId, CancellationToken ct = default)
    {
        var ctx = new NonDisposingNocturneDbContext(_options)
        {
            ReadContext = _source.ReadContext, TenantId = Guid.Empty, SubjectId = subjectId,
        };
        return ValueTask.FromResult(Prepared(ctx));
    }

    /// <inheritdoc />
    public ValueTask<NocturneDbContext> CreatePlatformAsync(CancellationToken ct = default) => Create(Guid.Empty);

    /// <inheritdoc />
    public ValueTask<NocturneDbContext> CreateSystemPinnedAsync(Guid tenantId, CancellationToken ct = default) =>
        Create(tenantId);

    /// <summary>
    /// Runs on every context the factory hands out, e.g. to swap in a set that fails when read.
    /// </summary>
    public Action<NocturneDbContext>? Prepare { get; init; }

    private ValueTask<NocturneDbContext> Create(Guid tenantId) =>
        ValueTask.FromResult(Prepared(new NonDisposingNocturneDbContext(_options)
        {
            ReadContext = _source.ReadContext, TenantId = tenantId,
        }));

    /// <summary>
    /// A test that disposes the seed context to make the service's reads fail gets a disposed context,
    /// which fails on first use as the service's own context would have.
    /// </summary>
    private NocturneDbContext Prepared(NocturneDbContext context)
    {
        if ((bool)DisposedField.GetValue(_source)!)
        {
            var disposed = new NocturneDbContext(_options) { TenantId = context.TenantId };
            disposed.Dispose();
            return disposed;
        }

        Prepare?.Invoke(context);
        return context;
    }

    private static readonly FieldInfo DisposedField =
        typeof(DbContext).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException("DbContext", "_disposed");

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

/// <summary>Wraps a test's context as the factory a per-operation service takes.</summary>
public static class TestNocturneDbContextFactoryExtensions
{
    /// <summary>
    /// A factory handing out fresh contexts over <paramref name="context"/>'s store and tenant, so a
    /// service under test reads and writes as it would per operation, not through the test's context.
    /// </summary>
    public static INocturneDbContextFactory AsFactory(this NocturneDbContext context) =>
        new TestNocturneDbContextFactory(context);
}
