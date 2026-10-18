using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data;
using Testcontainers.PostgreSql;

namespace Nocturne.Infrastructure.Data.Performance.Tests.Infrastructure;

public class PostgresFixture : IAsyncDisposable
{
    private readonly PostgreSqlContainer _container;
    private string? _connectionString;
    private DbContextOptions<NocturneDbContext>? _options;

    public PostgresFixture()
    {
        _container = new PostgreSqlBuilder("postgres:17.6")
            .WithDatabase("nocturne_perf")
            .WithUsername("test")
            .WithPassword("test")
            .Build();
    }

    public bool IsInitialized => _options is not null;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();

        _options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(_connectionString)
            .Options;

        // Keep the fixture schema coupled to the current EF mappings. Migrations enable RLS
        // policies that require application roles, while EnsureCreated builds the model schema
        // without those production-only policies.
        await using var context = CreateProvisioningContext();
        await context.Database.EnsureCreatedAsync();
    }

    public NocturneDbContext CreateContext(Guid tenantId)
    {
        if (_options is null)
            throw new InvalidOperationException("Call InitializeAsync before CreateContext");
        var context = new NocturneDbContext(_options) { TenantId = tenantId };
        return context;
    }

    private NocturneDbContext CreateProvisioningContext()
    {
        if (_options is null)
            throw new InvalidOperationException("Call InitializeAsync before CreateContext");
        return new NocturneDbContext(_options);
    }

    public async Task<Guid> CreateTenantAsync()
    {
        var tenantId = Guid.CreateVersion7();
        await using var context = CreateProvisioningContext();
        context.Tenants.Add(new TenantEntity
        {
            Id = tenantId,
            Slug = $"perf-{tenantId:N}",
            DisplayName = "Performance benchmark",
        });
        await context.SaveChangesAsync();
        return tenantId;
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
    }

}
