using System.Reflection;
using FluentAssertions;
using System.Reflection.Emit;
using Nocturne.API.Services.Platform;
using Xunit;

namespace Nocturne.API.Tests.Services.Platform;

[Trait("Category", "Unit")]
public class AppVersionTests
{
    [Theory]
    [InlineData("0.2.7+4f3c9a1b2d", "0.2.7")]
    [InlineData("0.3.0-beta.1+4f3c9a1b2d", "0.3.0-beta.1")]
    [InlineData("0.2.7", "0.2.7")]
    public void Resolve_StripsBuildMetadataFromInformationalVersion(string informational, string expected)
    {
        AppVersion.Resolve(informational, new Version(9, 9, 9, 9)).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("+4f3c9a1b2d")]
    public void Resolve_FallsBackToAssemblyVersion(string? informational)
    {
        AppVersion.Resolve(informational, new Version(1, 2, 3, 4)).Should().Be("1.2.3.4");
    }

    [Fact]
    public void Current_IsTheApiAssemblyInformationalVersionWithoutBuildMetadata()
    {
        var informational = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;

        AppVersion.Current.Should().Be(informational.Split('+')[0]);
    }

    [Fact]
    public void Resolve_ReadsTheInformationalVersionAttributeOfTheAssembly()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("AppVersionProbe"),
            AssemblyBuilderAccess.Run,
            [
                new CustomAttributeBuilder(
                    typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!,
                    ["7.8.9-rc.1+abc123"]
                ),
            ]
        );

        AppVersion.Resolve(assembly).Should().Be("7.8.9-rc.1");
    }
}
