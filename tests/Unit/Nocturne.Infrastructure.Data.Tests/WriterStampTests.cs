using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests;

[Trait("Category", "Unit")]
public class WriterStampTests : IDisposable
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId);

    public void Dispose() => _db.Dispose();

    private async Task<BolusEntity> InsertAsAsync(IAuditContext? actor, Guid? writer = null)
    {
        using var context = _db.CreateContext();
        context.AttributedTo(actor);
        var bolus = new BolusEntity { Id = Guid.CreateVersion7(), Timestamp = DateTime.UtcNow, Insulin = 1, WriterGrantId = writer };
        context.Boluses.Add(bolus);
        await context.SaveChangesAsync();
        return bolus;
    }

    [Theory]
    [InlineData(AuthType.OAuthAccessToken)]
    [InlineData(AuthType.ApiKey)]
    [InlineData(AuthType.DirectGrant)]
    [InlineData(AuthType.LegacyJwt)]
    public async Task AnInsertByAGrantBackedCredentialKeepsItsGrant(AuthType authType)
    {
        var grant = Guid.NewGuid();

        var bolus = await InsertAsAsync(new Actor(authType, grant));

        bolus.WriterGrantId.Should().Be(grant);
    }

    [Fact]
    public async Task ASessionCookiesTokenIdIsNotAGrant()
    {
        var bolus = await InsertAsAsync(new Actor(AuthType.SessionCookie, Guid.NewGuid()));

        bolus.WriterGrantId.Should().BeNull();
    }

    [Fact]
    public async Task ASystemWriteHasNoWriter()
    {
        var bolus = await InsertAsAsync(new Actor(AuthType.DirectGrant, Guid.NewGuid(), IsSystem: true));

        bolus.WriterGrantId.Should().BeNull();
    }

    [Fact]
    public async Task AWriterAlreadyOnTheRowIsKept()
    {
        var original = Guid.NewGuid();

        var bolus = await InsertAsAsync(new Actor(AuthType.DirectGrant, Guid.NewGuid()), writer: original);

        bolus.WriterGrantId.Should().Be(original);
    }

    [Fact]
    public async Task AnEditByAnotherCredentialLeavesTheFirstWriter()
    {
        var first = Guid.NewGuid();
        var bolus = await InsertAsAsync(new Actor(AuthType.DirectGrant, first));

        using var context = _db.CreateContext();
        context.AttributedTo(new Actor(AuthType.OAuthAccessToken, Guid.NewGuid()));
        var stored = await context.Boluses.SingleAsync(b => b.Id == bolus.Id);
        stored.Insulin = 2;
        await context.SaveChangesAsync();

        stored.WriterGrantId.Should().Be(first);
    }

    private sealed record Actor(AuthType Type, Guid? TokenId, bool IsSystem = false) : IAuditContext
    {
        public Guid? SubjectId => null;
        public string? SubjectName => null;
        public string? AuthType => Type.ToString();
        public string? IpAddress => null;
        public string? TraceId => null;
        public string? Endpoint => "POST /api/v3/treatments";
    }
}
