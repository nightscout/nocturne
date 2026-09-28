using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Runs <see cref="SubjectService.TryRemovePasskeyCredentialAsync"/> and
/// <see cref="SubjectService.TryRemoveOidcIdentityAsync"/> on PostgreSQL with retry on failure
/// enabled, as the runtime context is configured. The retrying strategy rejects a
/// user-initiated transaction that is not wrapped in it, which the InMemory provider cannot show.
/// </summary>
[Trait("Category", "Integration")]
public class SubjectServiceFactorRemovalPostgresTests(SubjectServiceFactorRemovalPostgresTests.Database database)
    : IClassFixture<SubjectServiceFactorRemovalPostgresTests.Database>
{
    public sealed class Database : IAsyncLifetime
    {
        private readonly SharedTestContainerFixture _container = new();

        public string ConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            await _container.InitializeAsync();

            var name = $"subject_factor_removal_{Guid.NewGuid():N}";
            await using (var create = new NpgsqlCommand($"CREATE DATABASE {name}", _container.Database))
                await create.ExecuteNonQueryAsync();

            ConnectionString = new NpgsqlConnectionStringBuilder(_container.PostgreSqlConnectionString)
            {
                Database = name,
            }.ConnectionString;

            await using var db = CreateContext();
            await db.Database.EnsureCreatedAsync();
        }

        public Task DisposeAsync() => _container.DisposeAsync();

        public NocturneDbContext CreateContext() =>
            new(new DbContextOptionsBuilder<NocturneDbContext>()
                .UseNpgsql(ConnectionString, npgsql => npgsql.EnableRetryOnFailure(
                    maxRetryCount: 5,
                    maxRetryDelay: TimeSpan.FromSeconds(1),
                    errorCodesToAdd: null))
                .Options);
    }

    private static SubjectService CreateService(NocturneDbContext db) =>
        new(db, Mock.Of<IAuthAuditService>(), Mock.Of<IRecoveryCodeService>(), NullLogger<SubjectService>.Instance);

    private async Task<Guid> SeedSubjectAsync()
    {
        await using var db = database.CreateContext();
        var subjectId = Guid.CreateVersion7();
        db.Subjects.Add(new SubjectEntity
        {
            Id = subjectId,
            Name = $"subject-{subjectId:N}",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return subjectId;
    }

    private async Task<Guid> AddPasskeyAsync(Guid subjectId)
    {
        await using var db = database.CreateContext();
        var passkeyId = Guid.CreateVersion7();
        db.PasskeyCredentials.Add(new PasskeyCredentialEntity
        {
            Id = passkeyId,
            SubjectId = subjectId,
            CredentialId = passkeyId.ToByteArray(),
            PublicKey = [4, 5, 6],
            SignCount = 0,
            Label = "pk",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return passkeyId;
    }

    private async Task<Guid> AddIdentityAsync(Guid subjectId, bool providerEnabled = true)
    {
        await using var db = database.CreateContext();
        var providerId = Guid.CreateVersion7();
        var issuer = $"https://{providerId:N}.issuer.example";
        db.OidcProviders.Add(new OidcProviderEntity
        {
            Id = providerId,
            Name = $"provider-{providerId:N}",
            IssuerUrl = issuer,
            ClientId = "nocturne",
            IsEnabled = providerEnabled,
        });
        var identityId = Guid.CreateVersion7();
        db.SubjectOidcIdentities.Add(new SubjectOidcIdentityEntity
        {
            Id = identityId,
            SubjectId = subjectId,
            ProviderId = providerId,
            OidcSubjectId = $"ext-{identityId:N}",
            Issuer = issuer,
            LinkedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return identityId;
    }

    private async Task<int> CountFactorsAsync(Guid subjectId)
    {
        await using var db = database.CreateContext();
        return await db.PasskeyCredentials.CountAsync(p => p.SubjectId == subjectId)
            + await db.SubjectOidcIdentities.CountAsync(i => i.SubjectId == subjectId);
    }

    [Fact]
    public async Task RemovingAPasskey_WhenAnIdentityRemains_RemovesIt()
    {
        var subjectId = await SeedSubjectAsync();
        var passkeyId = await AddPasskeyAsync(subjectId);
        await AddIdentityAsync(subjectId);
        await using var db = database.CreateContext();

        var result = await CreateService(db).TryRemovePasskeyCredentialAsync(subjectId, passkeyId);

        result.Should().Be(FactorRemovalResult.Removed);
        (await CountFactorsAsync(subjectId)).Should().Be(1);
    }

    [Fact]
    public async Task RemovingAnIdentity_WhenAPasskeyRemains_RemovesIt()
    {
        var subjectId = await SeedSubjectAsync();
        await AddPasskeyAsync(subjectId);
        var identityId = await AddIdentityAsync(subjectId);
        await using var db = database.CreateContext();

        var result = await CreateService(db).TryRemoveOidcIdentityAsync(subjectId, identityId);

        result.Should().Be(FactorRemovalResult.Removed);
        (await CountFactorsAsync(subjectId)).Should().Be(1);
    }

    [Fact]
    public async Task RemovingTheOnlyPasskey_IsRefused()
    {
        var subjectId = await SeedSubjectAsync();
        var passkeyId = await AddPasskeyAsync(subjectId);
        await using var db = database.CreateContext();

        var result = await CreateService(db).TryRemovePasskeyCredentialAsync(subjectId, passkeyId);

        result.Should().Be(FactorRemovalResult.LastPrimaryFactor);
        (await CountFactorsAsync(subjectId)).Should().Be(1);
    }

    [Fact]
    public async Task RemovingTheOnlyIdentity_IsRefused()
    {
        var subjectId = await SeedSubjectAsync();
        var identityId = await AddIdentityAsync(subjectId);
        await using var db = database.CreateContext();

        var result = await CreateService(db).TryRemoveOidcIdentityAsync(subjectId, identityId);

        result.Should().Be(FactorRemovalResult.LastPrimaryFactor);
        (await CountFactorsAsync(subjectId)).Should().Be(1);
    }

    [Fact]
    public async Task RemovingAPasskey_WhenTheOnlyIdentityIsOnADisabledProvider_IsRefused()
    {
        var subjectId = await SeedSubjectAsync();
        var passkeyId = await AddPasskeyAsync(subjectId);
        await AddIdentityAsync(subjectId, providerEnabled: false);
        await using var db = database.CreateContext();

        var result = await CreateService(db).TryRemovePasskeyCredentialAsync(subjectId, passkeyId);

        result.Should().Be(FactorRemovalResult.LastPrimaryFactor);
        (await CountFactorsAsync(subjectId)).Should().Be(2);
    }

    [Fact]
    public async Task RemovingAnIdentity_WhenTheOtherIsOnADisabledProvider_IsRefused()
    {
        var subjectId = await SeedSubjectAsync();
        var identityId = await AddIdentityAsync(subjectId);
        await AddIdentityAsync(subjectId, providerEnabled: false);
        await using var db = database.CreateContext();

        var result = await CreateService(db).TryRemoveOidcIdentityAsync(subjectId, identityId);

        result.Should().Be(FactorRemovalResult.LastPrimaryFactor);
        (await CountFactorsAsync(subjectId)).Should().Be(2);
    }

    [Fact]
    public async Task RemovingAnotherSubjectsFactors_IsNotFound()
    {
        var owner = await SeedSubjectAsync();
        var passkeyId = await AddPasskeyAsync(owner);
        var identityId = await AddIdentityAsync(owner);
        var caller = await SeedSubjectAsync();
        await AddPasskeyAsync(caller);
        await AddIdentityAsync(caller);
        await using var db = database.CreateContext();
        var service = CreateService(db);

        (await service.TryRemovePasskeyCredentialAsync(caller, passkeyId)).Should().Be(FactorRemovalResult.NotFound);
        (await service.TryRemoveOidcIdentityAsync(caller, identityId)).Should().Be(FactorRemovalResult.NotFound);
        (await CountFactorsAsync(owner)).Should().Be(2);
        (await CountFactorsAsync(caller)).Should().Be(2);
    }

    [Fact]
    public async Task RemovingBothFactorsAtOnce_LeavesOne()
    {
        var subjectId = await SeedSubjectAsync();
        var passkeyId = await AddPasskeyAsync(subjectId);
        var identityId = await AddIdentityAsync(subjectId);
        await using var passkeyDb = database.CreateContext();
        await using var identityDb = database.CreateContext();

        var results = await Task.WhenAll(
            Task.Run(() => CreateService(passkeyDb).TryRemovePasskeyCredentialAsync(subjectId, passkeyId)),
            Task.Run(() => CreateService(identityDb).TryRemoveOidcIdentityAsync(subjectId, identityId)));

        results.Should().BeEquivalentTo([FactorRemovalResult.Removed, FactorRemovalResult.LastPrimaryFactor]);
        (await CountFactorsAsync(subjectId)).Should().Be(1);
    }
}
