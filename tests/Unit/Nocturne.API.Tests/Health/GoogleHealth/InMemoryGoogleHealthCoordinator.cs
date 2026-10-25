using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Services.Health.GoogleHealth;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Tests.Health.GoogleHealth;

/// <summary>A coordinator whose sync cursors live in memory, one set per tenant.</summary>
internal static class InMemoryGoogleHealthCoordinator
{
    public static GoogleHealthCoordinator Create(TimeProvider? clock = null, ITenantService? tenants = null)
    {
        var stored = new ConcurrentDictionary<(Guid, string, string), JsonNode>();
        var services = new ServiceCollection();
        services.AddScoped<ITenantAccessor, Accessor>();
        services.AddScoped<IConnectorSyncCursorStore>(provider =>
            new Cursors(provider.GetRequiredService<ITenantAccessor>(), stored));
        services.AddSingleton(tenants ?? Mock.Of<ITenantService>(service =>
            service.GetAllAsync(It.IsAny<CancellationToken>()) == Task.FromResult(new List<TenantDto>())));
        return new(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), clock ?? TimeProvider.System);
    }

    private sealed class Accessor : ITenantAccessor
    {
        public TenantContext? Context { get; private set; }
        public Guid TenantId => Context?.TenantId ?? Guid.Empty;
        public bool IsResolved => Context is not null;
        public void SetTenant(TenantContext context) => Context = context;
    }

    private sealed class Cursors(ITenantAccessor tenant, ConcurrentDictionary<(Guid, string, string), JsonNode> stored)
        : IConnectorSyncCursorStore
    {
        private (Guid, string, string) Key(string connectorName, string resource) =>
            (tenant.TenantId, ConnectorNames.Canonical(connectorName), resource);

        public async Task<ConnectorSyncCursor?> GetAsync(string connectorName, string resource, CancellationToken cancellationToken = default) =>
            (await GetJsonAsync(connectorName, resource, cancellationToken))?.Deserialize<ConnectorSyncCursor>();

        public Task SetAsync(string connectorName, string resource, ConnectorSyncCursor cursor, CancellationToken cancellationToken = default) =>
            SetJsonAsync(connectorName, resource, JsonSerializer.SerializeToNode(cursor)!, cancellationToken);

        public Task<JsonNode?> GetJsonAsync(string connectorName, string resource, CancellationToken cancellationToken = default) =>
            Task.FromResult(stored.GetValueOrDefault(Key(connectorName, resource))?.DeepClone());

        public Task SetJsonAsync(string connectorName, string resource, JsonNode value, CancellationToken cancellationToken = default)
        {
            stored[Key(connectorName, resource)] = value.DeepClone();
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string connectorName, string resource, CancellationToken cancellationToken = default)
        {
            stored.TryRemove(Key(connectorName, resource), out _);
            return Task.CompletedTask;
        }
    }
}
