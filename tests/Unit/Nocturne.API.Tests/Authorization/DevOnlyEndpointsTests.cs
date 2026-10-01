using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using Nocturne.API.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Pins that the unauthenticated dev-only controllers stay out of a production host unless the
/// operator sets <see cref="DevOnlyEndpoints.EnableVariable"/> to exactly <c>true</c>, and that
/// nothing the project ships sets it for them.
/// </summary>
public class DevOnlyEndpointsTests
{
    [Fact]
    public void Production_WithNothingConfigured_LeavesDevOnlyControllersOut()
    {
        DevOnlyEndpoints.AreEnabled(Host(Environments.Production), Config()).Should().BeFalse();
        DevOnlyControllersRegistered(Host(Environments.Production), Config()).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("false")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData("truee")]
    public void Production_WithAnythingButTrue_LeavesDevOnlyControllersOut(string value)
    {
        var config = Config((DevOnlyEndpoints.EnableVariable, value));

        DevOnlyEndpoints.AreEnabled(Host(Environments.Production), config).Should().BeFalse();
        DevOnlyControllersRegistered(Host(Environments.Production), config).Should().BeFalse();
    }

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData(" true ")]
    public void Production_WithTheVariableSetToTrue_RegistersDevOnlyControllers(string value)
    {
        var config = Config((DevOnlyEndpoints.EnableVariable, value));

        DevOnlyEndpoints.AreEnabled(Host(Environments.Production), config).Should().BeTrue();
        DevOnlyControllersRegistered(Host(Environments.Production), config).Should().BeTrue();
    }

    [Fact]
    public void Development_RegistersDevOnlyControllers_WithoutTheVariable()
    {
        DevOnlyEndpoints.AreEnabled(Host(Environments.Development), Config()).Should().BeTrue();
    }

    [Fact]
    public void Staging_WithNothingConfigured_LeavesDevOnlyControllersOut()
    {
        DevOnlyEndpoints.AreEnabled(Host(Environments.Staging), Config()).Should().BeFalse();
    }

    /// <summary>
    /// The shipped appsettings, the self-hosted compose and Portainer bundles and the Helm chart
    /// are what a deployment starts from; the e2e stack under <c>e2e/</c> is the only place the
    /// variable belongs.
    /// </summary>
    [Fact]
    public void NothingADeploymentStartsFrom_SetsTheVariable()
    {
        var root = RepositoryRoot();
        var roots = new[]
        {
            Path.Combine(root, "src", "API", "Nocturne.API"),
            Path.Combine(root, "deploy"),
        };

        var offenders = roots
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            .Where(path => !IsBuildOutput(path))
            .Where(path => IsConfigFile(path))
            .Where(path => File.ReadAllText(path).Contains(DevOnlyEndpoints.EnableVariable, StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .ToList();

        offenders.Should().BeEmpty(
            $"{DevOnlyEndpoints.EnableVariable} opens unauthenticated owner-session endpoints and must "
            + "only ever be set by the e2e stack");
    }

    private static bool DevOnlyControllersRegistered(IHostEnvironment host, IConfiguration config)
    {
        var manager = new ApplicationPartManager();
        manager.ApplicationParts.Add(new AssemblyPart(typeof(DevOnlyEndpoints).Assembly));
        manager.FeatureProviders.Add(new ControllerFeatureProvider());

        AuthorizationConfiguration.ConfigureControllerDiscovery(
            manager, DevOnlyEndpoints.AreEnabled(host, config));

        var feature = new ControllerFeature();
        manager.PopulateFeature(feature);
        return feature.Controllers.Any(c => c.Namespace?.Contains(".DevOnly", StringComparison.Ordinal) == true);
    }

    private static IHostEnvironment Host(string environmentName)
    {
        var host = new Mock<IHostEnvironment>();
        host.SetupGet(h => h.EnvironmentName).Returns(environmentName);
        return host.Object;
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
        || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");

    private static bool IsConfigFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tpl", StringComparison.OrdinalIgnoreCase);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "tests", "Unit")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root not found above " + AppContext.BaseDirectory);
    }
}
