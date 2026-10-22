using FluentAssertions;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Entities;

public class MutationAuditLogEntityTests
{
    [Fact]
    public void Create_CopiesEveryActorFieldFromTheAuditContext()
    {
        var context = new StubAuditContext
        {
            SubjectId = Guid.NewGuid(),
            SubjectName = "Alex",
            AuthType = "Bearer",
            IpAddress = "203.0.113.7",
            TokenId = Guid.NewGuid(),
            TraceId = "trace-1",
            Endpoint = "PUT /api/v1/entries",
        };

        var row = MutationAuditLogEntity.Create(
            Guid.NewGuid(), context, "Entry", Guid.NewGuid(), "update", "{}", DateTime.UtcNow);

        var actorProperties = typeof(IAuditContext).GetProperties()
            .Where(p => p.Name != nameof(IAuditContext.IsSystem))
            .ToList();
        actorProperties.Should().NotBeEmpty();

        foreach (var property in actorProperties)
        {
            property.GetValue(context).Should().NotBeNull(
                $"set {property.Name} on the stub so the copy is observable");
            var onRow = typeof(MutationAuditLogEntity).GetProperty(property.Name);
            onRow.Should().NotBeNull($"the audit row must record {property.Name}");
            onRow!.GetValue(row).Should().Be(
                property.GetValue(context),
                $"{property.Name} must be copied from the audit context");
        }
    }

    [Fact]
    public void Create_WithNoAuditContext_LeavesActorFieldsNull()
    {
        var row = MutationAuditLogEntity.Create(
            Guid.NewGuid(), null, "Entry", null, "bulk_delete", null, DateTime.UtcNow);

        row.SubjectId.Should().BeNull();
        row.SubjectName.Should().BeNull();
        row.AuthType.Should().BeNull();
        row.IpAddress.Should().BeNull();
        row.TokenId.Should().BeNull();
        row.TraceId.Should().BeNull();
        row.Endpoint.Should().BeNull();
    }

    private sealed class StubAuditContext : IAuditContext
    {
        public Guid? SubjectId { get; init; }
        public string? SubjectName { get; init; }
        public string? AuthType { get; init; }
        public string? IpAddress { get; init; }
        public Guid? TokenId { get; init; }
        public string? TraceId { get; init; }
        public string? Endpoint { get; init; }
    }
}
