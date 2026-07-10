using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.DevOnly;

namespace Nocturne.API.Tests.Services.DevOnly;

public class DevIdentityFixtureSeederTests
{
    private static IConfiguration ConfigWith(string? fixturePath) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(fixturePath is null
                ? []
                : new Dictionary<string, string?>
                {
                    [DevIdentityFixtureSeeder.PathConfigKey] = fixturePath,
                })
            .Build();

    [Theory]
    [InlineData("dev-seed-0123456789abcdef", true)]
    [InlineData("dev-seed-", true)]
    [InlineData("dev-see", false)]
    [InlineData("", false)]
    public void IsSyntheticCredentialId_DetectsSeedPrefix(string credentialId, bool expected)
    {
        DevIdentityFixtureSeeder
            .IsSyntheticCredentialId(Encoding.UTF8.GetBytes(credentialId))
            .Should().Be(expected);
    }

    [Fact]
    public void IsSyntheticCredentialId_RealCredentialBytes_AreNotSynthetic()
    {
        // Real credential ids are opaque authenticator bytes, not UTF-8 prefixed.
        var real = new byte[] { 0x9a, 0x01, 0xff, 0x42, 0x10, 0x77, 0x00, 0x2c, 0x55 };
        DevIdentityFixtureSeeder.IsSyntheticCredentialId(real).Should().BeFalse();
    }

    [Fact]
    public void ResolveFixturePath_PrefersConfiguredPath()
    {
        var configured = Path.Combine(Path.GetTempPath(), "custom-identities.json");
        DevIdentityFixtureSeeder
            .ResolveFixturePath(ConfigWith(configured))
            .Should().Be(Path.GetFullPath(configured));
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json");
        DevIdentityFixtureSeeder
            .Load(ConfigWith(missing), NullLogger.Instance)
            .Should().BeNull();
    }

    [Fact]
    public void Load_MalformedJson_ReturnsNullInsteadOfThrowing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"malformed-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not json");
        try
        {
            DevIdentityFixtureSeeder
                .Load(ConfigWith(path), NullLogger.Instance)
                .Should().BeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ValidFixture_ParsesCamelCaseIdentities()
    {
        var subjectId = Guid.NewGuid();
        var path = Path.Combine(Path.GetTempPath(), $"fixture-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, $$"""
            {
              "identities": [
                {
                  "subjectId": "{{subjectId}}",
                  "name": "Dev User",
                  "username": "dev",
                  "credentials": [
                    {
                      "credentialId": "{{Convert.ToBase64String([1, 2, 3])}}",
                      "publicKey": "{{Convert.ToBase64String([4, 5, 6])}}",
                      "transports": ["internal"],
                      "label": "laptop"
                    }
                  ]
                }
              ]
            }
            """);
        try
        {
            var fixture = DevIdentityFixtureSeeder.Load(ConfigWith(path), NullLogger.Instance);

            fixture.Should().NotBeNull();
            fixture!.Identities.Should().HaveCount(1);
            fixture.Identities[0].SubjectId.Should().Be(subjectId);
            fixture.Identities[0].Credentials.Should().HaveCount(1);
            fixture.Identities[0].Credentials[0].Transports.Should().Equal("internal");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_EmptyIdentities_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"empty-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "identities": [] }""");
        try
        {
            DevIdentityFixtureSeeder
                .Load(ConfigWith(path), NullLogger.Instance)
                .Should().BeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
