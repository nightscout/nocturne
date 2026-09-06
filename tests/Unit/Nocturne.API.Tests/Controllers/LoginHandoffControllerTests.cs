using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Extensions;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.Auth;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Controllers;

public class LoginHandoffControllerTests : IDisposable
{
    private const string Code = "handoff-code";

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _tenantContext;
    private readonly NocturneDbContext _auditContext;
    private readonly Mock<ISessionService> _sessionService = new();
    private readonly DefaultHttpContext _httpContext = new();
    private readonly LoginHandoffController _controller;

    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _otherTenantId = Guid.CreateVersion7();
    private readonly Guid _memberSubjectId = Guid.CreateVersion7();

    public LoginHandoffControllerTests()
    {
        _db = TestDbContextFactory.CreateSqlite();
        _tenantContext = _db.CreateContext(_tenantId);

        _tenantContext.Tenants.AddRange(
            new TenantEntity { Id = _tenantId, Slug = "acme", DisplayName = "Acme", IsActive = true },
            new TenantEntity { Id = _otherTenantId, Slug = "other", DisplayName = "Other", IsActive = true });
        _tenantContext.Subjects.Add(new SubjectEntity { Id = _memberSubjectId, Name = "Owner", IsActive = true });
        _tenantContext.SaveChanges();

        _sessionService
            .Setup(s => s.IssueSessionAsync(
                It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionTokenPair("access-token", "refresh-token", 900));

        _auditContext = _db.CreateContext();
        var loginCodeService = new LoginCodeService(
            new Mock<IJwtService>().Object,
            new AuthAuditService(
                _auditContext,
                new HttpContextAccessor { HttpContext = _httpContext },
                new AuditContext(),
                new Mock<ILogger<AuthAuditService>>().Object));

        _controller = new LoginHandoffController(
            _tenantContext,
            loginCodeService,
            _sessionService.Object,
            Options.Create(new OidcOptions { Cookie = new CookieSettings { Secure = true } }),
            Options.Create(new BaseDomainOptions { BaseDomain = "nocturne.run" }))
        {
            ControllerContext = new ControllerContext { HttpContext = _httpContext },
        };
    }

    public void Dispose()
    {
        _auditContext.Dispose();
        _tenantContext.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task Exchange_AValidCode_IssuesTheSessionCookiesAndConsumesTheCode()
    {
        SeedCode();

        var response = await ExchangeAsync();

        response.ReturnUrl.Should().Be("/");
        SetCookies().Should().Contain(c => c.StartsWith(SessionCookieExtensions.IsAuthenticatedCookieName));
        _sessionService.Verify(
            s => s.IssueSessionAsync(_memberSubjectId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Once);

        var stored = await _tenantContext.LoginCodes.AsNoTracking().SingleAsync();
        stored.ConsumedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Exchange_TheSameCodeTwice_FailsTheSecondTime()
    {
        SeedCode();

        await ExchangeAsync();
        var second = await InvokeAsync();

        second.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Exchange_AnExpiredCode_Fails()
    {
        SeedCode(expiresAt: DateTime.UtcNow.AddSeconds(-1));

        var result = await InvokeAsync();

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Exchange_ACodeMintedForAnotherTenant_FailsAndLeavesItUnconsumed()
    {
        await using var otherContext = _db.CreateContext(_otherTenantId);
        otherContext.LoginCodes.Add(new LoginCodeEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _otherTenantId,
            SubjectId = _memberSubjectId,
            CodeHash = HashUtils.Sha256Hex(Code),
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
        });
        await otherContext.SaveChangesAsync();

        var result = await InvokeAsync();

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var stored = await otherContext.LoginCodes.AsNoTracking().SingleAsync();
        stored.ConsumedAt.Should().BeNull();
    }

    [Fact]
    public async Task Exchange_AnUnknownCode_Fails()
    {
        var result = await InvokeAsync();

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        SetCookies().Should().BeEmpty();
    }

    [Theory]
    [InlineData("/reports/day-to-day", "/reports/day-to-day")]
    [InlineData("https://nocturne.run/settings", "https://nocturne.run/settings")]
    [InlineData("https://evil.example/steal", "/")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("", "/")]
    public async Task Exchange_ReturnsOnlyASiteLocalTarget(string requested, string expected)
    {
        SeedCode();

        var response = await ExchangeAsync(requested);

        response.ReturnUrl.Should().Be(expected);
    }

    [Fact]
    public async Task Exchange_ASuccess_FilesAnAuditRowAgainstTheMemberAndTenant()
    {
        SeedCode();

        await ExchangeAsync();

        var row = await _auditContext.AuthAuditLog.AsNoTracking()
            .SingleAsync(a => a.EventType == AuthAuditEventType.LoginHandoff);
        row.Success.Should().BeTrue();
        row.SubjectId.Should().Be(_memberSubjectId);
        row.TenantId.Should().Be(_tenantId);
    }

    [Fact]
    public async Task Exchange_ARefusal_FilesAFailureAuditRowNamingNoSubject()
    {
        await InvokeAsync();

        var row = await _auditContext.AuthAuditLog.AsNoTracking()
            .SingleAsync(a => a.EventType == AuthAuditEventType.LoginHandoffFailed);
        row.Success.Should().BeFalse();
        row.SubjectId.Should().BeNull();
        row.TenantId.Should().Be(_tenantId);
    }

    private void SeedCode(DateTime? expiresAt = null)
    {
        _tenantContext.LoginCodes.Add(new LoginCodeEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            SubjectId = _memberSubjectId,
            CodeHash = HashUtils.Sha256Hex(Code),
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddMinutes(5),
        });
        _tenantContext.SaveChanges();
    }

    private Task<ActionResult<LoginHandoffResponse>> InvokeAsync(string? returnUrl = null) =>
        _controller.ExchangeLoginCode(new LoginHandoffRequest(Code, returnUrl), CancellationToken.None);

    private async Task<LoginHandoffResponse> ExchangeAsync(string? returnUrl = null)
    {
        var result = await InvokeAsync(returnUrl);
        return result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<LoginHandoffResponse>().Subject;
    }

    private IReadOnlyList<string> SetCookies() => _httpContext.Response.Headers.SetCookie.ToArray()!;
}
