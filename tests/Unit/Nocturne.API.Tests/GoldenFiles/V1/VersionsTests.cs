using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Nocturne.API.Tests.GoldenFiles.Infrastructure;

namespace Nocturne.API.Tests.GoldenFiles.V1;

public class VersionsTests : GoldenFileTestBase
{
    public VersionsTests(GoldenFileWebAppFactory factory) : base(factory) { }

    [Fact]
    public async Task ApiVersionsIsAnArrayOfVersionAndUrl()
    {
        var response = await Client.GetAsync("/api/versions");
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        root.ValueKind.Should().Be(JsonValueKind.Array);
        root.GetArrayLength().Should().BeGreaterThanOrEqualTo(3);

        foreach (var entry in root.EnumerateArray())
        {
            entry.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("url", "version");
            entry.GetProperty("version").GetString().Should().MatchRegex(@"^\d+\.\d+\.\d+$");
            entry.GetProperty("url").GetString().Should().StartWith("/api");
        }
    }

    [Fact]
    public async Task ApiVersionsListsEachLevelAtItsMountPath()
    {
        var response = await Client.GetAsync("/api/versions");
        var entries = JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement.EnumerateArray()
            .ToDictionary(e => e.GetProperty("url").GetString()!, e => e.GetProperty("version").GetString());

        entries["/api/v1"].Should().Be("1.0.0");
        entries["/api/v2"].Should().Be("2.0.0");

        var v3 = await Client.GetAsync("/api/v3/version");
        var v3Version = JsonDocument
            .Parse(await v3.Content.ReadAsStringAsync())
            .RootElement.GetProperty("result")
            .GetProperty("apiVersion")
            .GetString();
        entries["/api/v3"].Should().Be(v3Version);
    }
}
