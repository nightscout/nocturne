using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Authorization;
using Nocturne.API.Middleware.Handlers;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Middleware.Handlers;

/// <summary>
/// A token this instance minted has to authenticate the way every Nightscout-compatible client
/// presents one: <c>?token=</c> on the request, or an <c>Authorization: Bearer</c> header. The token
/// is minted through the real <see cref="SubjectService"/> over an EF InMemory context rather than
/// written out here, so what the handler accepts stays pinned to what the generator produces — a
/// length copied into a test would not have caught the shape check rejecting every minted token.
/// </summary>
public class AccessTokenHandlerMintedTokenTests : IDisposable
{
    private readonly NocturneDbContext _db;
    private readonly SubjectService _subjects;
    private readonly AccessTokenHandler _handler;

    public AccessTokenHandlerMintedTokenTests()
    {
        _db = TestDbContextFactory.CreateInMemoryContext();
        _subjects = new SubjectService(
            _db,
            Mock.Of<IAuthAuditService>(),
            Mock.Of<IRecoveryCodeService>(),
            NullLogger<SubjectService>.Instance);

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider
            .Setup(p => p.GetService(typeof(ISubjectService)))
            .Returns(_subjects);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(serviceProvider.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        _handler = new AccessTokenHandler(
            scopeFactory.Object,
            NullLogger<AccessTokenHandler>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<string> MintAsync()
    {
        var id = Guid.CreateVersion7();
        _db.Subjects.Add(new SubjectEntity
        {
            Id = id,
            Name = "Uploader",
            AccessTokenHash = "overwritten-by-the-mint",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var token = await _subjects.RegenerateAccessTokenAsync(id);
        token.Should().NotBeNull();
        return token!;
    }

    [Fact]
    public async Task A_minted_token_authenticates_on_the_query_string()
    {
        var token = await MintAsync();

        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString($"?token={token}");

        var result = await _handler.AuthenticateAsync(context);

        result.Succeeded.Should().BeTrue();
        result.AuthContext!.SubjectName.Should().Be("Uploader");
    }

    [Fact]
    public async Task A_minted_token_authenticates_as_a_bearer_credential()
    {
        var token = await MintAsync();

        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = $"Bearer {token}";

        var result = await _handler.AuthenticateAsync(context);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task The_shape_check_accepts_what_the_generator_mints()
    {
        var token = await MintAsync();

        TokenFormat.IsAccessToken(token).Should().BeTrue();
    }
}
