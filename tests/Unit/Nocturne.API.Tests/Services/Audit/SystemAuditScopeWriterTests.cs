using FluentAssertions;
using Nocturne.API.Services.Audit;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Services.Audit;

[Trait("Category", "Unit")]
public class SystemAuditScopeWriterTests
{
    private static readonly Guid Grant = Guid.NewGuid();

    private static AuditContext UploaderContext() =>
        new() { AuthType = nameof(AuthType.DirectGrant), TokenId = Grant, SubjectId = Guid.NewGuid() };

    [Fact]
    public void AnUploadersBatchKeepsItsWriterWhileItIsSystemAttributed()
    {
        var context = UploaderContext();

        using (SystemAuditScope.Push(context, keepWriter: true))
        {
            context.IsSystem.Should().BeTrue();
            context.TokenId.Should().BeNull();
            context.WriterGrantId.Should().Be(Grant);
        }

        context.WriterGrantId.Should().Be(Grant);
        context.IsSystem.Should().BeFalse();
    }

    [Fact]
    public void WorkTheCallerOnlyStartedHasNoWriter()
    {
        var context = UploaderContext();

        using (SystemAuditScope.Push(context))
            context.WriterGrantId.Should().BeNull();

        context.WriterGrantId.Should().Be(Grant);
    }

    [Fact]
    public void AScopeInsideAKeptWriterDropsItAndRestoresItAfterwards()
    {
        var context = UploaderContext();

        using (SystemAuditScope.Push(context, keepWriter: true))
        {
            using (SystemAuditScope.Push(context))
                context.WriterGrantId.Should().BeNull();

            context.WriterGrantId.Should().Be(Grant);
        }
    }

    [Fact]
    public void AJobScopeAttributedToTheSystemHasNoWriter()
    {
        var context = UploaderContext();
        using var kept = SystemAuditScope.Push(context, keepWriter: true);

        context.AttributeToSystem("service:test");

        context.WriterGrantId.Should().BeNull();
    }

    [Fact]
    public void ASessionCookieIsNoWriter()
    {
        IAuditContext context = new AuditContext { AuthType = nameof(AuthType.SessionCookie), TokenId = Guid.NewGuid() };

        context.WriterGrantId.Should().BeNull();
    }
}
