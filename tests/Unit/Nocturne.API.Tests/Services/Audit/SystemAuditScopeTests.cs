using FluentAssertions;
using Nocturne.API.Services.Audit;
using Xunit;

namespace Nocturne.API.Tests.Services.Audit;

[Trait("Category", "Unit")]
public class SystemAuditScopeTests
{
    [Fact]
    public void Push_NullsActorFields_PreservesTrace()
    {
        var ambient = new AuditContext
        {
            SubjectId = Guid.NewGuid(), SubjectName = "alice", AuthType = "Bearer",
            TokenId = Guid.NewGuid(), IpAddress = "10.0.0.1",
            CorrelationId = "trace-123", Endpoint = "POST /api/sync",
        };

        using (SystemAuditScope.Push(ambient))
        {
            ambient.SubjectId.Should().BeNull();
            ambient.SubjectName.Should().BeNull();
            ambient.AuthType.Should().BeNull();
            ambient.TokenId.Should().BeNull();
            ambient.IpAddress.Should().BeNull();
            ambient.CorrelationId.Should().Be("trace-123");
            ambient.Endpoint.Should().Be("POST /api/sync");
        }
    }

    [Fact]
    public void Dispose_RestoresOriginalFields()
    {
        var ambient = new AuditContext
        {
            SubjectId = Guid.NewGuid(), AuthType = "Bearer",
            CorrelationId = "trace-123",
        };
        var originalSubject = ambient.SubjectId;

        var scope = SystemAuditScope.Push(ambient);
        scope.Dispose();

        ambient.SubjectId.Should().Be(originalSubject);
        ambient.AuthType.Should().Be("Bearer");
    }

    [Fact]
    public void Push_WithNullAmbientFields_NoOp()
    {
        var ambient = new AuditContext();  // all null

        using (SystemAuditScope.Push(ambient))
        {
            ambient.AuthType.Should().BeNull();
        }

        ambient.AuthType.Should().BeNull();
    }

    [Fact]
    public void NestedScopes_RestoreInOrder()
    {
        var ambient = new AuditContext { AuthType = "Bearer" };

        using (SystemAuditScope.Push(ambient))
        {
            ambient.AuthType.Should().BeNull();
            using (SystemAuditScope.Push(ambient))
            {
                ambient.AuthType.Should().BeNull();
            }
            ambient.AuthType.Should().BeNull();
        }
        ambient.AuthType.Should().Be("Bearer");
    }
}
