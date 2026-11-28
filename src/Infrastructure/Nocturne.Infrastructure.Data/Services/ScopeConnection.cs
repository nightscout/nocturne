using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Infrastructure.Data.Interceptors;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// One open connection that the contexts <see cref="INocturneDbContextFactory.CreateAsync"/> gives in this scope run on
/// while it is held, so a run of reads takes one pool checkout and one session write
/// (<see cref="TenantConnectionInterceptor"/>) in place of one of each per query.
/// </summary>
/// <remarks>
/// A context runs on it only when the session it would write is the one the connection holds
/// (<see cref="ConnectionSession"/>): a context whose tenant, subject, share or clamp differs, and every context from
/// another entry point, opens its own. The contexts share one connection, so they must run one at a time. A held
/// connection is out of the pool until the hold is disposed, so hold it only for reads that run straight through.
/// <para>
/// The session is compared once, when a context is created, and never rewritten while the hold lasts. So hold only in a
/// scope whose reader is captured as values (<see cref="TenantJobScope.OpenFor(IServiceProvider, TenantReader, string)"/>),
/// never in a request scope, whose clamp and share categories resolve after its first contexts exist. Nothing that runs
/// under a hold may write session state: <see cref="Extensions.RlsPinningExtensions.PinTenantAsync"/> refuses a context
/// on a held connection. <c>ScopeConnectionHoldAllowlistTests</c> pins the callers.
/// </para>
/// </remarks>
public sealed class ScopeConnection(IServiceProvider scope) : IDisposable
{
    private NocturneDbContext? _opener;
    private ConnectionSession _session;

    /// <summary>
    /// Opens a connection that reads as the scope's reader and holds it until the returned handle is disposed. On a
    /// provider with no connection (the in-memory one) it holds nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">The scope's connection is already held.</exception>
    public async ValueTask<IAsyncDisposable> HoldConnectionAsync(CancellationToken ct)
    {
        if (_opener is not null)
            throw new InvalidOperationException("The scope's connection is already held.");

        var opener = await scope.GetRequiredService<INocturneDbContextFactory>().CreateAsync(ct);
        if (!opener.Database.IsRelational())
        {
            await opener.DisposeAsync();
            return new Hold(this, null);
        }

        try
        {
            await opener.Database.OpenConnectionAsync(ct);
        }
        catch
        {
            await opener.DisposeAsync();
            throw;
        }

        _session = ConnectionSession.Of(opener);
        _opener = opener;
        return new Hold(this, opener);
    }

    internal void Attach(NocturneDbContext context)
    {
        if (_opener is not { } opener || ConnectionSession.Of(context) != _session)
            return;

        context.Database.SetDbConnection(opener.Database.GetDbConnection(), contextOwnsConnection: false);
        context.OwnsConnection = false;
    }

    /// <summary>Releases a hold its owner never disposed, when the scope ends.</summary>
    public void Dispose()
    {
        var opener = _opener;
        _opener = null;
        opener?.Dispose();
    }

    private async ValueTask ReleaseAsync(NocturneDbContext? opener)
    {
        if (opener is null || !ReferenceEquals(_opener, opener))
            return;
        _opener = null;
        await opener.DisposeAsync();
    }

    private sealed class Hold(ScopeConnection owner, NocturneDbContext? opener) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => owner.ReleaseAsync(opener);
    }
}
