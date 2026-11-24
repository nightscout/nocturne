using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;

namespace Nocturne.Infrastructure.Data.Tests.V4Goldens;

[Trait("Category", "Integration")]
[Collection("V4 goldens")]
public class ApsSnapshotOverrideJsonTests(V4GoldenFixture fixture)
{
    private static readonly DateTime At = new(2026, 11, 16, 19, 49, 17, DateTimeKind.Utc);

    [Theory]
    [InlineData("""{"active":false,"timestamp":"2026-11-16T19:49:17Z"}""")]
    [InlineData("""{"active":true,"name":"Exercise","multiplier":0.8,"timestamp":"2026-11-16T12:37:00Z","currentCorrectionRange":{"minValue":140,"maxValue":160}}""")]
    public async Task Keeps_the_override_object_Loop_uploaded_with_the_snapshot(string uploaded)
    {
        using var scope = await fixture.BeginTenantScopeAsync(Guid.NewGuid());
        var repo = scope.ServiceProvider.GetRequiredService<IApsSnapshotRepository>();
        await repo.BulkCreateAsync(
            [new ApsSnapshot { Timestamp = At, AidAlgorithm = AidAlgorithm.Loop, DataSource = "override-json-test", OverrideJson = uploaded }],
            WriteOrigin.Live);

        var stored = (await repo.GetAsync(At.AddMinutes(-1), At.AddMinutes(1), null, null)).Single();

        JsonNode.DeepEquals(JsonNode.Parse(stored.OverrideJson!), JsonNode.Parse(uploaded))
            .Should().BeTrue("the column is jsonb, so key order may change but no key may be added or lost");
    }

    [Fact]
    public async Task Leaves_the_override_null_when_Loop_sent_none()
    {
        using var scope = await fixture.BeginTenantScopeAsync(Guid.NewGuid());
        var repo = scope.ServiceProvider.GetRequiredService<IApsSnapshotRepository>();
        await repo.BulkCreateAsync(
            [new ApsSnapshot { Timestamp = At, AidAlgorithm = AidAlgorithm.Loop, DataSource = "override-json-test" }],
            WriteOrigin.Live);

        (await repo.GetAsync(At.AddMinutes(-1), At.AddMinutes(1), null, null)).Single().OverrideJson.Should().BeNull();
    }
}
